using System.Net.Http;
using System.Net.Http.Headers;
using SpeechHandler.Audio;

namespace SpeechHandler.Transcription;

internal sealed class OpenAiWhisperClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public async Task<string> TranscribeAsync(
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

        var endpoint = translateToEnglish
            ? "https://api.openai.com/v1/audio/translations"
            : "https://api.openai.com/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("text"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = content;

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatApiError(response.StatusCode.ToString(), body));
        }

        return body.Trim();
    }

    public Task<string> TranscribePcmAsync(
        byte[] pcm16kMono,
        string apiKey,
        string model,
        bool translateToEnglish,
        CancellationToken cancellationToken)
    {
        var wav = Pcm16kMonoConverter.ToWavBytes(pcm16kMono);
        return TranscribeAsync(wav, apiKey, model, translateToEnglish, cancellationToken);
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
