using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The palette ViewModel's filtering and selection. Plain <c>[Fact]</c>s — the ViewModel has no
/// WPF in it, and none of this needs a window.
/// </summary>
public class CommandPaletteFilterTests
{
    private static CommandPaletteViewModel Vm(params string[] titles)
    {
        var registry = new CommandRegistry();
        foreach (var title in titles)
            registry.Register(new CommandDefinition(
                id: $"test.{title.Replace(" ", "").ToLowerInvariant()}",
                category: "View", title: title, execute: () => { }));
        return new CommandPaletteViewModel(registry);
    }

    [Fact]
    public void WithNoFilter_EveryCommandIsShown()
    {
        var vm = Vm("Go to Folder", "Reply", "Next Theme");

        Assert.Equal(3, vm.FilteredCommands.Count);
    }

    [Fact]
    public void TypingNarrowsTheList()
    {
        var vm = Vm("Go to Folder", "Reply", "Next Theme");

        vm.SearchText = "fold";

        Assert.Single(vm.FilteredCommands);
        Assert.Equal("Go to Folder", vm.FilteredCommands[0].Title);
    }

    [Fact]
    public void ClearingTheFilterRestoresEveryCommand()
    {
        var vm = Vm("Go to Folder", "Reply", "Next Theme");

        vm.SearchText = "fold";
        vm.SearchText = string.Empty;

        Assert.Equal(3, vm.FilteredCommands.Count);
    }

    [Fact]
    public void TheSelectionFollowsTheTopMatch()
    {
        // What Enter runs must always be the command the palette is reporting, so the selection
        // is reset on every keystroke rather than trying to hold on to a filtered-out command.
        var vm = Vm("Go to Folder", "Reply", "Next Theme");
        Assert.Equal("Go to Folder", vm.SelectedCommand?.Title);

        vm.SearchText = "reply";

        Assert.Equal("Reply", vm.SelectedCommand?.Title);
        Assert.Same(vm.FilteredCommands[0], vm.SelectedCommand);
    }

    [Fact]
    public void WithNoMatches_NothingIsSelectedAndTheCommandCannotRun()
    {
        var vm = Vm("Go to Folder", "Reply");

        vm.SearchText = "zzz";

        Assert.Empty(vm.FilteredCommands);
        Assert.Null(vm.SelectedCommand);
        Assert.False(vm.ExecuteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void CommandsStaysTheFullUnfilteredSet()
    {
        var vm = Vm("Go to Folder", "Reply");

        vm.SearchText = "reply";

        Assert.Equal(2, vm.Commands.Count);
        Assert.Single(vm.FilteredCommands);
    }

    // ── The announcement text, used by the fallback reporting mode ──

    [Fact]
    public void TheSummaryNamesTheCommandAndItsPlace()
    {
        var vm = Vm("Go to Folder", "Go to Date", "Reply");

        vm.SearchText = "go to";

        Assert.Equal($"{vm.FilteredCommands[0].Title}, 1 of 2", vm.ActiveCommandSummary);
    }

    [Fact]
    public void TheSummaryFollowsTheSelection()
    {
        var vm = Vm("Go to Folder", "Go to Date", "Reply");
        vm.SearchText = "go to";

        vm.SelectedCommand = vm.FilteredCommands[1];

        Assert.Equal($"{vm.FilteredCommands[1].Title}, 2 of 2", vm.ActiveCommandSummary);
    }

    [Fact]
    public void TheSummarySaysSoWhenNothingMatches()
    {
        var vm = Vm("Go to Folder");

        vm.SearchText = "zzz";

        Assert.Equal("No matching commands", vm.ActiveCommandSummary);
    }

    [Fact]
    public void ASingleMatchIsStillCounted()
    {
        var vm = Vm("Go to Folder", "Reply");

        vm.SearchText = "fold";

        Assert.Equal("Go to Folder, 1 of 1", vm.ActiveCommandSummary);
    }
}
