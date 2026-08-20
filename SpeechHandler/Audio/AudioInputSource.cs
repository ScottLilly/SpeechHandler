using NAudio.CoreAudioApi;

namespace SpeechHandler.Audio;

internal enum AudioSourceKind
{
    Microphone,
    SystemAudio
}

internal sealed record AudioInputSource(string Id, string DisplayName, AudioSourceKind Kind)
{
    public override string ToString() => DisplayName;
}

internal static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioInputSource> ListSources()
    {
        var sources = new List<AudioInputSource>();
        using var enumerator = new MMDeviceEnumerator();

        AddEndpoints(sources, enumerator, DataFlow.Capture, AudioSourceKind.Microphone, null);
        AddEndpoints(sources, enumerator, DataFlow.Render, AudioSourceKind.SystemAudio, "System audio: ");

        return sources;
    }

    private static void AddEndpoints(
        List<AudioInputSource> sources,
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        AudioSourceKind kind,
        string? namePrefix)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var name = string.IsNullOrWhiteSpace(device.FriendlyName)
                    ? device.ID
                    : device.FriendlyName;
                sources.Add(new AudioInputSource(device.ID, $"{namePrefix}{name}", kind));
            }
        }
    }
}
