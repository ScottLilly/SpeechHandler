using NAudio.Wave;

namespace SpeechHandler.Audio;

internal sealed class MixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _sourceBuffer = [];

    public MixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        if (_channels < 1)
        {
            throw new ArgumentException("Source must have at least one channel.", nameof(source));
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        if (_channels == 1)
        {
            return _source.Read(buffer);
        }

        int sourceNeeded = buffer.Length * _channels;
        if (_sourceBuffer.Length < sourceNeeded)
        {
            _sourceBuffer = new float[sourceNeeded];
        }

        int samplesRead = _source.Read(_sourceBuffer.AsSpan(0, sourceNeeded));
        int framesRead = samplesRead / _channels;
        for (int i = 0; i < framesRead; i++)
        {
            float sum = 0;
            int baseIndex = i * _channels;
            for (int c = 0; c < _channels; c++)
            {
                sum += _sourceBuffer[baseIndex + c];
            }

            buffer[i] = sum / _channels;
        }

        return framesRead;
    }
}
