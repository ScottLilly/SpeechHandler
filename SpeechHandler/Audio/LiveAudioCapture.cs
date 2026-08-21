using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpeechHandler.Audio;

internal readonly record struct AudioInputLevel(float CapturePeak, float DevicePeak)
{
    public float Peak => Math.Max(CapturePeak, DevicePeak);
}

internal sealed class LiveAudioCapture : IDisposable
{
    private readonly MMDevice _device;
    private readonly AudioSourceKind _kind;
    private readonly byte[] _readBuffer = new byte[4096]; // 128 ms of 16-bit mono
    private WasapiRecorder? _recorder;
    private BufferedWaveProvider? _buffer;
    private IWaveProvider? _converted;
    private WaveFormat? _captureFormat;
    private bool _disposed;

    public event Action<byte[]>? PcmAvailable;
    public event Action<AudioInputLevel>? LevelAvailable;
    public event Action<Exception?>? Stopped;

    public LiveAudioCapture(string deviceId, AudioSourceKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
        _kind = kind;
    }

    public float DevicePeak
    {
        get
        {
            try
            {
                return _device.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Exception? firstError = null;
        foreach (var requestPcm16k in new[] { true, false })
        {
            try
            {
                StartWith(requestPcm16k);
                return;
            }
            catch (Exception ex)
            {
                firstError ??= ex;
                TearDownRecorder();
            }
        }

        throw firstError ?? new InvalidOperationException("Could not start audio capture.");
    }

    public void Stop()
    {
        try
        {
            _recorder?.StopRecording();
        }
        catch (Exception)
        {
            // Dispose still releases the client.
        }
    }

    private void StartWith(bool requestPcm16kMono)
    {
        TearDownRecorder();

        var builder = new WasapiRecorderBuilder();
        builder.WithDevice(_device);
        builder.WithSharedMode();
        builder.WithBufferLength(100);
        if (_kind == AudioSourceKind.SystemAudio)
        {
            builder.WithLoopbackCapture();
            builder.WithPollingSync();
        }
        else
        {
            builder.WithEventSync();
        }

        if (requestPcm16kMono)
        {
            builder.WithFormat(new WaveFormat(Pcm16kMonoConverter.SampleRate, 16, 1));
        }

        _recorder = builder.Build();
        _captureFormat = _recorder.WaveFormat;
        if (!IsPcm16kMono(_captureFormat))
        {
            _buffer = new BufferedWaveProvider(_captureFormat, TimeSpan.FromSeconds(8))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
            _converted = Pcm16kMonoConverter.ToPcm16kMono(_buffer.ToSampleProvider());
        }

        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
        _recorder.StartRecording();
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_disposed || _captureFormat is null)
        {
            return;
        }

        var devicePeak = Peak(buffer, _captureFormat);
        float capturePeak = 0;

        if (_converted is null || _buffer is null)
        {
            if (!buffer.IsEmpty)
            {
                var chunk = buffer.ToArray();
                capturePeak = Peak(chunk, Pcm16kMonoConverter.PcmFormat);
                PcmAvailable?.Invoke(chunk);
            }

            RaiseLevel(capturePeak, devicePeak);
            return;
        }

        if (!buffer.IsEmpty)
        {
            _buffer.AddSamples(buffer);
        }

        while (true)
        {
            int want = DestinationBytesForBufferedAudio();
            if (want < 2)
            {
                break;
            }

            int read = _converted.Read(_readBuffer.AsSpan(0, want));
            if (read <= 0)
            {
                break;
            }

            var chunk = new byte[read];
            _readBuffer.AsSpan(0, read).CopyTo(chunk);
            var chunkPeak = Peak(chunk, Pcm16kMonoConverter.PcmFormat);
            if (chunkPeak > capturePeak)
            {
                capturePeak = chunkPeak;
            }

            PcmAvailable?.Invoke(chunk);
        }

        RaiseLevel(capturePeak, devicePeak);
    }

    private int DestinationBytesForBufferedAudio()
    {
        if (_buffer is null)
        {
            return 0;
        }

        int sourceBytes = _buffer.BufferedBytes;
        int blockAlign = Math.Max(1, _buffer.WaveFormat.BlockAlign);
        if (sourceBytes < blockAlign)
        {
            return 0;
        }

        double seconds = sourceBytes / (double)_buffer.WaveFormat.AverageBytesPerSecond;
        int destBytes = (int)(seconds * Pcm16kMonoConverter.PcmFormat.AverageBytesPerSecond);
        destBytes &= ~1;
        return Math.Clamp(destBytes, 0, _readBuffer.Length);
    }

    private void RaiseLevel(float capturePeak, float devicePeak)
    {
        var meter = DevicePeak;
        if (meter > devicePeak)
        {
            devicePeak = meter;
        }

        LevelAvailable?.Invoke(new AudioInputLevel(capturePeak, devicePeak));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => Stopped?.Invoke(e.Exception);

    private void TearDownRecorder()
    {
        if (_recorder is null)
        {
            return;
        }

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
        _recorder = null;
        _buffer = null;
        _converted = null;
        _captureFormat = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TearDownRecorder();
        _device.Dispose();
    }

    private static bool IsPcm16kMono(WaveFormat format)
    {
        var standard = format.AsStandardWaveFormat();
        return standard.Encoding == WaveFormatEncoding.Pcm
               && standard.SampleRate == Pcm16kMonoConverter.SampleRate
               && standard.BitsPerSample == 16
               && standard.Channels == 1;
    }

    private static float Peak(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var wf = format.AsStandardWaveFormat();
        if (wf.Encoding == WaveFormatEncoding.IeeeFloat && wf.BitsPerSample == 32)
        {
            float peak = 0;
            foreach (var sample in MemoryMarshal.Cast<byte, float>(buffer))
            {
                float abs = Math.Abs(sample);
                if (abs > peak)
                {
                    peak = abs;
                }
            }

            return peak > 1f ? 1f : peak;
        }

        if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 16)
        {
            int peak = 0;
            foreach (var sample in MemoryMarshal.Cast<byte, short>(buffer))
            {
                int abs = Math.Abs((int)sample);
                if (abs > peak)
                {
                    peak = abs;
                }
            }

            return peak / 32768f;
        }

        return 0;
    }
}
