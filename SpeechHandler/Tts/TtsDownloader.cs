using System.Diagnostics;
using System.Net.Http;

namespace SpeechHandler.Tts;

internal static class TtsDownloader
{
    public static async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destination + ".partial";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromHours(3) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SpeechHandler/1.0");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(99, copied * 99.0 / total));
                    }
                }
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(tempPath, destination);
            progress?.Report(100);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task ExtractTarBz2Async(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var tarExe = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tarExe))
        {
            throw new InvalidOperationException("Windows tar.exe is required to unpack the Kokoro voice pack.");
        }

        var start = new ProcessStartInfo
        {
            FileName = tarExe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-xf");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(destinationDirectory);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start tar.exe to unpack the voice pack.");
        }

        await using (cancellationToken.Register(() =>
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
                     }))
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}." : stderr.Trim();
                throw new InvalidOperationException("Could not unpack the voice pack: " + detail);
            }
        }
    }

    public static void TryDelete(string path)
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
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
