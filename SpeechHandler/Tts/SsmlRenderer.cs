using NAudio.Wave;

namespace SpeechHandler.Tts;

/// <summary>
/// Speaks a parsed SSML document with any engine: each spoken part is synthesized at its own speed,
/// then the parts are joined with their volume applied and silence written for each pause.
/// </summary>
internal static class SsmlRenderer
{
    private const float MinSpeed = 0.3f;
    private const float MaxSpeed = 3f;

    public static async Task RenderAsync(
        SsmlDocument document,
        ITextToSpeechEngine engine,
        float speed,
        string wavPath,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var spoken = document.Parts.Where(part => !part.IsPause).ToList();
        var partFiles = new Dictionary<SpeechPart, string>(ReferenceEqualityComparer.Instance);
        try
        {
            for (var index = 0; index < spoken.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status?.Report(spoken.Count == 1
                    ? "Generating speech..."
                    : $"Generating speech (part {index + 1} of {spoken.Count})...");
                var partPath = wavPath + $".ssml{index}.wav";
                partFiles[spoken[index]] = partPath;
                var partSpeed = Math.Clamp(speed * spoken[index].Rate, MinSpeed, MaxSpeed);
                await engine.SynthesizeWavFileAsync(spoken[index].Text, partPath, partSpeed, cancellationToken)
                    .ConfigureAwait(false);
            }

            await Task.Run(() => Join(document.Parts, partFiles, wavPath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in partFiles.Values)
            {
                TtsDownloader.TryDelete(path);
            }
        }
    }

    private static void Join(
        IReadOnlyList<SpeechPart> parts,
        IReadOnlyDictionary<SpeechPart, string> partFiles,
        string destination,
        CancellationToken cancellationToken)
    {
        int sampleRate;
        int channels;
        using (var first = new AudioFileReader(partFiles[parts.First(part => !part.IsPause)]))
        {
            sampleRate = first.WaveFormat.SampleRate;
            channels = first.WaveFormat.Channels;
        }

        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        using var writer = new WaveFileWriter(destination, new WaveFormat(sampleRate, 16, channels));
        var buffer = new float[sampleRate * channels];
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.IsPause)
            {
                WriteSilence(writer, buffer, (long)Math.Round(part.PauseSeconds * sampleRate) * channels);
                continue;
            }

            using var reader = new AudioFileReader(partFiles[part]);
            if (reader.WaveFormat.SampleRate != sampleRate || reader.WaveFormat.Channels != channels)
            {
                throw new InvalidOperationException("The voice produced audio parts in different formats.");
            }

            var samples = (ISampleProvider)reader;
            int read;
            while ((read = samples.Read(buffer)) > 0)
            {
                if (part.Volume != 1f)
                {
                    for (var index = 0; index < read; index++)
                    {
                        buffer[index] = Math.Clamp(buffer[index] * part.Volume, -1f, 1f);
                    }
                }

                writer.WriteSamples(buffer, 0, read);
            }
        }
    }

    private static void WriteSilence(WaveFileWriter writer, float[] buffer, long sampleCount)
    {
        Array.Clear(buffer);
        while (sampleCount > 0)
        {
            var count = (int)Math.Min(buffer.Length, sampleCount);
            writer.WriteSamples(buffer, 0, count);
            sampleCount -= count;
        }
    }
}
