using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ICommandRegistry _registry;
    private readonly IReadOnlyList<CommandDefinition> _allCommands;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CommandDefinition? _selectedCommand;

    public ObservableCollection<CommandDefinition> FilteredCommands { get; } = [];

    public CommandPaletteViewModel(ICommandRegistry registry)
    {
        _registry    = registry;
        _allCommands = registry.GetAll();
        PopulateList(string.Empty);
    }

    partial void OnSearchTextChanged(string value) => PopulateList(value);

    partial void OnSelectedCommandChanged(CommandDefinition? value)
    {
        ExecuteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void PopulateList(string filter)
    {
        FilteredCommands.Clear();

        var trimmed = filter.Trim();
        IEnumerable<CommandDefinition> matches = string.IsNullOrEmpty(trimmed)
            ? _allCommands
            : _allCommands.Where(c =>
                c.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                c.Category.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                c.Id.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        foreach (var cmd in matches)
            FilteredCommands.Add(cmd);

        SelectedCommand = FilteredCommands.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCommand))]
    private void ExecuteSelected()
    {
        SelectedCommand!.Execute();
    }

    private bool HasSelectedCommand() => SelectedCommand is not null;
}
