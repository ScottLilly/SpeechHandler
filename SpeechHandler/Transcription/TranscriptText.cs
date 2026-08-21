using System.Text.RegularExpressions;

namespace SpeechHandler.Transcription;

internal readonly record struct PreparedTranscript(string Existing, string Incoming);

internal static class TranscriptText
{
    internal const double CommaPauseSeconds = 0.3;
    internal const double PeriodPauseSeconds = 0.7;

    private static readonly HashSet<string> ProtectedShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "am", "an", "as", "at", "be", "by", "do", "go", "he", "i", "if", "in", "is", "it",
        "me", "my", "no", "of", "oh", "on", "or", "so", "to", "up", "us", "we"
    };

    private static readonly HashSet<string> NoCommaAfter = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "nor", "of", "to", "for", "with", "at", "by",
        "from", "in", "on", "is", "are", "was", "were", "be", "been", "i", "we", "you", "he",
        "she", "it", "they", "my", "your"
    };

    public static PreparedTranscript Prepare(string existing, string incoming, bool formatAsSentences)
    {
        incoming = incoming.Trim();
        existing = existing.TrimEnd();
        var (alignedExisting, alignedIncoming) = AlignOverlap(existing, incoming);
        if (formatAsSentences && alignedIncoming.Length > 0)
        {
            alignedIncoming = FormatAsSentences(alignedIncoming, alignedExisting);
        }

        return new PreparedTranscript(alignedExisting, alignedIncoming);
    }

    public static List<TimedWord> PrepareWords(
        List<TimedWord> existing,
        IReadOnlyList<TimedWord> incoming,
        string existingText,
        bool formatAsSentences)
    {
        var alignedIncoming = incoming
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => word with { Text = word.Text.Trim() })
            .ToList();
        AlignWordOverlap(existing, alignedIncoming);
        ApplyPausePunctuation(existing, alignedIncoming);
        if (formatAsSentences)
        {
            var prior = existing.Count > 0 ? existing[^1].Text : existingText.TrimEnd();
            FormatWordList(alignedIncoming, prior);
        }

        return alignedIncoming;
    }

    internal static void AlignWordOverlap(List<TimedWord> existing, List<TimedWord> incoming)
    {
        if (existing.Count == 0 || incoming.Count == 0)
        {
            return;
        }

        var lastWord = existing[^1].Text;
        var firstWord = incoming[0].Text;
        var lastCore = CoreWord(lastWord);
        var firstCore = CoreWord(firstWord);
        if (lastCore.Length == 0 || firstCore.Length == 0)
        {
            return;
        }

        if (lastCore.Equals(firstCore, StringComparison.OrdinalIgnoreCase))
        {
            incoming.RemoveAt(0);
            return;
        }

        if (firstCore.Length <= 3
            && firstCore.Length < lastCore.Length
            && lastCore.EndsWith(firstCore, StringComparison.OrdinalIgnoreCase)
            && !ProtectedShortWords.Contains(firstCore))
        {
            incoming.RemoveAt(0);
            return;
        }

        if (lastCore.Length >= 2
            && lastCore.Length < firstCore.Length
            && firstCore.StartsWith(lastCore, StringComparison.OrdinalIgnoreCase)
            && !ProtectedShortWords.Contains(lastCore))
        {
            existing[^1] = incoming[0];
            incoming.RemoveAt(0);
        }
    }

    internal static List<TimedWord> StripPrefixWords(IReadOnlyList<TimedWord> words, string prefix)
    {
        prefix = prefix.Trim();
        if (prefix.Length == 0 || words.Count == 0)
        {
            return words.ToList();
        }

        var consumed = 0;
        var index = 0;
        while (index < words.Count && consumed < prefix.Length)
        {
            while (consumed < prefix.Length && char.IsWhiteSpace(prefix[consumed]))
            {
                consumed++;
            }

            if (consumed >= prefix.Length)
            {
                break;
            }

            var token = words[index].Text.Trim();
            if (token.Length == 0)
            {
                index++;
                continue;
            }

            if (prefix.AsSpan(consumed).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                consumed += token.Length;
                index++;
                continue;
            }

            break;
        }

        return words.Skip(index).ToList();
    }

    internal static IReadOnlyList<TimedWord> Offset(IReadOnlyList<TimedWord> words, double seconds)
    {
        if (seconds == 0 || words.Count == 0)
        {
            return words;
        }

        return words
            .Select(word => word with
            {
                StartSeconds = word.StartSeconds + seconds,
                EndSeconds = word.EndSeconds + seconds
            })
            .ToList();
    }

    internal static string SyncTrailingWord(string existingText, string updatedLastWord)
    {
        existingText = existingText.TrimEnd();
        if (existingText.Length == 0 || string.IsNullOrWhiteSpace(updatedLastWord))
        {
            return existingText;
        }

        var lastWord = GetLastWord(existingText, out var start);
        if (!CoreWord(lastWord).Equals(CoreWord(updatedLastWord), StringComparison.OrdinalIgnoreCase))
        {
            return existingText;
        }

        return existingText[..start] + updatedLastWord;
    }

    internal static char? PunctuationForPause(double gapSeconds, string? previousWord = null)
    {
        if (gapSeconds >= PeriodPauseSeconds)
        {
            return '.';
        }

        if (gapSeconds >= CommaPauseSeconds && !ShouldSkipComma(previousWord))
        {
            return ',';
        }

        return null;
    }

    private static void ApplyPausePunctuation(List<TimedWord> existing, List<TimedWord> incoming)
    {
        if (incoming.Count == 0)
        {
            return;
        }

        if (existing.Count > 0)
        {
            MaybeAppendPausePunctuation(existing, existing.Count - 1, incoming[0].StartSeconds);
        }

        for (var i = 1; i < incoming.Count; i++)
        {
            MaybeAppendPausePunctuation(incoming, i - 1, incoming[i].StartSeconds);
        }
    }

    private static void MaybeAppendPausePunctuation(List<TimedWord> words, int index, double nextStart)
    {
        var previous = words[index];
        var mark = PunctuationForPause(nextStart - previous.EndSeconds, previous.Text);
        if (mark is null)
        {
            return;
        }

        var text = previous.Text.TrimEnd();
        if (text.Length == 0 || EndsWithPausePunctuation(text))
        {
            return;
        }

        words[index] = previous with { Text = text + mark };
    }

    private static bool ShouldSkipComma(string? previousWord)
    {
        if (string.IsNullOrWhiteSpace(previousWord))
        {
            return false;
        }

        return NoCommaAfter.Contains(CoreWord(previousWord));
    }

    private static bool EndsWithPausePunctuation(string text) =>
        text[^1] is '.' or ',' or '!' or '?' or ';' or ':';

    private static void FormatWordList(List<TimedWord> words, string existingText)
    {
        var capitalizeNext = existingText.Length == 0 || EndsWithSentencePunctuation(existingText);
        for (var i = 0; i < words.Count; i++)
        {
            var text = words[i].Text;
            text = ReplaceStandaloneI(text);
            if (capitalizeNext)
            {
                text = CapitalizeFirstLetter(text);
            }

            words[i] = words[i] with { Text = text };
            capitalizeNext = EndsWithSentencePunctuation(text);
        }
    }

    private static string ReplaceStandaloneI(string text)
    {
        return Regex.Replace(text, @"\bi\b", "I");
    }

    private static string CapitalizeFirstLetter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetter(text[i]))
            {
                var chars = text.ToCharArray();
                chars[i] = char.ToUpperInvariant(chars[i]);
                return new string(chars);
            }
        }

        return text;
    }

    public static string FormatAsSentences(string text, string existing)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return text;
        }

        text = Regex.Replace(text, @"\bi\b", "I");
        var capitalizeFirst = existing.Length == 0 || EndsWithSentencePunctuation(existing);
        return CapitalizeSentenceStarts(text, capitalizeFirst);
    }

    internal static (string Existing, string Incoming) AlignOverlap(string existing, string incoming)
    {
        if (incoming.Length == 0 || existing.Length == 0)
        {
            return (existing, incoming);
        }

        var lastWord = GetLastWord(existing, out var lastStart);
        var firstWord = GetFirstWord(incoming, out var firstEnd);
        if (lastWord.Length == 0 || firstWord.Length == 0)
        {
            return (existing, incoming);
        }

        var lastCore = CoreWord(lastWord);
        var firstCore = CoreWord(firstWord);
        if (lastCore.Length == 0 || firstCore.Length == 0)
        {
            return (existing, incoming);
        }

        // Whole word duplicated at the join: "the cat sat" + "sat on the mat".
        if (lastCore.Equals(firstCore, StringComparison.OrdinalIgnoreCase))
        {
            return (existing, incoming[firstEnd..].TrimStart());
        }

        // Trailing letters of the last word repeated as the next block's first token:
        // "store" + "re and bought". Skip common short words so "cat" + "at home" is kept.
        if (firstCore.Length <= 3
            && firstCore.Length < lastCore.Length
            && lastCore.EndsWith(firstCore, StringComparison.OrdinalIgnoreCase)
            && !ProtectedShortWords.Contains(firstCore))
        {
            return (existing, incoming[firstEnd..].TrimStart());
        }

        // Previous word was cut off and completed in the next block: "tes" + "test of".
        if (lastCore.Length >= 2
            && lastCore.Length < firstCore.Length
            && firstCore.StartsWith(lastCore, StringComparison.OrdinalIgnoreCase)
            && !ProtectedShortWords.Contains(lastCore))
        {
            return (existing[..lastStart] + firstWord, incoming[firstEnd..].TrimStart());
        }

        return (existing, incoming);
    }

    private static string CapitalizeSentenceStarts(string text, bool capitalizeFirst)
    {
        var chars = text.ToCharArray();
        var capitalizeNext = capitalizeFirst;
        for (var i = 0; i < chars.Length; i++)
        {
            if (capitalizeNext && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                capitalizeNext = false;
            }
            else if (chars[i] is '.' or '!' or '?')
            {
                capitalizeNext = true;
            }
        }

        return new string(chars);
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

    private static string GetLastWord(string text, out int startIndex)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end == 0)
        {
            startIndex = 0;
            return string.Empty;
        }

        var start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        startIndex = start;
        return text[start..end];
    }

    private static string GetFirstWord(string text, out int endIndex)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start >= text.Length)
        {
            endIndex = text.Length;
            return string.Empty;
        }

        var end = start + 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        endIndex = end;
        return text[start..end];
    }

    internal static string CoreWord(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && IsWordPunctuation(word[start]))
        {
            start++;
        }

        while (end > start && IsWordPunctuation(word[end - 1]))
        {
            end--;
        }

        return word[start..end];
    }

    internal static string ReplaceCoreWord(string token, string original, string replacement)
    {
        var start = 0;
        var end = token.Length;
        while (start < end && IsWordPunctuation(token[start]))
        {
            start++;
        }

        while (end > start && IsWordPunctuation(token[end - 1]))
        {
            end--;
        }

        if (!token[start..end].Equals(original, StringComparison.Ordinal))
        {
            return token;
        }

        return token[..start] + replacement + token[end..];
    }

    private static bool IsWordPunctuation(char c) =>
        c is '.' or ',' or '!' or '?' or ';' or ':' or '"' or '\'' or '(' or ')';
}
