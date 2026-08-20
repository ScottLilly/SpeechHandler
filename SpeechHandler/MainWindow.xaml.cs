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

    private const string OpenAiLanguage = "OpenAI Whisper";
    private const string ElevenLabsLanguage = "ElevenLabs";

    public MainWindow()
    {
        InitializeComponent();
        TranscriptSpelling.Attach(TranscriptBox);
        TtsVoiceCombo.ItemsSource = TtsVoiceCatalog.Voices;
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
        SetStatus("Ready", IdleBrush);
    }

    private bool UseLocalEngine =>
        string.Equals(_settings.Engine, "Local", StringComparison.OrdinalIgnoreCase);

    private bool UseElevenLabs =>
        string.Equals(_settings.Engine, "ElevenLabs", StringComparison.OrdinalIgnoreCase);

    private void LoadSettingsIntoUi()
    {
        TtsVoiceCombo.SelectedItem = TtsVoiceCatalog.Voices.FirstOrDefault(v => v.Id == _settings.TtsVoiceId)
                                     ?? TtsVoiceCatalog.Voices[0];
        if (VoskModelManager.EnsurePaths(_settings))
        {
            _settings.Save();
        }
    }

    private void PersistSettings()
    {
        _settings.TtsVoiceId = (TtsVoiceCombo.SelectedItem as TtsVoiceOption)?.Id ?? "lessac";
        _settings.Save();
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
        RefreshEngineSelector();
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
            TranscriptSpelling.ApplyLanguage(TranscriptBox, language);
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
                items.Add(new TranscriptionOption("Local", model.DisplayName, model.Path));
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
            TranscriptSpelling.ApplyLanguage(TranscriptBox, language);
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
                AppendFinal(vosk.Finish(), formatAsSentences: true);
                vosk.Dispose();
            }
            else if (apiBuffer is { Length: > 0 })
            {
                var pcm = apiBuffer.ToArray();
                apiBuffer.Dispose();
                await TranscribeApiChunkAsync(pcm, _workCts?.Token ?? CancellationToken.None);
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
        try
        {
            if (UseLocalEngine)
            {
                ProcessingMessage.Text = "Loading speech model…";
                await PrepareLocalModelAsync(cts.Token);
                ProcessingMessage.Text = "Processing audio file…";
                await TranscribeFileLocalAsync(path, cts.Token);
            }
            else
            {
                ValidateApiKey();
                await TranscribeFileApiAsync(path, cts.Token);
            }

            SetStatus($"Transcribed {Path.GetFileName(path)}.", IdleBrush);
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
        var text = TranscriptBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Clipboard.SetText(text);
        SetStatus("Copied transcript to the clipboard.", IdleBrush);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save transcript",
            Filter = "Text files|*.txt|All files|*.*",
            FileName = $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, TranscriptBox.Text, Encoding.UTF8);
        SetStatus("Saved transcript.", IdleBrush);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Clear();
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

        var text = TranscriptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "There is no transcript to speak yet.", "Speech Handler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TtsVoiceCombo.SelectedItem is not TtsVoiceOption voice)
        {
            MessageBox.Show(this, "Select a voice first.", "Speech Handler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PersistSettings();
        var cts = BeginWork("Preparing neural voice…");
        SpeakButton.Content = "Stop speaking";
        try
        {
            var status = new Progress<string>(message =>
            {
                ProcessingMessage.Text = message;
                SetStatus(message, ProcessingBrush);
            });

            await voice.Engine.EnsureReadyAsync(status, cts.Token);
            ProcessingMessage.Text = "Generating speech…";
            SetStatus("Generating speech…", ProcessingBrush);

            var wavPath = Path.Combine(Path.GetTempPath(), $"speechhandler-tts-{Guid.NewGuid():N}.wav");
            await voice.Engine.SynthesizeWavFileAsync(text, wavPath, cts.Token);
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
        _vosk.Dispose();
        _apiGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task PrepareLocalModelAsync(CancellationToken cancellationToken)
    {
        var path = _settings.ModelPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Choose File → Settings and download or browse to a Vosk model folder first.");
        }

        var resolved = VoskModelManager.FindModelFolder(path)
                       ?? throw new InvalidOperationException("The selected path is not a valid Vosk model folder. Update it in File → Settings.");
        _settings.ModelPath = resolved;
        _settings.Save();

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _vosk.EnsureLoaded(resolved);
        }, cancellationToken);
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
                    Dispatcher.Invoke(() => AppendFinal(final, formatAsSentences: true));
                }
                else
                {
                    Dispatcher.Invoke(() => SetPartial(partial));
                }
            }

            var last = session.Finish();
            Dispatcher.Invoke(() =>
            {
                AppendFinal(last, formatAsSentences: true);
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
            var text = await TranscribeCloudAsync(wavBytes, cancellationToken);
            AppendFinal(text);
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
            string? final = null;
            string? partial = null;
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
                    Dispatcher.BeginInvoke(() => AppendFinal(final, formatAsSentences: true));
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
            lock (apiBuffer)
            {
                apiBuffer.Write(pcm, 0, pcm.Length);
                if (apiBuffer.Length >= chunkBytes)
                {
                    chunk = apiBuffer.ToArray();
                    apiBuffer.SetLength(0);
                    apiBuffer.Position = 0;
                }
            }

            if (chunk is not null)
            {
                var token = _workCts?.Token ?? CancellationToken.None;
                _ = TranscribeApiChunkAsync(chunk, token);
            }
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => ShowError("Live transcription failed.", ex));
            _ = Dispatcher.BeginInvoke(async () => await StopLiveAsync());
        }
    }

    private async Task TranscribeApiChunkAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        await _apiGate.WaitAsync(cancellationToken);
        try
        {
            var text = await TranscribeCloudPcmAsync(pcm, cancellationToken);
            await Dispatcher.InvokeAsync(() => AppendFinal(text));
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
        LanguageCombo.IsEnabled = idle;
        InstalledModelCombo.IsEnabled = idle;
        SourcesCombo.IsEnabled = idle;
        RefreshSourcesButton.IsEnabled = idle;
        StartLiveButton.IsEnabled = idle;
        StopLiveButton.IsEnabled = _isLive;
        ChooseFileButton.IsEnabled = idle;
        TranscribeFileButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(_selectedAudioFile);
    }

    private void AppendFinal(string? text, bool formatAsSentences = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var original = text.Trim();
        var incoming = original;
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
                throw new InvalidOperationException("Enter an ElevenLabs API key in File → Settings, or set the ELEVENLABS_API_KEY environment variable.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Enter an OpenAI API key in File → Settings, or set the OPENAI_API_KEY environment variable.");
        }
    }

    private Task<string> TranscribeCloudAsync(byte[] wavBytes, CancellationToken cancellationToken)
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

    private Task<string> TranscribeCloudPcmAsync(byte[] pcm16kMono, CancellationToken cancellationToken)
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

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
