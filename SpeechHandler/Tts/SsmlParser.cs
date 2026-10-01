using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SpeechHandler.Tts;

/// <summary>
/// One piece of an SSML document: text spoken at a rate and volume, or a pause.
/// </summary>
internal sealed record SpeechPart(string Text, float Rate, float Volume, double PauseSeconds)
{
    public bool IsPause => Text.Length == 0;

    public static SpeechPart Speech(string text, float rate, float volume) => new(text, rate, volume, 0);

    public static SpeechPart Pause(double seconds) => new(string.Empty, 1f, 1f, seconds);
}

internal sealed record SsmlDocument(IReadOnlyList<SpeechPart> Parts, IReadOnlyList<string> Warnings)
{
    public int SpokenPartCount => Parts.Count(part => !part.IsPause);

    public double PauseSeconds => Parts.Where(part => part.IsPause).Sum(part => part.PauseSeconds);
}

internal sealed class SsmlException : Exception
{
    public SsmlException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads the subset of SSML the local engines can honor: break, prosody (rate and volume),
/// emphasis, sub, p and s. Other elements are read as plain text and reported as warnings.
/// </summary>
internal static partial class SsmlParser
{
    private const double ParagraphPauseSeconds = 0.6;
    private const double SentencePauseSeconds = 0.25;
    private const double MaxBreakSeconds = 60;
    private const string TrailingPunctuation = ".,!?;:)]";

    public static SsmlDocument Parse(string ssml)
    {
        var trimmed = ssml.Trim();
        if (trimmed.Length == 0)
        {
            throw new SsmlException("There is no SSML to speak.");
        }

        // Fragments without a <speak> root are allowed, so "Hello <break/> world" works.
        const string openSpeak = "<speak>";
        var wrapped = !trimmed.StartsWith("<speak", StringComparison.OrdinalIgnoreCase)
                      && !trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        var source = wrapped ? openSpeak + trimmed + "</speak>" : trimmed;

        XDocument document;
        try
        {
            document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            var position = wrapped && ex.LineNumber == 1
                ? Math.Max(1, ex.LinePosition - openSpeak.Length)
                : ex.LinePosition;
            throw new SsmlException(
                $"The SSML is not valid XML (line {ex.LineNumber}, position {position}): {FirstSentence(ex.Message)} " +
                "Write a literal & as &amp; and a literal < as &lt;.");
        }

        var root = document.Root!;
        if (!string.Equals(root.Name.LocalName, "speak", StringComparison.Ordinal))
        {
            throw new SsmlException($"The root element must be <speak>, not <{root.Name.LocalName}>.");
        }

        var builder = new Builder();
        builder.Walk(root, new Style(1f, 1f));
        builder.Flush();
        var parts = builder.Result();
        if (!parts.Any(part => !part.IsPause))
        {
            throw new SsmlException("The SSML has no text to speak.");
        }

        return new SsmlDocument(parts, builder.Warnings);
    }

    /// <summary>
    /// Wraps plain text in a speak element, escaping the characters XML reserves.
    /// </summary>
    public static string FromPlainText(string text)
    {
        var escaped = text.Trim()
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
        return escaped.Length == 0 ? string.Empty : "<speak>\n" + escaped + "\n</speak>";
    }

    private readonly record struct Style(float Rate, float Volume);

    private sealed class Builder
    {
        private readonly List<SpeechPart> _parts = [];
        private readonly StringBuilder _text = new();
        private readonly SortedSet<string> _warnings = new(StringComparer.Ordinal);
        private Style _textStyle = new(1f, 1f);

        public IReadOnlyList<string> Warnings => _warnings.ToList();

        public IReadOnlyList<SpeechPart> Result() => _parts;

        public void Walk(XElement element, Style style)
        {
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        AddText(text.Value, style);
                        break;
                    case XElement child:
                        WalkElement(child, style);
                        break;
                }
            }
        }

