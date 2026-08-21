namespace SpeechHandler.Tts;

/// <summary>
/// A playable voice. Additional engines can be added later
/// as options that implement <see cref="ITextToSpeechEngine"/>.
/// </summary>
internal sealed record TtsVoiceOption(
    string Id,
    string DisplayName,
    string EngineName,
    string PackId,
    ITextToSpeechEngine Engine)
{
    public override string ToString() => DisplayName;
}

internal sealed record TtsPackOption(
    string Id,
    string EngineName,
    string DisplayName,
    string SizeLabel,
    bool IsRecommended,
    bool ConfirmLargeDownload,
    bool IsDownloaded = false)
{
    public override string ToString() =>
        IsDownloaded ? $"{DisplayName}  ·  downloaded" : DisplayName;
}

internal interface ITextToSpeechEngine
{
    Task EnsureReadyAsync(IProgress<string>? status, CancellationToken cancellationToken);

    Task SynthesizeWavFileAsync(string text, string wavPath, float speed, CancellationToken cancellationToken);
}
