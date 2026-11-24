using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class FlagManagerViewModel : ObservableObject
{
    private const int MaxUserFlags  = 20;
    private const int MaxNameLength = 32;

    private readonly IFlagService _flagService;

    public ObservableCollection<FlagDefinition> Flags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanMoveUp))]
    [NotifyPropertyChangedFor(nameof(CanMoveDown))]
    [NotifyPropertyChangedFor(nameof(CanChangeColor))]
    [NotifyPropertyChangedFor(nameof(SelectedColorHex))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFlagCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetAsKDefaultCommand))]
    private FlagDefinition? _selectedFlag;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _renameError = string.Empty;

    [ObservableProperty]
    private Guid _kDefaultId;

    public bool HasSelection => HasSelectionNow();
    public bool CanDelete    => CanDeleteFlagNow();
    public bool CanMoveUp    => CanMoveUpNow();
    public bool CanMoveDown  => CanMoveDownNow();

    /// <summary>
    /// False for the built-in flag, whose color is fixed: <c>FlagService</c> resets it on every
    /// load. The color buttons are disabled then, so arrowing cannot show a color as chosen that
    /// was never applied (issue #1080).
    /// </summary>
    public bool CanChangeColor => SelectedFlag?.IsBuiltIn == false;

    /// <summary>
    /// The selected flag's color, which the preset color radio buttons bind to (issue #1080).
    /// Setting it applies the color to a flag the user created and saves; the built-in flag and
    /// no selection are ignored. Arrowing through the buttons sets this on every press, so the
    /// saves are serialized by <see cref="SaveAsync"/>.
    /// </summary>
    public string? SelectedColorHex
    {
        get => SelectedFlag?.ColorHex;
        set
        {
            var flag = SelectedFlag;
            if (flag == null || flag.IsBuiltIn || string.IsNullOrEmpty(value)) return;
            if (string.Equals(flag.ColorHex, value, StringComparison.OrdinalIgnoreCase)) return;
            flag.ColorHex = value;
            OnPropertyChanged();
            _ = SaveColorAsync();
        }
    }

    private async Task SaveColorAsync()
    {
        try { await SaveAsync(); }
        catch (Exception ex) { LogService.Log("FlagManager: saving a flag color failed", ex); }
    }

    // Track user-flag count so CanAddFlagNow doesn't need a LINQ scan on every check.
    private int _userFlagCount;

    public static string[] PresetColors { get; } =
    [
        "#E53E3E", "#DD6B20", "#C05621", "#B7791F",
        "#38A169", "#276749", "#319795", "#0987A0",
        "#3182CE", "#5A67D8", "#6B46C1", "#D53F8C",
    ];

    public event Func<string, string, Task<bool>>? ConfirmDeleteRequested;
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;
    public event EventHandler? RenameStarted;

    public FlagManagerViewModel(IFlagService flagService)
    {
        _flagService = flagService;
        Flags.CollectionChanged += (_, _) => RecomputeUserFlagCount();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var flags = await _flagService.LoadFlagDefinitionsAsync();
        ct.ThrowIfCancellationRequested();
        Flags.Clear();
        foreach (var f in flags.OrderBy(f => f.SortOrder))
            Flags.Add(f);
        RecomputeUserFlagCount();
        var kFlag = await _flagService.GetKDefaultFlagAsync();
        KDefaultId = kFlag.Id;
        SelectedFlag = Flags.Count > 0 ? Flags[0] : null;
    }

    private bool CanAddFlagNow() => _userFlagCount < MaxUserFlags;
    private bool CanDeleteFlagNow() => SelectedFlag?.IsBuiltIn == false;
    private bool HasSelectionNow() => SelectedFlag != null;
    private bool CanRenameNow() => SelectedFlag?.IsBuiltIn == false;
    private bool CanMoveUpNow()   => SelectedFlag != null && Flags.IndexOf(SelectedFlag) > 0;
    private bool CanMoveDownNow() => SelectedFlag != null && Flags.IndexOf(SelectedFlag) < Flags.Count - 1;

    private void RecomputeUserFlagCount()
    {
        _userFlagCount = Flags.Count(f => !f.IsBuiltIn);
        AddFlagCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddFlagNow))]
    private async Task AddFlag()
    {
        const string baseName = "New Flag";
        var name = baseName;
        int n = 2;
        while (Flags.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {n++}";

        var newFlag = new FlagDefinition
        {
            Id        = Guid.NewGuid(),
            Name      = name,
            ColorHex  = "#3182CE",
            SortOrder = Flags.Count > 0 ? Flags.Max(f => f.SortOrder) + 1 : 1,
            IsBuiltIn = false,
        };
        Flags.Add(newFlag);
        SelectedFlag = newFlag;
        await SaveAsync();
        BeginRename();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteFlagNow))]
    private async Task DeleteFlag()
    {
        if (SelectedFlag == null || SelectedFlag.IsBuiltIn) return;
        var name = SelectedFlag.Name;
        bool confirmed = ConfirmDeleteRequested == null
            || await ConfirmDeleteRequested($"Delete the flag \"{name}\"?", "Delete Flag");
        if (!confirmed) return;

        int idx = Flags.IndexOf(SelectedFlag);
        Flags.Remove(SelectedFlag);
        SelectedFlag = Flags.Count > 0 ? Flags[Math.Min(idx, Flags.Count - 1)] : null;
        await SaveAsync();
        AnnouncementRequested?.Invoke($"Deleted flag {name}", AnnouncementCategory.Result);
    }

    [RelayCommand(CanExecute = nameof(CanRenameNow))]
    private void BeginRename()
    {
        if (SelectedFlag == null || SelectedFlag.IsBuiltIn) return;
        EditName = SelectedFlag.Name;
        RenameError = string.Empty;
        IsRenaming = true;
        RenameStarted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SaveRename()
    {
        if (SelectedFlag == null) return;
        var trimmed = EditName.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            RenameError = "Name cannot be empty.";
            return;
        }
        if (trimmed.Length > MaxNameLength)
        {
            RenameError = $"Name must be {MaxNameLength} characters or fewer.";
            return;
        }
        if (Flags.Any(f => f != SelectedFlag && f.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            RenameError = $"\"{trimmed}\" is already in use.";
            return;
        }
        SelectedFlag.Name = trimmed;
        IsRenaming = false;
        RenameError = string.Empty;
        await SaveAsync();
        AnnouncementRequested?.Invoke($"Flag renamed to {trimmed}", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanMoveUpNow))]
    private async Task MoveUp()
    {
        if (SelectedFlag == null) return;
        int idx = Flags.IndexOf(SelectedFlag);
        if (idx <= 0) return;
        var other = Flags[idx - 1];
        (SelectedFlag.SortOrder, other.SortOrder) = (other.SortOrder, SelectedFlag.SortOrder);
        Flags.Move(idx, idx - 1);
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        await SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDownNow))]
    private async Task MoveDown()
    {
        if (SelectedFlag == null) return;
        int idx = Flags.IndexOf(SelectedFlag);
        if (idx >= Flags.Count - 1) return;
        var other = Flags[idx + 1];
        (SelectedFlag.SortOrder, other.SortOrder) = (other.SortOrder, SelectedFlag.SortOrder);
        Flags.Move(idx, idx + 1);
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        await SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectionNow))]
    private async Task SetAsKDefault()
    {
        if (SelectedFlag == null) return;
        await _flagService.SetKDefaultFlagAsync(SelectedFlag.Id);
        KDefaultId = SelectedFlag.Id;
        AnnouncementRequested?.Invoke(
            $"{SelectedFlag.Name} is now the default flag for K",
            AnnouncementCategory.Result);
    }

    // Saves run one at a time. FlagService writes through a single temp file, so two writes in
    // flight at once (arrowing through the colors saves on every press) could collide on it.
    private Task _lastSave = Task.CompletedTask;

    private Task SaveAsync()
    {
        List<FlagDefinition> snapshot = [.. Flags];
        _lastSave = SaveAfterAsync(_lastSave, snapshot);
        return _lastSave;
    }

    private async Task SaveAfterAsync(Task previous, List<FlagDefinition> flags)
    {
        // A failure of the previous save was reported to whoever awaited it; this one still runs.
        try { await previous; }
        catch (Exception ex) { LogService.Debug($"FlagManager: an earlier save failed ({ex.Message}); saving again."); }
        await _flagService.SaveFlagDefinitionsAsync(flags);
    }
}
