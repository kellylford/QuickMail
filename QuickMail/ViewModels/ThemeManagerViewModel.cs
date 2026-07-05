using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Backs the Theme Manager window: list, apply, duplicate, rename, delete,
/// export, import, open themes folder. Follows the ViewManagerViewModel event
/// pattern — the View owns every dialog, file picker, and announcement; this VM
/// raises requests and exposes state only.
/// </summary>
public partial class ThemeManagerViewModel : ObservableObject
{
    private readonly IThemeService _themeService;
    private readonly IConfigService _configService;

    /// <summary>What the open name panel will do on OK.</summary>
    private enum NameAction { None, Duplicate, Rename }
    private NameAction _pendingNameAction = NameAction.None;

    public ObservableCollection<ThemeRowViewModel> Themes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanDuplicate))]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private ThemeRowViewModel? _selectedTheme;

    /// <summary>True while the inline name panel (duplicate/rename) is open.</summary>
    [ObservableProperty]
    private bool _isNamePanelOpen;

    [ObservableProperty]
    private string _editName = string.Empty;

    public bool CanApply     => SelectedTheme != null;
    public bool CanDuplicate => SelectedTheme != null && !SelectedTheme.IsSystem;
    public bool CanRename    => SelectedTheme is { IsBuiltIn: false };
    public bool CanDelete    => SelectedTheme is { IsBuiltIn: false };
    public bool CanExport    => SelectedTheme != null && !SelectedTheme.IsSystem;

    // ── View requests ─────────────────────────────────────────────────────────

    /// <summary>Announce text through AccessibilityHelper with the given category.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>The name panel opened; the View focuses the name box.</summary>
    public event EventHandler? NameEditStarted;

    /// <summary>Focus should return to the list item with this theme id.</summary>
    public event Action<string>? FocusListItemRequested;

    /// <summary>Show an error dialog with this plain-language message.</summary>
    public event Action<string>? ErrorRequested;

    /// <summary>Confirm deletion; returns true to proceed. Message, then title.</summary>
    public Func<string, string, bool>? ConfirmDeleteRequested { get; set; }

    /// <summary>Pick a save path for export; argument is the suggested file name.</summary>
    public Func<string, string?>? ExportPathRequested { get; set; }

    /// <summary>Pick a theme file to import.</summary>
    public Func<string?>? ImportPathRequested { get; set; }

    /// <summary>Open the user themes folder in the shell.</summary>
    public event Action<string>? OpenFolderRequested;

    public ThemeManagerViewModel(IThemeService themeService, IConfigService configService)
    {
        _themeService = themeService;
        _configService = configService;
        ReloadThemes(selectId: themeService.ConfiguredThemeId);
    }

    private void ReloadThemes(string? selectId)
    {
        Themes.Clear();
        var configured = _themeService.ConfiguredThemeId;
        foreach (var theme in _themeService.GetAvailableThemes())
            Themes.Add(new ThemeRowViewModel(theme,
                isCurrent: string.Equals(theme.Id, configured, StringComparison.OrdinalIgnoreCase)));

        SelectedTheme = Themes.FirstOrDefault(t =>
                string.Equals(t.Id, selectId, StringComparison.OrdinalIgnoreCase))
            ?? Themes.FirstOrDefault();
    }

    private void RefreshCurrentMarkers()
    {
        var configured = _themeService.ConfiguredThemeId;
        foreach (var row in Themes)
            row.IsCurrent = string.Equals(row.Id, configured, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistConfiguredTheme() =>
        Helpers.ThemePersistence.PersistConfiguredTheme(_themeService, _configService);

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply()
    {
        if (SelectedTheme is null) return;
        _themeService.ApplyTheme(SelectedTheme.Id);
        PersistConfiguredTheme();
        RefreshCurrentMarkers();
        // "Theme changed to …" is announced by the main window's ThemeChanged
        // handler — no announcement here, so it is never doubled. Focus stays put.
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedTheme is null || SelectedTheme.IsSystem) return;
        _pendingNameAction = NameAction.Duplicate;
        EditName = $"{SelectedTheme.Name} copy";
        IsNamePanelOpen = true;
        NameEditStarted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedTheme is not { IsBuiltIn: false }) return;
        _pendingNameAction = NameAction.Rename;
        EditName = SelectedTheme.Name;
        IsNamePanelOpen = true;
        NameEditStarted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ConfirmName()
    {
        var name = EditName.Trim();
        if (name.Length == 0 || SelectedTheme is null)
        {
            CancelName();
            return;
        }

        try
        {
            switch (_pendingNameAction)
            {
                case NameAction.Duplicate:
                {
                    var copy = SelectedTheme.Definition.Clone();
                    copy.Id = Guid.NewGuid().ToString("N");
                    copy.Name = name;
                    copy.IsBuiltIn = false;
                    _themeService.SaveUserTheme(copy);
                    ReloadThemes(selectId: copy.Id);
                    AnnouncementRequested?.Invoke($"Theme {name} created.", AnnouncementCategory.Result);
                    FocusListItemRequested?.Invoke(copy.Id);
                    break;
                }
                case NameAction.Rename:
                {
                    var renamed = SelectedTheme.Definition.Clone();
                    renamed.Name = name;
                    _themeService.SaveUserTheme(renamed);
                    ReloadThemes(selectId: renamed.Id);
                    AnnouncementRequested?.Invoke($"Theme renamed to {name}.", AnnouncementCategory.Result);
                    FocusListItemRequested?.Invoke(renamed.Id);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorRequested?.Invoke($"The theme could not be saved: {ex.Message}");
        }
        finally
        {
            _pendingNameAction = NameAction.None;
            IsNamePanelOpen = false;
        }
    }

    [RelayCommand]
    private void CancelName()
    {
        _pendingNameAction = NameAction.None;
        IsNamePanelOpen = false;
        if (SelectedTheme != null)
            FocusListItemRequested?.Invoke(SelectedTheme.Id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedTheme is not { IsBuiltIn: false } row) return;

        var confirmed = ConfirmDeleteRequested?.Invoke(
            $"Delete theme {row.Name}? This cannot be undone.", "Delete Theme") ?? false;
        if (!confirmed) return;

        var index = Themes.IndexOf(row);
        try
        {
            // If the deleted theme was active the service falls back to System.
            _themeService.DeleteUserTheme(row.Id);
            PersistConfiguredTheme();
        }
        catch (Exception ex)
        {
            ErrorRequested?.Invoke($"The theme could not be deleted: {ex.Message}");
            return;
        }

        ReloadThemes(selectId: null);
        // Land on the next item, or the previous when the deleted one was last.
        if (Themes.Count > 0)
            SelectedTheme = Themes[Math.Min(index, Themes.Count - 1)];
        AnnouncementRequested?.Invoke("Theme deleted.", AnnouncementCategory.Result);
        if (SelectedTheme != null)
            FocusListItemRequested?.Invoke(SelectedTheme.Id);
    }

    [RelayCommand]
    private void Export()
    {
        if (SelectedTheme is null || SelectedTheme.IsSystem) return;

        var path = ExportPathRequested?.Invoke($"{SelectedTheme.Name}.quickmailtheme");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            _themeService.ExportTheme(SelectedTheme.Id, path);
            AnnouncementRequested?.Invoke("Theme exported.", AnnouncementCategory.Result);
        }
        catch (Exception ex)
        {
            ErrorRequested?.Invoke($"The theme could not be exported: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Import()
    {
        var path = ImportPathRequested?.Invoke();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var imported = _themeService.ImportTheme(path);
            ReloadThemes(selectId: imported.Id);
            AnnouncementRequested?.Invoke($"Theme {imported.Name} imported.", AnnouncementCategory.Result);
            FocusListItemRequested?.Invoke(imported.Id);
        }
        catch (ThemeFormatException ex)
        {
            // Plain-language message from the parser, shown verbatim; the list is unchanged.
            ErrorRequested?.Invoke(ex.Message);
        }
        catch (Exception ex)
        {
            ErrorRequested?.Invoke($"The theme could not be imported: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenThemesFolder() => OpenFolderRequested?.Invoke(_themeService.UserThemesFolder);

    // ── Row ───────────────────────────────────────────────────────────────────

    public partial class ThemeRowViewModel : ObservableObject
    {
        public ThemeDefinition Definition { get; }

        public string Id => Definition.Id;
        public string Name => Definition.Name;
        public bool IsBuiltIn => Definition.IsBuiltIn;
        public bool IsSystem => string.Equals(Id, "system", StringComparison.OrdinalIgnoreCase);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AccessibleName))]
        [NotifyPropertyChangedFor(nameof(DetailLabel))]
        private bool _isCurrent;

        /// <summary>Secondary line under the theme name, e.g. "Built-in · current theme".</summary>
        public string DetailLabel
        {
            get
            {
                var kind = IsBuiltIn ? "Built-in" : "Custom";
                return IsCurrent ? $"{kind} · current theme" : kind;
            }
        }

        /// <summary>List-item label for screen readers: name, kind, current marker.</summary>
        public string AccessibleName
        {
            get
            {
                var kind = IsBuiltIn ? "built-in" : "custom";
                return IsCurrent ? $"{Name}, {kind}, current theme" : $"{Name}, {kind}";
            }
        }

        public ThemeRowViewModel(ThemeDefinition definition, bool isCurrent)
        {
            Definition = definition;
            IsCurrent = isCurrent;
        }
    }
}
