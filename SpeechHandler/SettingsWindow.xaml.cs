using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SpeechHandler.Transcription;

namespace SpeechHandler;

internal partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _suppressModelSelection;
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;

    public string OpenAiKey { get; private set; }
    public string ElevenLabsKey { get; private set; }

    public SettingsWindow(AppSettings settings, string openAiKey, string elevenLabsKey)
    {
        _settings = settings;
        OpenAiKey = openAiKey;
        ElevenLabsKey = elevenLabsKey;
        InitializeComponent();
        RefreshVoskModelList();
        LoadFromSettings();
        UpdateEnginePanels();
    }

    private int EngineIndex => EngineCombo.SelectedIndex;

    private void LoadFromSettings()
    {
        _suppressModelSelection = true;
        try
        {
            EngineCombo.SelectedIndex = _settings.Engine switch
            {
                "Api" => 1,
                "ElevenLabs" => 2,
                _ => 0
            };
            SetModelPath(!string.IsNullOrWhiteSpace(_settings.ModelPath)
                ? _settings.ModelPath
                : (VoskModelManager.LooksLikeModel(VoskModelManager.DefaultSmallEnglishPath)
                    ? VoskModelManager.DefaultSmallEnglishPath
                    : string.Empty));
            VoskModelCombo.SelectedItem = VoskModelManager.FindOptionForPath(ModelPathText.Text)
                                          ?? VoskModelManager.EnglishModels[0];
            TranslateCheck.IsChecked = _settings.TranslateToEnglish;
            SelectComboItem(WhisperModelCombo, _settings.WhisperModel);
            SelectComboItem(ElevenLabsModelCombo, _settings.ElevenLabsModel);
            ApiKeyBox.Password = OpenAiKey;
            ElevenLabsApiKeyBox.Password = ElevenLabsKey;
        }
        finally
        {
            _suppressModelSelection = false;
        }
    }

    private void SaveToSettings()
    {
        _settings.Engine = EngineIndex switch
        {
            1 => "Api",
            2 => "ElevenLabs",
            _ => "Local"
        };
        _settings.ModelPath = ModelFolderPath;
        _settings.TranslateToEnglish = TranslateCheck.IsChecked == true;
        _settings.WhisperModel = SelectedComboText(WhisperModelCombo) ?? "whisper-1";
        _settings.ElevenLabsModel = SelectedComboText(ElevenLabsModelCombo) ?? "scribe_v2";
        OpenAiKey = ApiKeyBox.Password;
        ElevenLabsKey = ElevenLabsApiKeyBox.Password;
        _settings.Save();
    }

    private string ModelFolderPath =>
        string.Equals(ModelPathText.Text, "No folder selected", StringComparison.Ordinal)
            ? string.Empty
            : ModelPathText.Text.Trim();

    private void SetModelPath(string? path)
    {
        ModelPathText.Text = string.IsNullOrWhiteSpace(path) ? "No folder selected" : path;
        ModelPathText.ToolTip = string.IsNullOrWhiteSpace(path) ? null : path;
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
        LocalSettingsPanel.Visibility = EngineIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApiSettingsPanel.Visibility = EngineIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ElevenLabsSettingsPanel.Visibility = EngineIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshVoskModelList()
    {
        var selectedId = (VoskModelCombo.SelectedItem as VoskModelOption)?.Id
                         ?? VoskModelManager.FindOptionForPath(ModelFolderPath)?.Id;
        _suppressModelSelection = true;
        try
        {
            VoskModelCombo.ItemsSource = null;
            VoskModelCombo.ItemsSource = VoskModelManager.EnglishModels;
            VoskModelCombo.SelectedItem = VoskModelManager.EnglishModels.FirstOrDefault(model => model.Id == selectedId)
                                          ?? VoskModelManager.EnglishModels[0];
        }
        finally
        {
            _suppressModelSelection = false;
        }
    }

    private void VoskModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressModelSelection)
        {
            return;
        }

        if (VoskModelCombo.SelectedItem is not VoskModelOption model)
        {
            return;
        }

        var installed = VoskModelManager.FindInstalledPath(model);
        if (installed is not null)
        {
            SetModelPath(installed);
            SaveToSettings();
        }
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
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
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetModelPath(found);
        SaveToSettings();
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }

        if (VoskModelCombo.SelectedItem is not VoskModelOption model)
        {
            MessageBox.Show(this, "Select a Vosk model to download.", "Settings",
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

        _downloading = true;
        _downloadCts = new CancellationTokenSource();
        SetDownloadUi(true, $"Downloading {model.FolderName}…");
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadBar.IsIndeterminate = false;
                DownloadBar.Value = value;
                DownloadMessage.Text = $"Downloading {model.FolderName}… {value:0}%";
            });

            var path = await VoskModelManager.DownloadAsync(model, progress, _downloadCts.Token);
            SetModelPath(path);
            RefreshVoskModelList();
            SaveToSettings();
            DownloadMessage.Text = $"{model.FolderName} is ready.";
        }
        catch (OperationCanceledException)
        {
            DownloadMessage.Text = "Download canceled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not download the Vosk model.",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _downloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            SetDownloadUi(false, DownloadMessage.Text);
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
        DownloadProgressPanel.Visibility = downloading || !string.IsNullOrWhiteSpace(message)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DownloadMessage.Text = message;
        DownloadBar.IsIndeterminate = downloading;
        if (!downloading)
        {
            DownloadBar.Value = 0;
        }

        DownloadButton.IsEnabled = !downloading;
        BrowseButton.IsEnabled = !downloading;
        EngineCombo.IsEnabled = !downloading;
        VoskModelCombo.IsEnabled = !downloading;
        CloseButton.IsEnabled = !downloading;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
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
}
