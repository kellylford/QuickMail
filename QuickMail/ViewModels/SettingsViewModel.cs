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
    private bool _announceSpellingSuggestions;

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
        AnnounceSpellingSuggestions = cfg.AnnounceSpellingSuggestions;

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
        cfg.AnnounceSpellingSuggestions = AnnounceSpellingSuggestions;

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
