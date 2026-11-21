// Arranging the folder tree — issue #1058.
//
// Pinned here:
//   * FolderTreeOrder.Normalize makes a stored order whole for any number of accounts;
//   * ListMove moves among the visible items and leaves hidden ones in place;
//   * FolderTreeOrder round-trips through config.ini, CalendarAtEndOfFolderList migrates to it, and
//     the old key is no longer written;
//   * in the ViewModel, an account moving past an account reorders accounts.json, while one moving
//     past a section only changes the stored order; sections move past hidden sections; the moved
//     node is the same object afterwards (so the View can put focus back on it); the order survives
//     a restart; removing an account keeps everyone else's place; and anything that is not an
//     account, a section or a favorite says it cannot be moved.
//
// Replaces CalendarFolderPositionTests: Calendar's place is now just its place in this order.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;
using static QuickMail.Tests.FolderTreeFixture;

namespace QuickMail.Tests;

public class FolderTreeOrderTests
{
    private static string N(string stored, int accounts) =>
        FolderTreeOrder.Format(FolderTreeOrder.Normalize(FolderTreeOrder.Parse(stored), accounts));

    [Fact]
    public void Empty_GivesTheDefault_SectionsThenEveryAccount()
    {
        Assert.Equal("calendar, favorites, views, allmail, account, account", N("", 2));
        Assert.Equal("calendar, favorites, views, allmail", N("", 0));
    }

    [Fact]
    public void UnknownAndRepeatedTokensAreDropped_AndCaseIsIgnored()
        => Assert.Equal("allmail, calendar, favorites, views, account",
                        N("AllMail, bogus, Calendar, CALENDAR, Favorites, views, Account", 1));

    [Fact]
    public void AMissingSectionGoesBackBesideItsDefaultNeighbour()
    {
        // Favorites belongs after Calendar, and Views after Favorites; with Calendar gone too, the
        // first missing section goes before the next one present.
        Assert.Equal("account, calendar, favorites, views, allmail", N("account, calendar, views, allmail", 1));
        Assert.Equal("allmail, calendar, favorites, views", N("allmail, calendar", 0));
        Assert.Equal("account, calendar, favorites, views, allmail", N("account, views, allmail", 1));
    }

    [Fact]
    public void MissingAccountsGoAfterTheLastAccount_SoANewAccountAppearsAfterTheOthers()
    {
        Assert.Equal("account, account, account, calendar, favorites, views, allmail",
                     N("account, calendar, favorites, views, allmail", 3));
        // None stored at all: at the end.
        Assert.Equal("calendar, favorites, views, allmail, account, account", N("calendar, favorites, views, allmail", 2));
    }

    [Fact]
    public void ExtraAccountsAreRemovedFromTheEnd()
        => Assert.Equal("account, calendar, favorites, views, allmail",
                        N("account, calendar, favorites, account, views, allmail, account", 1));

    [Fact]
    public void TheRetiredCalendarSettingMeansTheSameTreeAsBefore()
    {
        var accounts = 2;
        Assert.Equal("calendar, favorites, views, allmail, account, account",
                     N(FolderTreeOrder.FromCalendarAtEnd(false), accounts));
        Assert.Equal("favorites, views, allmail, account, account, calendar",
                     N(FolderTreeOrder.FromCalendarAtEnd(true), accounts));
    }

    // ── ListMove ────────────────────────────────────────────────────────────────

    private static (List<string> All, ListMoveResult<string> Result) Move(string all, string item, TreeMove move, string hidden = "")
    {
        var list = all.Split(' ').ToList();
        var hide = hidden.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var result = ListMove.Apply(list, s => !hide.Contains(s), list.IndexOf(item), move);
        return (list, result);
    }

    [Fact]
    public void UpAndDown_PassTheVisibleNeighbour_LeavingHiddenItemsWhereTheyAre()
    {
        var (up, r1) = Move("a b c d", "d", TreeMove.Up, hidden: "c");
        Assert.Equal("a d b c", string.Join(" ", up));
        Assert.Equal((ListMoveOutcome.Moved, "b", true), (r1.Outcome, r1.Neighbour, r1.Above));

        var (down, r2) = Move("a b c d", "a", TreeMove.Down, hidden: "b");
        Assert.Equal("b c a d", string.Join(" ", down));
        Assert.Equal((ListMoveOutcome.Moved, "c", false), (r2.Outcome, r2.Neighbour, r2.Above));
    }

