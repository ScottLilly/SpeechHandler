using SpeechHandler.Transcription;

namespace SpeechHandler;

internal static class SpellingSync
{
    public static int CountOccurrencesBefore(string text, string word, int index, bool skipSrtMetadata)
    {
        var count = 0;
        foreach (var start in FindOccurrences(text, word, skipSrtMetadata))
        {
            if (start >= index)
            {
                break;
            }

            count++;
        }

        return count;
    }

    public static string? ReplaceOccurrence(
        string text,
        string word,
        string replacement,
        int occurrence,
        bool skipSrtMetadata)
    {
        var starts = FindOccurrences(text, word, skipSrtMetadata).ToList();
        var index = ResolveOccurrence(starts, occurrence);
        if (index is null)
        {
            return null;
        }

        var start = index.Value;
        return text[..start] + replacement + text[(start + word.Length)..];
    }

    public static bool ReplaceTimedWord(
        List<TimedWord> words,
        string original,
        string replacement,
        int occurrence)
    {
        var matches = new List<int>();
        for (var i = 0; i < words.Count; i++)
        {
            if (TranscriptText.CoreWord(words[i].Text).Equals(original, StringComparison.Ordinal))
            {
                matches.Add(i);
            }
        }

        var index = ResolveOccurrence(matches, occurrence);
        if (index is null)
        {
            return false;
        }

        var wordIndex = index.Value;
        words[wordIndex] = words[wordIndex] with
        {
            Text = TranscriptText.ReplaceCoreWord(words[wordIndex].Text, original, replacement)
        };
        return true;
    }

    private static int? ResolveOccurrence(IReadOnlyList<int> matches, int occurrence)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (occurrence >= 0 && occurrence < matches.Count)
        {
            return matches[occurrence];
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<int> FindOccurrences(string text, string word, bool skipSrtMetadata)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
        {
            yield break;
        }

        var lineStart = 0;
        var skipLine = false;
        for (var i = 0; i <= text.Length - word.Length; i++)
        {
            if (i == lineStart)
            {
                skipLine = skipSrtMetadata && IsSrtMetadataLine(text, lineStart);
            }

            if (text[i] == '\n')
            {
                lineStart = i + 1;
                continue;
            }

            if (skipLine || !MatchesAt(text, i, word) || !IsWholeWord(text, i, word.Length))
            {
                continue;
            }

            yield return i;
            i += word.Length - 1;
        }
    }

    private static bool MatchesAt(string text, int start, string word)
    {
        return string.Compare(text, start, word, 0, word.Length, StringComparison.Ordinal) == 0;
    }

    private static bool IsWholeWord(string text, int start, int length)
    {
        if (start > 0 && IsWordChar(text[start - 1]))
        {
            return false;
        }

        var end = start + length;
        return end >= text.Length || !IsWordChar(text[end]);
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c is '\'' or '\u2019' or '-';

    private static bool IsSrtMetadataLine(string text, int lineStart)
    {
        var end = text.IndexOf('\n', lineStart);
        if (end < 0)
        {
            end = text.Length;
        }

        var line = text.AsSpan(lineStart, end - lineStart).TrimEnd('\r').Trim();
        if (line.IsEmpty)
        {
            return true;
        }

        if (IsAllDigits(line))
        {
            return true;
        }

        return line.Contains("-->", StringComparison.Ordinal) && ContainsTimestamp(line);
    }

    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    private static bool ContainsTimestamp(ReadOnlySpan<char> line)
    {
        var colon = 0;
        var comma = 0;
        foreach (var c in line)
        {
            if (c == ':')
            {
                colon++;
            }
            else if (c == ',')
            {
                comma++;
            }
        }

        return colon >= 4 && comma >= 2;
    }
}
