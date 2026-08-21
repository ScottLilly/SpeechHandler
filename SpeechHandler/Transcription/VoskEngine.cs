using System.Text;
using System.Text.Json;
using Vosk;

namespace SpeechHandler.Transcription;

internal sealed record LoadedVoskModelInfo(
    string Path,
    string DisplayName,
    long PrivateBytes,
    DateTime LastUsedUtc,
    bool Pinned,
    int ActiveSessionCount)
{
    public bool InUse => ActiveSessionCount > 0;

    public bool CanUnload => ActiveSessionCount == 0;
}

internal sealed class VoskLoadPlan
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public bool AlreadyLoaded { get; init; }
    public long EstimatedBytes { get; init; }
    public long BudgetBytes { get; init; }
    public long UsedBytes { get; init; }
    public long AvailablePhysicalBytes { get; init; }
    public IReadOnlyList<LoadedVoskModelInfo> EvictionCandidates { get; init; } = [];
    public bool ExceedsBudgetAlone { get; init; }
    public bool CanLoad { get; init; }
    public string? RefusalReason { get; init; }
    public bool NeedsConfirmation { get; init; }
    public string ConfirmationMessage { get; init; } = string.Empty;
}

internal sealed class VoskEngine : IDisposable
{
    private const long LoadHeadroomBytes = 512L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _budgetGigabytes;
    private string? _currentPath;
    private bool _disposed;

    public event EventHandler? CacheChanged;

    public string? ModelPath
    {
        get
        {
            lock (_sync)
            {
                return _currentPath;
            }
        }
    }

    public long BudgetBytes
    {
        get
        {
            lock (_sync)
            {
                return ProcessMemory.BytesFromGigabytes(_budgetGigabytes);
            }
        }
    }