    [Fact]
    public void StartAndEnd_GoToTheVeryEnds_NamingTheVisibleItemPassed()
    {
        var (start, r1) = Move("a b c d", "c", TreeMove.ToStart, hidden: "a");
        Assert.Equal("c a b d", string.Join(" ", start));
        Assert.Equal(("b", true), (r1.Neighbour, r1.Above));

        var (end, r2) = Move("a b c d", "b", TreeMove.ToEnd, hidden: "d");
        Assert.Equal("a c d b", string.Join(" ", end));
        Assert.Equal(("c", false), (r2.Neighbour, r2.Above));
    }

    [Fact]
    public void AtTheEnds_AndAlone_NothingMoves()
    {
        Assert.Equal(ListMoveOutcome.AlreadyAtStart, Move("a b c", "b", TreeMove.Up, hidden: "a").Result.Outcome);
        Assert.Equal(ListMoveOutcome.AlreadyAtStart, Move("a b c", "a", TreeMove.ToStart).Result.Outcome);
        Assert.Equal(ListMoveOutcome.AlreadyAtEnd, Move("a b c", "c", TreeMove.Down).Result.Outcome);
        Assert.Equal(ListMoveOutcome.AlreadyAtEnd, Move("a b c", "b", TreeMove.ToEnd, hidden: "c").Result.Outcome);
        Assert.Equal(ListMoveOutcome.NothingToReorder, Move("a b c", "b", TreeMove.Up, hidden: "a c").Result.Outcome);
        var (unchanged, _) = Move("a b c", "b", TreeMove.Up, hidden: "a");
        Assert.Equal("a b c", string.Join(" ", unchanged));
    }

    [Fact]
    public void DuplicateValuesMoveByPosition()
    {
        // The stored order has one "account" per account; moving one must not move a twin.
        var (list, _) = Move("x account account", "x", TreeMove.Down);
        Assert.Equal("account x account", string.Join(" ", list));
    }

    // ── config.ini ──────────────────────────────────────────────────────────────

    private static ConfigModel Parse(params string[] lines) => ConfigService.ParseFile(["[global]", .. lines]);

    [Fact]
    public void FolderTreeOrder_RoundTripsThroughConfigIni()
    {
        var profile = new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-TREEORDER-{Guid.NewGuid():N}"));
        var config = new ConfigService(profile).Load();
        config.FolderTreeOrder = "allmail, account, calendar, favorites, views";
        new ConfigService(profile).Save(config);

        Assert.Equal("allmail, account, calendar, favorites, views", new ConfigService(profile).Load().FolderTreeOrder);
    }

    [Theory]
    [InlineData("on", "favorites, views, allmail, account, calendar")]
    [InlineData("off", "calendar, favorites, views, allmail")]
    public void TheOldCalendarSettingAlone_Migrates(string value, string expected)
        => Assert.Equal(expected, Parse($"CalendarAtEndOfFolderList = {value}").FolderTreeOrder);

    [Fact]
    public void WithBothKeys_TheNewOneWins_InEitherOrder()
    {
        Assert.Equal("views, allmail", Parse("CalendarAtEndOfFolderList = on", "FolderTreeOrder = views, allmail").FolderTreeOrder);
        Assert.Equal("views, allmail", Parse("FolderTreeOrder = views, allmail", "CalendarAtEndOfFolderList = on").FolderTreeOrder);
    }

    [Fact]
    public void TheOldKeyIsNoLongerWritten_AndADefaultOrderWritesNothing()
    {
        var text = ConfigService.BuildFileText(new ConfigModel());
        Assert.DoesNotContain("CalendarAtEndOfFolderList", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FolderTreeOrder =", text, StringComparison.Ordinal);

        var arranged = ConfigService.BuildFileText(new ConfigModel { FolderTreeOrder = "allmail, calendar" });
        Assert.Contains("FolderTreeOrder = allmail, calendar", arranged, StringComparison.Ordinal);
    }
}

public class FolderTreeArrangeTests
{
    [Fact]
    public async Task TheDefaultTree_IsCalendarAllMailThenTheAccounts()
    {
        var (vm, _, _) = await MakeAsync();

        Assert.Equal(["Calendar", "All Mail", "Work", "Home"], RootLabels(vm));
    }

