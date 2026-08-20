using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SpeechHandler.Transcription;

namespace SpeechHandler;

internal partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _suppressModelSelection;
    private bool _downloading;
    private CancellationTokenSource? _downloadCts;
    private string _modelFolderPath = string.Empty;

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
            VoskModelCombo.SelectedItem = VoskModelManager.FindOptionForPath(ModelFolderPath)
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

    private string ModelFolderPath => _modelFolderPath;

    private void SetModelPath(string? path)
    {
        _modelFolderPath = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path.Trim());

        if (string.IsNullOrWhiteSpace(_modelFolderPath))
        {
            ModelPathText.Text = "No folder selected";
            ModelPathText.ToolTip = null;
            return;
        }

        ModelPathText.Text = Path.GetFileName(
            _modelFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ModelPathText.ToolTip = _modelFolderPath;
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

        var startFolder = ResolveBrowseStartFolder();
        if (!string.IsNullOrWhiteSpace(startFolder))
        {
            dialog.InitialDirectory = startFolder;
            dialog.DefaultDirectory = startFolder;
            dialog.FolderName = startFolder;
        }

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var selected = Path.GetFullPath(dialog.FolderName);
        var current = ModelFolderPath;
        if (!string.IsNullOrWhiteSpace(current)
            && string.Equals(current, selected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current) || !HasModelFiles(current))
        {
            ApplySelectedModelFolder(selected, moving: false);
            return;
        }

        var folderName = Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var choice = MessageBox.Show(
            this,
            $"Do you want to move the current model files into “{folderName}”?\n\n"
            + "Yes — move the files and use the new folder.\n"
            + "No — use the new folder without moving files.\n"
            + "Cancel — keep the current folder.",
            "Move model files?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel)
        {
            return;
        }

        if (choice == MessageBoxResult.Yes)
        {
            try
            {
                MoveModelFiles(current, selected);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not move the model files.",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ApplySelectedModelFolder(selected, moving: true);
            return;
        }

        ApplySelectedModelFolder(selected, moving: false);
    }

    private string? ResolveBrowseStartFolder()
    {
        if (!string.IsNullOrWhiteSpace(ModelFolderPath) && Directory.Exists(ModelFolderPath))
        {
            return ModelFolderPath;
        }

        var parent = Path.GetDirectoryName(ModelFolderPath);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            return parent;
        }

        return Directory.Exists(AppStorage.ModelsDirectory) ? AppStorage.ModelsDirectory : null;
    }

    private void ApplySelectedModelFolder(string selected, bool moving)
    {
        var found = VoskModelManager.FindModelFolder(selected);
        if (found is null)
        {
            if (moving)
            {
                MessageBox.Show(
                    this,
                    "The files were moved, but that folder does not look like a Vosk model.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                SetModelPath(selected);
                SaveToSettings();
                return;
            }

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

    private static bool HasModelFiles(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void MoveModelFiles(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The new folder is inside the current model folder, so the files cannot be moved there.");
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(directory));
            if (Directory.Exists(destDir))
            {
                throw new IOException($"The destination already contains a folder named “{Path.GetFileName(directory)}”.");
            }

            Directory.Move(directory, destDir);
        }

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(destFile))
            {
                throw new IOException($"The destination already contains a file named “{Path.GetFileName(file)}”.");
            }

            File.Move(file, destFile);
        }
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
