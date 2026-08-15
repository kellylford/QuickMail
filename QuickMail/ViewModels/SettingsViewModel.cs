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

    /// <summary>Read messages as plain text instead of HTML (issue #34).</summary>
    [ObservableProperty]
    private bool _readAsPlainText;

    [ObservableProperty]
    private string _viewMode = "messages";

    /// <summary>Each folder remembers the presentation it was last given (issue #520).</summary>
    [ObservableProperty]
    private bool _rememberViewPerFolder = true;

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
    private bool _ruleListShowFieldLabels;

    [ObservableProperty]
    private bool _calendarReminders;

    [ObservableProperty]
    private int _calendarReminderMinutes = 10;

    [ObservableProperty]
    private bool _confirmEmptyTrash;

    [ObservableProperty]
    private bool _notifyOnNewMail;

    [ObservableProperty]
    private bool _notifyOnWatchedConversation;

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

    /// <summary>
    /// Bound to the Advanced tab's Google sign-in checkbox, and stored as the GoogleAuth flag in the
    /// config.ini [features] section rather than as a setting of its own — it is the same switch as
    /// <c>--feature GoogleAuth</c>, and two spellings of one flag would disagree the moment a user
    /// set both. Read at startup by ConfigFeatureGate, hence the restart.
    /// </summary>
    [ObservableProperty]
    private bool _googleSignIn;

    /// <summary>
    /// Bound to the Advanced tab's POP3 checkbox, and stored as the Pop3Backend flag in the
    /// config.ini [features] section for the same reason as <see cref="GoogleSignIn"/> — it is the
    /// same switch as <c>--feature Pop3Backend</c>, and a setting of its own would be a second
    /// spelling of one flag. Read at startup by ConfigFeatureGate, hence the restart (#128).
    ///
    /// <para>Off by default. It gates only the OFFER of POP3 in Add Account: an account already
    /// using POP3 keeps working whatever this is set to, so turning it back off hides the option
    /// without stranding anyone's mail.</para>
    /// </summary>
    [ObservableProperty]
    private bool _offerPop3;

    // ── Diagnostics (debug-only, #175) ─────────────────────────────────────────────
    // Deliberately NOT [ObservableProperty] over a field: the value lives on the
    // session service, never in ConfigModel/config.ini — non-persistence is the
    // safety story. The row is collapsed entirely outside /debug.

    private readonly IScreenshotCaptureService? _screenshotCapture;

    public bool IsDebugDiagnosticsVisible => LogService.DebugMode && _screenshotCapture != null;

    /// <summary>
    /// Meta-announcement about the diagnostic mode. The View forwards it to
    /// AccessibilityHelper.Announce with force:true (like the custom-announcements
    /// toggle, it must be heard regardless of announcement preferences).
    /// </summary>
    public event System.Action<string>? DiagnosticsAnnouncementRequested;

    public bool ScreenshotCaptureEnabled
    {
        get => _screenshotCapture?.Enabled == true;
        set
        {
            if (_screenshotCapture is null || _screenshotCapture.Enabled == value) return;
            _screenshotCapture.Enabled = value;
            // Announce what actually happened, not what was requested — the
            // service refuses to enable if its folder cannot be created.
            var actual = _screenshotCapture.Enabled;
            OnPropertyChanged(nameof(ScreenshotCaptureEnabled));
            DiagnosticsAnnouncementRequested?.Invoke(
                actual != value
                    ? "Screenshot capture could not be turned on. Check the log for details."
                    : actual
                        ? "Screenshot capture on. QuickMail is saving screen images to disk this session."
                        : "Screenshot capture off.");
        }
    }

    [RelayCommand]
    private void OpenScreenshotsFolder() => _screenshotCapture?.OpenFolder();

    /// <summary>Reads a [features] flag, treating anything unparseable or absent as the default.</summary>
    private static bool ReadFeature(ConfigModel cfg, FeatureFlag flag, bool fallback) =>
        cfg.Features.TryGetValue(flag.ToString(), out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;

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

    /// <summary>
    /// Message-list density, "comfortable" or "compact" (#421). Padding only —
    /// both modes present the identical accessibility surface, by design.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListDensityComfortable))]
    [NotifyPropertyChangedFor(nameof(IsListDensityCompact))]
    private string _appearanceListDensity = "comfortable";

    public bool IsListDensityComfortable
    {
        get => AppearanceListDensity == "comfortable";
        set { if (value) AppearanceListDensity = "comfortable"; }
    }
    public bool IsListDensityCompact
    {
        get => AppearanceListDensity == "compact";
        set { if (value) AppearanceListDensity = "compact"; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogFormatActionFirst))]
    [NotifyPropertyChangedFor(nameof(IsLogFormatTimeFirst))]
    private string _logFormat = "actionFirst";

    [ObservableProperty]
    private bool _enableLogging;

    /// <summary>
    /// Records connection diagnostics and adds Connection Diagnostics to the Help menu.
    /// Off by default; applied immediately on save, so a problem already in progress can be
    /// captured without restarting.
    /// </summary>
    [ObservableProperty]
    private bool _connectionDiagnostics;

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

    // ── Startup (#516) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Set by the View to show the folder picker and return the chosen folder, or null if the user
    /// cancelled. Same shape as <c>MainViewModel.SaveFolderPathRequested</c>: picking needs a window,
    /// which is the View's job, so the VM asks rather than opens.
    ///
    /// <para>Returns the picked <see cref="MailFolderModel"/> — a Models type, so no UI type crosses
    /// the boundary — and the VM converts it to storage form. The View must not do that conversion:
    /// it is the same rule <see cref="MainViewModel.SetStartupFolder"/> applies, and a second copy
    /// in code-behind is a data transformation the MVVM rules put in the VM precisely so the two
    /// cannot drift.</para>
    /// </summary>
    public Func<MailFolderModel?>? PickStartupFolderRequested { get; set; }

    [ObservableProperty]
    private string _startupFolder = string.Empty;

    [ObservableProperty]
    private string _startupFolderAccount = string.Empty;

    /// <summary>Read-only text of the current choice. "All Mail" stands for "nothing configured",
    /// which is what an empty <see cref="StartupFolder"/> means and what the app does anyway.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartupFolderDisplay))]
    private string _startupFolderLabel = string.Empty;

    public string StartupFolderDisplay =>
        string.IsNullOrWhiteSpace(StartupFolderLabel) ? "All Mail" : StartupFolderLabel;

    [RelayCommand]
    private void ChooseStartupFolder()
    {
        if (PickStartupFolderRequested?.Invoke() is not { } folder) return;   // cancelled

        // Same rule as MainViewModel.SetStartupFolder, and the same allow-list: a virtual aggregate
        // is stored without its NUL sentinel prefix (an INI cannot carry one) and with no account,
        // since it spans them all. A real folder keeps its name and carries its owning account,
        // because folder names collide across accounts and the pair is what resolves at startup.
        var isVirtual = MainViewModel.AllVirtualFolders.Any(v =>
            string.Equals(v.FullName, folder.FullName, StringComparison.Ordinal));

        StartupFolder        = isVirtual ? folder.FullName[1..] : folder.FullName;
        StartupFolderAccount = isVirtual ? string.Empty : folder.AccountId.ToString();
        StartupFolderLabel   = folder.DisplayName;
    }

    [RelayCommand]
    private void ClearStartupFolder()
    {
        StartupFolder        = string.Empty;
        StartupFolderAccount = string.Empty;
        StartupFolderLabel   = string.Empty;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupSyncScopeStartupFolder))]
    [NotifyPropertyChangedFor(nameof(IsStartupSyncScopeInboxes))]
    [NotifyPropertyChangedFor(nameof(IsStartupSyncScopeAll))]
    private string _startupSyncScope = ConfigModel.StartupSyncScopeStartupFolder;

    public bool IsStartupSyncScopeStartupFolder
    {
        get => StartupSyncScope == ConfigModel.StartupSyncScopeStartupFolder;
        set { if (value) StartupSyncScope = ConfigModel.StartupSyncScopeStartupFolder; }
    }

    public bool IsStartupSyncScopeInboxes
    {
        get => StartupSyncScope == ConfigModel.StartupSyncScopeInboxes;
        set { if (value) StartupSyncScope = ConfigModel.StartupSyncScopeInboxes; }
    }

    public bool IsStartupSyncScopeAll
    {
        get => StartupSyncScope == ConfigModel.StartupSyncScopeAll;
        set { if (value) StartupSyncScope = ConfigModel.StartupSyncScopeAll; }
    }

    public ObservableCollection<HotkeyRowViewModel> HotkeyRows { get; } = [];

    [ObservableProperty]
    private HotkeyRowViewModel? _selectedHotkey;

    public SettingsViewModel(
        IConfigService configService,
        ICommandRegistry registry,
        IThemeService? themeService = null,
        System.Collections.Generic.IEnumerable<string>? fontFamilies = null,
        IScreenshotCaptureService? screenshotCapture = null)
    {
        _configService = configService;
        _screenshotCapture = screenshotCapture;
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
        AppearanceListDensity       = cfg.AppearanceListDensity == "compact" ? "compact" : "comfortable";
        AppearanceForceMessageTheme = cfg.AppearanceForceMessageTheme;

        PreviewLines = cfg.PreviewLines;
        ReadAsPlainText = cfg.ReadAsPlainText;
        ViewMode = cfg.ViewMode;
        RememberViewPerFolder = cfg.RememberViewPerFolder;
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
        RuleListShowFieldLabels          = cfg.RuleListShowFieldLabels;
        CalendarReminders                = cfg.CalendarReminders;
        CalendarReminderMinutes          = cfg.CalendarReminderMinutes;
        ConfirmEmptyTrash                = cfg.ConfirmEmptyTrash;
        NotifyOnNewMail                  = cfg.NotifyOnNewMail;
        NotifyOnWatchedConversation      = cfg.NotifyOnWatchedConversation;
        CloseToTray                      = cfg.CloseToTray;
        DesktopShortcut                  = Helpers.DesktopShortcut.Exists();
        AutoUpdate                       = cfg.AutoUpdate;
        ShowUpdateInstalledAlerts        = cfg.ShowUpdateInstalledAlerts;
        GoogleSignIn                     = ReadFeature(cfg, FeatureFlag.GoogleAuth, false);
        OfferPop3                        = ReadFeature(cfg, FeatureFlag.Pop3Backend, false);
        AutoSaveDrafts                   = cfg.AutoSaveDrafts;
        AutoSaveIntervalSeconds          = cfg.AutoSaveIntervalSeconds;
        DefaultComposeMode = cfg.DefaultComposeMode switch
        {
            Models.ComposeMode.Markdown => "markdown",
            Models.ComposeMode.Html     => "html",
            _                           => "plain",
        };
        LogFormat                        = cfg.LogFormat;
        StartupFolder                    = cfg.StartupFolder;
        StartupFolderAccount             = cfg.StartupFolderAccount;
        StartupFolderLabel               = cfg.StartupFolderLabel;
        StartupSyncScope                 = ConfigModel.ParseStartupSyncScope(cfg.StartupSyncScope);
        EnableLogging                    = cfg.EnableLogging;
        ConnectionDiagnostics            = cfg.ConnectionDiagnostics;
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
        cfg.AppearanceListDensity       = AppearanceListDensity;
        cfg.AppearanceForceMessageTheme = AppearanceForceMessageTheme;

        cfg.PreviewLines = PreviewLines;
        cfg.ReadAsPlainText = ReadAsPlainText;
        cfg.ViewMode = ViewMode;
        cfg.RememberViewPerFolder = RememberViewPerFolder;
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
        cfg.RuleListShowFieldLabels          = RuleListShowFieldLabels;
        cfg.CalendarReminders                = CalendarReminders;
        cfg.CalendarReminderMinutes          = Math.Clamp(CalendarReminderMinutes, 1, 1440);
        cfg.ConfirmEmptyTrash                = ConfirmEmptyTrash;
        cfg.NotifyOnNewMail                  = NotifyOnNewMail;
        cfg.NotifyOnWatchedConversation      = NotifyOnWatchedConversation;
        cfg.CloseToTray                      = CloseToTray;
        cfg.AutoUpdate                       = AutoUpdate;
        cfg.ShowUpdateInstalledAlerts        = ShowUpdateInstalledAlerts;
        // Written both ways round, never removed when false: an explicit "false" in the file is how
        // a user who turns this back off stays off if the built-in default ever changes again.
        cfg.Features[FeatureFlag.GoogleAuth.ToString()] = GoogleSignIn ? "true" : "false";
        cfg.Features[FeatureFlag.Pop3Backend.ToString()] = OfferPop3 ? "true" : "false";
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
        cfg.StartupFolder                    = StartupFolder;
        cfg.StartupFolderAccount             = StartupFolderAccount;
        cfg.StartupFolderLabel               = StartupFolderLabel;
        cfg.StartupSyncScope                 = StartupSyncScope;
        cfg.EnableLogging                    = EnableLogging;
        cfg.ConnectionDiagnostics            = ConnectionDiagnostics;
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
