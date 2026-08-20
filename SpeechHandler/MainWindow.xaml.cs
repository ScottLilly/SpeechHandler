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

    public MainWindow()
    {
        InitializeComponent();
        VoskModelCombo.ItemsSource = VoskModelManager.EnglishModels;
        TtsVoiceCombo.ItemsSource = TtsVoiceCatalog.Voices;
        LoadSettingsIntoUi();
        RefreshSources();
        UpdateEnginePanels();
        SetStatus("Ready", IdleBrush);

        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            ApiKeyBox.Password = envKey;
        }
    }

    private bool UseLocalEngine => EngineCombo.SelectedIndex == 0;

    private void LoadSettingsIntoUi()
    {
        EngineCombo.SelectedIndex = string.Equals(_settings.Engine, "Api", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ModelPathBox.Text = !string.IsNullOrWhiteSpace(_settings.ModelPath)
            ? _settings.ModelPath
            : (VoskModelManager.LooksLikeModel(VoskModelManager.DefaultSmallEnglishPath)
                ? VoskModelManager.DefaultSmallEnglishPath
                : string.Empty);
        VoskModelCombo.SelectedItem = VoskModelManager.FindOptionForPath(ModelPathBox.Text)
                                      ?? VoskModelManager.EnglishModels[0];
        TtsVoiceCombo.SelectedItem = TtsVoiceCatalog.Voices.FirstOrDefault(v => v.Id == _settings.TtsVoiceId)
                                     ?? TtsVoiceCatalog.Voices[0];
        TranslateCheck.IsChecked = _settings.TranslateToEnglish;
        SelectComboItem(WhisperModelCombo, _settings.WhisperModel);
    }

    private void PersistSettings()
    {
        _settings.Engine = UseLocalEngine ? "Local" : "Api";
        _settings.ModelPath = ModelPathBox.Text.Trim();
        _settings.TranslateToEnglish = TranslateCheck.IsChecked == true;
        _settings.WhisperModel = SelectedComboText(WhisperModelCombo) ?? "whisper-1";
        _settings.TtsVoiceId = (TtsVoiceCombo.SelectedItem as TtsVoiceOption)?.Id ?? "lessac";
        _settings.Save();
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateEnginePanels();
    }

    private void UpdateEnginePanels()
    {
        LocalSettingsPanel.Visibility = UseLocalEngine ? Visibility.Visible : Visibility.Collapsed;
        ApiSettingsPanel.Visibility = UseLocalEngine ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private void RefreshSources()
    {
        var previousId = (SourcesList.SelectedItem as AudioInputSource)?.Id;
        var sources = AudioDeviceCatalog.ListSources();
        SourcesList.ItemsSource = sources;
        if (sources.Count == 0)
        {
            SetStatus("No audio input devices were found.", ErrorBrush);
            return;
        }

        var match = sources.FirstOrDefault(s => s.Id == previousId);
        SourcesList.SelectedItem = match ?? sources[0];
        if (!_isLive && !_busy)
        {
            SetStatus("Ready", IdleBrush);
        }
    }

    private async void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _isLive)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select a Vosk model folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var found = VoskModelManager.FindModelFolder(dialog.FolderName);
        if (found is null)
        {
            MessageBox.Show(
                this,
                "That folder does not look like a Vosk model. Choose the extracted model directory (it contains am, conf, or graph).",
                "Speech Handler",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ModelPathBox.Text = found;
        PersistSettings();
        await Task.CompletedTask;
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _isLive)
        {
            return;
        }

        if (VoskModelCombo.SelectedItem is not VoskModelOption model)
        {
            MessageBox.Show(this, "Select a Vosk model to download.", "Speech Handler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (model.ConfirmLargeDownload)
        {
            var confirm = MessageBox.Show(
                this,
                $"{model.DisplayName} is a large download and needs several gigabytes of disk space. Continue?",
                "Download Vosk model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var cts = BeginWork($"Downloading {model.FolderName}…", determinate: true);
        try
        {
            var progress = new Progress<double>(value =>
            {
                ProcessingBar.IsIndeterminate = false;
                ProcessingBar.Value = value;
                ProcessingMessage.Text = $"Downloading {model.FolderName}… {value:0}%";
            });

            var path = await VoskModelManager.DownloadAsync(model, progress, cts.Token);
            ModelPathBox.Text = path;
            PersistSettings();
            SetStatus($"{model.FolderName} is ready.", IdleBrush);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Download canceled.", IdleBrush);
        }
        catch (Exception ex)
        {
            ShowError("Could not download the Vosk model.", ex);
        }
        finally
        {
            EndWork();
        }
    }

    private async void StartLive_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        if (SourcesList.SelectedItem is not AudioInputSource source)
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

            _capture = new LiveAudioCapture(source.Id, source.Kind);
            _capture.PcmAvailable += OnLivePcm;
            _capture.Stopped += OnCaptureStopped;
            _capture.Start();

            _isLive = true;
            ProcessingMessage.Text = $"Processing live audio from {source.DisplayName}…";
            ProcessingBar.IsIndeterminate = true;
            ProcessingOverlay.Visibility = Visibility.Visible;
            SetLiveUi(true);
            SetStatus($"Processing live audio from {source.DisplayName}…", LiveBrush);
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
                AppendFinal(vosk.Finish());
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

    private async void TranscribeFile_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _busy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose an audio file",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.mp4;*.wma;*.flac;*.ogg;*.aac;*.webm|All files|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var cts = BeginWork("Processing audio file…");
        try
        {
            if (UseLocalEngine)
            {
                ProcessingMessage.Text = "Loading speech model…";
                await PrepareLocalModelAsync(cts.Token);
                ProcessingMessage.Text = "Processing audio file…";
                await TranscribeFileLocalAsync(dialog.FileName, cts.Token);
            }
            else
            {
                ValidateApiKey();
                await TranscribeFileApiAsync(dialog.FileName, cts.Token);
            }

            SetStatus($"Transcribed {Path.GetFileName(dialog.FileName)}.", IdleBrush);
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
                EngineCombo.IsEnabled = !_isLive;
                StartLiveButton.IsEnabled = !_isLive;
                SourcesList.IsEnabled = !_isLive;
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
        _workCts?.Cancel();
        StopTtsPlayback();
        CleanupLive();
        _vosk.Dispose();
        _apiGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task PrepareLocalModelAsync(CancellationToken cancellationToken)
    {
        var path = ModelPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Download or browse to a Vosk model folder first.");
        }

        var resolved = VoskModelManager.FindModelFolder(path)
                       ?? throw new InvalidOperationException("The selected path is not a valid Vosk model folder.");
        ModelPathBox.Text = resolved;

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
                    Dispatcher.Invoke(() => AppendFinal(final));
                }
                else
                {
                    Dispatcher.Invoke(() => SetPartial(partial));
                }
            }

            var last = session.Finish();
            Dispatcher.Invoke(() =>
            {
                AppendFinal(last);
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
            var text = await _openAi.TranscribeAsync(
                wavBytes,
                ApiKeyBox.Password,
                SelectedWhisperModel(),
                TranslateCheck.IsChecked == true,
                cancellationToken);
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
                    Dispatcher.BeginInvoke(() => AppendFinal(final));
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
            var text = await _openAi.TranscribePcmAsync(
                pcm,
                ApiKeyBox.Password,
                SelectedWhisperModel(),
                TranslateCheck.IsChecked == true,
                cancellationToken);
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

        SetLiveUi(false);
    }

    private CancellationTokenSource BeginWork(string message, bool determinate = false, bool showOverlay = true)
    {
        _busy = true;
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        EngineCombo.IsEnabled = false;
        StartLiveButton.IsEnabled = false;
        SourcesList.IsEnabled = false;
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
        EngineCombo.IsEnabled = true;
        StartLiveButton.IsEnabled = true;
        SourcesList.IsEnabled = true;
        SetLiveUi(false);
        PersistSettings();
    }

    private void HideOverlay() => ProcessingOverlay.Visibility = Visibility.Collapsed;

    private void SetLiveUi(bool listening)
    {
        StartLiveButton.IsEnabled = !listening && !_busy;
        StopLiveButton.IsEnabled = listening;
        EngineCombo.IsEnabled = !listening && !_busy;
        SourcesList.IsEnabled = !listening && !_busy;
    }

    private void AppendFinal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = text.Trim();
        if (TranscriptBox.Text.Length > 0 && !char.IsWhiteSpace(TranscriptBox.Text[^1]))
        {
            TranscriptBox.AppendText(" ");
        }

        TranscriptBox.AppendText(text);
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
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            throw new InvalidOperationException("Enter an OpenAI API key, or set the OPENAI_API_KEY environment variable.");
        }
    }

    private string SelectedWhisperModel()
    {
        var selected = SelectedComboText(WhisperModelCombo) ?? "whisper-1";
        if (TranslateCheck.IsChecked == true)
        {
            return "whisper-1";
        }

        return selected;
    }

    private void ShowError(string title, Exception ex)
    {
        SetStatus(title, ErrorBrush);
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string? SelectedComboText(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static void SelectComboItem(ComboBox combo, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
