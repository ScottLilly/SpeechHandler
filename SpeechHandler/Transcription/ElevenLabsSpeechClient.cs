using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SpeechHandler.Audio;

namespace SpeechHandler.Transcription;

internal sealed class ElevenLabsSpeechClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Enter an ElevenLabs API key to use the ElevenLabs engine.");
        }

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(model) ? "scribe_v2" : model), "model_id");
        content.Add(new StringContent("word"), "timestamps_granularity");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Trim());
        request.Content = content;

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatApiError(response.StatusCode.ToString(), body));
        }

        return Parse(body, WavDurationSeconds(wavBytes));
    }

    public Task<TranscriptionResult> TranscribePcmAsync(
        byte[] pcm16kMono,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        var wav = Pcm16kMonoConverter.ToWavBytes(pcm16kMono);
        return TranscribeAsync(wav, apiKey, model, cancellationToken);
    }

    internal static TranscriptionResult Parse(string body, double durationSeconds)
    {
        body = body.Trim();
        if (body.Length == 0)
        {
            return TranscriptionResult.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var text = root.TryGetProperty("text", out var textElement)
                ? textElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            var words = ReadWords(root);
            if (text.Length == 0 && words.Count > 0)
            {
                text = string.Join(' ', words.Select(word => word.Text));
            }

            if (text.Length == 0)
            {
                return TranscriptionResult.Empty;
            }

            if (words.Count == 0)
            {
                return TranscriptionResult.FromText(text, 0, durationSeconds);
            }

            return new TranscriptionResult(text, words);
        }
        catch (JsonException)
        {
            return TranscriptionResult.FromText(body, 0, durationSeconds);
        }
    }

    private static List<TimedWord> ReadWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<TimedWord>();
        foreach (var word in words.EnumerateArray())
        {
            var type = word.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : "word";
            if (string.Equals(type, "spacing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "audio_event", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var token = word.TryGetProperty("text", out var textElement)
                ? textElement.GetString()
                : word.TryGetProperty("word", out var wordElement) ? wordElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var start = ReadTime(word, "start", list.Count > 0 ? list[^1].EndSeconds : 0);
            var end = ReadTime(word, "end", start);
            list.Add(new TimedWord(token.Trim(), start, Math.Max(end, start)));
        }

        return list;
    }

    private static double ReadTime(JsonElement element, string name, double fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static double WavDurationSeconds(byte[] wavBytes)
    {
        if (wavBytes.Length <= 44)
        {
            return 0;
        }

        return (wavBytes.Length - 44) / (double)(Pcm16kMonoConverter.SampleRate * 2);
    }

    private static string FormatApiError(string status, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"ElevenLabs request failed ({status}).";
        }

        var snippet = body.Length > 400 ? body[..400] + "…" : body;
        return $"ElevenLabs request failed ({status}): {snippet}";
    }
}
