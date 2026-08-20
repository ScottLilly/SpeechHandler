using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpeechHandler.Audio;

internal sealed class LiveAudioCapture : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _converted;
    private readonly byte[] _readBuffer = new byte[Pcm16kMonoConverter.SampleRate]; // 500 ms of 16-bit mono
    private bool _disposed;

    public event Action<byte[]>? PcmAvailable;
    public event Action<Exception?>? Stopped;

    public LiveAudioCapture(string deviceId, AudioSourceKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);

        var builder = new WasapiRecorderBuilder();
        builder.WithDevice(_device);
        builder.WithSharedMode();
        builder.WithEventSync();
        builder.WithBufferLength(50);
        if (kind == AudioSourceKind.SystemAudio)
        {
            builder.WithLoopbackCapture();
        }

        _recorder = builder.Build();
        _buffer = new BufferedWaveProvider(_recorder.WaveFormat, TimeSpan.FromSeconds(8))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        _converted = Pcm16kMonoConverter.ToPcm16kMono(_buffer.ToSampleProvider());
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public void Start() => _recorder.StartRecording();

    public void Stop()
    {
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception)
        {
            // Dispose still releases the client.
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        _buffer.AddSamples(buffer);

        while (true)
        {
            int read = _converted.Read(_readBuffer);
            if (read <= 0)
            {
                break;
            }

            var chunk = new byte[read];
            _readBuffer.AsSpan(0, read).CopyTo(chunk);
            PcmAvailable?.Invoke(chunk);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => Stopped?.Invoke(e.Exception);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception)
        {
            // ignored
        }

        _recorder.Dispose();
        _device.Dispose();
    }
}
