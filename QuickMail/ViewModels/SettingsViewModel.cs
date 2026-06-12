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

    [ObservableProperty]
    private string _viewMode = "messages";

    [ObservableProperty]
    private int _syncDays;

    [ObservableProperty]
    private int _initialSyncCount;

    [ObservableProperty]
    private bool _customAnnouncements;

    [ObservableProperty]
    private bool _announceHints;

    [ObservableProperty]
    private bool _announceStatus;

    [ObservableProperty]
    private bool _announceResults;

    [ObservableProperty]
    private bool _announceSpellingWhileTyping;

    [ObservableProperty]
    private bool _announceSpellingWhileNavigating;

    [ObservableProperty]
    private bool _announceSpellingSuggestions;

    [ObservableProperty]
    private bool _announceFormattingWhileNavigating;

    [ObservableProperty]
    private bool _confirmEmptyTrash;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogFormatActionFirst))]
    [NotifyPropertyChangedFor(nameof(IsLogFormatTimeFirst))]
    private string _logFormat = "actionFirst";

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

    public SettingsViewModel(IConfigService configService, ICommandRegistry registry)
    {
        _configService = configService;
        var cfg = configService.Load();

        PreviewLines = cfg.PreviewLines;
        ShowMessageStatus = cfg.ShowMessageStatus;
        ViewMode = cfg.ViewMode;
        SyncDays = cfg.SyncDays;
        InitialSyncCount = cfg.InitialSyncCount;
        CustomAnnouncements = cfg.CustomAnnouncements;
        AnnounceHints       = cfg.AnnounceHints;
        AnnounceStatus      = cfg.AnnounceStatus;
        AnnounceResults     = cfg.AnnounceResults;
        AnnounceSpellingWhileTyping      = cfg.AnnounceSpellingWhileTyping;
        AnnounceSpellingWhileNavigating  = cfg.AnnounceSpellingWhileNavigating;
        AnnounceSpellingSuggestions      = cfg.AnnounceSpellingSuggestions;
        AnnounceFormattingWhileNavigating = cfg.AnnounceFormattingWhileNavigating;
        ConfirmEmptyTrash                = cfg.ConfirmEmptyTrash;
        AutoSaveDrafts                   = cfg.AutoSaveDrafts;
        AutoSaveIntervalSeconds          = cfg.AutoSaveIntervalSeconds;
        DefaultComposeMode = cfg.DefaultComposeMode switch
        {
            Models.ComposeMode.Markdown => "markdown",
            Models.ComposeMode.Html     => "html",
            _                           => "plain",
        };
        LogFormat                        = cfg.LogFormat;
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

        cfg.PreviewLines = PreviewLines;
        cfg.ShowMessageStatus = ShowMessageStatus;
        cfg.ViewMode = ViewMode;
        cfg.SyncDays = SyncDays;
        cfg.InitialSyncCount = InitialSyncCount;
        cfg.CustomAnnouncements = CustomAnnouncements;
        cfg.AnnounceHints       = AnnounceHints;
        cfg.AnnounceStatus      = AnnounceStatus;
        cfg.AnnounceResults     = AnnounceResults;
        cfg.AnnounceSpellingWhileTyping      = AnnounceSpellingWhileTyping;
        cfg.AnnounceSpellingWhileNavigating  = AnnounceSpellingWhileNavigating;
        cfg.AnnounceSpellingSuggestions      = AnnounceSpellingSuggestions;
        cfg.AnnounceFormattingWhileNavigating = AnnounceFormattingWhileNavigating;
        cfg.ConfirmEmptyTrash                = ConfirmEmptyTrash;
        cfg.AutoSaveDrafts                   = AutoSaveDrafts;
        cfg.AutoSaveIntervalSeconds          = AutoSaveIntervalSeconds;
        cfg.DefaultComposeMode = DefaultComposeMode switch
        {
            "markdown" => Models.ComposeMode.Markdown,
            "html"     => Models.ComposeMode.Html,
            _          => Models.ComposeMode.PlainText,
        };
        cfg.LogFormat                        = LogFormat;
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
    private void ClearHotkey(HotkeyRowViewModel? row)
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
        private string _customGesture = string.Empty;

        /// <summary>The binding currently in effect — custom if set, otherwise the default.</summary>
        public string ActiveGesture => HasCustomBinding ? CustomGesture : DefaultGesture;

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
