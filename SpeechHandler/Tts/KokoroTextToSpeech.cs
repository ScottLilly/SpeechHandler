using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SherpaOnnx;
using SpeechHandler.Transcription;

namespace SpeechHandler.Tts;

internal sealed class KokoroTtsEngine : ITextToSpeechEngine
{
    private readonly KokoroTtsRuntime _runtime;
    private readonly int _speakerId;

    public KokoroTtsEngine(KokoroTtsRuntime runtime, int speakerId)
    {
        _runtime = runtime;
        _speakerId = speakerId;
    }

    public Task EnsureReadyAsync(IProgress<string>? status, CancellationToken cancellationToken) =>
        _runtime.EnsureReadyAsync(status, cancellationToken);

    public Task SynthesizeWavFileAsync(
        string text,
        string wavPath,
        float speed,
        CancellationToken cancellationToken) =>
        _runtime.SynthesizeAsync(text, _speakerId, speed, wavPath, cancellationToken);
}

/// <summary>
/// Kokoro 82M via sherpa-onnx. One English v1.0 pack unlocks all Kokoro voices.
/// </summary>
internal sealed class KokoroTtsRuntime : IDisposable
{
    public const string FolderName = "kokoro-multi-lang-v1_0";

    private const string ArchiveUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2";

    public static KokoroTtsRuntime Shared { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;

    public static string ModelDirectory => Path.Combine(AppStorage.TtsVoicesDirectory, FolderName);

    public static bool IsInstalled()
    {
        var folder = ModelDirectory;
        return File.Exists(Path.Combine(folder, "model.onnx"))
               && File.Exists(Path.Combine(folder, "voices.bin"))
               && File.Exists(Path.Combine(folder, "tokens.txt"))
               && File.Exists(Path.Combine(folder, "lexicon-us-en.txt"))
               && File.Exists(Path.Combine(folder, "lexicon-zh.txt"))
               && Directory.Exists(Path.Combine(folder, "espeak-ng-data"));
    }

    public async Task EnsureReadyAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        if (!IsInstalled())
        {
            await DownloadAsync(progress: null, status, cancellationToken).ConfigureAwait(false);
        }

        await EnsureLoadedAsync(status, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (IsInstalled())
        {
            progress?.Report(100);
            return;
        }

        Directory.CreateDirectory(AppStorage.TtsVoicesDirectory);
        var archivePath = Path.Combine(AppStorage.TtsVoicesDirectory, FolderName + ".tar.bz2");
        status?.Report("Downloading Kokoro English voices…");
        var downloadProgress = new Progress<double>(value => progress?.Report(value * 0.9));
        await TtsDownloader.DownloadFileAsync(ArchiveUrl, archivePath, downloadProgress, cancellationToken)
            .ConfigureAwait(false);

        status?.Report("Unpacking Kokoro voice pack…");
        progress?.Report(92);
        var extractRoot = AppStorage.TtsVoicesDirectory;
        try
        {
            await TtsDownloader.ExtractTarBz2Async(archivePath, extractRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TtsDownloader.TryDelete(archivePath);
        }

        if (!IsInstalled())
        {
            throw new InvalidOperationException("Kokoro downloaded but the voice files were not found.");
        }

        progress?.Report(100);
    }

    public async Task SynthesizeAsync(
        string text,
        int speakerId,
        float speed,
        string wavPath,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(status: null, cancellationToken).ConfigureAwait(false);
        var tts = _tts ?? throw new InvalidOperationException("Kokoro failed to load.");
        var chunks = SplitForTts(text);
        var safeSpeed = speed <= 0 ? 1f : speed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (chunks.Count == 1)
            {
                await GenerateToFileAsync(tts, chunks[0], speakerId, safeSpeed, wavPath, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var parts = new List<string>(chunks.Count);
            try
            {
                for (var index = 0; index < chunks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var partPath = wavPath + $".part{index}.wav";
                    await GenerateToFileAsync(tts, chunks[index], speakerId, safeSpeed, partPath, cancellationToken)
                        .ConfigureAwait(false);
                    parts.Add(partPath);
                }

                ConcatenateWavFiles(parts, wavPath);
            }
            finally
            {
                foreach (var part in parts)
                {
                    TtsDownloader.TryDelete(part);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _tts?.Dispose();
        _tts = null;
        _gate.Dispose();
    }

    private async Task EnsureLoadedAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        if (_tts is not null)
        {
            return;
        }

        if (!IsInstalled())
        {
            throw new InvalidOperationException("The Kokoro voice pack is not installed. Open File → Voice settings to download it.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tts is not null)
            {
                return;
            }

            status?.Report("Loading Kokoro…");
            var folder = ModelDirectory;
            var config = new OfflineTtsConfig();
            config.Model.Kokoro.Model = Path.Combine(folder, "model.onnx");
            config.Model.Kokoro.Voices = Path.Combine(folder, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(folder, "tokens.txt");
            config.Model.Kokoro.DataDir = Path.Combine(folder, "espeak-ng-data");
            config.Model.Kokoro.Lexicon =
                Path.Combine(folder, "lexicon-us-en.txt") + "," + Path.Combine(folder, "lexicon-zh.txt");
            config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 2;
            config.SilenceScale = 0.2f;

            _tts = await Task.Run(() => new OfflineTts(config), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task GenerateToFileAsync(
        OfflineTts tts,
        string text,
        int speakerId,
        float speed,
        string wavPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(wavPath))
        {
            File.Delete(wavPath);
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audio = tts.GenerateWithCallbackProgress(
                text,
                speed,
                speakerId,
                (_, _, _) => cancellationToken.IsCancellationRequested ? 0 : 1);
            try
            {
                if (audio.Handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Kokoro did not generate audio.");
                }

                if (!audio.SaveToWaveFile(wavPath) || !File.Exists(wavPath))
                {
                    throw new InvalidOperationException("Kokoro could not write the speech file.");
                }
            }
            finally
            {
                audio.Dispose();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> SplitForTts(string text, int maxChars = 480)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
        {
            return [trimmed];
        }

        var sentences = Regex.Split(trimmed, @"(?<=[.!?])\s+");
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in sentences)
        {
            foreach (var piece in SplitLongSentence(sentence, maxChars))
            {
                if (current.Length > 0 && current.Length + 1 + piece.Length > maxChars)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.Count > 0 ? parts : [trimmed];
    }

    private static IEnumerable<string> SplitLongSentence(string sentence, int maxChars)
    {
        if (sentence.Length <= maxChars)
        {
            yield return sentence;
            yield break;
        }

        var remaining = sentence;
        while (remaining.Length > maxChars)
        {
            var cut = remaining.LastIndexOf(' ', maxChars);
            if (cut < maxChars / 2)
            {
                cut = maxChars;
            }

            yield return remaining[..cut].Trim();
            remaining = remaining[cut..].Trim();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static void ConcatenateWavFiles(IReadOnlyList<string> parts, string destination)
    {
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("Kokoro produced no audio.");
        }

        if (parts.Count == 1)
        {
            File.Copy(parts[0], destination, overwrite: true);
            return;
        }

        using var first = new AudioFileReader(parts[0]);
        using var writer = new WaveFileWriter(destination, first.WaveFormat);
        CopySamples(first, writer);
        for (var index = 1; index < parts.Count; index++)
        {
            using var reader = new AudioFileReader(parts[index]);
            if (reader.WaveFormat.SampleRate != writer.WaveFormat.SampleRate
                || reader.WaveFormat.Channels != writer.WaveFormat.Channels)
            {
                throw new InvalidOperationException("Kokoro audio chunks used different formats.");
            }

            CopySamples(reader, writer);
        }
    }

    private static void CopySamples(AudioFileReader reader, WaveFileWriter writer)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.Write(buffer, 0, read);
        }
    }
}