    [Fact]
    public async Task AnAccountPastAnAccount_ReordersAccountsJson_AndTheStoredOrderStays()
    {
        var (vm, accounts, config) = await MakeAsync();

        var message = vm.MoveFolderTreeNode(Root(vm, "Home"), TreeMove.Up);

        Assert.Equal("Moved above Work.", message);
        Assert.Equal(["Calendar", "All Mail", "Home", "Work"], RootLabels(vm));
        Assert.Equal([Home, Work], vm.Accounts.Select(a => a.Id));
        Assert.Equal([Home, Work], accounts.LastSaved.Select(a => a.Id));   // the Accounts list and From list read this
        Assert.Equal("calendar, favorites, views, allmail, account, account", config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task AnAccountPastASection_ChangesOnlyTheStoredOrder()
    {
        var (vm, accounts, config) = await MakeAsync();
        var savesBefore = accounts.Saves;

        var message = vm.MoveFolderTreeNode(Root(vm, "Work"), TreeMove.Up);

        Assert.Equal("Moved above All Mail.", message);
        Assert.Equal(["Calendar", "Work", "All Mail", "Home"], RootLabels(vm));
        Assert.Equal([Work, Home], vm.Accounts.Select(a => a.Id));
        Assert.Equal(savesBefore, accounts.Saves);
        Assert.Equal("calendar, favorites, views, account, allmail, account", config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task ASectionMovesPastHiddenSections_ToTheVisibleOneAbove()
    {
        // Favorites (no favorites) and Views (no saved views) are not showing between Calendar and
        // All Mail, so Alt+Up on All Mail passes straight to Calendar.
        var (vm, _, config) = await MakeAsync();

        Assert.Equal("Moved above Calendar.", vm.MoveFolderTreeNode(Root(vm, "All Mail"), TreeMove.Up));
        Assert.Equal(["All Mail", "Calendar", "Work", "Home"], RootLabels(vm));
        Assert.Equal("allmail, calendar, favorites, views, account, account", config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task StartAndEnd_GoToTheTopAndBottomOfTheTree()
    {
        var (vm, _, _) = await MakeAsync();

        Assert.Equal("Moved below Home.", vm.MoveFolderTreeNode(Root(vm, "Calendar"), TreeMove.ToEnd));
        Assert.Equal(["All Mail", "Work", "Home", "Calendar"], RootLabels(vm));

        // An account to the start passes the other account too, so accounts.json follows.
        Assert.Equal("Moved above All Mail.", vm.MoveFolderTreeNode(Root(vm, "Home"), TreeMove.ToStart));
        Assert.Equal(["Home", "All Mail", "Work", "Calendar"], RootLabels(vm));
        Assert.Equal([Home, Work], vm.Accounts.Select(a => a.Id));
    }

    [Fact]
    public async Task AtTheEnds_ItSaysSo_AndNothingChanges()
    {
        var (vm, _, config) = await MakeAsync();

        Assert.Equal("Already at the top of the folder list.", vm.MoveFolderTreeNode(Root(vm, "Calendar"), TreeMove.Up));
        Assert.Equal("Already at the top of the folder list.", vm.MoveFolderTreeNode(Root(vm, "Calendar"), TreeMove.ToStart));
        Assert.Equal("Already at the bottom of the folder list.", vm.MoveFolderTreeNode(Root(vm, "Home"), TreeMove.Down));
        Assert.Equal("Already at the bottom of the folder list.", vm.MoveFolderTreeNode(Root(vm, "Home"), TreeMove.ToEnd));
        Assert.Equal(["Calendar", "All Mail", "Work", "Home"], RootLabels(vm));
        Assert.Equal(string.Empty, config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task TheMovedNodeIsTheSameObjectAfterwards_SoFocusCanGoBackToIt()
    {
        var (vm, _, _) = await MakeAsync();
        var allMail = Root(vm, "All Mail");
        var home = Root(vm, "Home");
        var inbox = NodeFor(vm, Work, "INBOX");

        vm.MoveFolderTreeNode(allMail, TreeMove.ToEnd);
        vm.MoveFolderTreeNode(home, TreeMove.Up);   // past Work: the accounts.json path

        Assert.Same(allMail, Root(vm, "All Mail"));
        Assert.Same(home, Root(vm, "Home"));
        Assert.Same(inbox, NodeFor(vm, Work, "INBOX"));
    }

    [Fact]
    public async Task TheOrderIsThereAtTheNextStart()
    {
        var config = new StubConfigService();
        var (first, _, _) = await MakeAsync(config: config);
        first.MoveFolderTreeNode(Root(first, "Calendar"), TreeMove.ToEnd);
        first.MoveFolderTreeNode(Root(first, "Work"), TreeMove.ToStart);

        var (second, _, _) = await MakeAsync(config: config);

        Assert.Equal(["Work", "All Mail", "Home", "Calendar"], RootLabels(second));
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("view")]
    [InlineData("allmailchild")]
    [InlineData("calendarchild")]
    public async Task WhatIsNotAnAccountASectionOrAFavorite_SaysItCannotBeMoved(string which)
    {
        var view = new SavedView { Name = "Both" };
        var (vm, _, _) = await MakeAsync(views: new ViewsStub(view));
        var node = which switch
        {
            "folder"       => NodeFor(vm, Work, "Lists"),
            "view"         => Root(vm, "Views").Children[0],
            "allmailchild" => Root(vm, "All Mail").Children[1],
            _              => Root(vm, "Calendar").Children[0],
        };
        var before = RootLabels(vm);

        Assert.Equal($"'{node.Label}' cannot be moved. Only accounts, sections and favorites can be moved.",
                     vm.MoveFolderTreeNode(node, TreeMove.Up));
        Assert.Equal(before, RootLabels(vm));
    }

    [Fact]
    public async Task WithNothingSelected_ItSaysSo()
    {
        var (vm, _, _) = await MakeAsync();
        Assert.Equal("Select an item in the folder tree first.", vm.MoveFolderTreeNode(null, TreeMove.Up));
    }

    [Fact]
    public async Task ViewsMovesLikeAnyOtherSection_WhenItIsShowing()
    {
        var (vm, _, _) = await MakeAsync(views: new ViewsStub(new SavedView { Name = "Both" }));
        Assert.Equal(["Calendar", "Views", "All Mail", "Work", "Home"], RootLabels(vm));

        Assert.Equal("Moved below All Mail.", vm.MoveFolderTreeNode(Root(vm, "Views"), TreeMove.Down));
        Assert.Equal(["Calendar", "All Mail", "Views", "Work", "Home"], RootLabels(vm));
    }

    [Fact]
    public async Task RemovingAnAccount_KeepsEveryOtherItemInItsPlace()
    {
        var (vm, _, config) = await MakeAsync();
        // Work first, then Calendar and All Mail, then Home.
        vm.MoveFolderTreeNode(Root(vm, "Work"), TreeMove.ToStart);
        Assert.Equal(["Work", "Calendar", "All Mail", "Home"], RootLabels(vm));
        vm.ConfirmationRequested = (_, _) => true;

        await vm.DeleteAccountCommand.ExecuteAsync(vm.Accounts.Single(a => a.Id == Work));

        // Dropping the last placeholder instead would have put Home at the top.
        Assert.Equal(["Calendar", "All Mail", "Home"], RootLabels(vm));
        Assert.Equal("calendar, favorites, views, allmail, account", config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task RemovingAnAccountInManageAccounts_AlsoKeepsEveryOtherItemInItsPlace()
    {
        var (vm, accounts, config) = await MakeAsync();
        vm.MoveFolderTreeNode(Root(vm, "Work"), TreeMove.ToStart);   // Work, Calendar, All Mail, Home
        // Manage Accounts removed Work and saved; the main window reads accounts.json back.
        accounts.ToLoad = [vm.Accounts.Single(a => a.Id == Home)];

        vm.RefreshAccountList();

        Assert.Equal(["Calendar", "All Mail", "Home"], RootLabels(vm));
        Assert.Equal("calendar, favorites, views, allmail, account", config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task AccountsPassingEachOther_LeaveAHiddenSectionBetweenThemWhereItWas()
    {
        // Work, then Views (no saved views, so hidden), then Home.
        var config = new StubConfigService();
        config.Save(new ConfigModel { FolderTreeOrder = "calendar, favorites, allmail, account, views, account" });
        var (vm, _, _) = await MakeAsync(config: config);

        Assert.Equal("Moved above Work.", vm.MoveFolderTreeNode(Root(vm, "Home"), TreeMove.Up));

        Assert.Equal([Home, Work], vm.Accounts.Select(a => a.Id));
        Assert.Equal("calendar, favorites, allmail, account, views, account", config.Load().FolderTreeOrder);
    }

    [Fact]
    public async Task ANewAccount_AppearsAfterTheLastAccount()
    {
        var (vm, _, _) = await MakeAsync();
        vm.MoveFolderTreeNode(Root(vm, "Calendar"), TreeMove.ToEnd);   // All Mail, Work, Home, Calendar

        vm.Accounts.Add(new AccountModel { Id = Guid.NewGuid(), AccountName = "New", Username = "new@example.com" });
        vm.UpdateSavedViews();   // rebuilds the tree

        Assert.Equal(["All Mail", "Work", "Home", "New", "Calendar"], RootLabels(vm));
    }

    [Fact]
    public async Task WithNoCalendarService_ThereIsNoCalendarToPassOver()
    {
        var (vm, _, _) = await MakeAsync(withCalendar: false);

        Assert.Equal(["All Mail", "Work", "Home"], RootLabels(vm));
        Assert.Equal("Already at the top of the folder list.", vm.MoveFolderTreeNode(Root(vm, "All Mail"), TreeMove.Up));
    }
}