        public void Flush()
        {
            var text = WhitespaceRegex().Replace(_text.ToString(), " ").Trim();
            _text.Clear();
            if (text.Length == 0)
            {
                return;
            }

            // A "!" or "." just after a closing tag belongs to the words before the tag.
            var lastSpoken = _parts.FindLastIndex(part => !part.IsPause);
            var leading = 0;
            while (leading < text.Length && TrailingPunctuation.Contains(text[leading]))
            {
                leading++;
            }

            if (leading > 0 && lastSpoken >= 0)
            {
                _parts[lastSpoken] = _parts[lastSpoken] with { Text = _parts[lastSpoken].Text + text[..leading] };
                text = text[leading..].TrimStart();
            }

            if (!text.Any(char.IsLetterOrDigit))
            {
                return;
            }

            if (_parts.Count > 0 && !_parts[^1].IsPause
                && _parts[^1].Rate == _textStyle.Rate && _parts[^1].Volume == _textStyle.Volume)
            {
                _parts[^1] = _parts[^1] with { Text = _parts[^1].Text + " " + text };
                return;
            }

            _parts.Add(SpeechPart.Speech(text, _textStyle.Rate, _textStyle.Volume));
        }

        private void AddText(string text, Style style)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (style != _textStyle && _text.Length > 0 && !string.IsNullOrWhiteSpace(_text.ToString()))
            {
                Flush();
            }

            if (_text.Length == 0 || string.IsNullOrWhiteSpace(_text.ToString()))
            {
                _textStyle = style;
            }

