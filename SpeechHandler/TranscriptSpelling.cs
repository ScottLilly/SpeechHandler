using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using SpeechHandler.Transcription;

namespace SpeechHandler;

internal static class TranscriptSpelling
{
    private static readonly object Sync = new();
    private static readonly Guid SpellCheckerFactoryClsid = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private static readonly List<(string Path, string Tag)> Registrations = [];

    private static readonly List<AttachedBox> Attached = [];

    private static string _currentLanguage = "English (US)";

    public static Action<TextBox, string, string, int>? WordCorrected { get; set; }

    public static void Attach(TextBox box, bool skipSrtMetadata = false)
    {
        MigrateLegacyDictionary();
        SpellCheck.SetIsEnabled(box, true);
        if (box.ContextMenu is null)
        {
            box.ContextMenu = CreateMenu(box);
        }

        box.ContextMenuOpening -= OnContextMenuOpening;
        box.ContextMenuOpening += OnContextMenuOpening;
        if (!Attached.Any(item => ReferenceEquals(item.Box, box)))
        {
            Attached.Add(new AttachedBox(box, skipSrtMetadata));
        }
    }

    public static void ApplyLanguage(TextBox box, string? transcriptionLanguage)
    {
        var language = string.IsNullOrWhiteSpace(transcriptionLanguage)
            ? "English (US)"
            : transcriptionLanguage.Trim();
        var tag = ToLanguageTag(language);
        try
        {
            box.Language = XmlLanguage.GetLanguage(tag);
        }
        catch (ArgumentException)
        {
            tag = "en-US";
            box.Language = XmlLanguage.GetLanguage(tag);
        }

        var path = DictionaryPath(language);
        UnregisterIfChanged(path, tag);
        lock (Sync)
        {
            _currentLanguage = language;
            EnsureDictionaryFile(path);
        }

        SyncWordsToWindows(path, tag);
    }

    public static void Detach()
    {
        WordCorrected = null;
        foreach (var attached in Attached)
        {
            attached.Box.ContextMenuOpening -= OnContextMenuOpening;
        }

        Attached.Clear();

        (string Path, string Tag)[] registrations;
        lock (Sync)
        {
            registrations = Registrations.ToArray();
            Registrations.Clear();
        }

        foreach (var registration in registrations)
        {
            TryUnregisterDictionary(registration.Path, registration.Tag);
        }
    }

    private static ContextMenu CreateMenu(TextBox box)
    {
        return new ContextMenu
        {
            Background = (System.Windows.Media.Brush)box.FindResource("CardBg"),
            Foreground = (System.Windows.Media.Brush)box.FindResource("TextPrimary"),
            BorderBrush = (System.Windows.Media.Brush)box.FindResource("CardBorder"),
            BorderThickness = new Thickness(1)
        };
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox box || box.ContextMenu is null)
        {
            return;
        }

        var menu = box.ContextMenu;
        menu.Items.Clear();

        var index = ResolveCharacterIndex(box, e);
        var error = index >= 0 ? box.GetSpellingError(index) : null;
        if (error is not null)
        {
            var start = box.GetSpellingErrorStart(index);
            var length = box.GetSpellingErrorLength(index);
            var word = start >= 0 && length > 0 && start + length <= box.Text.Length
                ? box.Text.Substring(start, length)
                : string.Empty;
            if (word.Any(char.IsLetter))
            {
                var addedSuggestion = false;
                var skipSrtMetadata = Attached.Any(item =>
                    ReferenceEquals(item.Box, box) && item.SkipSrtMetadata);
                foreach (var suggestion in error.Suggestions)
                {
                    var item = new MenuItem { Header = suggestion };
                    var replacement = suggestion;
                    item.Click += (_, _) => CorrectWord(box, error, word, replacement, start, skipSrtMetadata);
                    menu.Items.Add(item);
                    addedSuggestion = true;
                }

                if (!addedSuggestion)
                {
                    menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
                }

                menu.Items.Add(new Separator());

                var ignore = new MenuItem { Header = "Ignore All" };
                ignore.Click += (_, _) => error.IgnoreAll();
                menu.Items.Add(ignore);

                var add = new MenuItem { Header = "Add to Dictionary", IsEnabled = word.Length > 0 };
                add.Click += (_, _) => AddWord(box, word);
                menu.Items.Add(add);
                menu.Items.Add(new Separator());
            }
        }

