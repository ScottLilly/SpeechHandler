using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace SpeechHandler.Tts;

/// <summary>
/// A playable voice. Cloned / speaker-tuned voices can be added later
/// as additional options that implement <see cref="ITextToSpeechEngine"/>.
/// </summary>
internal sealed record TtsVoiceOption(string Id, string DisplayName, ITextToSpeechEngine Engine)
{
    public override string ToString() => DisplayName;
}

internal interface ITextToSpeechEngine
{
    Task EnsureReadyAsync(IProgress<string>? status, CancellationToken cancellationToken);

    Task SynthesizeWavFileAsync(string text, string wavPath, CancellationToken cancellationToken);
}

internal static class TtsVoiceCatalog
{
    public static IReadOnlyList<TtsVoiceOption> Voices { get; } =
    [
        new("lessac", "Lessac (US English, neural)", new PiperTtsEngine(
            "en_US-lessac-medium",
            "en/en_US/lessac/medium/en_US-lessac-medium")),
        new("amy", "Amy (US English, neural)", new PiperTtsEngine(
            "en_US-amy-medium",
            "en/en_US/amy/medium/en_US-amy-medium")),
        new("ryan", "Ryan (US English, neural)", new PiperTtsEngine(
            "en_US-ryan-medium",
            "en/en_US/ryan/medium/en_US-ryan-medium"))
    ];
}

/// <summary>
/// Offline neural TTS via Piper. Much more natural than Windows SAPI voices.
/// </summary>
internal sealed class PiperTtsEngine : ITextToSpeechEngine
{
    private const string PiperZipUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";

    private const string VoiceBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";

    private readonly string _fileStem;
    private readonly string _repoPath;

    public PiperTtsEngine(string fileStem, string repoPath)
    {
        _fileStem = fileStem;
        _repoPath = repoPath;
    }

    public async Task EnsureReadyAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Transcription.AppStorage.Root);
        Directory.CreateDirectory(Transcription.AppStorage.TtsVoicesDirectory);

        var piperExe = Path.Combine(Transcription.AppStorage.PiperDirectory, "piper.exe");
        if (!File.Exists(piperExe))
        {
            status?.Report("Downloading Piper neural TTS…");
            await DownloadAndExtractPiperAsync(cancellationToken).ConfigureAwait(false);
        }

        var onnx = VoiceOnnxPath();
        var json = VoiceJsonPath();
        if (!File.Exists(onnx) || !File.Exists(json))
        {
            status?.Report($"Downloading {_fileStem} voice…");
            await DownloadVoiceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SynthesizeWavFileAsync(string text, string wavPath, CancellationToken cancellationToken)
    {
        var piperExe = Path.Combine(Transcription.AppStorage.PiperDirectory, "piper.exe");
        var onnx = VoiceOnnxPath();
        if (!File.Exists(piperExe) || !File.Exists(onnx))
        {
            throw new InvalidOperationException("The Piper voice is not installed. Try speaking again to download it.");
        }

        if (File.Exists(wavPath))
        {
            File.Delete(wavPath);
        }

        var start = new ProcessStartInfo
        {
            FileName = piperExe,
            Arguments = $"--model \"{onnx}\" --output_file \"{wavPath}\" --quiet",
            WorkingDirectory = Transcription.AppStorage.PiperDirectory,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Piper failed to start.");
        }

        await using (cancellationToken.Register(() => TryKill(process)))
        {
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            var stderr = await stderrTask.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(wavPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}." : stderr.Trim();
                throw new InvalidOperationException("Piper could not generate speech: " + detail);
            }
        }
    }

    private string VoiceFolder => Path.Combine(Transcription.AppStorage.TtsVoicesDirectory, _fileStem);

    private string VoiceOnnxPath() => Path.Combine(VoiceFolder, _fileStem + ".onnx");

    private string VoiceJsonPath() => Path.Combine(VoiceFolder, _fileStem + ".onnx.json");

    private async Task DownloadAndExtractPiperAsync(CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(Transcription.AppStorage.Root, "piper_windows_amd64.zip");
        await DownloadFileAsync(PiperZipUrl, zipPath, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, Transcription.AppStorage.Root, overwriteFiles: true), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            File.Delete(zipPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        if (!File.Exists(Path.Combine(Transcription.AppStorage.PiperDirectory, "piper.exe")))
        {
            throw new InvalidOperationException("Piper downloaded but piper.exe was not found.");
        }
    }

    private async Task DownloadVoiceAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(VoiceFolder);
        await DownloadFileAsync(VoiceBaseUrl + _repoPath + ".onnx?download=true", VoiceOnnxPath(), cancellationToken).ConfigureAwait(false);
        await DownloadFileAsync(VoiceBaseUrl + _repoPath + ".onnx.json?download=true", VoiceJsonPath(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SpeechHandler/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }
}
