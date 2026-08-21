using System.Windows;
using System.Windows.Controls;
using SpeechHandler.Transcription;
using SpeechHandler.Tts;

namespace SpeechHandler;

internal partial class TtsSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _suppressEngineSelection;
    private bool _suppressVoiceSelection;
    private bool _downloading;
    private CancellationTokenSource? _downloadCts;

    public TtsSettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var voice = TtsVoiceCatalog.FindVoice(_settings.TtsVoiceId)
                    ?? TtsVoiceCatalog.FindVoice(TtsVoiceCatalog.DefaultVoiceId)
                    ?? TtsVoiceCatalog.Voices[0];

        _suppressEngineSelection = true;
        try
        {
            EngineCombo.ItemsSource = TtsVoiceCatalog.Engines;
            EngineCombo.SelectedItem = voice.EngineName;
        }
        finally
        {
            _suppressEngineSelection = false;
        }

        RefreshVoiceList(voice.Id);
        SpeedCombo.ItemsSource = TtsVoiceCatalog.Speeds;
        SpeedCombo.SelectedItem = TtsVoiceCatalog.ClosestSpeed(_settings.TtsSpeed);
        VoicesFolderText.Text = "Voices are stored in " + AppStorage.TtsVoicesDirectory;
        RefreshPackList(voice.PackId);
    }

    private void SaveToSettings()
    {
        _settings.TtsVoiceId = (VoiceCombo.SelectedItem as TtsVoiceOption)?.Id
                               ?? TtsVoiceCatalog.DefaultVoiceId;
        _settings.TtsSpeed = (SpeedCombo.SelectedItem as TtsSpeedOption)?.Value ?? 1.0;
        _settings.Save();
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressEngineSelection)
        {
            return;
        }

        RefreshVoiceList(preferredId: null);
        if (VoiceCombo.SelectedItem is TtsVoiceOption voice)
        {
            RefreshPackList(voice.PackId);
        }
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressVoiceSelection)
        {
            return;
        }

        if (VoiceCombo.SelectedItem is TtsVoiceOption voice)
        {
            RefreshPackList(voice.PackId);
        }
    }

    private void RefreshVoiceList(string? preferredId)
    {
        var engine = EngineCombo.SelectedItem as string ?? TtsVoiceCatalog.KokoroEngine;
        var voices = TtsVoiceCatalog.VoicesForEngine(engine);
        var selectedId = preferredId
                         ?? (VoiceCombo.SelectedItem as TtsVoiceOption)?.Id
                         ?? _settings.TtsVoiceId;

        _suppressVoiceSelection = true;
        try
        {
            VoiceCombo.ItemsSource = voices;
            VoiceCombo.SelectedItem = voices.FirstOrDefault(voice => voice.Id == selectedId)
                                      ?? voices.FirstOrDefault();
        }
        finally
        {
            _suppressVoiceSelection = false;
        }
    }

    private void RefreshPackList(string? preferredPackId)
    {
        var packs = TtsModelManager.ListPacks();
        var selectedId = preferredPackId
                         ?? (PackCombo.SelectedItem as TtsPackOption)?.Id
                         ?? TtsVoiceCatalog.KokoroPackId;
        PackCombo.ItemsSource = packs;
        PackCombo.SelectedItem = packs.FirstOrDefault(pack => pack.Id == selectedId)
                                 ?? packs.FirstOrDefault();
    }

    private async void DownloadPack_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || PackCombo.SelectedItem is not TtsPackOption pack)
        {
            return;
        }

        if (pack.ConfirmLargeDownload)
        {
            var confirm = MessageBox.Show(
                this,
                $"{pack.DisplayName} is about {pack.SizeLabel} and may take a few minutes. Continue?",
                "Download voice pack",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await RunDownloadAsync(
            $"Downloading {pack.DisplayName}…",
            (progress, status, token) => TtsModelManager.DownloadAsync(pack, progress, status, token));
    }

    private async void DownloadRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "Download Kokoro English (~340 MB) and Piper Lessac high (~113 MB)? This may take a few minutes.",
            "Download recommended voices",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await RunDownloadAsync(
            "Downloading recommended voices…",
            TtsModelManager.DownloadRecommendedAsync);
    }

    private async Task RunDownloadAsync(
        string message,
        Func<IProgress<double>, IProgress<string>, CancellationToken, Task> download)
    {
        _downloading = true;
        _downloadCts = new CancellationTokenSource();
        SetDownloadUi(true, message);
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadBar.IsIndeterminate = false;
                DownloadBar.Value = value;
            });
            var status = new Progress<string>(text => DownloadMessage.Text = text);
            await download(progress, status, _downloadCts.Token);
            RefreshPackList((PackCombo.SelectedItem as TtsPackOption)?.Id);
            DownloadMessage.Text = "Download finished.";
        }
        catch (OperationCanceledException)
        {
            DownloadMessage.Text = "Download canceled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not download the voice pack.",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _downloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            SetDownloadUi(false, string.Empty);
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }

        SaveToSettings();
        DialogResult = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _downloadCts?.Cancel();
        if (!_downloading)
        {
            SaveToSettings();
        }
    }

    private void SetDownloadUi(bool downloading, string message)
    {
        DownloadProgressPanel.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadMessage.Text = message;
        DownloadBar.IsIndeterminate = downloading;
        if (!downloading)
        {
            DownloadBar.Value = 0;
        }

        EngineCombo.IsEnabled = !downloading;
        VoiceCombo.IsEnabled = !downloading;
        SpeedCombo.IsEnabled = !downloading;
        PackCombo.IsEnabled = !downloading;
        DownloadButton.IsEnabled = !downloading;
        DownloadRecommendedButton.IsEnabled = !downloading;
        CloseButton.IsEnabled = !downloading;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }
}
