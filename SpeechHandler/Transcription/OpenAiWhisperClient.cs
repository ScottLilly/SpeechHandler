using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SpeechHandler.Audio;

namespace SpeechHandler.Transcription;

internal sealed class OpenAiWhisperClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        string apiKey,
        string model,
        bool translateToEnglish,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Enter an OpenAI API key to use the cloud engine.");
        }

        var duration = WavDurationSeconds(wavBytes);
        var useTimestamps = SupportsWordTimestamps(model);
        if (useTimestamps
            && await TrySendAsync(
                wavBytes,
                apiKey,
                model,
                translateToEnglish,
                useTimestamps: true,
                cancellationToken).ConfigureAwait(false) is { } timed)
        {
            var parsed = Parse(timed, duration);
            if (!parsed.IsEmpty)
            {
                return parsed;
            }
        }

        var body = await SendAsync(
            wavBytes,
            apiKey,
            model,
            translateToEnglish,
            useTimestamps: false,
            cancellationToken).ConfigureAwait(false);
        return Parse(body, duration);
    }

    public Task<TranscriptionResult> TranscribePcmAsync(
        byte[] pcm16kMono,
        string apiKey,
        string model,
        bool translateToEnglish,
        CancellationToken cancellationToken)
    {
        var wav = Pcm16kMonoConverter.ToWavBytes(pcm16kMono);
        return TranscribeAsync(wav, apiKey, model, translateToEnglish, cancellationToken);
    }

    private static bool SupportsWordTimestamps(string model) =>
        string.Equals(model, "whisper-1", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> SendAsync(
        byte[] wavBytes,
        string apiKey,
        string model,
        bool translateToEnglish,
        bool useTimestamps,
        CancellationToken cancellationToken)
    {
        var endpoint = translateToEnglish
            ? "https://api.openai.com/v1/audio/translations"
            : "https://api.openai.com/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        if (useTimestamps)
        {
            content.Add(new StringContent("verbose_json"), "response_format");
            content.Add(new StringContent("word"), "timestamp_granularities[]");
            content.Add(new StringContent("segment"), "timestamp_granularities[]");
        }
        else if (SupportsWordTimestamps(model))
        {
            content.Add(new StringContent("text"), "response_format");
        }
        else
        {
            content.Add(new StringContent("json"), "response_format");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = content;

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatApiError(response.StatusCode.ToString(), body));
        }

        return body;
    }

    private static async Task<string?> TrySendAsync(
        byte[] wavBytes,
        string apiKey,
        string model,
        bool translateToEnglish,
        bool useTimestamps,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(
                wavBytes,
                apiKey,
                model,
                translateToEnglish,
                useTimestamps,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
            if (words.Count == 0)
            {
                words = ReadWordsFromSegments(root);
            }

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
            var token = word.TryGetProperty("word", out var tokenElement)
                ? tokenElement.GetString()
                : word.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
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

    private static List<TimedWord> ReadWordsFromSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<TimedWord>();
        foreach (var segment in segments.EnumerateArray())
        {
            var text = segment.TryGetProperty("text", out var textElement)
                ? textElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var start = ReadTime(segment, "start", list.Count > 0 ? list[^1].EndSeconds : 0);
            var end = ReadTime(segment, "end", start);
            list.AddRange(SrtFormatter.EstimateWords(text.Trim(), start, Math.Max(0.4, end - start)));
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
            return $"OpenAI request failed ({status}).";
        }

        var snippet = body.Length > 400 ? body[..400] + "…" : body;
        return $"OpenAI request failed ({status}): {snippet}";
    }
}
