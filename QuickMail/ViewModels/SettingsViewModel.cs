using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _configService;

    [ObservableProperty]
    private int _previewLines;

    [ObservableProperty]
    private bool _showMessageStatus;

    /// <summary>Read messages as plain text instead of HTML (issue #34).</summary>
    [ObservableProperty]
    private bool _readAsPlainText;

    [ObservableProperty]
    private string _viewMode = "messages";

    [ObservableProperty]
    private int _syncDays;

    [ObservableProperty]
    private int _initialSyncCount;

    [ObservableProperty]
    private int _mailSyncPollMinutes;

    [ObservableProperty]
    private bool _customAnnouncements;

    [ObservableProperty]
    private bool _announceHints;

    [ObservableProperty]
    private bool _announceStatus;

    [ObservableProperty]
    private bool _announceResults;

    [ObservableProperty]
    private bool _announceMessageActions;

    [ObservableProperty]
    private bool _announceSpellingWhileTyping;

    [ObservableProperty]
    private bool _announceSpellingWhileNavigating;

    [ObservableProperty]
    private bool _announceSpellingSuggestions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerbosityJustSuggestions))]
    [NotifyPropertyChangedFor(nameof(IsVerbosityNumbersWithSuggestions))]
    private string _spellingSuggestionsVerbosity = "numbersWithSuggestions";

    public bool IsVerbosityJustSuggestions
    {
        get => SpellingSuggestionsVerbosity == "justSuggestions";
        set { if (value) SpellingSuggestionsVerbosity = "justSuggestions"; }
    }
    public bool IsVerbosityNumbersWithSuggestions
    {
        get => SpellingSuggestionsVerbosity == "numbersWithSuggestions";
        set { if (value) SpellingSuggestionsVerbosity = "numbersWithSuggestions"; }
    }

    [ObservableProperty]
    private bool _announceFormattingWhileNavigating;

    [ObservableProperty]
    private bool _announceFlagStatus;

    [ObservableProperty]
    private bool _contactListShowFieldLabels;

    [ObservableProperty]
    private bool _calendarListShowFieldLabels;

    [ObservableProperty]
    private bool _calendarReminders;

    [ObservableProperty]
    private int _calendarReminderMinutes = 10;

    [ObservableProperty]
    private bool _confirmEmptyTrash;

    [ObservableProperty]
    private bool _notifyOnNewMail;

    [ObservableProperty]
    private bool _closeToTray;

    // Desktop shortcut: the .lnk on the desktop is the source of truth, not config —
    // loaded from the filesystem and applied on save only when it differs from the file's
    // current state (re-checked at save time; the file can change outside this dialog).
    [ObservableProperty]
    private bool _desktopShortcut;

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    private bool _showUpdateInstalledAlerts;

    // ── Composing ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _autoSaveDrafts;

    /// <summary>Bound to a ComboBox of fixed choices; values are seconds.</summary>
    [ObservableProperty]
    private int _autoSaveIntervalSeconds;

    /// <summary>Bound to the Settings ComboBox by tag: "plain", "markdown", or "html".</summary>
    [ObservableProperty]
    private string _defaultComposeMode = "plain";

    // ── Windowing ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadingPaneMode))]
    [NotifyPropertyChangedFor(nameof(IsTabMode))]
    [NotifyPropertyChangedFor(nameof(IsWindowMode))]
    private string _messageOpenMode = "readingPane";

    public bool IsReadingPaneMode
    {
        get => MessageOpenMode == "readingPane";
        set { if (value) MessageOpenMode = "readingPane"; }
    }
    public bool IsTabMode
    {
        get => MessageOpenMode == "tab";
        set { if (value) MessageOpenMode = "tab"; }
    }
    public bool IsWindowMode
    {
        get => MessageOpenMode == "window";
        set { if (value) MessageOpenMode = "window"; }
    }

    [ObservableProperty]
    private bool _confirmCloseTabWithUnsaved = true;

    // ── Appearance ─────────────────────────────────────────────────────────────────

    /// <summary>Sentinel shown in the font ComboBox for "use the theme's font".</summary>
    public const string ThemeDefaultFontLabel = "(Theme default)";

    /// <summary>One row of the theme ComboBox — id + display name only, no UI types.</summary>
    public sealed record ThemeOption(string Id, string Name)
    {
        // A screen reader reads a data-bound Selector item's UIA Name from ToString()
        // (DisplayMemberPath="Name" only sets the visual). A record's default ToString
        // is "ThemeOption { Id = ..., Name = ... }", so without this the theme ComboBox
        // announced that punctuation-laden string. See CLAUDE.md.
        public override string ToString() => Name;
    }

    /// <summary>Selectable themes in display order. Empty when no theme service is wired (tests).</summary>
    public ObservableCollection<ThemeOption> ThemeOptions { get; } = [];

    /// <summary>Font choices: the theme-default sentinel followed by installed families.</summary>
    public ObservableCollection<string> FontOptions { get; } = [];

    /// <summary>True while Windows High Contrast supplies the colors; shows the notice in the tab.</summary>
    public bool IsHighContrastActive { get; }

    [ObservableProperty]
    private string _appearanceThemeId = "system";

    /// <summary>Bound to the text-size ComboBox by tag; values are percent (100–200).</summary>
    [ObservableProperty]
    private int _appearanceTextScalePercent = 100;

    /// <summary>The selected font option; <see cref="ThemeDefaultFontLabel"/> means no override.</summary>
    [ObservableProperty]
    private string _appearanceFontOption = ThemeDefaultFontLabel;

    [ObservableProperty]
    private bool _appearanceUnderlineLinks;

    [ObservableProperty]
    private bool _appearanceThickFocus;

    [ObservableProperty]
    private bool _appearanceForceMessageTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogFormatActionFirst))]
    [NotifyPropertyChangedFor(nameof(IsLogFormatTimeFirst))]
    private string _logFormat = "actionFirst";

    [ObservableProperty]
    private bool _enableLogging;

    public bool IsLogFormatActionFirst
    {
        get => LogFormat == "actionFirst";
        set { if (value) LogFormat = "actionFirst"; }
    }

    public bool IsLogFormatTimeFirst
    {
        get => LogFormat == "timeFirst";
        set { if (value) LogFormat = "timeFirst"; }
    }

    public ObservableCollection<HotkeyRowViewModel> HotkeyRows { get; } = [];

    [ObservableProperty]
    private HotkeyRowViewModel? _selectedHotkey;

    public SettingsViewModel(
        IConfigService configService,
        ICommandRegistry registry,
        IThemeService? themeService = null,
        System.Collections.Generic.IEnumerable<string>? fontFamilies = null)
    {
        _configService = configService;
        var cfg = configService.Load();

        // Appearance: themes from the service; installed fonts from the View
        // (font enumeration is a presentation concern the caller supplies).
        if (themeService != null)
        {
            foreach (var t in themeService.GetAvailableThemes())
                ThemeOptions.Add(new ThemeOption(t.Id, t.Name));
            IsHighContrastActive = themeService.IsHighContrastActive;
        }
        FontOptions.Add(ThemeDefaultFontLabel);
        if (fontFamilies != null)
            foreach (var f in fontFamilies)
                FontOptions.Add(f);

        AppearanceThemeId = cfg.AppearanceThemeId;
        AppearanceTextScalePercent = (int)System.Math.Round(cfg.AppearanceTextScale * 100);
        if (AppearanceTextScalePercent is not (100 or 110 or 125 or 150 or 175 or 200))
            AppearanceTextScalePercent = 100;
        AppearanceFontOption = string.IsNullOrWhiteSpace(cfg.AppearanceFontFamily)
            ? ThemeDefaultFontLabel
            : cfg.AppearanceFontFamily;
        if (AppearanceFontOption != ThemeDefaultFontLabel && !FontOptions.Contains(AppearanceFontOption))
            FontOptions.Add(AppearanceFontOption); // keep an uninstalled configured font selectable
        AppearanceUnderlineLinks    = cfg.AppearanceUnderlineLinks;
        AppearanceThickFocus        = cfg.AppearanceThickFocus;
        AppearanceForceMessageTheme = cfg.AppearanceForceMessageTheme;

        PreviewLines = cfg.PreviewLines;
        ShowMessageStatus = cfg.ShowMessageStatus;
        ReadAsPlainText = cfg.ReadAsPlainText;
        ViewMode = cfg.ViewMode;
        SyncDays = cfg.SyncDays;
        InitialSyncCount = cfg.InitialSyncCount;
        MailSyncPollMinutes = cfg.MailSyncPollMinutes;
        CustomAnnouncements = cfg.CustomAnnouncements;
        AnnounceHints       = cfg.AnnounceHints;
        AnnounceStatus      = cfg.AnnounceStatus;
        AnnounceResults     = cfg.AnnounceResults;
        AnnounceMessageActions = cfg.AnnounceMessageActions;
        AnnounceSpellingWhileTyping      = cfg.AnnounceSpellingWhileTyping;
        AnnounceSpellingWhileNavigating  = cfg.AnnounceSpellingWhileNavigating;
        AnnounceSpellingSuggestions      = cfg.AnnounceSpellingSuggestions;
        SpellingSuggestionsVerbosity     = cfg.SpellingSuggestionsVerbosity;
        AnnounceFormattingWhileNavigating = cfg.AnnounceFormattingWhileNavigating;
        AnnounceFlagStatus               = cfg.AnnounceFlagStatus;
        ContactListShowFieldLabels       = cfg.ContactListShowFieldLabels;
        CalendarListShowFieldLabels      = cfg.CalendarListShowFieldLabels;
        CalendarReminders                = cfg.CalendarReminders;
        CalendarReminderMinutes          = cfg.CalendarReminderMinutes;
        ConfirmEmptyTrash                = cfg.ConfirmEmptyTrash;
        NotifyOnNewMail                  = cfg.NotifyOnNewMail;
        CloseToTray                      = cfg.CloseToTray;
        DesktopShortcut                  = Helpers.DesktopShortcut.Exists();
        AutoUpdate                       = cfg.AutoUpdate;
        ShowUpdateInstalledAlerts        = cfg.ShowUpdateInstalledAlerts;
        AutoSaveDrafts                   = cfg.AutoSaveDrafts;
        AutoSaveIntervalSeconds          = cfg.AutoSaveIntervalSeconds;
        DefaultComposeMode = cfg.DefaultComposeMode switch
        {
            Models.ComposeMode.Markdown => "markdown",
            Models.ComposeMode.Html     => "html",
            _                           => "plain",
        };
        LogFormat                        = cfg.LogFormat;
        EnableLogging                    = cfg.EnableLogging;
        MessageOpenMode = cfg.Windowing.MessageOpenMode switch
        {
            Models.MessageOpenMode.Tab    => "tab",
            Models.MessageOpenMode.Window => "window",
            _                             => "readingPane",
        };
        ConfirmCloseTabWithUnsaved = cfg.Windowing.ConfirmCloseTabWithUnsaved;

        foreach (var cmd in registry.GetAll())
        {
            var row = new HotkeyRowViewModel(cmd);
            var customBinding = cfg.CustomHotkeys.FirstOrDefault(h => h.CommandId == cmd.Id);
            if (customBinding != null &&
                GestureHelper.TryParse(customBinding.Gesture, out var key, out var mods))
            {
                row.SetCustomBinding(key, mods);
            }
            HotkeyRows.Add(row);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var cfg = _configService.Load();

        cfg.AppearanceThemeId = AppearanceThemeId;
        cfg.AppearanceTextScale = AppearanceTextScalePercent / 100.0;
        cfg.AppearanceFontFamily = AppearanceFontOption == ThemeDefaultFontLabel
            ? string.Empty
            : AppearanceFontOption;
        cfg.AppearanceUnderlineLinks    = AppearanceUnderlineLinks;
        cfg.AppearanceThickFocus        = AppearanceThickFocus;
        cfg.AppearanceForceMessageTheme = AppearanceForceMessageTheme;

        cfg.PreviewLines = PreviewLines;
        cfg.ShowMessageStatus = ShowMessageStatus;
        cfg.ReadAsPlainText = ReadAsPlainText;
        cfg.ViewMode = ViewMode;
        cfg.SyncDays = SyncDays;
        cfg.InitialSyncCount = InitialSyncCount;
        cfg.MailSyncPollMinutes = MailSyncPollMinutes;
        cfg.CustomAnnouncements = CustomAnnouncements;
        cfg.AnnounceHints       = AnnounceHints;
        cfg.AnnounceStatus      = AnnounceStatus;
        cfg.AnnounceResults     = AnnounceResults;
        cfg.AnnounceMessageActions = AnnounceMessageActions;
        cfg.AnnounceSpellingWhileTyping      = AnnounceSpellingWhileTyping;
        cfg.AnnounceSpellingWhileNavigating  = AnnounceSpellingWhileNavigating;
        cfg.AnnounceSpellingSuggestions      = AnnounceSpellingSuggestions;
        cfg.SpellingSuggestionsVerbosity     = SpellingSuggestionsVerbosity;
        cfg.AnnounceFormattingWhileNavigating = AnnounceFormattingWhileNavigating;
        cfg.AnnounceFlagStatus               = AnnounceFlagStatus;
        cfg.ContactListShowFieldLabels       = ContactListShowFieldLabels;
        cfg.CalendarListShowFieldLabels      = CalendarListShowFieldLabels;
        cfg.CalendarReminders                = CalendarReminders;
        cfg.CalendarReminderMinutes          = Math.Clamp(CalendarReminderMinutes, 1, 1440);
        cfg.ConfirmEmptyTrash                = ConfirmEmptyTrash;
        cfg.NotifyOnNewMail                  = NotifyOnNewMail;
        cfg.CloseToTray                      = CloseToTray;
        cfg.AutoUpdate                       = AutoUpdate;
        cfg.ShowUpdateInstalledAlerts        = ShowUpdateInstalledAlerts;
        if (DesktopShortcut != Helpers.DesktopShortcut.Exists())
        {
            try
            {
                if (DesktopShortcut)
                {
                    // Create() can decline silently; reflect reality in the checkbox so the
                    // user sees the setting did not take rather than a phantom success.
                    if (!Helpers.DesktopShortcut.Create())
                        DesktopShortcut = false;
                }
                else
                {
                    Helpers.DesktopShortcut.Delete();
                }
            }
            catch (Exception ex)
            {
                LogService.Debug($"Desktop shortcut: {ex.Message}");
                DesktopShortcut = Helpers.DesktopShortcut.Exists();
            }
        }
        cfg.AutoSaveDrafts                   = AutoSaveDrafts;
        cfg.AutoSaveIntervalSeconds          = AutoSaveIntervalSeconds;
        cfg.DefaultComposeMode = DefaultComposeMode switch
        {
            "markdown" => Models.ComposeMode.Markdown,
            "html"     => Models.ComposeMode.Html,
            _          => Models.ComposeMode.PlainText,
        };
        cfg.LogFormat                        = LogFormat;
        cfg.EnableLogging                    = EnableLogging;
        cfg.Windowing.MessageOpenMode = MessageOpenMode switch
        {
            "tab"    => Models.MessageOpenMode.Tab,
            "window" => Models.MessageOpenMode.Window,
            _        => Models.MessageOpenMode.ReadingPane,
        };
        cfg.Windowing.ConfirmCloseTabWithUnsaved = ConfirmCloseTabWithUnsaved;

        cfg.CustomHotkeys = HotkeyRows
            .Where(r => r.HasCustomBinding)
            .Select(r => r.ToBinding())
            .ToList();

        _configService.Save(cfg);
    }

    [RelayCommand]
    private static void ClearHotkey(HotkeyRowViewModel? row)
    {
        row?.ClearCustomBinding();
    }

    internal HotkeyRowViewModel? FindConflict(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return null;
        return HotkeyRows.FirstOrDefault(r => r.HasCustomBinding && r.MatchesBinding(key, modifiers));
    }

    // ── HotkeyRowViewModel ─────────────────────────────────────────────────────────

    public partial class HotkeyRowViewModel : ObservableObject
    {
        private readonly CommandDefinition _command;
        private Key _customKey = Key.None;
        private ModifierKeys _customModifiers = ModifierKeys.None;

        public string CommandId => _command.Id;
        public string Category  => _command.Category;
        public string Title     => _command.Title;

        public string DefaultGesture => _command.GestureText;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ActiveGesture))]
        [NotifyPropertyChangedFor(nameof(AccessibleName))]
        private string _customGesture = string.Empty;

        /// <summary>The binding currently in effect — custom if set, otherwise the default.</summary>
        public string ActiveGesture => HasCustomBinding ? CustomGesture : DefaultGesture;

        /// <summary>Screen-reader label: "Title, Category, shortcut" (or "no shortcut").</summary>
        public string AccessibleName
        {
            get
            {
                var gesture = string.IsNullOrEmpty(ActiveGesture) ? "no shortcut" : ActiveGesture;
                return $"{Title}, {Category}, {gesture}";
            }
        }

        public bool HasCustomBinding => _customKey != Key.None;

        public HotkeyRowViewModel(CommandDefinition command)
        {
            _command = command;
        }

        public void SetCustomBinding(Key key, ModifierKeys modifiers)
        {
            _customKey      = key;
            _customModifiers = modifiers;
            UpdateCustomGesture();
            OnPropertyChanged(nameof(HasCustomBinding));
            OnPropertyChanged(nameof(ActiveGesture));
        }

        public void ClearCustomBinding()
        {
            _customKey      = Key.None;
            _customModifiers = ModifierKeys.None;
            CustomGesture   = string.Empty;
            OnPropertyChanged(nameof(HasCustomBinding));
            OnPropertyChanged(nameof(ActiveGesture));
        }

        public HotkeyBinding ToBinding() => new()
        {
            CommandId = CommandId,
            Gesture   = GestureHelper.Format(_customKey, _customModifiers),
        };

        private void UpdateCustomGesture()
        {
            CustomGesture = GestureHelper.Format(_customKey, _customModifiers);
        }

        internal bool MatchesBinding(Key key, ModifierKeys modifiers)
            => _customKey == key && _customModifiers == modifiers;
    }
}
