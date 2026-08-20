using NAudio.Wave;

namespace SpeechHandler.Audio;

internal sealed record AudioFileDetails(
    string Path,
    string FileName,
    string Duration,
    string Size,
    string Format);

internal static class AudioFileInspector
{
    public static AudioFileDetails Read(string path)
    {
        var fileName = System.IO.Path.GetFileName(path);
        var size = FormatSize(new FileInfo(path).Length);
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = "Audio";
        }

        try
        {
            using var reader = new AudioFileReader(path);
            var format = reader.WaveFormat;
            var channels = format.Channels switch
            {
                1 => "mono",
                2 => "stereo",
                _ => $"{format.Channels} channels"
            };

            return new AudioFileDetails(
                path,
                fileName,
                FormatDuration(reader.TotalTime),
                size,
                $"{extension} · {format.SampleRate / 1000.0:0.###} kHz · {channels}");
        }
        catch (Exception)
        {
            return new AudioFileDetails(path, fileName, "Unknown", size, extension);
        }
    }

    private static string FormatDuration(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
