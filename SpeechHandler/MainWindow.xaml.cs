using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;
using SpeechHandler.Audio;
using SpeechHandler.Transcription;
using SpeechHandler.Tts;

namespace SpeechHandler;

public partial class MainWindow : Window
{
    private static readonly Color[] CacheSliceColors =
    [
        Color.FromRgb(0x3D, 0x9C, 0xF0),
        Color.FromRgb(0x3D, 0xDC, 0x97),
        Color.FromRgb(0xF5, 0xC1, 0x4A),
        Color.FromRgb(0xA7, 0x8B, 0xFA),
        Color.FromRgb(0x5A, 0xAE, 0xF5),
        Color.FromRgb(0xE1, 0x5D, 0x64)
    ];

    private static readonly SolidColorBrush CacheFreeBrush = BrushFrom("#2A333C");
    private static readonly SolidColorBrush IdleBrush = BrushFrom("#6B7682");
    private static readonly SolidColorBrush ProcessingBrush = BrushFrom("#F5C14A");
    private static readonly SolidColorBrush LiveBrush = BrushFrom("#3DDC97");
    private static readonly SolidColorBrush ErrorBrush = BrushFrom("#E15D64");

    private readonly VoskEngine _vosk = new();
    private readonly OpenAiWhisperClient _openAi = new();
    private readonly ElevenLabsSpeechClient _elevenLabs = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SemaphoreSlim _apiGate = new(1, 1);
    private readonly object _liveSync = new();

    private LiveAudioCapture? _capture;
    private VoskSession? _liveVosk;
    private MemoryStream? _liveApiBuffer;
    private CancellationTokenSource? _workCts;
    private WaveOut? _ttsPlayer;
    private AudioFileReader? _ttsReader;
    private string? _ttsTempFile;
    private bool _isLive;
    private bool _busy;
    private bool _ttsPlaying;
    private string? _lastFinalRaw;
    private string? _selectedAudioFile;
    private string _apiKey = string.Empty;
    private string _elevenLabsKey = string.Empty;
    private bool _suppressLanguageSelection;
    private bool _suppressInstalledModelSelection;
    private bool _suppressTtsVoiceSelection;
    private readonly List<TimedWord> _timedWords = [];
    private double _sessionTimeOffset;
    private long _liveApiPcmBytes;

    private const string OpenAiLanguage = "OpenAI Whisper";
    private const string ElevenLabsLanguage = "ElevenLabs";

    public MainWindow()
    {
        InitializeComponent();
        TranscriptSpelling.Attach(TranscriptBox);
        TranscriptSpelling.Attach(SrtBox, skipSrtMetadata: true);
        TranscriptSpelling.WordCorrected = SyncSpellingCorrection;
        _vosk.CacheChanged += (_, _) => Dispatcher.BeginInvoke(RefreshCacheUi);
        LoadSettingsIntoUi();

        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            _apiKey = envKey;
        }

        var elevenKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        if (!string.IsNullOrWhiteSpace(elevenKey))
        {
            _elevenLabsKey = elevenKey;
        }

