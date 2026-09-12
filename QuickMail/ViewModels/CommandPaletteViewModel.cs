using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// Deliberately a plain collection updated in place — see <see cref="Reconcile"/>. Batching the
    /// refill into one Reset, which is the usual way to keep a rebuild quiet, is the wrong trade
    /// here: a Reset makes the list drop and retake its selection, and a retaken selection is
    /// spoken.
    /// </summary>
    public ObservableCollection<CommandDefinition> FilteredCommands { get; } = [];

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
        var ranked = CommandMatcher.Rank(_allCommands, SearchText);

        // Reconciled in place rather than cleared and refilled. Clear + Add raises a Reset, the
        // list re-selects off the back of it, and the assignment below then selects again — two
        // selection changes for one keystroke, which is heard as the command being named twice:
        //
        //     Month View
        //     Month View
        //     1 of 78
        //
        // Updating in place leaves a top match that has not changed sitting in its own container,
        // untouched, so nothing is reported until what Enter would run actually changes.
        Reconcile(ranked);

        // The top match is always what Enter runs, so the selection follows the filter rather
        // than trying to keep hold of a command that may no longer be in the list.
        SelectedCommand = FilteredCommands.Count > 0 ? FilteredCommands[0] : null;
    }

    /// <summary>
    /// Brings <see cref="FilteredCommands"/> to <paramref name="ranked"/> with removals, moves and
    /// inserts, so items that survive keep their identity — and their list container — instead of
    /// every keystroke replacing all of them.
    /// </summary>
    private void Reconcile(IReadOnlyList<CommandDefinition> ranked)
    {
        var wanted = new HashSet<CommandDefinition>(ranked);

        for (var i = FilteredCommands.Count - 1; i >= 0; i--)
            if (!wanted.Contains(FilteredCommands[i]))
                FilteredCommands.RemoveAt(i);

        for (var i = 0; i < ranked.Count; i++)
        {
            if (i < FilteredCommands.Count && ReferenceEquals(FilteredCommands[i], ranked[i]))
                continue;

            var existing = FilteredCommands.IndexOf(ranked[i]);
            if (existing >= 0) FilteredCommands.Move(existing, i);
            else FilteredCommands.Insert(i, ranked[i]);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCommand))]
    private void ExecuteSelected()
    {
        SelectedCommand!.Execute();
    }

    private bool HasSelectedCommand() => SelectedCommand is not null;
}
