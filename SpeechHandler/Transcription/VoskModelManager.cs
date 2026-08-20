using System.IO.Compression;
using System.Text.Json;

namespace SpeechHandler.Transcription;

internal static class AppStorage
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpeechHandler");

    public static string ModelsDirectory { get; } = Path.Combine(Root, "models");

    public static string PiperDirectory { get; } = Path.Combine(Root, "piper");

    public static string TtsVoicesDirectory { get; } = Path.Combine(Root, "tts");

    public static string SettingsPath { get; } = Path.Combine(Root, "settings.json");
}

internal sealed class AppSettings
{
    public string Engine { get; set; } = "Local";
    public string? ModelsDirectory { get; set; }
    public string? ModelPath { get; set; }
    public string WhisperModel { get; set; } = "whisper-1";
    public string ElevenLabsModel { get; set; } = "scribe_v2";
    public bool TranslateToEnglish { get; set; }
    public string TtsVoiceId { get; set; } = "lessac";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppStorage.SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppStorage.SettingsPath))
                       ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // Keep defaults if the file is missing or corrupt.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppStorage.Root);
        File.WriteAllText(
            AppStorage.SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record VoskModelOption(
    string Id,
    string Language,
    string DisplayName,
    string FolderName,
    string Url,
    bool ConfirmLargeDownload)
{
    public bool IsDownloaded { get; init; }

    public override string ToString() =>
        IsDownloaded ? $"{DisplayName}  ·  downloaded" : DisplayName;
}

internal sealed record InstalledVoskModel(string Id, string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}

internal sealed record TranscriptionOption(string Engine, string DisplayName, string? ModelPath = null)
{
    public override string ToString() => DisplayName;
}

internal static class VoskModelManager
{
    public const string DefaultLanguage = "English (US)";

    public static IReadOnlyList<VoskModelOption> Catalog { get; } =
    [
        Entry("English (US)", "en-us-small", "Small — 40 MB (fast, less accurate)", "vosk-model-small-en-us-0.15"),
        Entry("English (US)", "en-us-medium", "Medium — 128 MB (better accuracy)", "vosk-model-en-us-0.22-lgraph"),
        Entry("English (US)", "en-us-large", "Large — 1.8 GB (accurate)", "vosk-model-en-us-0.22", true),
        Entry("English (US)", "en-us-xlarge", "Extra large — 2.3 GB (highest accuracy)", "vosk-model-en-us-0.42-gigaspeech", true),
        Entry("English (India)", "en-in-small", "Small — 36 MB", "vosk-model-small-en-in-0.4"),
        Entry("English (India)", "en-in-large", "Large — 1 GB", "vosk-model-en-in-0.5", true),
        Entry("Arabic", "ar-medium", "Medium — 318 MB", "vosk-model-ar-mgb2-0.4"),
        Entry("Arabic (Tunisian)", "ar-tn-small", "Small — 158 MB", "vosk-model-small-ar-tn-0.1-linto"),
        Entry("Arabic (Tunisian)", "ar-tn-large", "Large — 517 MB", "vosk-model-ar-tn-0.1-linto", true),
        Entry("Breton", "br-small", "Small — 70 MB", "vosk-model-br-0.8"),
        Entry("Catalan", "ca-small", "Small — 42 MB", "vosk-model-small-ca-0.4"),
        Entry("Chinese", "zh-small", "Small — 42 MB", "vosk-model-small-cn-0.22"),
        Entry("Chinese", "zh-large", "Large — 1.3 GB", "vosk-model-cn-0.22", true),
        Entry("Czech", "cs-small", "Small — 44 MB", "vosk-model-small-cs-0.4-rhasspy"),
        Entry("Dutch", "nl-small", "Small — 39 MB", "vosk-model-small-nl-0.22"),
        Entry("Esperanto", "eo-small", "Small — 42 MB", "vosk-model-small-eo-0.42"),
        Entry("French", "fr-small", "Small — 41 MB", "vosk-model-small-fr-0.22"),
        Entry("French", "fr-large", "Large — 1.4 GB", "vosk-model-fr-0.22", true),
        Entry("Georgian", "ka-small", "Small — 45 MB", "vosk-model-small-ka-0.42"),
        Entry("Georgian", "ka-large", "Large — 700 MB", "vosk-model-ka-0.42", true),
        Entry("German", "de-small", "Small — 45 MB", "vosk-model-small-de-0.15"),
        Entry("German", "de-large", "Large — 1.9 GB", "vosk-model-de-0.21", true),
        Entry("Greek", "el-large", "Large — 1.1 GB", "vosk-model-el-gr-0.7", true),
        Entry("Gujarati", "gu-small", "Small — 100 MB", "vosk-model-small-gu-0.42"),
        Entry("Gujarati", "gu-large", "Large — 700 MB", "vosk-model-gu-0.42", true),
        Entry("Hindi", "hi-small", "Small — 42 MB", "vosk-model-small-hi-0.22"),
        Entry("Hindi", "hi-large", "Large — 1.5 GB", "vosk-model-hi-0.22", true),
        Entry("Italian", "it-small", "Small — 48 MB", "vosk-model-small-it-0.22"),
        Entry("Italian", "it-large", "Large — 1.2 GB", "vosk-model-it-0.22", true),
        Entry("Japanese", "ja-small", "Small — 48 MB", "vosk-model-small-ja-0.22"),
        Entry("Japanese", "ja-large", "Large — 1 GB", "vosk-model-ja-0.22", true),
        Entry("Kazakh", "kk-small", "Small — 58 MB", "vosk-model-small-kz-0.42"),
        Entry("Kazakh", "kk-large", "Large — 1.3 GB", "vosk-model-kz-0.42", true),
        Entry("Korean", "ko-small", "Small — 82 MB", "vosk-model-small-ko-0.22"),
        Entry("Kyrgyz", "ky-small", "Small — 49 MB", "vosk-model-small-ky-0.42"),
        Entry("Kyrgyz", "ky-large", "Large — 1.1 GB", "vosk-model-ky-0.42", true),
        Entry("Persian", "fa-small", "Small — 53 MB", "vosk-model-small-fa-0.42"),
        Entry("Persian", "fa-large", "Large — 1.6 GB", "vosk-model-fa-0.42", true),
        Entry("Polish", "pl-small", "Small — 50 MB", "vosk-model-small-pl-0.22"),
        Entry("Portuguese", "pt-small", "Small — 31 MB", "vosk-model-small-pt-0.3"),
        Entry("Russian", "ru-small", "Small — 45 MB", "vosk-model-small-ru-0.22"),
        Entry("Russian", "ru-large", "Large — 1.8 GB", "vosk-model-ru-0.42", true),
        Entry("Spanish", "es-small", "Small — 39 MB", "vosk-model-small-es-0.42"),
        Entry("Spanish", "es-large", "Large — 1.4 GB", "vosk-model-es-0.42", true),
        Entry("Swedish", "sv-medium", "Medium — 289 MB", "vosk-model-small-sv-rhasspy-0.15"),
        Entry("Tajik", "tg-small", "Small — 50 MB", "vosk-model-small-tg-0.22"),
        Entry("Tajik", "tg-large", "Large — 327 MB", "vosk-model-tg-0.22"),
        Entry("Telugu", "te-small", "Small — 58 MB", "vosk-model-small-te-0.42"),
        Entry("Turkish", "tr-small", "Small — 35 MB", "vosk-model-small-tr-0.3"),
        Entry("Ukrainian", "uk-nano", "Nano — 73 MB", "vosk-model-small-uk-v3-nano"),
        Entry("Ukrainian", "uk-small", "Small — 133 MB", "vosk-model-small-uk-v3-small"),
        Entry("Ukrainian", "uk-large", "Large — 343 MB", "vosk-model-uk-v3"),
        Entry("Uzbek", "uz-small", "Small — 49 MB", "vosk-model-small-uz-0.22"),
        Entry("Vietnamese", "vi-small", "Small — 32 MB", "vosk-model-small-vn-0.4"),
        Entry("Vietnamese", "vi-large", "Large — 78 MB", "vosk-model-vn-0.4")
    ];

    public static IReadOnlyList<string> Languages { get; } =
        Catalog.Select(model => model.Language).Distinct().ToArray();

    public static VoskModelOption DefaultSmallEnglish => Catalog[0];

    public static bool EnsurePaths(AppSettings settings)
    {
        var previousDirectory = settings.ModelsDirectory;
        var directory = ResolveModelsDirectory(settings);
        settings.ModelsDirectory = directory;

        if (string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            var small = FindInstalledPath(DefaultSmallEnglish, directory);
            if (small is not null)
            {
                settings.ModelPath = small;
            }
        }

        return !string.Equals(previousDirectory, settings.ModelsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveModelsDirectory(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelsDirectory))
        {
            return Path.GetFullPath(settings.ModelsDirectory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            var modelPath = Path.GetFullPath(settings.ModelPath.Trim());
            if (LooksLikeModel(modelPath))
            {
                var parent = Path.GetDirectoryName(modelPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    return parent;
                }
            }

            if (Directory.Exists(modelPath) && !LooksLikeModel(modelPath))
            {
                return modelPath;
            }
        }

        return AppStorage.ModelsDirectory;
    }

    public static IReadOnlyList<VoskModelOption> ModelsForLanguage(string language, string modelsDirectory) =>
        Catalog
            .Where(model => string.Equals(model.Language, language, StringComparison.Ordinal))
            .Select(model => model with { IsDownloaded = FindInstalledPath(model, modelsDirectory) is not null })
            .ToArray();

    public static string? FindInstalledPath(VoskModelOption model, string modelsDirectory) =>
        FindModelFolder(Path.Combine(modelsDirectory, model.FolderName));

    public static IReadOnlyList<InstalledVoskModel> ListInstalled(string modelsDirectory, string? currentPath)
    {
        var items = new List<InstalledVoskModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in Catalog)
        {
            var path = FindInstalledPath(model, modelsDirectory);
            if (path is not null && seen.Add(path))
            {
                items.Add(new InstalledVoskModel(model.Id, $"{model.Language} · {model.DisplayName}", path));
            }
        }

        if (Directory.Exists(modelsDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(modelsDirectory))
            {
                var found = FindModelFolder(directory);
                if (found is not null && seen.Add(found))
                {
                    items.Add(new InstalledVoskModel(
                        $"custom:{Path.GetFileName(found)}",
                        $"Other · {Path.GetFileName(found)}",
                        found));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(currentPath) && LooksLikeModel(currentPath))
        {
            var full = Path.GetFullPath(currentPath);
            if (seen.Add(full))
            {
                items.Add(new InstalledVoskModel("custom", $"Custom — {Path.GetFileName(full)}", full));
            }
        }

        return items;
    }

    public static VoskModelOption? FindOptionForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Catalog.FirstOrDefault(model =>
            string.Equals(model.FolderName, name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool LooksLikeModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "am", "final.mdl"))
               || File.Exists(Path.Combine(path, "conf", "model.conf"))
               || File.Exists(Path.Combine(path, "graph", "Gr.fst"));
    }

    public static string? FindModelFolder(string path)
    {
        if (LooksLikeModel(path))
        {
            return Path.GetFullPath(path);
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        foreach (var child in Directory.EnumerateDirectories(path))
        {
            if (LooksLikeModel(child))
            {
                return Path.GetFullPath(child);
            }
        }

        return null;
    }

    public static async Task<string> DownloadAsync(
        VoskModelOption model,
        string modelsDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(modelsDirectory);

        var destination = Path.Combine(modelsDirectory, model.FolderName);
        var existing = FindModelFolder(destination);
        if (existing is not null)
        {
            progress?.Report(100);
            return existing;
        }

        var zipPath = Path.Combine(modelsDirectory, model.FolderName + ".zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(3) };
        using var response = await http.GetAsync(
            model.Url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(zipPath))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(95, copied * 95.0 / total));
                }
            }
        }

        progress?.Report(96);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, modelsDirectory, overwriteFiles: true), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            File.Delete(zipPath);
        }
        catch (IOException)
        {
            // Leave the zip if it is locked; the extracted model is what we need.
        }

        progress?.Report(100);
        return FindModelFolder(destination)
               ?? throw new InvalidOperationException("The Vosk model downloaded but the model folder was not found.");
    }

    private static VoskModelOption Entry(
        string language,
        string id,
        string displayName,
        string folderName,
        bool confirmLargeDownload = false) =>
        new(
            id,
            language,
            displayName,
            folderName,
            $"https://alphacephei.com/vosk/models/{folderName}.zip",
            confirmLargeDownload);
}
