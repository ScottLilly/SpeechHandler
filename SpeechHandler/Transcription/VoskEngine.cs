using System.Text;
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
        _recognizer.SetWords(true);
    }

    public bool Accept(byte[] data, int length, out TranscriptionResult? final, out string? partialText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recognizer.AcceptWaveform(data, length))
        {
            final = ReadResult(_recognizer.Result());
            partialText = null;
            return true;
        }

        final = null;
        partialText = ReadJsonString(_recognizer.PartialResult(), "partial");
        return false;
    }

    public TranscriptionResult? Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadResult(_recognizer.FinalResult());
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

    private static TranscriptionResult? ReadResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var words = ReadWords(root);
        if (words.Count > 0)
        {
            var fromWords = BuildTextFromWords(words);
            if (!string.IsNullOrWhiteSpace(fromWords))
            {
                return new TranscriptionResult(fromWords, words);
            }
        }

        if (!root.TryGetProperty("text", out var value))
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : new TranscriptionResult(text.Trim(), words);
    }

    private static List<TimedWord> ReadWords(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var words) || words.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<TimedWord>();
        foreach (var word in words.EnumerateArray())
        {
            if (!word.TryGetProperty("word", out var tokenElement))
            {
                continue;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var start = word.TryGetProperty("start", out var startElement)
                ? startElement.GetDouble()
                : list.Count > 0 ? list[^1].EndSeconds : 0;
            var end = word.TryGetProperty("end", out var endElement)
                ? endElement.GetDouble()
                : start;
            if (end < start)
            {
                end = start;
            }

            list.Add(new TimedWord(token.Trim(), start, end));
        }

        return list;
    }

    private static string BuildTextFromWords(IReadOnlyList<TimedWord> words)
    {
        var builder = new StringBuilder();
        var lastEnd = -1.0;
        foreach (var word in words)
        {
            if (builder.Length > 0)
            {
                // A long pause inside an utterance is a reliable sentence break.
                // Commas are not: pauses and comma placement often disagree.
                builder.Append(lastEnd >= 0 && word.StartSeconds - lastEnd >= 0.7
                    ? ". "
                    : " ");
            }

            builder.Append(word.Text);
            lastEnd = word.EndSeconds;
        }

        return builder.ToString();
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
