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

    public async Task<string> TranscribeAsync(
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Trim());
        request.Content = content;

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatApiError(response.StatusCode.ToString(), body));
        }

        return ReadTranscript(body);
    }

    public Task<string> TranscribePcmAsync(
        byte[] pcm16kMono,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        var wav = Pcm16kMonoConverter.ToWavBytes(pcm16kMono);
        return TranscribeAsync(wav, apiKey, model, cancellationToken);
    }

    private static string ReadTranscript(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("text", out var text)
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return text.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }

        return body.Trim();
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
