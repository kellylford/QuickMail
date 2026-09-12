using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<CommandDefinition> _allCommands;

    /// <summary>Every registered command, in the registry's category-then-title order.</summary>
    public IReadOnlyList<CommandDefinition> Commands => _allCommands;

    /// <summary>
    /// What the palette actually shows: <see cref="Commands"/> ranked against <see cref="SearchText"/>.
    ///
    /// Batched, so refilling it raises one Reset rather than an event per command. Unbatched,
    /// every keystroke would fire ~200 UIA structure notifications at whatever is listening.
    /// </summary>
    public BatchObservableCollection<CommandDefinition> FilteredCommands { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CommandDefinition? _selectedCommand;

    public CommandPaletteViewModel(ICommandRegistry registry)
    {
        _allCommands = registry.GetAll();
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedCommandChanged(CommandDefinition? value)
    {
        ExecuteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What a screen reader is told when the active command changes, used only by the
    /// announcement fallback in the window — the UIA path lets the platform compose this
    /// itself. Built here so it is unit-testable without standing up a window.
    /// </summary>
    public string ActiveCommandSummary
    {
        get
        {
            if (FilteredCommands.Count == 0) return "No matching commands";

            var index = SelectedCommand is null ? 0 : FilteredCommands.IndexOf(SelectedCommand);
            if (index < 0) index = 0;

            return $"{FilteredCommands[index].Title}, {index + 1} of {FilteredCommands.Count}";
        }
    }

    private void ApplyFilter()
    {
        using (FilteredCommands.BeginBatchScope())
        {
            FilteredCommands.Clear();
            foreach (var cmd in CommandMatcher.Rank(_allCommands, SearchText))
                FilteredCommands.Add(cmd);
        }

        // The top match is always what Enter runs, so the selection follows the filter rather
        // than trying to keep hold of a command that may no longer be in the list.
        SelectedCommand = FilteredCommands.Count > 0 ? FilteredCommands[0] : null;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCommand))]
    private void ExecuteSelected()
    {
        SelectedCommand!.Execute();
    }

    private bool HasSelectedCommand() => SelectedCommand is not null;
}
