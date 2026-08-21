using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SpeechHandler.Transcription;

namespace SpeechHandler;

internal partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _originalEngine;
    private readonly bool _originalTranslateToEnglish;
    private readonly string _originalWhisperModel;
    private readonly string _originalElevenLabsModel;
    private readonly int _originalCacheBudgetGb;
    private bool _suppressLanguageSelection;
    private bool _suppressModelSelection;
    private bool _downloading;
    private bool _committed;
    private CancellationTokenSource? _downloadCts;
    private string _modelsDirectory = string.Empty;

    public string OpenAiKey { get; private set; }
    public string ElevenLabsKey { get; private set; }

    public SettingsWindow(AppSettings settings, string openAiKey, string elevenLabsKey)
    {
        _settings = settings;
        OpenAiKey = openAiKey;
        ElevenLabsKey = elevenLabsKey;
        _originalEngine = settings.Engine;
        _originalTranslateToEnglish = settings.TranslateToEnglish;
        _originalWhisperModel = settings.WhisperModel;
        _originalElevenLabsModel = settings.ElevenLabsModel;
        _originalCacheBudgetGb = settings.ModelCacheBudgetGb;
        InitializeComponent();
        DataObject.AddPastingHandler(CacheBudgetBox, CacheBudgetBox_Pasting);
        LoadFromSettings();
        UpdateEnginePanels();
    }

    private int EngineIndex => EngineCombo.SelectedIndex;

    private void LoadFromSettings()
    {
        VoskModelManager.EnsurePaths(_settings);
        EngineCombo.SelectedIndex = _settings.Engine switch
        {
            "Api" => 1,
            "ElevenLabs" => 2,
            _ => 0
        };
        SetModelsDirectory(_settings.ModelsDirectory);
        RefreshVoskModelList();
        TranslateCheck.IsChecked = _settings.TranslateToEnglish;
        SelectComboItem(WhisperModelCombo, _settings.WhisperModel);
        SelectComboItem(ElevenLabsModelCombo, _settings.ElevenLabsModel);
        RefreshBudgetUi();
        ApiKeyBox.Password = OpenAiKey;
        ElevenLabsApiKeyBox.Password = ElevenLabsKey;
    }

    private void SaveToSettings()
    {
        _settings.Engine = EngineIndex switch
        {
            1 => "Api",
            2 => "ElevenLabs",
            _ => "Local"
        };
        _settings.ModelsDirectory = ModelsDirectoryPath;
        _settings.TranslateToEnglish = TranslateCheck.IsChecked == true;
        _settings.WhisperModel = SelectedComboText(WhisperModelCombo) ?? "whisper-1";
        _settings.ElevenLabsModel = SelectedComboText(ElevenLabsModelCombo) ?? "scribe_v2";
        TryCommitCacheBudget(showError: false);
        OpenAiKey = ApiKeyBox.Password;
        ElevenLabsKey = ElevenLabsApiKeyBox.Password;
        _settings.Save();
    }

    private string ModelsDirectoryPath => _modelsDirectory;

    private void SetModelsDirectory(string? path)
    {
        _modelsDirectory = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path.Trim());

        if (string.IsNullOrWhiteSpace(_modelsDirectory))
        {
            ModelPathText.Text = "No folder selected";
            ModelPathText.ToolTip = null;
            return;
        }

        ModelPathText.Text = Path.GetFileName(
            _modelsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ModelPathText.ToolTip = _modelsDirectory;
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
        var current = VoskModelManager.FindOptionForPath(_settings.ModelPath);
        var selectedLanguage = (VoskLanguageCombo.SelectedItem as string)
                               ?? current?.Language
                               ?? VoskModelManager.DefaultLanguage;
        var selectedId = (VoskModelCombo.SelectedItem as VoskModelOption)?.Id ?? current?.Id;

        _suppressLanguageSelection = true;
        _suppressModelSelection = true;
        try
        {
            VoskLanguageCombo.ItemsSource = VoskModelManager.Languages;
            VoskLanguageCombo.SelectedItem = VoskModelManager.Languages.Contains(selectedLanguage)
                ? selectedLanguage
                : VoskModelManager.DefaultLanguage;

            var language = VoskLanguageCombo.SelectedItem as string ?? VoskModelManager.DefaultLanguage;
            var models = VoskModelManager.ModelsForLanguage(language, ModelsDirectoryPath);
            VoskModelCombo.ItemsSource = models;
            VoskModelCombo.SelectedItem = models.FirstOrDefault(model => model.Id == selectedId)
                                          ?? models.FirstOrDefault();
        }
        finally
        {
            _suppressLanguageSelection = false;
            _suppressModelSelection = false;
        }
    }

    private void VoskLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressLanguageSelection)
        {
            return;
        }

        RefreshVoskModelList();
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

        var installed = VoskModelManager.FindInstalledPath(model, ModelsDirectoryPath);
        if (installed is not null)
        {
            _settings.ModelPath = installed;
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
            Title = "Select a folder for Vosk models",
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
        var current = ModelsDirectoryPath;
        if (!string.IsNullOrWhiteSpace(current)
            && string.Equals(current, selected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current) || !HasModelFiles(current))
        {
            ApplySelectedModelsFolder(selected, current, moving: false);
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

            ApplySelectedModelsFolder(selected, current, moving: true);
            return;
        }

        ApplySelectedModelsFolder(selected, current, moving: false);
    }

    private string? ResolveBrowseStartFolder()
    {
        if (!string.IsNullOrWhiteSpace(ModelsDirectoryPath) && Directory.Exists(ModelsDirectoryPath))
        {
            return ModelsDirectoryPath;
        }

        var parent = Path.GetDirectoryName(ModelsDirectoryPath);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            return parent;
        }

        return Directory.Exists(AppStorage.ModelsDirectory) ? AppStorage.ModelsDirectory : null;
    }

    private void ApplySelectedModelsFolder(string selected, string? previous, bool moving)
    {
        Directory.CreateDirectory(selected);
        if (moving && !string.IsNullOrWhiteSpace(previous))
        {
            RemapModelPath(previous, selected);
        }

        SetModelsDirectory(selected);
        RefreshVoskModelList();
        SaveToSettings();
    }

    private void RemapModelPath(string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(_settings.ModelPath))
        {
            return;
        }

        var full = Path.GetFullPath(_settings.ModelPath);
        var oldPrefix = oldRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (full.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(oldRoot, full);
            _settings.ModelPath = Path.GetFullPath(Path.Combine(newRoot, relative));
        }
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

        if (string.IsNullOrWhiteSpace(ModelsDirectoryPath))
        {
            MessageBox.Show(this, "Choose a models folder before downloading.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (model.ConfirmLargeDownload)
        {
            var confirm = MessageBox.Show(
                this,
                $"{model.Language} · {model.DisplayName} is a large download and may need a lot of disk space. Continue?",
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

            var path = await VoskModelManager.DownloadAsync(
                model, ModelsDirectoryPath, progress, _downloadCts.Token);
            _settings.ModelPath = path;
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

        if (!TryCommitCacheBudget(showError: true))
        {
            return;
        }

        SaveToSettings();
        _committed = true;
        DialogResult = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _downloadCts?.Cancel();
        if (_committed)
        {
            return;
        }

        _settings.Engine = _originalEngine;
        _settings.TranslateToEnglish = _originalTranslateToEnglish;
        _settings.WhisperModel = _originalWhisperModel;
        _settings.ElevenLabsModel = _originalElevenLabsModel;
        _settings.ModelCacheBudgetGb = _originalCacheBudgetGb;
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
        CacheBudgetBox.IsEnabled = !downloading;
        CacheBudgetMinus.IsEnabled = !downloading;
        CacheBudgetPlus.IsEnabled = !downloading;
        VoskLanguageCombo.IsEnabled = !downloading;
        VoskModelCombo.IsEnabled = !downloading;
        CloseButton.IsEnabled = !downloading;
        CancelDownloadButton.IsEnabled = true;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshBudgetUi()
    {
        _settings.EnsureCacheBudget();
        var total = ProcessMemory.TotalPhysicalBytes;
        var suggested = ProcessMemory.SuggestedBudgetGigabytes();
        var max = ProcessMemory.MaxBudgetGigabytes();
        PhysicalRamText.Text = total > 0
            ? $"This computer has {ProcessMemory.FormatBytes(total)} of RAM."
            : "Could not read the amount of RAM on this computer.";
        SuggestedRamText.Text =
            $"Suggested cache: {suggested} GB ({ProcessMemory.AutoBudgetReason()}).";
        CacheBudgetHint.Text =
            $"Enter a whole number from {ProcessMemory.MinBudgetGigabytes} to {max} GB. "
            + "The upper limit leaves memory for Windows and other apps.";
        CacheBudgetBox.Text = _settings.ModelCacheBudgetGb.ToString();
    }

    private void CacheBudgetMinus_Click(object sender, RoutedEventArgs e) =>
        AdjustCacheBudget(-1);

    private void CacheBudgetPlus_Click(object sender, RoutedEventArgs e) =>
        AdjustCacheBudget(1);

    private void AdjustCacheBudget(int delta)
    {
        var current = TryReadCacheBudget(out var gigabytes)
            ? gigabytes
            : _settings.ModelCacheBudgetGb;
        CacheBudgetBox.Text = ProcessMemory.ClampBudgetGigabytes(current + delta).ToString();
    }

    private void CacheBudgetBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Length == 0 || !e.Text.All(char.IsDigit);

    private void CacheBudgetBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            e.Handled = true;
        }
    }

    private static void CacheBudgetBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string)))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(typeof(string)) as string;
        if (string.IsNullOrEmpty(text) || !text.All(char.IsDigit))
        {
            e.CancelCommand();
        }
    }

    private void CacheBudgetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryCommitCacheBudget(showError: false))
        {
            CacheBudgetBox.Text = ProcessMemory.ClampBudgetGigabytes(
                _settings.ModelCacheBudgetGb <= 0
                    ? ProcessMemory.SuggestedBudgetGigabytes()
                    : _settings.ModelCacheBudgetGb).ToString();
        }
    }

    private bool TryCommitCacheBudget(bool showError)
    {
        var max = ProcessMemory.MaxBudgetGigabytes();
        if (!TryReadCacheBudget(out var gigabytes))
        {
            if (showError)
            {
                MessageBox.Show(
                    this,
                    "Enter a whole number of gigabytes for the cache limit.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return false;
        }

        if (gigabytes < ProcessMemory.MinBudgetGigabytes)
        {
            if (showError)
            {
                MessageBox.Show(
                    this,
                    $"The cache limit must be at least {ProcessMemory.MinBudgetGigabytes} GB.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return false;
        }

        if (gigabytes > max)
        {
            if (showError)
            {
                MessageBox.Show(
                    this,
                    $"The cache limit cannot exceed {max} GB on this computer. A higher amount would not leave enough memory for Windows and other apps.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return false;
        }

        _settings.ModelCacheBudgetGb = gigabytes;
        CacheBudgetBox.Text = gigabytes.ToString();
        return true;
    }

    private bool TryReadCacheBudget(out int gigabytes) =>
        int.TryParse(CacheBudgetBox.Text.Trim(), out gigabytes);

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
