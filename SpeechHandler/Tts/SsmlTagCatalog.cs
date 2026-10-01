namespace SpeechHandler.Tts;

/// <summary>
/// An SSML tag the app can honor, as offered in the SSML tab's tag list.
/// A tag with no closing part is inserted at the caret; one with a closing part wraps the selection.
/// </summary>
internal sealed record SsmlTag(string Label, string Description, string Open, string Close)
{
    public string Syntax => Close.Length == 0 ? Open : Open + "..." + Close;
}

internal static class SsmlTagCatalog
{
    public const string UnsupportedNote =
        "Not supported: pitch, say-as, phoneme, voice, audio. Their text is read normally.";

    public static IReadOnlyList<SsmlTag> Tags { get; } =
    [
        new("Short pause",
            "Silence for a set time, in ms or s (up to 60 s).",
            "<break time=\"500ms\"/>", string.Empty),
        new("Long pause",
            "Same tag, longer time.",
            "<break time=\"1.5s\"/>", string.Empty),
        new("Pause by strength",
            "x-weak 0.1 s, weak 0.25 s, medium 0.5 s, strong 0.75 s, x-strong 1.25 s.",
            "<break strength=\"strong\"/>", string.Empty),
        new("Emphasis",
            "Slower and louder, with a short pause either side. level: strong, moderate, reduced, none.",
            "<emphasis>", "</emphasis>"),
        new("Strong emphasis",
            "More of the same.",
            "<emphasis level=\"strong\">", "</emphasis>"),
        new("Slower",
            "rate: x-slow, slow, medium, fast, x-fast, 80%, +20%, or 1.2.",
            "<prosody rate=\"slow\">", "</prosody>"),
        new("Faster",
            "Same attribute, faster value.",
            "<prosody rate=\"fast\">", "</prosody>"),
        new("Louder",
            "volume: silent, x-soft, soft, medium, loud, x-loud, +6dB, or +50%.",
            "<prosody volume=\"loud\">", "</prosody>"),
        new("Softer",
            "Same attribute, softer value.",
            "<prosody volume=\"soft\">", "</prosody>"),
        new("Read as",
            "Speaks the alias in place of the text, for example WWW.",
            "<sub alias=\"spoken form\">", "</sub>"),
        new("Paragraph",
            "Adds a 0.6 s pause after the paragraph.",
            "<p>", "</p>"),
        new("Sentence",
            "Adds a 0.25 s pause after the sentence.",
            "<s>", "</s>")
    ];
}
