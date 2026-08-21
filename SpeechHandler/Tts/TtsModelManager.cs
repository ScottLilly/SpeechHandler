namespace SpeechHandler.Tts;

internal static class TtsModelManager
{
    public static IReadOnlyList<TtsPackOption> ListPacks() =>
        TtsVoiceCatalog.Packs
            .Select(pack => pack with { IsDownloaded = IsPackInstalled(pack.Id) })
            .ToArray();

    public static bool IsPackInstalled(string packId)
    {
        if (string.Equals(packId, TtsVoiceCatalog.KokoroPackId, StringComparison.OrdinalIgnoreCase))
        {
            return KokoroTtsRuntime.IsInstalled();
        }

        var voice = TtsVoiceCatalog.FindVoice(packId);
        return voice?.Engine is PiperTtsEngine piper && piper.IsInstalled();
    }

    public static async Task DownloadAsync(
        TtsPackOption pack,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (string.Equals(pack.Id, TtsVoiceCatalog.KokoroPackId, StringComparison.OrdinalIgnoreCase))
        {
            await KokoroTtsRuntime.Shared.DownloadAsync(progress, status, cancellationToken).ConfigureAwait(false);
            return;
        }

        var voice = TtsVoiceCatalog.FindVoice(pack.Id)
                    ?? throw new InvalidOperationException($"Unknown voice pack '{pack.Id}'.");
        if (voice.Engine is not PiperTtsEngine piper)
        {
            throw new InvalidOperationException($"Voice pack '{pack.Id}' is not a Piper voice.");
        }

        status?.Report($"Downloading {pack.DisplayName}…");
        await piper.DownloadAsync(progress, status, cancellationToken).ConfigureAwait(false);
    }

    public static async Task DownloadRecommendedAsync(
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var packs = ListPacks().Where(pack => pack.IsRecommended && !pack.IsDownloaded).ToArray();
        if (packs.Length == 0)
        {
            status?.Report("Recommended voices are already downloaded.");
            progress?.Report(100);
            return;
        }

        for (var index = 0; index < packs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pack = packs[index];
            var slice = new Progress<double>(value =>
            {
                var overall = ((index * 100.0) + value) / packs.Length;
                progress?.Report(overall);
            });
            await DownloadAsync(pack, slice, status, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(100);
    }
}
