using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class FlagPickerViewModel : ObservableObject
{
    private readonly IFlagService _flagService;

    public ObservableCollection<FlagDefinition> Flags { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyFlagCommand))]
    private FlagDefinition? _selectedFlag;

    public bool CurrentlyFlagged { get; }

    public event Action<FlagDefinition?>? FlagSelected;

    public FlagPickerViewModel(IFlagService flagService, bool currentlyFlagged)
    {
        _flagService = flagService;
        CurrentlyFlagged = currentlyFlagged;
    }

    public async Task LoadAsync()
    {
        var flags = await _flagService.LoadFlagDefinitionsAsync();
        Flags.Clear();
        foreach (var f in flags)
            Flags.Add(f);
        SelectedFlag = Flags.Count > 0 ? Flags[0] : null;
    }

    private bool HasSelection() => SelectedFlag != null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplyFlag()
    {
        FlagSelected?.Invoke(SelectedFlag);
    }

    [RelayCommand]
    private void ClearFlag()
    {
        FlagSelected?.Invoke(null);
    }
}
