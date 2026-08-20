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
    string DisplayName,
    string FolderName,
    string Url,
    bool ConfirmLargeDownload)
{
    public bool IsDownloaded => VoskModelManager.FindInstalledPath(this) is not null;

    public override string ToString() =>
        IsDownloaded ? $"{DisplayName}  ·  downloaded" : DisplayName;
}

internal sealed record InstalledVoskModel(string Id, string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}

internal static class VoskModelManager
{
    public static IReadOnlyList<VoskModelOption> EnglishModels { get; } =
    [
        new(
            "small",
            "Small — 40 MB (fast, less accurate)",
            "vosk-model-small-en-us-0.15",
            "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip",
            false),
        new(
            "medium",
            "Medium — 128 MB (better accuracy)",
            "vosk-model-en-us-0.22-lgraph",
            "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip",
            false),
        new(
            "large",
            "Large — 1.8 GB (accurate)",
            "vosk-model-en-us-0.22",
            "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip",
            true),
        new(
            "xlarge",
            "Extra large — 2.3 GB (highest accuracy)",
            "vosk-model-en-us-0.42-gigaspeech",
            "https://alphacephei.com/vosk/models/vosk-model-en-us-0.42-gigaspeech.zip",
            true)
    ];

    public static string DefaultSmallEnglishPath { get; } =
        Path.Combine(AppStorage.ModelsDirectory, EnglishModels[0].FolderName);

    public static string? FindInstalledPath(VoskModelOption model) =>
        FindModelFolder(Path.Combine(AppStorage.ModelsDirectory, model.FolderName));

    public static IReadOnlyList<InstalledVoskModel> ListInstalled(string? currentPath)
    {
        var items = new List<InstalledVoskModel>();
        foreach (var model in EnglishModels)
        {
            var path = FindInstalledPath(model);
            if (path is not null)
            {
                items.Add(new InstalledVoskModel(model.Id, model.DisplayName, path));
            }
        }

        if (!string.IsNullOrWhiteSpace(currentPath)
            && LooksLikeModel(currentPath)
            && items.TrueForAll(item => !string.Equals(item.Path, currentPath, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new InstalledVoskModel("custom", $"Custom — {Path.GetFileName(currentPath)}", currentPath));
        }

        return items;
    }

    public static VoskModelOption? FindOptionForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return EnglishModels.FirstOrDefault(model =>
            path.Contains(model.FolderName, StringComparison.OrdinalIgnoreCase));
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
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppStorage.ModelsDirectory);

        var destination = Path.Combine(AppStorage.ModelsDirectory, model.FolderName);
        var existing = FindModelFolder(destination);
        if (existing is not null)
        {
            progress?.Report(100);
            return existing;
        }

        var zipPath = Path.Combine(AppStorage.ModelsDirectory, model.FolderName + ".zip");
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
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, AppStorage.ModelsDirectory, overwriteFiles: true), cancellationToken)
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
}
