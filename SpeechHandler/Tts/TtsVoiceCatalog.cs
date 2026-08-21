namespace SpeechHandler.Tts;

internal static class TtsVoiceCatalog
{
    public const string KokoroEngine = "Kokoro";
    public const string PiperEngine = "Piper";
    public const string KokoroPackId = "kokoro-v1";
    public const string DefaultVoiceId = "kokoro-af_bella";

    public static IReadOnlyList<string> Engines { get; } = [KokoroEngine, PiperEngine];

    public static IReadOnlyList<TtsPackOption> Packs { get; } =
    [
        new(KokoroPackId, KokoroEngine, "Kokoro English — more natural voices", "~340 MB", true, true),
        new("lessac-high", PiperEngine, "Lessac high (US)", "~113 MB", true, false),
        new("hfc-female", PiperEngine, "HFC female (US)", "~60 MB", false, false),
        new("hfc-male", PiperEngine, "HFC male (US)", "~60 MB", false, false),
        new("lessac", PiperEngine, "Lessac medium (US)", "~63 MB", false, false),
        new("amy", PiperEngine, "Amy medium (US)", "~63 MB", false, false),
        new("ryan", PiperEngine, "Ryan medium (US)", "~63 MB", false, false)
    ];

    public static IReadOnlyList<TtsVoiceOption> Voices { get; } = BuildVoices();

    public static IReadOnlyList<TtsVoiceOption> VoicesForEngine(string engine) =>
        Voices.Where(voice => string.Equals(voice.EngineName, engine, StringComparison.Ordinal)).ToArray();

    public static TtsVoiceOption? FindVoice(string? id) =>
        Voices.FirstOrDefault(voice => string.Equals(voice.Id, id, StringComparison.OrdinalIgnoreCase));

    public static TtsPackOption? FindPack(string? id) =>
        Packs.FirstOrDefault(pack => string.Equals(pack.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<TtsPackOption> RecommendedPacks =>
        Packs.Where(pack => pack.IsRecommended).ToArray();

    public static IReadOnlyList<TtsSpeedOption> Speeds { get; } =
    [
        new(0.75, "Slow"),
        new(0.9, "Slightly slow"),
        new(1.0, "Normal"),
        new(1.1, "Slightly fast"),
        new(1.25, "Fast")
    ];

    public static TtsSpeedOption ClosestSpeed(double speed) =>
        Speeds.OrderBy(option => Math.Abs(option.Value - speed)).First();

    private static IReadOnlyList<TtsVoiceOption> BuildVoices()
    {
        var kokoro = KokoroTtsRuntime.Shared;
        return
        [
            Kokoro("kokoro-af_bella", "Bella · Kokoro (US female, recommended)", 2, kokoro),
            Kokoro("kokoro-af_heart", "Heart · Kokoro (US female)", 3, kokoro),
            Kokoro("kokoro-af_sarah", "Sarah · Kokoro (US female)", 9, kokoro),
            Kokoro("kokoro-af_nicole", "Nicole · Kokoro (US female)", 6, kokoro),
            Kokoro("kokoro-af_nova", "Nova · Kokoro (US female)", 7, kokoro),
            Kokoro("kokoro-af_sky", "Sky · Kokoro (US female)", 10, kokoro),
            Kokoro("kokoro-af_alloy", "Alloy · Kokoro (US female)", 0, kokoro),
            Kokoro("kokoro-af_aoede", "Aoede · Kokoro (US female)", 1, kokoro),
            Kokoro("kokoro-af_jessica", "Jessica · Kokoro (US female)", 4, kokoro),
            Kokoro("kokoro-af_kore", "Kore · Kokoro (US female)", 5, kokoro),
            Kokoro("kokoro-af_river", "River · Kokoro (US female)", 8, kokoro),
            Kokoro("kokoro-am_michael", "Michael · Kokoro (US male)", 16, kokoro),
            Kokoro("kokoro-am_adam", "Adam · Kokoro (US male)", 11, kokoro),
            Kokoro("kokoro-am_echo", "Echo · Kokoro (US male)", 12, kokoro),
            Kokoro("kokoro-am_eric", "Eric · Kokoro (US male)", 13, kokoro),
            Kokoro("kokoro-am_fenrir", "Fenrir · Kokoro (US male)", 14, kokoro),
            Kokoro("kokoro-am_liam", "Liam · Kokoro (US male)", 15, kokoro),
            Kokoro("kokoro-am_onyx", "Onyx · Kokoro (US male)", 17, kokoro),
            Kokoro("kokoro-am_puck", "Puck · Kokoro (US male)", 18, kokoro),
            Kokoro("kokoro-bf_emma", "Emma · Kokoro (British female)", 21, kokoro),
            Kokoro("kokoro-bf_alice", "Alice · Kokoro (British female)", 20, kokoro),
            Kokoro("kokoro-bf_isabella", "Isabella · Kokoro (British female)", 22, kokoro),
            Kokoro("kokoro-bf_lily", "Lily · Kokoro (British female)", 23, kokoro),
            Kokoro("kokoro-bm_george", "George · Kokoro (British male)", 26, kokoro),
            Kokoro("kokoro-bm_lewis", "Lewis · Kokoro (British male)", 27, kokoro),
            Kokoro("kokoro-bm_daniel", "Daniel · Kokoro (British male)", 24, kokoro),
            Kokoro("kokoro-bm_fable", "Fable · Kokoro (British male)", 25, kokoro),
            Piper("lessac-high", "Lessac high · Piper (US)", "en_US-lessac-high", "en/en_US/lessac/high/en_US-lessac-high"),
            Piper("hfc-female", "HFC female · Piper (US)", "en_US-hfc_female-medium", "en/en_US/hfc_female/medium/en_US-hfc_female-medium"),
            Piper("hfc-male", "HFC male · Piper (US)", "en_US-hfc_male-medium", "en/en_US/hfc_male/medium/en_US-hfc_male-medium"),
            Piper("lessac", "Lessac medium · Piper (US)", "en_US-lessac-medium", "en/en_US/lessac/medium/en_US-lessac-medium"),
            Piper("amy", "Amy · Piper (US)", "en_US-amy-medium", "en/en_US/amy/medium/en_US-amy-medium"),
            Piper("ryan", "Ryan · Piper (US)", "en_US-ryan-medium", "en/en_US/ryan/medium/en_US-ryan-medium")
        ];
    }

    private static TtsVoiceOption Kokoro(string id, string displayName, int speakerId, KokoroTtsRuntime runtime) =>
        new(id, displayName, KokoroEngine, KokoroPackId, new KokoroTtsEngine(runtime, speakerId));

    private static TtsVoiceOption Piper(string id, string displayName, string fileStem, string repoPath) =>
        new(id, displayName, PiperEngine, id, new PiperTtsEngine(fileStem, repoPath));
}

internal sealed record TtsSpeedOption(double Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
