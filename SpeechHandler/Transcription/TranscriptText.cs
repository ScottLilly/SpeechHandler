using System.Text.RegularExpressions;

namespace SpeechHandler.Transcription;

internal readonly record struct PreparedTranscript(string Existing, string Incoming);

internal static class TranscriptText
{
    private static readonly HashSet<string> ProtectedShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "am", "an", "as", "at", "be", "by", "do", "go", "he", "i", "if", "in", "is", "it",
        "me", "my", "no", "of", "oh", "on", "or", "so", "to", "up", "us", "we"
    };

    public static PreparedTranscript Prepare(string existing, string incoming, bool formatAsSentences)
    {
        incoming = incoming.Trim();
        existing = existing.TrimEnd();
        var (alignedExisting, alignedIncoming) = AlignOverlap(existing, incoming);
        if (formatAsSentences && alignedIncoming.Length > 0)
        {
            alignedIncoming = FormatAsSentences(alignedIncoming);
        }

        return new PreparedTranscript(alignedExisting, alignedIncoming);
    }

    public static string FormatAsSentences(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return text;
        }

        text = Regex.Replace(text, @"\bi\b", "I");
        text = CapitalizeSentenceStarts(text);
        if (!EndsWithSentencePunctuation(text))
        {
            text += ".";
        }

        return text;
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

    private static string CapitalizeSentenceStarts(string text)
    {
        var chars = text.ToCharArray();
        var capitalizeNext = true;
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

    private static string CoreWord(string word)
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

    private static bool IsWordPunctuation(char c) =>
        c is '.' or ',' or '!' or '?' or ';' or ':' or '"' or '\'' or '(' or ')';
}
