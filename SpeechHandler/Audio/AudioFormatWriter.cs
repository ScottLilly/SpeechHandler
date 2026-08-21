using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpeechHandler.Audio;

internal static class AudioFormatWriter
{
    private static readonly object Sync = new();
    private static bool _mediaFoundationStarted;

    public const string FileDialogFilter =
        "WAV (PCM)|*.wav|MP3|*.mp3|AAC (M4A)|*.m4a|FLAC|*.flac|Windows Media Audio|*.wma";

    public static int FilterIndexForExtension(string? extension) =>
        NormalizedExtension(extension) switch
        {
            ".mp3" => 2,
            ".m4a" or ".aac" => 3,
            ".flac" => 4,
            ".wma" => 5,
            _ => 1
        };

    public static string DefaultExtension(string? extension)
    {
        var normalized = NormalizedExtension(extension);
        return normalized switch
        {
            ".mp3" or ".m4a" or ".flac" or ".wma" or ".wav" => normalized,
            ".aac" => ".m4a",
            _ => ".wav"
        };
    }

    public static void WriteFromWav(string sourceWavPath, string destinationPath)
    {
        var extension = NormalizedExtension(Path.GetExtension(destinationPath));
        if (extension is ".wav")
        {
            File.Copy(sourceWavPath, destinationPath, overwrite: true);
            return;
        }

        if (extension is not ".mp3" and not ".m4a" and not ".aac" and not ".flac" and not ".wma")
        {
            throw new InvalidOperationException(
                $"Unsupported audio format '{extension}'. Choose WAV, MP3, M4A, FLAC, or WMA.");
        }

        EnsureMediaFoundation();
        try
        {
            Encode(sourceWavPath, destinationPath, extension, resampleTo44100: false);
            return;
        }
        catch (Exception) when (extension is not ".flac")
        {
            TryDelete(destinationPath);
        }

        try
        {
            Encode(sourceWavPath, destinationPath, extension, resampleTo44100: true);
        }
        catch (Exception retry)
        {
            TryDelete(destinationPath);
            throw new InvalidOperationException(
                $"Could not encode {extension.TrimStart('.').ToUpperInvariant()} audio ({FormatReason(retry)}). Save as WAV instead.",
                retry);
        }
    }

    private static void Encode(
        string sourceWavPath,
        string destinationPath,
        string extension,
        bool resampleTo44100)
    {
        using var reader = new AudioFileReader(sourceWavPath);
        ISampleProvider samples = reader;
        if (resampleTo44100 && reader.WaveFormat.SampleRate != 44100)
        {
            samples = new WdlResamplingSampleProvider(samples, 44100);
        }

        var pcm = samples.ToWaveProvider16();
        switch (extension)
        {
            case ".mp3":
                MediaFoundationEncoder.EncodeToMp3(pcm, destinationPath, 192000);
                break;
            case ".m4a":
            case ".aac":
                MediaFoundationEncoder.EncodeToAac(pcm, destinationPath, 192000);
                break;
            case ".wma":
                MediaFoundationEncoder.EncodeToWma(pcm, destinationPath, 192000);
                break;
            case ".flac":
                MediaFoundationEncoder.EncodeToFlac(pcm, destinationPath);
                break;
        }
    }

    private static string NormalizedExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".wav";
        }

        var trimmed = extension.Trim();
        if (trimmed[0] != '.')
        {
            trimmed = "." + trimmed;
        }

        return trimmed.ToLowerInvariant();
    }

    private static void EnsureMediaFoundation()
    {
        lock (Sync)
        {
            if (_mediaFoundationStarted)
            {
                return;
            }

            MediaFoundationApi.Startup();
            _mediaFoundationStarted = true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a failed encode.
        }
    }

    private static string FormatReason(Exception ex)
    {
        var message = ex.Message.Trim();
        return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message.TrimEnd('.');
    }
}
