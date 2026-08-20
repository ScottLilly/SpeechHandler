using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpeechHandler.Audio;

internal static class Pcm16kMonoConverter
{
    public const int SampleRate = 16000;

    public static WaveFormat PcmFormat { get; } = new WaveFormat(SampleRate, 16, 1);

    public static IWaveProvider ToPcm16kMono(ISampleProvider source)
    {
        ISampleProvider samples = source.WaveFormat.Channels switch
        {
            1 => source,
            2 => source.ToMono(0.5f, 0.5f),
            _ => new MixToMonoSampleProvider(source)
        };

        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        }

        return samples.ToWaveProvider16();
    }

    public static byte[] ToWavBytes(ReadOnlySpan<byte> pcm16kMono)
    {
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), PcmFormat))
        {
            writer.Write(pcm16kMono);
        }

        return ms.ToArray();
    }
}
