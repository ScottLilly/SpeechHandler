namespace SpeechHandler.Transcription;

internal readonly record struct TimedWord(string Text, double StartSeconds, double EndSeconds);

internal sealed record TranscriptionResult(string Text, IReadOnlyList<TimedWord> Words)
{
    public static TranscriptionResult Empty { get; } = new(string.Empty, []);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Words.Count == 0;

    public static TranscriptionResult FromText(string? text, double startSeconds, double durationSeconds)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Empty;
        }

        return new TranscriptionResult(text, SrtFormatter.EstimateWords(text, startSeconds, durationSeconds));
    }
}
