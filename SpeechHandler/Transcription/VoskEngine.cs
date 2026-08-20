using System.Text.Json;
using Vosk;

namespace SpeechHandler.Transcription;

internal sealed class VoskEngine : IDisposable
{
    private Model? _model;
    private string? _modelPath;
    private bool _disposed;

    public string? ModelPath => _modelPath;

    public void EnsureLoaded(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = Path.GetFullPath(modelPath);
        if (_model is not null && string.Equals(_modelPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _model?.Dispose();
        _model = null;
        global::Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(fullPath);
        _modelPath = fullPath;
    }

    public VoskSession CreateSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_model is null)
        {
            throw new InvalidOperationException("Load a Vosk model before transcribing.");
        }

        return new VoskSession(_model);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _model?.Dispose();
        _model = null;
    }
}

internal sealed class VoskSession : IDisposable
{
    private readonly VoskRecognizer _recognizer;
    private bool _disposed;

    public VoskSession(Model model)
    {
        _recognizer = new VoskRecognizer(model, Audio.Pcm16kMonoConverter.SampleRate);
        _recognizer.SetMaxAlternatives(0);
        _recognizer.SetWords(false);
    }

    public bool Accept(byte[] data, int length, out string? finalText, out string? partialText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recognizer.AcceptWaveform(data, length))
        {
            finalText = ReadJsonString(_recognizer.Result(), "text");
            partialText = null;
            return true;
        }

        finalText = null;
        partialText = ReadJsonString(_recognizer.PartialResult(), "partial");
        return false;
    }

    public string? Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadJsonString(_recognizer.FinalResult(), "text");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer.Dispose();
    }

    private static string? ReadJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
