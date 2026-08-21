using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using SpeechHandler.Transcription;

namespace SpeechHandler.Tts;

/// <summary>
/// Offline neural TTS via Piper. Smaller and faster than Kokoro, with a more synthetic sound.
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

    public bool IsInstalled() => File.Exists(VoiceOnnxPath()) && File.Exists(VoiceJsonPath());

    public async Task EnsureReadyAsync(IProgress<string>? status, CancellationToken cancellationToken) =>
        await DownloadAsync(progress: null, status, cancellationToken).ConfigureAwait(false);

    public async Task DownloadAsync(
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppStorage.Root);
        Directory.CreateDirectory(AppStorage.TtsVoicesDirectory);

        var piperExe = Path.Combine(AppStorage.PiperDirectory, "piper.exe");
        if (!File.Exists(piperExe))
        {
            status?.Report("Downloading Piper neural TTS…");
            await DownloadAndExtractPiperAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsInstalled())
        {
            status?.Report($"Downloading {_fileStem} voice…");
            await DownloadVoiceAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(100);
        }
    }

    public async Task SynthesizeWavFileAsync(
        string text,
        string wavPath,
        float speed,
        CancellationToken cancellationToken)
    {
        var piperExe = Path.Combine(AppStorage.PiperDirectory, "piper.exe");
        var onnx = VoiceOnnxPath();
        if (!File.Exists(piperExe) || !File.Exists(onnx))
        {
            throw new InvalidOperationException("The Piper voice is not installed. Open File → Voice settings to download it.");
        }

        if (File.Exists(wavPath))
        {
            File.Delete(wavPath);
        }

        var lengthScale = speed <= 0 ? 1f : 1f / speed;
        var start = new ProcessStartInfo
        {
            FileName = piperExe,
            Arguments =
                $"--model \"{onnx}\" --output_file \"{wavPath}\" --length-scale {lengthScale.ToString(CultureInfo.InvariantCulture)} --quiet",
            WorkingDirectory = AppStorage.PiperDirectory,
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

    private string VoiceFolder => Path.Combine(AppStorage.TtsVoicesDirectory, _fileStem);

    private string VoiceOnnxPath() => Path.Combine(VoiceFolder, _fileStem + ".onnx");

    private string VoiceJsonPath() => Path.Combine(VoiceFolder, _fileStem + ".onnx.json");

    private async Task DownloadAndExtractPiperAsync(CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(AppStorage.Root, "piper_windows_amd64.zip");
        await TtsDownloader.DownloadFileAsync(PiperZipUrl, zipPath, progress: null, cancellationToken)
            .ConfigureAwait(false);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, AppStorage.Root, overwriteFiles: true), cancellationToken)
            .ConfigureAwait(false);
        TtsDownloader.TryDelete(zipPath);

        if (!File.Exists(Path.Combine(AppStorage.PiperDirectory, "piper.exe")))
        {
            throw new InvalidOperationException("Piper downloaded but piper.exe was not found.");
        }
    }

    private async Task DownloadVoiceAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(VoiceFolder);
        var onnxProgress = new Progress<double>(value => progress?.Report(value * 0.92));
        await TtsDownloader.DownloadFileAsync(
            VoiceBaseUrl + _repoPath + ".onnx?download=true",
            VoiceOnnxPath(),
            onnxProgress,
            cancellationToken).ConfigureAwait(false);
        await TtsDownloader.DownloadFileAsync(
            VoiceBaseUrl + _repoPath + ".onnx.json?download=true",
            VoiceJsonPath(),
            progress: null,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
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