        RefreshSources();
        RefreshEngineSelector();
        RefreshCacheUi();
        SetStatus("Ready", IdleBrush);
    }

    private bool UseLocalEngine =>
        string.Equals(_settings.Engine, "Local", StringComparison.OrdinalIgnoreCase);

    private bool UseElevenLabs =>
        string.Equals(_settings.Engine, "ElevenLabs", StringComparison.OrdinalIgnoreCase);

    private void LoadSettingsIntoUi()
    {
        LoadTtsVoiceCombo();
        var settingsChanged = VoskModelManager.EnsurePaths(_settings);
        settingsChanged |= _settings.EnsureCacheBudget();
        if (settingsChanged)
        {
            _settings.Save();
        }

        ApplyCacheBudget();
    }

    private void PersistSettings()
    {
        _settings.TtsVoiceId = (TtsVoiceCombo.SelectedItem as TtsVoiceOption)?.Id ?? TtsVoiceCatalog.DefaultVoiceId;
        _settings.TtsAudioFormat = AudioFormatWriter.DefaultExtension(_settings.TtsAudioFormat).TrimStart('.');
        _settings.EnsureCacheBudget();
        _settings.Save();
    }

    private void ApplyAudioSaveDirectory(SaveFileDialog dialog)
    {
        dialog.RestoreDirectory = true;
        var directory = _settings.TtsAudioDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(directory);
            if (!Directory.Exists(full))
            {
                return;
            }

            dialog.InitialDirectory = full;
            var fileName = Path.GetFileName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                dialog.FileName = Path.Combine(full, fileName);
            }
        }
        catch (Exception)
        {
            // Ignore a missing drive or an invalid saved path.
        }
    }

    private void RememberAudioSaveDirectory(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                _settings.TtsAudioDirectory = directory;
            }
        }
        catch (Exception)
        {
            // Keep the previously stored folder if this path can't be used.
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        var dialog = new SettingsWindow(_settings, _apiKey, _elevenLabsKey) { Owner = this };
        dialog.ShowDialog();
        _apiKey = dialog.OpenAiKey;
        _elevenLabsKey = dialog.ElevenLabsKey;
        ApplyCacheBudget();
        RefreshEngineSelector();
        RefreshCacheUi();
    }

    private void TtsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        var dialog = new TtsSettingsWindow(_settings) { Owner = this };
        dialog.ShowDialog();
        LoadTtsVoiceCombo();
    }

    private void LoadTtsVoiceCombo()
    {
        _suppressTtsVoiceSelection = true;
        try
        {
            TtsVoiceCombo.ItemsSource = TtsVoiceCatalog.Voices;
            TtsVoiceCombo.SelectedItem = TtsVoiceCatalog.FindVoice(_settings.TtsVoiceId)
                                         ?? TtsVoiceCatalog.FindVoice(TtsVoiceCatalog.DefaultVoiceId)
                                         ?? TtsVoiceCatalog.Voices[0];
        }
        finally
        {
            _suppressTtsVoiceSelection = false;
        }
    }

    private void TtsVoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressTtsVoiceSelection || TtsVoiceCombo.SelectedItem is not TtsVoiceOption voice)
        {
            return;
        }

        if (string.Equals(_settings.TtsVoiceId, voice.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.TtsVoiceId = voice.Id;
        _settings.Save();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private bool OpenAiConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    private bool ElevenLabsConfigured => !string.IsNullOrWhiteSpace(_elevenLabsKey);

    private void RefreshEngineSelector()
    {
        _suppressLanguageSelection = true;
        _suppressInstalledModelSelection = true;
        try
        {
            var installed = VoskModelManager.ListInstalled(
                VoskModelManager.ResolveModelsDirectory(_settings), _settings.ModelPath);
            var languages = BuildLanguageList(installed);
            LanguageCombo.ItemsSource = languages;
            var language = ResolveCurrentLanguage(installed, languages);
            LanguageCombo.SelectedItem = language;
            FillModelCombo(language, installed, persist: false);
            ApplyTranscriptLanguage(language);
            RefreshCacheUi();
        }
        finally
        {
            _suppressLanguageSelection = false;
            _suppressInstalledModelSelection = false;
        }
    }

    private List<string> BuildLanguageList(IReadOnlyList<InstalledVoskModel> installed)
    {
        var downloaded = installed.Select(model => model.Language).ToHashSet(StringComparer.Ordinal);
        var languages = VoskModelManager.Languages.Where(downloaded.Contains).ToList();
        foreach (var language in installed.Select(model => model.Language).Distinct())
        {
            if (!languages.Contains(language))
            {
                languages.Add(language);
            }
        }

        if (OpenAiConfigured)
        {
            languages.Add(OpenAiLanguage);
        }

        if (ElevenLabsConfigured)
        {
            languages.Add(ElevenLabsLanguage);
        }

        return languages;
    }

    private string? ResolveCurrentLanguage(
        IReadOnlyList<InstalledVoskModel> installed,
        IReadOnlyList<string> languages)
    {
        if (UseOpenAiEngine && OpenAiConfigured)
        {
            return OpenAiLanguage;
        }

        if (UseElevenLabs && ElevenLabsConfigured)
        {
            return ElevenLabsLanguage;
        }

        var current = installed.FirstOrDefault(model =>
            string.Equals(model.Path, _settings.ModelPath, StringComparison.OrdinalIgnoreCase));
        if (current is not null)
        {
            return current.Language;
        }

        return languages.FirstOrDefault();
    }

    private void FillModelCombo(
        string? language,
        IReadOnlyList<InstalledVoskModel> installed,
        bool persist)
    {
        var items = new List<TranscriptionOption>();
        if (string.Equals(language, OpenAiLanguage, StringComparison.Ordinal) && OpenAiConfigured)
        {
            var whisper = string.IsNullOrWhiteSpace(_settings.WhisperModel) ? "whisper-1" : _settings.WhisperModel;
            items.Add(new TranscriptionOption("Api", whisper));
        }
        else if (string.Equals(language, ElevenLabsLanguage, StringComparison.Ordinal) && ElevenLabsConfigured)
        {
            var scribe = string.IsNullOrWhiteSpace(_settings.ElevenLabsModel) ? "scribe_v2" : _settings.ElevenLabsModel;
            items.Add(new TranscriptionOption("ElevenLabs", scribe));
        }
        else if (!string.IsNullOrWhiteSpace(language))
        {
            foreach (var model in installed.Where(item =>
                         string.Equals(item.Language, language, StringComparison.Ordinal)))
            {
                items.Add(new TranscriptionOption(
                    "Local",
                    model.DisplayName,
                    model.Path,
                    _vosk.IsLoaded(model.Path)));
            }
        }

        InstalledModelCombo.ItemsSource = items;
        var selected = items.FirstOrDefault(MatchesCurrentEngine) ?? items.FirstOrDefault();
        InstalledModelCombo.SelectedItem = selected;
        if (selected is not null)
        {
            ApplyTranscriptionOption(selected, persist);
        }
        else if (!UseLocalEngine)
        {
            _settings.Engine = "Local";
        }
    }

    private bool MatchesCurrentEngine(TranscriptionOption option) =>
        option.Engine switch
        {
            "Local" => UseLocalEngine
                       && string.Equals(option.ModelPath, _settings.ModelPath, StringComparison.OrdinalIgnoreCase),
            "Api" => UseOpenAiEngine,
            "ElevenLabs" => UseElevenLabs,
            _ => false
        };

    private bool UseOpenAiEngine =>
        string.Equals(_settings.Engine, "Api", StringComparison.OrdinalIgnoreCase);

    private void ApplyTranscriptionOption(TranscriptionOption option, bool persist)
    {
        _settings.Engine = option.Engine;
        if (option.Engine == "Local" && !string.IsNullOrWhiteSpace(option.ModelPath))
        {
            _settings.ModelPath = option.ModelPath;
        }

        if (persist)
        {
            _settings.Save();
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressLanguageSelection)
        {
            return;
        }

        var installed = VoskModelManager.ListInstalled(
            VoskModelManager.ResolveModelsDirectory(_settings), _settings.ModelPath);
        _suppressInstalledModelSelection = true;
        try
        {
            var language = LanguageCombo.SelectedItem as string;
            FillModelCombo(language, installed, persist: true);
            ApplyTranscriptLanguage(language);
            RefreshCacheUi();
        }
        finally
        {
            _suppressInstalledModelSelection = false;
        }
    }

    private void InstalledModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressInstalledModelSelection)
        {
            return;
        }

        if (InstalledModelCombo.SelectedItem is not TranscriptionOption option)
        {
            return;
        }

        ApplyTranscriptionOption(option, persist: true);
        RefreshCacheUi();
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private void RefreshSources()
    {
        var previousId = (SourcesCombo.SelectedItem as AudioInputSource)?.Id;
        var sources = AudioDeviceCatalog.ListSources();
        SourcesCombo.ItemsSource = sources;
        if (sources.Count == 0)
        {
            SetStatus("No audio input devices were found.", ErrorBrush);
            return;
        }

        var match = sources.FirstOrDefault(s => s.Id == previousId);
        SourcesCombo.SelectedItem = match ?? sources[0];
        if (!_isLive && !_busy)
        {
            SetStatus("Ready", IdleBrush);
        }
    }

    private async void StartLive_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        if (SourcesCombo.SelectedItem is not AudioInputSource source)
        {
            MessageBox.Show(this, "Select a microphone or system-audio source first.", "Speech Handler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cts = BeginWork("Processing…");
        try
        {
            if (UseLocalEngine)
            {
                ProcessingMessage.Text = "Loading speech model…";
                await PrepareLocalModelAsync(cts.Token);
            }
            else
            {
                ValidateApiKey();
            }

            lock (_liveSync)
            {
                _liveVosk = UseLocalEngine ? _vosk.CreateSession() : null;
                _liveApiBuffer = UseLocalEngine ? null : new MemoryStream();
            }

            _lastFinalRaw = null;
            _sessionTimeOffset = NextSessionTimeOffset();
            _liveApiPcmBytes = 0;

            _capture = new LiveAudioCapture(source.Id, source.Kind);
            _capture.PcmAvailable += OnLivePcm;
            _capture.Stopped += OnCaptureStopped;
            _capture.Start();

            _isLive = true;
            ProcessingMessage.Text = $"Processing live audio from {source.DisplayName}…";
            ProcessingBar.IsIndeterminate = true;
            ProcessingOverlay.Visibility = Visibility.Visible;
            SetStatus($"Processing live audio from {source.DisplayName}…", LiveBrush);
            UpdateActionButtons();
        }
        catch (OperationCanceledException)
        {
            CleanupLive();
            SetStatus("Canceled.", IdleBrush);
        }
        catch (Exception ex)
        {
            CleanupLive();
            ShowError("Could not start live transcription.", ex);
        }
        finally
        {
            if (!_isLive)
            {
                EndWork();
            }
            else
            {
                _busy = false;
                CancelProcessingButton.IsEnabled = true;
                UpdateActionButtons();
            }
        }
    }

    private async void StopLive_Click(object sender, RoutedEventArgs e) => await StopLiveAsync();

    private async Task StopLiveAsync()
    {
        if (!_isLive)
        {
            return;
        }

        SetStatus("Finishing transcription…", ProcessingBrush);
        VoskSession? vosk;
        MemoryStream? apiBuffer;
        lock (_liveSync)
        {
            vosk = _liveVosk;
            apiBuffer = _liveApiBuffer;
            _liveVosk = null;
            _liveApiBuffer = null;
        }

        CleanupLive(disposeSession: false);

        try
        {
            if (vosk is not null)
            {
                AppendFinal(vosk.Finish(), formatAsSentences: true, timeOffsetSeconds: _sessionTimeOffset);
                vosk.Dispose();
            }
            else if (apiBuffer is { Length: > 0 })
            {
                var pcm = apiBuffer.ToArray();
                var offset = _sessionTimeOffset + PcmDurationSeconds(_liveApiPcmBytes);
                apiBuffer.Dispose();
                await TranscribeApiChunkAsync(pcm, offset, _workCts?.Token ?? CancellationToken.None);
            }
            else
            {
                apiBuffer?.Dispose();
            }
        }
        catch (Exception ex)
        {
            ShowError("Live transcription stopped with an error.", ex);
            return;
        }

        PartialText.Text = string.Empty;
        EndWork();
        SetStatus("Ready", IdleBrush);
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        var dialog = CreateAudioOpenDialog();
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ShowSelectedFile(dialog.FileName);
        UpdateActionButtons();
    }

    private async void TranscribeFile_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedAudioFile) || !File.Exists(_selectedAudioFile))
        {
            MessageBox.Show(this, "Choose an audio file first.", "Speech Handler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = _selectedAudioFile;
        var cts = BeginWork("Processing audio file…");
        _lastFinalRaw = null;
        _sessionTimeOffset = NextSessionTimeOffset();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            if (UseLocalEngine)
            {
                ProcessingMessage.Text = "Loading speech model…";
                await PrepareLocalModelAsync(cts.Token);
                ProcessingMessage.Text = "Processing audio file…";
                stopwatch.Restart();
                await TranscribeFileLocalAsync(path, cts.Token);
            }
            else
            {
                ValidateApiKey();
                await TranscribeFileApiAsync(path, cts.Token);
            }

            stopwatch.Stop();
            SetStatus(
                $"Transcribed {Path.GetFileName(path)} in {stopwatch.Elapsed.TotalSeconds:F1} seconds.",
                IdleBrush);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Processing canceled.", IdleBrush);
        }
        catch (Exception ex)
        {
            ShowError("Could not transcribe that file.", ex);
        }
        finally
        {
            EndWork();
        }
    }

    private static OpenFileDialog CreateAudioOpenDialog() =>
        new()
        {
            Title = "Choose an audio file",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.mp4;*.wma;*.flac;*.ogg;*.aac;*.webm|All files|*.*"
        };

    private void ShowSelectedFile(string path)
    {
        var details = AudioFileInspector.Read(path);
        _selectedAudioFile = details.Path;
        SelectedFileName.Text = details.FileName;
        SelectedFileName.ToolTip = details.Path;
        SelectedFileDuration.Text = details.Duration;
        SelectedFileSize.Text = details.Size;
        SelectedFileFormat.Text = details.Format;
        FilePlaceholder.Visibility = Visibility.Collapsed;
        FileDetailsPanel.Visibility = Visibility.Visible;
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e)
    {
        _workCts?.Cancel();
        if (_isLive)
        {
            _ = StopLiveAsync();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var srt = SrtTabSelected;
        var text = srt ? SrtBox.Text : TranscriptBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Clipboard.SetText(text);
        SetStatus(srt ? "Copied subtitles to the clipboard." : "Copied transcript to the clipboard.", IdleBrush);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var srtSelected = SrtTabSelected;
        var baseName = !string.IsNullOrWhiteSpace(_selectedAudioFile)
            ? Path.GetFileNameWithoutExtension(_selectedAudioFile)
            : $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}";
        var dialog = new SaveFileDialog
        {
            Title = srtSelected ? "Save subtitles" : "Save transcript",
            Filter = srtSelected
                ? "SubRip subtitles|*.srt|Text files|*.txt|All files|*.*"
                : "Text files|*.txt|SubRip subtitles|*.srt|All files|*.*",
            FileName = srtSelected ? $"{baseName}.srt" : $"{baseName}.txt"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var saveSrt = string.Equals(Path.GetExtension(dialog.FileName), ".srt", StringComparison.OrdinalIgnoreCase);
        var text = saveSrt ? SrtBox.Text : TranscriptBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(
                this,
                saveSrt ? "There are no subtitles to save yet." : "There is no transcript to save yet.",
                "Speech Handler",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var encoding = saveSrt ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) : Encoding.UTF8;
        File.WriteAllText(dialog.FileName, text, encoding);
        SetStatus(saveSrt ? "Saved subtitles." : "Saved transcript.", IdleBrush);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Clear();
        SrtBox.Clear();
        _timedWords.Clear();
        PartialText.Text = string.Empty;
        _lastFinalRaw = null;
        if (!_isLive && !_busy)
        {
            SetStatus("Ready", IdleBrush);
        }
    }

    private async void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (_ttsPlaying)
        {
            StopTtsPlayback();
            return;
        }

        if (!TryGetTranscriptAndVoice(out var text, out var voice))
        {
            return;
        }

        PersistSettings();
        var cts = BeginWork("Preparing neural voice…");
        SpeakButton.Content = "Stop speaking";
        try
        {
            var wavPath = await SynthesizeTranscriptWavAsync(text, voice, cts.Token);
            HideOverlay();
            _busy = false;
            await PlayTtsAsync(wavPath, cts.Token);
            SetStatus("Ready", IdleBrush);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Speech canceled.", IdleBrush);
        }
        catch (Exception ex)
        {
            ShowError("Could not speak the transcript.", ex);
        }
        finally
        {
            if (!_ttsPlaying)
            {
                SpeakButton.Content = "Speak transcript";
            }

            if (_busy)
            {
                EndWork();
            }
            else
            {
                HideOverlay();
                UpdateActionButtons();
            }
        }
    }

    private async void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_ttsPlaying)
        {
            StopTtsPlayback();
        }

        if (!TryGetTranscriptAndVoice(out var text, out var voice))
        {
            return;
        }

        var extension = AudioFormatWriter.DefaultExtension(_settings.TtsAudioFormat);
        var baseName = !string.IsNullOrWhiteSpace(_selectedAudioFile)
            ? Path.GetFileNameWithoutExtension(_selectedAudioFile)
            : $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}";
        var dialog = new SaveFileDialog
        {
            Title = "Save transcript audio",
            Filter = AudioFormatWriter.FileDialogFilter,
            FilterIndex = AudioFormatWriter.FilterIndexForExtension(extension),
            DefaultExt = extension.TrimStart('.'),
            FileName = $"{baseName}{extension}",
            AddExtension = true
        };
        ApplyAudioSaveDirectory(dialog);

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.TtsAudioFormat = AudioFormatWriter.DefaultExtension(Path.GetExtension(dialog.FileName))
            .TrimStart('.');
        RememberAudioSaveDirectory(dialog.FileName);
        PersistSettings();

        var cts = BeginWork("Preparing neural voice…");
        var wavPath = Path.Combine(Path.GetTempPath(), $"speechhandler-tts-{Guid.NewGuid():N}.wav");
        try
        {
            await SynthesizeTranscriptWavAsync(text, voice, cts.Token, wavPath);
            ProcessingMessage.Text = "Writing audio file…";
            SetStatus("Writing audio file…", ProcessingBrush);
            await Task.Run(() => AudioFormatWriter.WriteFromWav(wavPath, dialog.FileName), cts.Token);
            SetStatus("Saved audio.", IdleBrush);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Audio export canceled.", IdleBrush);
        }
        catch (Exception ex)
        {
            ShowError("Could not save the audio file.", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(wavPath))
                {
                    File.Delete(wavPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }

            EndWork();
        }
    }

    private bool TryGetTranscriptAndVoice(out string text, out TtsVoiceOption voice)
    {
        text = TranscriptBox.Text.Trim();
        voice = TtsVoiceCombo.SelectedItem as TtsVoiceOption
                ?? TtsVoiceCatalog.FindVoice(_settings.TtsVoiceId)
                ?? TtsVoiceCatalog.FindVoice(TtsVoiceCatalog.DefaultVoiceId)
                ?? TtsVoiceCatalog.Voices[0];

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "There is no transcript yet.", "Speech Handler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private async Task<string> SynthesizeTranscriptWavAsync(
        string text,
        TtsVoiceOption voice,
        CancellationToken cancellationToken,
        string? wavPath = null)
    {
        var status = new Progress<string>(message =>
        {
            ProcessingMessage.Text = message;
            SetStatus(message, ProcessingBrush);
        });

        await voice.Engine.EnsureReadyAsync(status, cancellationToken);
        ProcessingMessage.Text = "Generating speech…";
        SetStatus("Generating speech…", ProcessingBrush);

        wavPath ??= Path.Combine(Path.GetTempPath(), $"speechhandler-tts-{Guid.NewGuid():N}.wav");
        var speed = (float)(_settings.TtsSpeed <= 0 ? 1.0 : _settings.TtsSpeed);
        await voice.Engine.SynthesizeWavFileAsync(text, wavPath, speed, cancellationToken);
        return wavPath;
    }

    private async Task PlayTtsAsync(string wavPath, CancellationToken cancellationToken)
    {
        StopTtsPlayback();
        _ttsTempFile = wavPath;
        _ttsReader = new AudioFileReader(wavPath);
        _ttsPlayer = new WaveOut();
        var finished = new TaskCompletionSource();
        _ttsPlayer.PlaybackStopped += (_, _) => finished.TrySetResult();
        _ttsPlayer.Init(_ttsReader);
        _ttsPlaying = true;
        SpeakButton.Content = "Stop speaking";
        SetStatus("Playing transcript…", LiveBrush);
        _ttsPlayer.Play();

        await using (cancellationToken.Register(StopTtsPlayback))
        {
            await finished.Task;
        }

        StopTtsPlayback();
    }

    private void StopTtsPlayback()
    {
        _ttsPlaying = false;
        try
        {
            _ttsPlayer?.Stop();
        }
        catch (Exception)
        {
            // ignored
        }

        _ttsPlayer?.Dispose();
        _ttsPlayer = null;
        _ttsReader?.Dispose();
        _ttsReader = null;
        if (_ttsTempFile is not null)
        {
            try
            {
                File.Delete(_ttsTempFile);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }

            _ttsTempFile = null;
        }

        SpeakButton.Content = "Speak transcript";
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        PersistSettings();
        TranscriptSpelling.Detach();
        _workCts?.Cancel();
        StopTtsPlayback();
        CleanupLive();
        KokoroTtsRuntime.Shared.Dispose();
        _vosk.Dispose();
        _apiGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task PrepareLocalModelAsync(CancellationToken cancellationToken)
    {
        var path = _settings.ModelPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Choose File → Transcription settings and download or browse to a Vosk model folder first.");
        }

        var resolved = VoskModelManager.FindModelFolder(path)
                       ?? throw new InvalidOperationException("The selected path is not a valid Vosk model folder. Update it in File → Transcription settings.");
        _settings.ModelPath = resolved;
        _settings.Save();
        ApplyCacheBudget();

        var plan = _vosk.PlanLoad(resolved);
        if (plan.AlreadyLoaded)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _vosk.EnsureLoaded(resolved);
            }, cancellationToken);
            RefreshCacheUi();
            return;
        }

        if (!plan.CanLoad)
        {
            throw new InvalidOperationException(plan.RefusalReason ?? "There is not enough memory to load that model.");
        }

        if (plan.NeedsConfirmation)
        {
            var confirm = MessageBox.Show(
                this,
                plan.ConfirmationMessage,
                "Load Vosk model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                throw new OperationCanceledException();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var evicted = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _vosk.EnsureLoaded(resolved);
        }, cancellationToken);

        RefreshCacheUi();
        if (evicted.Count > 0)
        {
            var message = FormatEviction(evicted, plan.DisplayName);
            ProcessingMessage.Text = message;
            SetStatus(message, ProcessingBrush);
        }
    }

    private void ApplyCacheBudget()
    {
        _settings.EnsureCacheBudget();
        _vosk.SetBudgetGigabytes(_settings.ModelCacheBudgetGb);
    }

    private void RefreshCacheUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshCacheUi);
            return;
        }

        var snapshot = _vosk.Snapshot();
        var used = snapshot.Sum(item => item.PrivateBytes);
        var budget = _vosk.BudgetBytes;
        var remaining = Math.Max(0, budget - used);
        var over = used > budget && snapshot.Count > 0;
        var countText = snapshot.Count switch
        {
            0 => "No models loaded",
            1 => "1 model loaded",
            _ => $"{snapshot.Count} models loaded"
        };
        var parts = new List<string> { countText };
        if (snapshot.Count > 0)
        {
            parts.Add($"{ProcessMemory.FormatBytes(used)} used");
        }

        if (over)
        {
            parts.Add("over budget");
        }
        else
        {
            parts.Add($"{ProcessMemory.FormatBytes(remaining)} free");
        }

        CacheSummaryText.Text = string.Join("  ·  ", parts);
        RefreshCacheBar(snapshot, budget, used);

        if (InstalledModelCombo.ItemsSource is IEnumerable<TranscriptionOption> items)
        {
            var current = items.ToList();
            if (current.Exists(item => item.Engine == "Local"))
            {
                var selectedPath = (InstalledModelCombo.SelectedItem as TranscriptionOption)?.ModelPath;
                var selectedEngine = (InstalledModelCombo.SelectedItem as TranscriptionOption)?.Engine;
                var updated = current
                    .Select(item => item with { InMemory = item.Engine == "Local" && _vosk.IsLoaded(item.ModelPath) })
                    .ToList();
                _suppressInstalledModelSelection = true;
                try
                {
                    InstalledModelCombo.ItemsSource = updated;
                    InstalledModelCombo.SelectedItem = updated.FirstOrDefault(item =>
                        item.Engine == selectedEngine
                        && string.Equals(item.ModelPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                        ?? updated.FirstOrDefault(MatchesCurrentEngine)
                        ?? updated.FirstOrDefault();
                }
                finally
                {
                    _suppressInstalledModelSelection = false;
                }
            }
        }

        RefreshLoadedList();
    }

    private void RefreshLoadedList()
    {
        var rows = _vosk.Snapshot().Select(ToLoadedRow).ToList();
        var used = rows.Sum(row => row.PrivateBytes);
        var budget = _vosk.BudgetBytes;
        var remaining = Math.Max(0, budget - used);
        LoadedModelsTotals.Text = rows.Count == 0
            ? $"{ProcessMemory.FormatBytes(remaining)} free of {ProcessMemory.FormatBytes(budget)}"
            : $"{ProcessMemory.FormatBytes(used)} used"
              + (used > budget ? " (over budget)" : $"  ·  {ProcessMemory.FormatBytes(remaining)} free");
        LoadedModelsList.ItemsSource = rows;
        LoadedModelsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadedModelsList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static LoadedModelRow ToLoadedRow(LoadedVoskModelInfo info) =>
        new()
        {
            ModelPath = info.Path,
            Title = info.DisplayName,
            MemoryText = ProcessMemory.FormatBytes(info.PrivateBytes),
            LastUsedText = FormatLastUsed(info),
            PrivateBytes = info.PrivateBytes,
            CanUnload = info.CanUnload
        };

    private static string FormatLastUsed(LoadedVoskModelInfo info)
    {
        if (info.InUse)
        {
            return "In use";
        }

        var ago = DateTime.UtcNow - info.LastUsedUtc;
        if (ago.TotalSeconds < 45)
        {
            return "Just now";
        }

        if (ago.TotalMinutes < 60)
        {
            var minutes = Math.Max(1, (int)ago.TotalMinutes);
            return minutes == 1 ? "1 min ago" : $"{minutes} min ago";
        }

        return info.LastUsedUtc.ToLocalTime().ToString("t");
    }

    private static string FormatEviction(IReadOnlyList<LoadedVoskModelInfo> evicted, string incomingDisplayName)
    {
        var first = evicted[0];
        var size = ProcessMemory.FormatBytes(first.PrivateBytes);
        if (evicted.Count == 1)
        {
            return $"Unloaded {first.DisplayName} ({size}) to load {incomingDisplayName}.";
        }

        return $"Unloaded {first.DisplayName} ({size}) and {evicted.Count - 1} other model(s) to load {incomingDisplayName}.";
    }

    private void RefreshCacheBar(IReadOnlyList<LoadedVoskModelInfo> snapshot, long budget, long used)
    {
        CacheBarGrid.Children.Clear();
        CacheBarGrid.ColumnDefinitions.Clear();

        var scale = Math.Max(budget, used);
        if (scale <= 0)
        {
            scale = 1;
        }

        var column = 0;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var model = snapshot[i];
            AddCacheSlice(
                column++,
                Math.Max(1, model.PrivateBytes),
                SliceBrush(i),
                $"{model.DisplayName} · {ProcessMemory.FormatBytes(model.PrivateBytes)}");
        }

        var free = Math.Max(0, scale - used);
        if (snapshot.Count == 0 || free > 0)
        {
            AddCacheSlice(
                column,
                Math.Max(1, free),
                CacheFreeBrush,
                snapshot.Count == 0
                    ? $"No models loaded · {ProcessMemory.FormatBytes(budget)} available"
                    : $"{ProcessMemory.FormatBytes(free)} free in cache");
        }
    }

    private void AddCacheSlice(int column, long weight, Brush fill, string tooltip)
    {
        CacheBarGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(weight, GridUnitType.Star)
        });
        var slice = new Border
        {
            Background = fill,
            ToolTip = tooltip
        };
        Grid.SetColumn(slice, column);
        CacheBarGrid.Children.Add(slice);
    }

    private static SolidColorBrush SliceBrush(int index)
    {
        var color = CacheSliceColors[index % CacheSliceColors.Length];
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void LoadedModelsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLoadedList();
        LoadedModelsPopup.IsOpen = !LoadedModelsPopup.IsOpen;
    }

    private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!LoadedModelsPopup.IsOpen || e.OriginalSource is not DependencyObject node)
        {
            return;
        }

        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, CachePanel)
                || ReferenceEquals(current, LoadedModelsButton)
                || ReferenceEquals(current, LoadedModelsPopup.Child))
            {
                return;
            }
        }

        LoadedModelsPopup.IsOpen = false;
    }

    private void UnloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        if (!_vosk.Unload(path, out var reason))
        {
            MessageBox.Show(
                this,
                reason ?? "That model could not be unloaded.",
                "Speech Handler",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetStatus($"Unloaded {VoskModelManager.DisplayLabel(path)}.", IdleBrush);
        RefreshCacheUi();
    }

    private sealed class LoadedModelRow
    {
        public required string ModelPath { get; init; }
        public required string Title { get; init; }
        public required string MemoryText { get; init; }
        public required string LastUsedText { get; init; }
        public long PrivateBytes { get; init; }
        public bool CanUnload { get; init; }
    }

    private async Task TranscribeFileLocalAsync(string path, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(path);
        var converted = Pcm16kMonoConverter.ToPcm16kMono(reader);
        using var session = _vosk.CreateSession();
        var buffer = new byte[4096];

        await Task.Run(() =>
        {
            int read;
            while ((read = converted.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Accept(buffer, read, out var final, out var partial))
                {
                    Dispatcher.Invoke(() => AppendFinal(final, formatAsSentences: true, timeOffsetSeconds: _sessionTimeOffset));
                }
                else
                {
                    Dispatcher.Invoke(() => SetPartial(partial));
                }
            }

            var last = session.Finish();
            Dispatcher.Invoke(() =>
            {
                AppendFinal(last, formatAsSentences: true, timeOffsetSeconds: _sessionTimeOffset);
                PartialText.Text = string.Empty;
            });
        }, cancellationToken);
    }

    private async Task TranscribeFileApiAsync(string path, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"speechhandler-{Guid.NewGuid():N}.wav");
        try
        {
            await Task.Run(() =>
            {
                using var reader = new AudioFileReader(path);
                var converted = Pcm16kMonoConverter.ToPcm16kMono(reader);
                WaveFileWriter.CreateWaveFile(temp, converted);
            }, cancellationToken);

            var wavBytes = await File.ReadAllBytesAsync(temp, cancellationToken);
            var result = await TranscribeCloudAsync(wavBytes, cancellationToken);
            AppendFinal(result, timeOffsetSeconds: _sessionTimeOffset);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort.
            }
        }
    }

    private void OnLivePcm(byte[] pcm)
    {
        if (!_isLive)
        {
            return;
        }

        try
        {
            string? partial = null;
            TranscriptionResult? final = null;
            bool accepted = false;
            var hasVosk = false;

            lock (_liveSync)
            {
                if (_liveVosk is not null)
                {
                    hasVosk = true;
                    accepted = _liveVosk.Accept(pcm, pcm.Length, out final, out partial);
                }
            }

            if (hasVosk)
            {
                if (accepted)
                {
                    Dispatcher.BeginInvoke(() => AppendFinal(final, formatAsSentences: true, timeOffsetSeconds: _sessionTimeOffset));
                }
                else
                {
                    Dispatcher.BeginInvoke(() => SetPartial(partial));
                }

                return;
            }

            MemoryStream? apiBuffer;
            lock (_liveSync)
            {
                apiBuffer = _liveApiBuffer;
            }

            if (apiBuffer is null)
            {
                return;
            }

            const int chunkBytes = Pcm16kMonoConverter.SampleRate * 2 * 4;
            byte[]? chunk = null;
            long offsetBytes = 0;
            lock (apiBuffer)
            {
                apiBuffer.Write(pcm, 0, pcm.Length);
                if (apiBuffer.Length >= chunkBytes)
                {
                    chunk = apiBuffer.ToArray();
                    offsetBytes = _liveApiPcmBytes;
                    _liveApiPcmBytes += chunk.Length;
                    apiBuffer.SetLength(0);
                    apiBuffer.Position = 0;
                }
            }

            if (chunk is not null)
            {
                var token = _workCts?.Token ?? CancellationToken.None;
                var offset = _sessionTimeOffset + PcmDurationSeconds(offsetBytes);
                _ = TranscribeApiChunkAsync(chunk, offset, token);
            }
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => ShowError("Live transcription failed.", ex));
            _ = Dispatcher.BeginInvoke(async () => await StopLiveAsync());
        }
    }

    private async Task TranscribeApiChunkAsync(byte[] pcm, double timeOffsetSeconds, CancellationToken cancellationToken)
    {
        await _apiGate.WaitAsync(cancellationToken);
        try
        {
            var result = await TranscribeCloudPcmAsync(pcm, cancellationToken);
            await Dispatcher.InvokeAsync(() => AppendFinal(result, timeOffsetSeconds: timeOffsetSeconds));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ShowError("A live API chunk failed.", ex));
        }
        finally
        {
            _apiGate.Release();
        }
    }

    private void OnCaptureStopped(Exception? error)
    {
        if (error is null || !_isLive)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            ShowError("Audio capture stopped unexpectedly.", error);
            _ = StopLiveAsync();
        });
    }

    private void CleanupLive(bool disposeSession = true)
    {
        _isLive = false;
        if (_capture is not null)
        {
            _capture.PcmAvailable -= OnLivePcm;
            _capture.Stopped -= OnCaptureStopped;
            _capture.Dispose();
            _capture = null;
        }

        if (disposeSession)
        {
            lock (_liveSync)
            {
                _liveVosk?.Dispose();
                _liveVosk = null;
                _liveApiBuffer?.Dispose();
                _liveApiBuffer = null;
            }
        }

        UpdateActionButtons();
    }

    private CancellationTokenSource BeginWork(string message, bool determinate = false, bool showOverlay = true)
    {
        _busy = true;
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        UpdateActionButtons();
        SetStatus(message, ProcessingBrush);
        if (showOverlay)
        {
            ProcessingMessage.Text = message;
            ProcessingBar.IsIndeterminate = !determinate;
            ProcessingBar.Value = 0;
            ProcessingOverlay.Visibility = Visibility.Visible;
        }

        return _workCts;
    }

    private void EndWork()
    {
        _busy = false;
        HideOverlay();
        UpdateActionButtons();
        PersistSettings();
    }

    private void HideOverlay() => ProcessingOverlay.Visibility = Visibility.Collapsed;

    private void UpdateActionButtons()
    {
        var idle = !_isLive && !_busy;
        SettingsMenuItem.IsEnabled = idle;
        TtsSettingsMenuItem.IsEnabled = idle;
        LanguageCombo.IsEnabled = idle;
        InstalledModelCombo.IsEnabled = idle;
        SourcesCombo.IsEnabled = idle;
        RefreshSourcesButton.IsEnabled = idle;
        StartLiveButton.IsEnabled = idle;
        StopLiveButton.IsEnabled = _isLive;
        ChooseFileButton.IsEnabled = idle;
        TranscribeFileButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(_selectedAudioFile);
        SaveAudioButton.IsEnabled = !_busy;
    }

    private void AppendFinal(TranscriptionResult? result, bool formatAsSentences = false, double timeOffsetSeconds = 0)
    {
        if (result is null || result.IsEmpty)
        {
            return;
        }

        var original = result.Text.Trim();
        var incoming = original;
        var words = result.Words.ToList();
        if (_lastFinalRaw is not null)
        {
            if (incoming.Equals(_lastFinalRaw, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Vosk's final flush can repeat the previous utterance and then continue.
            if (_lastFinalRaw.Length >= 16
                && incoming.StartsWith(_lastFinalRaw, StringComparison.OrdinalIgnoreCase))
            {
                var at = _lastFinalRaw.Length;
                if (at == incoming.Length || char.IsWhiteSpace(incoming[at]))
                {
                    incoming = incoming[at..].TrimStart();
                    words = TranscriptText.StripPrefixWords(words, _lastFinalRaw);
                    if (incoming.Length == 0)
                    {
                        return;
                    }
                }
            }
        }

        _lastFinalRaw = original;
        var prepared = TranscriptText.Prepare(TranscriptBox.Text, incoming, formatAsSentences);
        if (!string.Equals(TranscriptBox.Text, prepared.Existing, StringComparison.Ordinal))
        {
            TranscriptBox.Text = prepared.Existing;
        }

        if (prepared.Incoming.Length == 0)
        {
            PartialText.Text = string.Empty;
            return;
        }

        if (words.Count > 0)
        {
            words = TranscriptText.Offset(words, timeOffsetSeconds).ToList();
        }
        else
        {
            var start = _timedWords.Count > 0 ? _timedWords[^1].EndSeconds : timeOffsetSeconds;
            words = SrtFormatter.EstimateWords(
                prepared.Incoming,
                start,
                Math.Max(1.2, CountWords(prepared.Incoming) * 0.35)).ToList();
        }

        var preparedWords = TranscriptText.PrepareWords(_timedWords, words, prepared.Existing, formatAsSentences);
        if (preparedWords.Count > 0)
        {
            _timedWords.AddRange(preparedWords);
        }

        RefreshSrtBox();

        if (TranscriptBox.Text.Length > 0 && !char.IsWhiteSpace(TranscriptBox.Text[^1]))
        {
            TranscriptBox.AppendText(" ");
        }

        TranscriptBox.AppendText(prepared.Incoming);
        TranscriptBox.ScrollToEnd();
        PartialText.Text = string.Empty;
    }

    private void SetPartial(string? text)
    {
        PartialText.Text = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusText.Text = text;
        StatusDot.Fill = brush;
    }

    private void ValidateApiKey()
    {
        if (UseElevenLabs)
        {
            if (string.IsNullOrWhiteSpace(_elevenLabsKey))
            {
                throw new InvalidOperationException("Enter an ElevenLabs API key in File → Transcription settings, or set the ELEVENLABS_API_KEY environment variable.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Enter an OpenAI API key in File → Transcription settings, or set the OPENAI_API_KEY environment variable.");
        }
    }

    private Task<TranscriptionResult> TranscribeCloudAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        if (UseElevenLabs)
        {
            return _elevenLabs.TranscribeAsync(
                wavBytes,
                _elevenLabsKey,
                string.IsNullOrWhiteSpace(_settings.ElevenLabsModel) ? "scribe_v2" : _settings.ElevenLabsModel,
                cancellationToken);
        }

        return _openAi.TranscribeAsync(
            wavBytes,
            _apiKey,
            SelectedWhisperModel(),
            _settings.TranslateToEnglish,
            cancellationToken);
    }

    private Task<TranscriptionResult> TranscribeCloudPcmAsync(byte[] pcm16kMono, CancellationToken cancellationToken)
    {
        var wav = Pcm16kMonoConverter.ToWavBytes(pcm16kMono);
        return TranscribeCloudAsync(wav, cancellationToken);
    }

    private string SelectedWhisperModel()
    {
        if (_settings.TranslateToEnglish)
        {
            return "whisper-1";
        }

        return string.IsNullOrWhiteSpace(_settings.WhisperModel) ? "whisper-1" : _settings.WhisperModel;
    }

    private void ShowError(string title, Exception ex)
    {
        SetStatus(title, ErrorBrush);
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private bool SrtTabSelected => TranscriptViewTabs.SelectedIndex == 1;

    private void ApplyTranscriptLanguage(string? language)
    {
        TranscriptSpelling.ApplyLanguage(TranscriptBox, language);
        TranscriptSpelling.ApplyLanguage(SrtBox, language);
    }

    private void SyncSpellingCorrection(TextBox source, string original, string replacement, int occurrence)
    {
        var target = ReferenceEquals(source, TranscriptBox) ? SrtBox : TranscriptBox;
        var updated = SpellingSync.ReplaceOccurrence(
            target.Text,
            original,
            replacement,
            occurrence,
            skipSrtMetadata: ReferenceEquals(target, SrtBox));
        if (updated is not null && !string.Equals(updated, target.Text, StringComparison.Ordinal))
        {
            target.Text = updated;
        }

        SpellingSync.ReplaceTimedWord(_timedWords, original, replacement, occurrence);
    }

    private double NextSessionTimeOffset() =>
        _timedWords.Count == 0 ? 0 : _timedWords[^1].EndSeconds + 0.5;

    private static double PcmDurationSeconds(long byteCount) =>
        byteCount / (double)(Pcm16kMonoConverter.SampleRate * 2);

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void RefreshSrtBox()
    {
        SrtBox.Text = SrtFormatter.ToSrt(_timedWords);
        SrtBox.CaretIndex = SrtBox.Text.Length;
        SrtBox.ScrollToEnd();
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