    public long UsedBytes
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values.Sum(entry => entry.PrivateBytes);
            }
        }
    }

    public bool IsLoaded(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(modelPath);
        lock (_sync)
        {
            return _entries.ContainsKey(fullPath);
        }
    }

    public void SetBudgetGigabytes(int gigabytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<Entry> retiring;
        lock (_sync)
        {
            _budgetGigabytes = ProcessMemory.ClampBudgetGigabytes(gigabytes);
            retiring = DetachLocked(ChooseEvictionCandidatesLocked(extraBytes: 0, protectPath: string.Empty));
        }

        foreach (var entry in retiring)
        {
            DisposeEntry(entry);
        }

        Notify(retiring.Count > 0);
    }

    public IReadOnlyList<LoadedVoskModelInfo> Snapshot()
    {
        lock (_sync)
        {
            return SnapshotLocked();
        }
    }

    public VoskLoadPlan PlanLoad(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = Path.GetFullPath(modelPath);
        var displayName = VoskModelManager.DisplayLabel(fullPath);
        var estimated = VoskModelManager.EstimateRuntimeBytes(fullPath);
        var available = ProcessMemory.AvailablePhysicalBytes;

        lock (_sync)
        {
            var budget = ProcessMemory.BytesFromGigabytes(_budgetGigabytes);
            var used = _entries.Values.Sum(entry => entry.PrivateBytes);
            if (_entries.ContainsKey(fullPath))
            {
                return new VoskLoadPlan
                {
                    Path = fullPath,
                    DisplayName = displayName,
                    AlreadyLoaded = true,
                    EstimatedBytes = _entries[fullPath].PrivateBytes,
                    BudgetBytes = budget,
                    UsedBytes = used,
                    AvailablePhysicalBytes = available,
                    CanLoad = true
                };
            }

            var candidates = ChooseEvictionCandidatesLocked(estimated, fullPath);
            var freed = candidates.Sum(item => item.PrivateBytes);
            var remaining = Math.Max(0, used - freed);
            var exceedsAlone = estimated > budget;
            var physicalTooLow = available > 0
                                 && available + freed < estimated + LoadHeadroomBytes;
            var canLoad = !physicalTooLow;
            var refusal = canLoad
                ? null
                : $"There is not enough free memory to load {displayName} (about {ProcessMemory.FormatBytes(estimated)}). Close other apps or unload a pinned model, then try again.";

            var remainingBudget = Math.Max(0, budget - remaining);
            var needsConfirm = canLoad
                               && (VoskModelManager.IsLargeModel(fullPath)
                                   || candidates.Count > 0
                                   || estimated > remainingBudget
                                   || exceedsAlone);

            return new VoskLoadPlan
            {
                Path = fullPath,
                DisplayName = displayName,
                EstimatedBytes = estimated,
                BudgetBytes = budget,
                UsedBytes = used,
                AvailablePhysicalBytes = available,
                EvictionCandidates = candidates,
                ExceedsBudgetAlone = exceedsAlone,
                CanLoad = canLoad,
                RefusalReason = refusal,
                NeedsConfirmation = needsConfirm,
                ConfirmationMessage = BuildConfirmation(displayName, estimated, budget, exceedsAlone, candidates)
            };
        }
    }

    public IReadOnlyList<LoadedVoskModelInfo> EnsureLoaded(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = Path.GetFullPath(modelPath);
        List<LoadedVoskModelInfo> evicted;
        List<Entry> retiring;
        lock (_sync)
        {
            if (_entries.TryGetValue(fullPath, out var existing))
            {
                existing.LastUsedUtc = DateTime.UtcNow;
                _currentPath = fullPath;
                return [];
            }

            var estimated = VoskModelManager.EstimateRuntimeBytes(fullPath);
            evicted = ChooseEvictionCandidatesLocked(estimated, fullPath);
            retiring = DetachLocked(evicted);
        }

        foreach (var entry in retiring)
        {
            DisposeEntry(entry);
        }

        try
        {
            global::Vosk.Vosk.SetLogLevel(-1);
            var before = ProcessMemory.PrivateBytes();
            var model = new Model(fullPath);
            var after = ProcessMemory.PrivateBytes();
            var measured = after - before;
            var privateBytes = measured > 8L * 1024 * 1024
                ? measured
                : VoskModelManager.EstimateRuntimeBytes(fullPath);

            lock (_sync)
            {
                _entries[fullPath] = new Entry
                {
                    Path = fullPath,
                    DisplayName = VoskModelManager.DisplayLabel(fullPath),
                    Model = model,
                    PrivateBytes = privateBytes,
                    LastUsedUtc = DateTime.UtcNow
                };
                _currentPath = fullPath;
            }
        }
        catch
        {
            if (retiring.Count > 0)
            {
                Notify(changed: true);
            }

            throw;
        }

        Notify(changed: true);
        return evicted;
    }

    public VoskSession CreateSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        VoskSession session;
        lock (_sync)
        {
            if (_currentPath is null || !_entries.TryGetValue(_currentPath, out var entry))
            {
                throw new InvalidOperationException("Load a Vosk model before transcribing.");
            }

            entry.ActiveSessionCount++;
            entry.LastUsedUtc = DateTime.UtcNow;
            var path = entry.Path;
            session = new VoskSession(entry.Model, () => ReleaseSession(path));
        }

        Notify(changed: true);
        return session;
    }

    public bool Unload(string modelPath, out string? reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = Path.GetFullPath(modelPath);
        Entry? retiring = null;
        lock (_sync)
        {
            if (!_entries.TryGetValue(fullPath, out var entry))
            {
                reason = "That model is not loaded.";
                return false;
            }

            if (entry.ActiveSessionCount > 0)
            {
                reason = $"{entry.DisplayName} is in use and cannot be unloaded until transcription stops.";
                return false;
            }

            _entries.Remove(fullPath);
            retiring = entry;
            if (string.Equals(_currentPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentPath = _entries.Keys.FirstOrDefault();
            }
        }

        DisposeEntry(retiring);
        reason = null;
        Notify(changed: true);
        return true;
    }

    public void SetPinned(string modelPath, bool pinned)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = Path.GetFullPath(modelPath);
        lock (_sync)
        {
            if (_entries.TryGetValue(fullPath, out var entry))
            {
                entry.Pinned = pinned;
            }
        }

        Notify(changed: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            foreach (var entry in _entries.Values)
            {
                DisposeEntry(entry);
            }

            _entries.Clear();
            _currentPath = null;
        }
    }

    private void ReleaseSession(string path)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(path, out var entry) && entry.ActiveSessionCount > 0)
            {
                entry.ActiveSessionCount--;
            }
        }

        Notify(changed: true);
    }

    private List<LoadedVoskModelInfo> ChooseEvictionCandidatesLocked(long extraBytes, string protectPath)
    {
        var budget = ProcessMemory.BytesFromGigabytes(_budgetGigabytes);
        var used = _entries.Values.Sum(entry => entry.PrivateBytes);
        if (extraBytes > budget)
        {
            return UnusedEvictableLocked(protectPath);
        }

        var needed = used + extraBytes - budget;
        if (needed <= 0)
        {
            return [];
        }

        var selected = new List<LoadedVoskModelInfo>();
        long freed = 0;
        foreach (var entry in UnusedEvictableLocked(protectPath)
                     .OrderBy(item => item.LastUsedUtc))
        {
            selected.Add(entry);
            freed += entry.PrivateBytes;
            if (freed >= needed)
            {
                break;
            }
        }

        return selected;
    }

    private List<LoadedVoskModelInfo> UnusedEvictableLocked(string protectPath) =>
        _entries.Values
            .Where(entry =>
                entry.ActiveSessionCount == 0
                && !entry.Pinned
                && !string.Equals(entry.Path, protectPath, StringComparison.OrdinalIgnoreCase))
            .Select(ToInfo)
            .ToList();

    private List<Entry> DetachLocked(IReadOnlyList<LoadedVoskModelInfo> items)
    {
        var retiring = new List<Entry>();
        foreach (var item in items)
        {
            if (!_entries.Remove(item.Path, out var entry))
            {
                continue;
            }

            retiring.Add(entry);
            if (string.Equals(_currentPath, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                _currentPath = null;
            }
        }

        return retiring;
    }

    private List<LoadedVoskModelInfo> SnapshotLocked() =>
        _entries.Values
            .OrderByDescending(entry => entry.LastUsedUtc)
            .Select(ToInfo)
            .ToList();

    private static LoadedVoskModelInfo ToInfo(Entry entry) =>
        new(
            entry.Path,
            entry.DisplayName,
            entry.PrivateBytes,
            entry.LastUsedUtc,
            entry.Pinned,
            entry.ActiveSessionCount);

    private static void DisposeEntry(Entry entry)
    {
        try
        {
            entry.Model.Dispose();
        }
        catch (Exception)
        {
            // Native dispose is best-effort.
        }
    }

    private void Notify(bool changed)
    {
        if (changed)
        {
            CacheChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string BuildConfirmation(
        string displayName,
        long estimated,
        long budget,
        bool exceedsAlone,
        IReadOnlyList<LoadedVoskModelInfo> candidates)
    {
        var estimateText = ProcessMemory.FormatBytes(estimated);
        if (exceedsAlone)
        {
            return $"{displayName} is larger than the cache budget ({ProcessMemory.FormatBytes(budget)}). "
                   + $"It typically needs about {estimateText} of RAM. Other unused models will be unloaded. Continue?";
        }

        if (candidates.Count > 0)
        {
            var first = candidates[0];
            var unload = candidates.Count == 1
                ? $"Unload {first.DisplayName} ({ProcessMemory.FormatBytes(first.PrivateBytes)})"
                : $"Unload {first.DisplayName} ({ProcessMemory.FormatBytes(first.PrivateBytes)}) and {candidates.Count - 1} other model(s)";
            return $"{displayName} typically needs about {estimateText} of RAM. {unload} and continue?";
        }

        return $"{displayName} typically needs several GB of RAM (about {estimateText}). Continue?";
    }

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required string DisplayName { get; init; }
        public required Model Model { get; init; }
        public long PrivateBytes { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public bool Pinned { get; set; }
        public int ActiveSessionCount { get; set; }
    }
}

internal sealed class VoskSession : IDisposable
{
    private readonly VoskRecognizer _recognizer;
    private readonly Action? _onRelease;
    private bool _disposed;

    public VoskSession(Model model, Action? onRelease = null)
    {
        _recognizer = new VoskRecognizer(model, Audio.Pcm16kMonoConverter.SampleRate);
        _recognizer.SetMaxAlternatives(0);
        _recognizer.SetWords(true);
        _onRelease = onRelease;
    }

    public bool Accept(byte[] data, int length, out TranscriptionResult? final, out string? partialText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recognizer.AcceptWaveform(data, length))
        {
            final = ReadResult(_recognizer.Result());
            partialText = null;
            return true;
        }

        final = null;
        partialText = ReadJsonString(_recognizer.PartialResult(), "partial");
        return false;
    }

    public TranscriptionResult? Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadResult(_recognizer.FinalResult());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer.Dispose();
        _onRelease?.Invoke();
    }

    private static TranscriptionResult? ReadResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var words = ReadWords(root);
        if (words.Count > 0)
        {
            var fromWords = BuildTextFromWords(words);
            if (!string.IsNullOrWhiteSpace(fromWords))
            {
                return new TranscriptionResult(fromWords, words);
            }
        }

        if (!root.TryGetProperty("text", out var value))
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : new TranscriptionResult(text.Trim(), words);
    }

    private static List<TimedWord> ReadWords(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var words) || words.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<TimedWord>();
        foreach (var word in words.EnumerateArray())
        {
            if (!word.TryGetProperty("word", out var tokenElement))
            {
                continue;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var start = word.TryGetProperty("start", out var startElement)
                ? startElement.GetDouble()
                : list.Count > 0 ? list[^1].EndSeconds : 0;
            var end = word.TryGetProperty("end", out var endElement)
                ? endElement.GetDouble()
                : start;
            if (end < start)
            {
                end = start;
            }

            list.Add(new TimedWord(token.Trim(), start, end));
        }

        return list;
    }

    private static string BuildTextFromWords(IReadOnlyList<TimedWord> words)
    {
        var builder = new StringBuilder();
        var lastEnd = -1.0;
        foreach (var word in words)
        {
            if (builder.Length > 0)
            {
                // A long pause inside an utterance is a reliable sentence break.
                // Commas are not: pauses and comma placement often disagree.
                builder.Append(lastEnd >= 0 && word.StartSeconds - lastEnd >= 0.7
                    ? ". "
                    : " ");
            }

            builder.Append(word.Text);
            lastEnd = word.EndSeconds;
        }

        return builder.ToString();
    }

    private static string? ReadJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
