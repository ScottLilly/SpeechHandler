using System.Text;

namespace SpeechHandler.Transcription;

internal static class SrtFormatter
{
    private const int MaxCharsPerLine = 42;
    private const int MaxLines = 2;
    private const double MaxCueSeconds = 6.0;
    private const double PauseBreakSeconds = 0.7;

    public static string ToSrt(IReadOnlyList<TimedWord> words)
    {
        var cues = BuildCues(words);
        if (cues.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var cue = cues[i];
            builder.Append(i + 1);
            builder.Append('\n');
            builder.Append(FormatTimestamp(cue.StartSeconds));
            builder.Append(" --> ");
            builder.Append(FormatTimestamp(cue.EndSeconds));
            builder.Append('\n');
            builder.Append(cue.Text);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static IReadOnlyList<TimedWord> EstimateWords(string text, double startSeconds, double durationSeconds)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var duration = Math.Max(durationSeconds, Math.Max(1.0, tokens.Length * 0.35));
        var totalChars = tokens.Sum(token => token.Length);
        if (totalChars <= 0)
        {
            totalChars = tokens.Length;
        }

        var words = new List<TimedWord>(tokens.Length);
        var cursor = Math.Max(0, startSeconds);
        for (var i = 0; i < tokens.Length; i++)
        {
            var span = duration * tokens[i].Length / totalChars;
            var end = i == tokens.Length - 1
                ? startSeconds + duration
                : cursor + Math.Max(span, 0.08);
            words.Add(new TimedWord(tokens[i], cursor, end));
            cursor = end;
        }

        return words;
    }

    public static IReadOnlyList<SrtCue> BuildCues(IReadOnlyList<TimedWord> words)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var cues = new List<SrtCue>();
        var current = new List<TimedWord>();
        var maxChars = MaxCharsPerLine * MaxLines;

        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text))
            {
                continue;
            }

            if (current.Count == 0)
            {
                current.Add(word);
                continue;
            }

            var previous = current[^1];
            var gap = word.StartSeconds - previous.EndSeconds;
            var nextText = JoinWords(current, word);
            var duration = word.EndSeconds - current[0].StartSeconds;
            var sentenceEnd = EndsWithSentencePunctuation(previous.Text);

            if (gap >= PauseBreakSeconds
                || duration > MaxCueSeconds
                || nextText.Length > maxChars
                || (sentenceEnd && JoinWords(current).Length >= 20))
            {
                FlushCue(cues, current);
                current.Add(word);
            }
            else
            {
                current.Add(word);
            }
        }

        FlushCue(cues, current);
        PreventOverlap(cues);
        return cues;
    }

    public static string FormatTimestamp(double seconds)
    {
        var totalMs = (long)Math.Round(Math.Max(0, seconds) * 1000.0, MidpointRounding.AwayFromZero);
        var ms = (int)(totalMs % 1000);
        var totalSeconds = totalMs / 1000;
        var s = (int)(totalSeconds % 60);
        var totalMinutes = totalSeconds / 60;
        var m = (int)(totalMinutes % 60);
        var h = totalMinutes / 60;
        return $"{h:00}:{m:00}:{s:00},{ms:000}";
    }

    private static void FlushCue(List<SrtCue> cues, List<TimedWord> current)
    {
        if (current.Count == 0)
        {
            return;
        }

        var text = WrapCueText(JoinWords(current));
        var start = current[0].StartSeconds;
        var end = current[^1].EndSeconds;
        if (end <= start)
        {
            end = start + 0.5;
        }

        cues.Add(new SrtCue(start, end, text));
        current.Clear();
    }

    private static void PreventOverlap(List<SrtCue> cues)
    {
        for (var i = 1; i < cues.Count; i++)
        {
            if (cues[i].StartSeconds >= cues[i - 1].EndSeconds)
            {
                continue;
            }

            var start = cues[i - 1].EndSeconds;
            var end = Math.Max(cues[i].EndSeconds, start + 0.4);
            cues[i] = cues[i] with { StartSeconds = start, EndSeconds = end };
        }
    }

    private static string JoinWords(List<TimedWord> words, TimedWord? extra = null)
    {
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            AppendWord(builder, word.Text);
        }

        if (extra is { } extraWord)
        {
            AppendWord(builder, extraWord.Text);
        }

        return builder.ToString();
    }

    private static void AppendWord(StringBuilder builder, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(text);
    }

    private static string WrapCueText(string text)
    {
        if (text.Length <= MaxCharsPerLine)
        {
            return text;
        }

        var breakAt = FindLineBreak(text, MaxCharsPerLine);
        if (breakAt <= 0 || breakAt >= text.Length)
        {
            return text;
        }

        var first = text[..breakAt].TrimEnd();
        var rest = text[breakAt..].TrimStart();
        if (rest.Length == 0)
        {
            return first;
        }

        if (rest.Length > MaxCharsPerLine)
        {
            var secondBreak = FindLineBreak(rest, MaxCharsPerLine);
            if (secondBreak > 0 && secondBreak < rest.Length)
            {
                rest = rest[..secondBreak].TrimEnd();
            }
        }

        return first + "\n" + rest;
    }

    private static int FindLineBreak(string text, int target)
    {
        var limit = Math.Min(text.Length - 1, Math.Max(target, text.Length / 2));
        for (var i = limit; i > 8; i--)
        {
            if (text[i] == ' ' || text[i] is '.' or ',' or '!' or '?' or ';' or ':')
            {
                return i + (text[i] == ' ' ? 0 : 1);
            }
        }

        var space = text.LastIndexOf(' ', Math.Min(text.Length - 1, target));
        return space > 0 ? space : 0;
    }

    private static bool EndsWithSentencePunctuation(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            return text[i] is '.' or '!' or '?';
        }

        return false;
    }
}

internal readonly record struct SrtCue(double StartSeconds, double EndSeconds, string Text);
