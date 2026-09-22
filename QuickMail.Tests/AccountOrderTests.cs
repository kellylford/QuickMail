using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Account order is not a field on AccountModel — it IS the array order in accounts.json, and every
/// consumer reads it straight through: the account list, the folder tree's account roots, and the
/// compose window's From picker, which re-reads the file rather than sharing the ViewModel's
/// collection. So the thing worth guarding is that a move writes the new order back. A reorder that
/// only shuffles the ObservableCollection looks completely correct until the window is reopened, at
/// which point it is gone — and the From picker never sees it at all.
///
/// The announcement text is guarded too. It names the neighbour the account landed next to ("Moved
/// above Work") rather than a position number, which is what tells someone arrowing the list what
/// actually happened; a refactor that reduced it to "Moved." would pass every other assertion here.
/// </summary>
public class AccountOrderTests
{
    /// <summary>Records what was written back, which is the half of a move a test cannot see in
    /// <c>vm.Accounts</c>.</summary>
    private sealed class RecordingAccountService : IAccountService
    {
        public List<List<AccountModel>> Saved { get; } = [];
        public List<AccountModel> LoadAccounts() => [];
        public void SaveAccounts(List<AccountModel> accounts) => Saved.Add([.. accounts]);
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (MainViewModel vm, RecordingAccountService accounts) MakeVm(params string[] names)
    {
        var accountService = new RecordingAccountService();
        var vm = new MainViewModel(new StubImapMailService(), accountService, new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

        foreach (var name in names)
            vm.Accounts.Add(new AccountModel { Id = Guid.NewGuid(), AccountName = name });

        return (vm, accountService);
    }

    private static string Order(MainViewModel vm) => string.Join(",", vm.Accounts.Select(a => a.AccountLabel));

    private static string SavedOrder(RecordingAccountService accounts) =>
        string.Join(",", accounts.Saved[^1].Select(a => a.AccountLabel));

    [Fact]
    public void MoveUp_SwapsWithTheAccountAboveAndNamesIt()
    {
        var (vm, saved) = MakeVm("Home", "Work", "List");

        var message = vm.MoveAccountUp(vm.Accounts[1]);

        Assert.Equal("Work,Home,List", Order(vm));
        Assert.Equal("Moved above Home.", message);
        Assert.Equal("Work,Home,List", SavedOrder(saved));
    }

    [Fact]
    public void MoveDown_SwapsWithTheAccountBelowAndNamesIt()
    {
        var (vm, saved) = MakeVm("Home", "Work", "List");

        var message = vm.MoveAccountDown(vm.Accounts[0]);

        Assert.Equal("Work,Home,List", Order(vm));
        Assert.Equal("Moved below Work.", message);
        Assert.Equal("Work,Home,List", SavedOrder(saved));
    }

    [Fact]
    public void MoveToStart_LandsFirstAndNamesWhatItIsNowAbove()
    {
        var (vm, saved) = MakeVm("Home", "Work", "List");

        var message = vm.MoveAccountToStart(vm.Accounts[2]);

        Assert.Equal("List,Home,Work", Order(vm));
        Assert.Equal("Moved above Home.", message);
        Assert.Equal("List,Home,Work", SavedOrder(saved));
    }

    [Fact]
    public void MoveToEnd_LandsLastAndNamesWhatItIsNowBelow()
    {
        var (vm, saved) = MakeVm("Home", "Work", "List");

        var message = vm.MoveAccountToEnd(vm.Accounts[0]);

        Assert.Equal("Work,List,Home", Order(vm));
        Assert.Equal("Moved below List.", message);
        Assert.Equal("Work,List,Home", SavedOrder(saved));
    }

    /// <summary>
    /// The folder tree's account roots are built from the same collection, so they have to follow.
    /// They are what the user is actually looking at most of the time — a reorder that moved the
    /// account list and left the tree alone would look like it had not worked.
    /// </summary>
    [Fact]
    public void TheFolderTreesAccountRootsFollow()
    {
        var (vm, _) = MakeVm("Home", "Work", "List");

        vm.MoveAccountToEnd(vm.Accounts[0]);

        Assert.NotNull(vm.FolderTree);
        var roots = vm.FolderTree!.Select(n => n.Label).Where(l => l is "Home" or "Work" or "List").ToList();
        Assert.Equal(new[] { "Work", "List", "Home" }, roots);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────
    // Each says what happened rather than doing nothing: a silently no-op menu item is the dead end
    // #250 was filed about, and at the end of a list it is the case the user hits most.

    [Fact]
    public void MoveUp_AtTheTop_SaysSoAndWritesNothing()
    {
        var (vm, saved) = MakeVm("Home", "Work");

        Assert.Equal("Already at the start of the account list.", vm.MoveAccountUp(vm.Accounts[0]));
        Assert.Equal("Home,Work", Order(vm));
        Assert.Empty(saved.Saved);
    }

    [Fact]
    public void MoveDown_AtTheBottom_SaysSoAndWritesNothing()
    {
        var (vm, saved) = MakeVm("Home", "Work");

        Assert.Equal("Already at the end of the account list.", vm.MoveAccountDown(vm.Accounts[1]));
        Assert.Equal("Home,Work", Order(vm));
        Assert.Empty(saved.Saved);
    }

    [Fact]
    public void MoveToEnd_WhenAlreadyLast_SaysSo()
    {
        var (vm, saved) = MakeVm("Home", "Work");

        Assert.Equal("Already at the end of the account list.", vm.MoveAccountToEnd(vm.Accounts[1]));
        Assert.Empty(saved.Saved);
    }

    [Fact]
    public void MoveToStart_WhenAlreadyFirst_SaysSo()
    {
        var (vm, saved) = MakeVm("Home", "Work");

        Assert.Equal("Already at the start of the account list.", vm.MoveAccountToStart(vm.Accounts[0]));
        Assert.Empty(saved.Saved);
    }

    [Fact]
    public void WithOneAccount_EveryMoveSaysThereIsNothingToReorder()
    {
        var (vm, saved) = MakeVm("Home");
        var only = vm.Accounts[0];

        Assert.Equal("There is only one account, so there is nothing to reorder.", vm.MoveAccountToEnd(only));
        Assert.Equal("There is only one account, so there is nothing to reorder.", vm.MoveAccountToStart(only));
        Assert.Empty(saved.Saved);
    }

    [Fact]
    public void WithNoSelection_EveryMoveAsksForOne()
    {
        var (vm, saved) = MakeVm("Home", "Work");

        Assert.Equal("Select an account first.", vm.MoveAccountUp(null));
        Assert.Equal("Select an account first.", vm.MoveAccountDown(null));
        Assert.Equal("Select an account first.", vm.MoveAccountToStart(null));
        Assert.Equal("Select an account first.", vm.MoveAccountToEnd(null));
        Assert.Empty(saved.Saved);
    }

    /// <summary>
    /// An account the collection does not hold — a stale reference from a list rebuilt underneath a
    /// still-open context menu — must not be treated as "at position -1" and moved to the top.
    /// </summary>
    [Fact]
    public void AnAccountNotInTheList_IsRefused()
    {
        var (vm, saved) = MakeVm("Home", "Work");
        var stranger = new AccountModel { Id = Guid.NewGuid(), AccountName = "Gone" };

        Assert.Equal("Select an account first.", vm.MoveAccountUp(stranger));
        Assert.Equal("Select an account first.", vm.MoveAccountToStart(stranger));
        Assert.Equal("Home,Work", Order(vm));
        Assert.Empty(saved.Saved);
    }
}