        menu.Items.Add(CommandItem("Cu_t", ApplicationCommands.Cut, box));
        menu.Items.Add(CommandItem("_Copy", ApplicationCommands.Copy, box));
        menu.Items.Add(CommandItem("_Paste", ApplicationCommands.Paste, box));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("Select _All", ApplicationCommands.SelectAll, box));
    }

    private static MenuItem CommandItem(string header, RoutedUICommand command, TextBox box) =>
        new()
        {
            Header = header,
            Command = command,
            CommandTarget = box
        };

    private static int ResolveCharacterIndex(TextBox box, ContextMenuEventArgs e)
    {
        if (e.CursorLeft >= 0 && e.CursorTop >= 0)
        {
            var fromPoint = box.GetCharacterIndexFromPoint(new Point(e.CursorLeft, e.CursorTop), true);
            if (fromPoint >= 0)
            {
                return fromPoint;
            }
        }

        return box.CaretIndex;
    }

    private static void AddWord(TextBox box, string word)
    {
        word = NormalizeWord(word);
        if (word.Length == 0)
        {
            return;
        }

        string language;
        string path;
        lock (Sync)
        {
            language = _currentLanguage;
            path = DictionaryPath(language);
            var words = ReadWords(path);
            if (words.All(existing => !existing.Equals(word, StringComparison.OrdinalIgnoreCase)))
            {
                words.Add(word);
                WriteWords(path, words);
            }
        }

        var tag = box.Language?.IetfLanguageTag ?? ToLanguageTag(language);
        TryAddToWindowsDictionary(tag, word);
        TryRegisterDictionary(path, tag);
        foreach (var attached in Attached)
        {
            Refresh(attached.Box);
        }
    }

    private static void CorrectWord(
        TextBox box,
        SpellingError error,
        string original,
        string replacement,
        int start,
        bool skipSrtMetadata)
    {
        var occurrence = SpellingSync.CountOccurrencesBefore(box.Text, original, start, skipSrtMetadata);
        error.Correct(replacement);
        WordCorrected?.Invoke(box, original, replacement, occurrence);
    }

    private static void Refresh(TextBox box)
    {
        SpellCheck.SetIsEnabled(box, false);
        SpellCheck.SetIsEnabled(box, true);
    }

    private static void SyncWordsToWindows(string path, string languageTag)
    {
        IReadOnlyList<string> words;
        lock (Sync)
        {
            EnsureDictionaryFile(path);
            words = ReadWords(path);
        }

        TryRegisterDictionary(path, languageTag);
        if (words.Count == 0)
        {
            return;
        }

        object? factory = null;
        ISpellChecker? checker = null;
        try
        {
            if (!TryCreateFactory(out factory, out var typedFactory) || typedFactory is null)
            {
                return;
            }

            checker = CreateChecker(typedFactory, languageTag);
            if (checker is null)
            {
                return;
            }

            foreach (var word in words)
            {
                checker.Add(word);
            }
        }
        catch (COMException)
        {
            // Spell checking remains available without the custom list.
        }
        catch (InvalidCastException)
        {
            // Same fallback.
        }
        finally
        {
            Release(checker);
            Release(factory);
        }
    }

    private static void TryAddToWindowsDictionary(string languageTag, string word)
    {
        object? factory = null;
        ISpellChecker? checker = null;
        try
        {
            if (!TryCreateFactory(out factory, out var typedFactory) || typedFactory is null)
            {
                return;
            }

            checker = CreateChecker(typedFactory, languageTag);
            checker?.Add(word);
        }
        catch (COMException)
        {
            // The word is still stored in the app dictionary for the next launch.
        }
        catch (InvalidCastException)
        {
            // Same fallback.
        }
        finally
        {
            Release(checker);
            Release(factory);
        }
    }

    private static void UnregisterIfChanged(string path, string languageTag)
    {
        (string Path, string Tag)[] stale;
        lock (Sync)
        {
            stale = Registrations
                .Where(registration =>
                    !string.Equals(registration.Path, path, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(registration.Tag, languageTag, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Registrations.RemoveAll(registration =>
                !string.Equals(registration.Path, path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(registration.Tag, languageTag, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var registration in stale)
        {
            TryUnregisterDictionary(registration.Path, registration.Tag);
        }
    }

    private static void TryRegisterDictionary(string path, string languageTag)
    {
        object? factory = null;
        try
        {
            if (!TryCreateFactory(out factory, out _))
            {
                return;
            }

            if (factory is not IUserDictionariesRegistrar registrar)
            {
                return;
            }

            registrar.RegisterUserDictionary(path, languageTag);
            lock (Sync)
            {
                if (!Registrations.Any(registration =>
                        string.Equals(registration.Path, path, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(registration.Tag, languageTag, StringComparison.OrdinalIgnoreCase)))
                {
                    Registrations.Add((path, languageTag));
                }
            }
        }
        catch (COMException)
        {
            // Registration is best-effort; ISpellChecker.Add is the persistence path.
        }
        catch (InvalidCastException)
        {
            // Same fallback.
        }
        finally
        {
            Release(factory);
        }
    }

    private static void TryUnregisterDictionary(string path, string languageTag)
    {
        object? factory = null;
        try
        {
            if (!TryCreateFactory(out factory, out _))
            {
                return;
            }

            if (factory is not IUserDictionariesRegistrar registrar)
            {
                return;
            }

            registrar.UnregisterUserDictionary(path, languageTag);
        }
        catch (COMException)
        {
            // Ignore shutdown cleanup failures.
        }
        catch (InvalidCastException)
        {
            // Ignore shutdown cleanup failures.
        }
        finally
        {
            Release(factory);
        }
    }

    private static bool TryCreateFactory(out object? instance, out ISpellCheckerFactory? factory)
    {
        instance = null;
        factory = null;
        var type = Type.GetTypeFromCLSID(SpellCheckerFactoryClsid, throwOnError: false);
        if (type is null)
        {
            return false;
        }

        instance = Activator.CreateInstance(type);
        factory = instance as ISpellCheckerFactory;
        return factory is not null;
    }

    private static ISpellChecker? CreateChecker(ISpellCheckerFactory factory, string languageTag)
    {
        try
        {
            return factory.CreateSpellChecker(languageTag);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void MigrateLegacyDictionary()
    {
        MergeDictionaryFile(AppStorage.LegacyDictionaryPath, DictionaryPath("English (US)"));

        if (!Directory.Exists(AppStorage.DictionariesDirectory))
        {
            return;
        }

        foreach (var language in LegacyLanguageNames)
        {
            var slugPath = Path.Combine(AppStorage.DictionariesDirectory, ToLegacySlug(language) + ".dic");
            MergeDictionaryFile(slugPath, DictionaryPath(language));
        }
    }

    private static void MergeDictionaryFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath)
            || string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (Sync)
        {
            var merged = ReadWords(destinationPath);
            foreach (var word in ReadWords(sourcePath))
            {
                if (merged.All(existing => !existing.Equals(word, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(word);
                }
            }

            WriteWords(destinationPath, merged);
        }

        try
        {
            File.Delete(sourcePath);
        }
        catch (IOException)
        {
            // Keep the old file if it cannot be removed; the destination already has the words.
        }
    }

    // BCP 47 / RFC 5646: ISO 639 language code + ISO 3166-1 country code, e.g. en-US.
    private static string DictionaryPath(string language) =>
        Path.Combine(AppStorage.DictionariesDirectory, ToLanguageTag(language) + ".dic");

    private static IEnumerable<string> LegacyLanguageNames =>
        VoskModelManager.Languages.Concat(["OpenAI Whisper", "ElevenLabs"]);

    private static string ToLegacySlug(string language)
    {
        var slug = new string(language
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return slug.Length == 0 ? "english-us" : slug;
    }

    private static void EnsureDictionaryFile(string path)
    {
        Directory.CreateDirectory(AppStorage.DictionariesDirectory);
        if (!File.Exists(path))
        {
            WriteWords(path, []);
        }
    }

    private static List<string> ReadWords(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path, Encoding.Unicode)
            .Select(NormalizeWord)
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteWords(string path, IEnumerable<string> words)
    {
        Directory.CreateDirectory(AppStorage.DictionariesDirectory);
        File.WriteAllLines(
            path,
            words.Where(word => word.Length > 0),
            Encoding.Unicode);
    }

    private static string NormalizeWord(string word)
    {
        word = word.Trim();
        var start = 0;
        var end = word.Length;
        while (start < end && IsWordPunctuation(word[start]))
        {
            start++;
        }

        while (end > start && IsWordPunctuation(word[end - 1]))
        {
            end--;
        }

        return start == 0 && end == word.Length ? word : word[start..end];
    }

    private static bool IsWordPunctuation(char c) =>
        c is '.' or ',' or '!' or '?' or ';' or ':' or '"' or '\'' or '(' or ')' or '“' or '”';

    internal static string ToLanguageTag(string? language) => language switch
    {
        "English (US)" or "OpenAI Whisper" or "ElevenLabs" => "en-US",
        "English (India)" => "en-IN",
        "Arabic" => "ar-SA",
        "Arabic (Tunisian)" => "ar-TN",
        "Breton" => "br-FR",
        "Catalan" => "ca-ES",
        "Chinese" => "zh-CN",
        "Czech" => "cs-CZ",
        "Dutch" => "nl-NL",
        "Esperanto" => "eo",
        "French" => "fr-FR",
        "Georgian" => "ka-GE",
        "German" => "de-DE",
        "Greek" => "el-GR",
        "Gujarati" => "gu-IN",
        "Hindi" => "hi-IN",
        "Italian" => "it-IT",
        "Japanese" => "ja-JP",
        "Kazakh" => "kk-KZ",
        "Korean" => "ko-KR",
        "Kyrgyz" => "ky-KG",
        "Persian" => "fa-IR",
        "Polish" => "pl-PL",
        "Portuguese" => "pt-BR",
        "Russian" => "ru-RU",
        "Spanish" => "es-ES",
        "Swedish" => "sv-SE",
        "Tajik" => "tg-TJ",
        "Telugu" => "te-IN",
        "Turkish" => "tr-TR",
        "Ukrainian" => "uk-UA",
        "Uzbek" => "uz-UZ",
        "Vietnamese" => "vi-VN",
        _ => "en-US"
    };

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    [ComImport]
    [Guid("8E018A9D-2415-4119-877D-0A5D00F0A8A7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }

        [PreserveSig]
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, [MarshalAs(UnmanagedType.Bool)] out bool value);

        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport]
    [Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { get; }

        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);

        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);

        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport]
    [Guid("AA176B85-0E12-4844-8E09-BDF3DCDA7D9C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUserDictionariesRegistrar
    {
        void RegisterUserDictionary(
            [MarshalAs(UnmanagedType.LPWStr)] string dictionaryPath,
            [MarshalAs(UnmanagedType.LPWStr)] string languageTag);

        void UnregisterUserDictionary(
            [MarshalAs(UnmanagedType.LPWStr)] string dictionaryPath,
            [MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport]
    [Guid("00000101-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumString
    {
        void Next(
            int celt,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)]
            string[] rgelt,
            out int pceltFetched);

        void Skip(int celt);

        void Reset();

        IEnumString Clone();
    }

    [ComImport]
    [Guid("803E3BD4-2828-4410-8290-74884D03C1C7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        ISpellingError Next();
    }

    [ComImport]
    [Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex { get; }

        uint Length { get; }

        CORRECTIVE_ACTION CorrectiveAction { get; }

        string Replacement { get; }
    }

    private enum CORRECTIVE_ACTION
    {
        NONE = 0,
        GET_SUGGESTIONS = 1,
        REPLACE = 2,
        DELETE = 3
    }

    private readonly record struct AttachedBox(TextBox Box, bool SkipSrtMetadata);
}
