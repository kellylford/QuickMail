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

    [RelayCommand]
    private async Task ChangeColor(string hex)
    {
        if (SelectedFlag == null || string.IsNullOrEmpty(hex)) return;
        if (SelectedFlag.IsBuiltIn) return;
        SelectedFlag.ColorHex = hex;
        await SaveAsync();
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

    private async Task SaveAsync()
    {
        await _flagService.SaveFlagDefinitionsAsync([.. Flags]);
    }
}