            _text.Append(text);
        }

        private void AddPause(double seconds)
        {
            Flush();
            if (seconds <= 0)
            {
                return;
            }

            if (_parts.Count > 0 && _parts[^1].IsPause)
            {
                _parts[^1] = SpeechPart.Pause(_parts[^1].PauseSeconds + seconds);
                return;
            }

            _parts.Add(SpeechPart.Pause(seconds));
        }

        private void WalkElement(XElement element, Style style)
        {
            switch (element.Name.LocalName)
            {
                case "break":
                    AddPause(BreakSeconds(element));
                    break;

                case "prosody":
                    if (element.Attribute("pitch") is not null || element.Attribute("contour") is not null
                        || element.Attribute("range") is not null || element.Attribute("duration") is not null)
                    {
                        _warnings.Add("<prosody> pitch, contour, range and duration are not supported and are ignored.");
                    }

                    Walk(element, new Style(
                        style.Rate * RateFactor(element.Attribute("rate")?.Value),
                        style.Volume * VolumeFactor(element.Attribute("volume")?.Value)));
                    break;

                case "emphasis":
                    WalkEmphasis(element, style);
                    break;

                case "sub":
                    var alias = element.Attribute("alias")?.Value
                                ?? throw new SsmlException("<sub> needs an alias attribute, for example <sub alias=\"World Wide Web\">WWW</sub>.");
                    AddText(" " + alias + " ", style);
                    break;

                case "p":
                case "paragraph":
                    Walk(element, style);
                    AddPause(ParagraphPauseSeconds);
                    break;

                case "s":
                case "sentence":
                    Walk(element, style);
                    AddPause(SentencePauseSeconds);
                    break;

                case "speak":
                    Walk(element, style);
                    break;

                case "mark":
                case "desc":
                case "lexicon":
                case "meta":
                case "metadata":
                    _warnings.Add($"<{element.Name.LocalName}> is not supported and is skipped.");
                    break;

                default:
                    _warnings.Add($"<{element.Name.LocalName}> is not supported. Its text is read normally.");
                    Walk(element, style);
                    break;
            }
        }

        private void WalkEmphasis(XElement element, Style style)
        {
            var level = element.Attribute("level")?.Value.Trim().ToLowerInvariant() ?? "moderate";
            var (rate, volume, pause) = level switch
            {
                "strong" => (0.8f, 1.5f, 0.25),
                "moderate" => (0.9f, 1.25f, 0.15),
                "reduced" => (1.1f, 0.8f, 0.0),
                "none" => (1f, 1f, 0.0),
                _ => throw new SsmlException(
                    $"<emphasis level=\"{level}\"> is not recognized. Use strong, moderate, reduced, or none.")
            };

            AddPause(pause);
            Walk(element, new Style(style.Rate * rate, style.Volume * volume));
            AddPause(pause);
        }
    }

    private static double BreakSeconds(XElement element)
    {
        var time = element.Attribute("time")?.Value;
        if (time is not null)
        {
            var match = TimeRegex().Match(time);
            if (!match.Success)
            {
                throw new SsmlException($"<break time=\"{time}\"> is not recognized. Use a value such as 500ms or 1.5s.");
            }

            var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            var seconds = match.Groups["unit"].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? value / 1000 : value;
            if (seconds > MaxBreakSeconds)
            {
                throw new SsmlException($"<break time=\"{time}\"> is too long. The longest pause is {MaxBreakSeconds:0} seconds.");
            }

            return seconds;
        }

        var strength = element.Attribute("strength")?.Value.Trim().ToLowerInvariant() ?? "medium";
        return strength switch
        {
            "none" => 0,
            "x-weak" => 0.1,
            "weak" => 0.25,
            "medium" => 0.5,
            "strong" => 0.75,
            "x-strong" => 1.25,
            _ => throw new SsmlException(
                $"<break strength=\"{strength}\"> is not recognized. Use none, x-weak, weak, medium, strong, or x-strong.")
        };
    }

    private static float RateFactor(string? value)
    {
        if (value is null)
        {
            return 1f;
        }

        var normalized = value.Trim().ToLowerInvariant();
        var named = normalized switch
        {
            "x-slow" => 0.5f,
            "slow" => 0.75f,
            "medium" or "default" => 1f,
            "fast" => 1.25f,
            "x-fast" => 1.5f,
            _ => (float?)null
        };
        if (named is not null)
        {
            return named.Value;
        }

        var factor = ParseRelative(normalized)
                     ?? throw new SsmlException(
                         $"<prosody rate=\"{value}\"> is not recognized. Use x-slow, slow, medium, fast, x-fast, a percentage such as 80% or +20%, or a multiplier such as 1.2.");
        if (factor < 0.25f || factor > 4f)
        {
            throw new SsmlException($"<prosody rate=\"{value}\"> is out of range. Use a rate between 25% and 400%.");
        }

        return factor;
    }

    private static float VolumeFactor(string? value)
    {
        if (value is null)
        {
            return 1f;
        }

        var normalized = value.Trim().ToLowerInvariant();
        var named = normalized switch
        {
            "silent" => 0f,
            "x-soft" => 0.35f,
            "soft" => 0.6f,
            "medium" or "default" => 1f,
            "loud" => 1.4f,
            "x-loud" => 2f,
            _ => (float?)null
        };
        if (named is not null)
        {
            return named.Value;
        }

        var decibels = DecibelRegex().Match(normalized);
        if (decibels.Success)
        {
            var db = double.Parse(decibels.Groups["value"].Value, CultureInfo.InvariantCulture);
            return (float)Math.Pow(10, db / 20);
        }

        var factor = ParseRelative(normalized)
                     ?? throw new SsmlException(
                         $"<prosody volume=\"{value}\"> is not recognized. Use silent, x-soft, soft, medium, loud, x-loud, decibels such as +6dB, or a percentage such as +50%.");
        if (factor < 0f || factor > 4f)
        {
            throw new SsmlException($"<prosody volume=\"{value}\"> is out of range.");
        }

        return factor;
    }

    /// <summary>
    /// "80%" is 0.8 of normal, "+20%" and "-20%" change it by that much, and "1.2" is a multiplier.
    /// </summary>
    private static float? ParseRelative(string value)
    {
        var match = RelativeRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        var number = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        var sign = match.Groups["sign"].Value;
        if (match.Groups["percent"].Success)
        {
            return sign switch
            {
                "+" => (float)(1 + number / 100),
                "-" => (float)(1 - number / 100),
                _ => (float)(number / 100)
            };
        }

        return sign == "-" ? null : (float)number;
    }

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? message : message[..(end + 1)];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*(?<value>\d+(\.\d+)?)\s*(?<unit>ms|s)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^(?<value>[+-]?\d+(\.\d+)?)\s*db$")]
    private static partial Regex DecibelRegex();

    [GeneratedRegex(@"^(?<sign>[+-]?)(?<value>\d+(\.\d+)?)(?<percent>%)?$")]
    private static partial Regex RelativeRegex();
}
