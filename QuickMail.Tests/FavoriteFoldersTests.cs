// Favorite folders — issue #1056, with the arranging of #1058.
//
// Pinned here:
//   * toggling adds the folder to a Favorites section (saved on its account) and toggling again
//     removes it; the heading exists only while there is a favorite to show;
//   * a favorite holds the very folder object its account's node holds, so it opens the same folder
//     and its unread count is the same count;
//   * order: added last, across accounts, and rearranged with the move commands; stale entries keep
//     their places; a missing Order sorts by when it was added;
//   * labels: "Inbox" with one account, "Inbox - Work" with more, the count in the accessible name;
//   * what cannot be a favorite, and the sentence for each;
//   * moves and deletes forget favorites, hiding keeps them, and conversion remaps them;
//   * persistence through accounts.json;
//   * the three entry points and the key.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;
using static QuickMail.Tests.FolderTreeFixture;

namespace QuickMail.Tests;

public class FavoriteFoldersTests
{
    private static Action<AccountModel, AccountModel> Favorites(
        params (Guid Account, string FullName, int Minute, int? Order)[] entries) => (work, home) =>
    {
        foreach (var (account, fullName, minute, order) in entries)
            (account == Work ? work : home).FavoriteFolders.Add(new FavoriteFolder
            {
                FullName = fullName,
                AddedUtc = new DateTime(2026, 11, 21, 15, minute, 0, DateTimeKind.Utc),
                Order = order,
            });
    };

    // ── Adding and removing ─────────────────────────────────────────────────────

    [Fact]
    public async Task NoFavorites_NoHeading()
    {
        var (vm, _, _) = await MakeAsync();
        Assert.Null(FavoritesHeader(vm));
    }

    [Fact]
    public async Task Toggle_AddsTheFolder_SavesItOnTheAccount_AndSharesTheFolderObject()
    {
        var (vm, accounts, _) = await MakeAsync();
        var lists = NodeFor(vm, Work, "Lists");
        var saves = accounts.Saves;

        var message = vm.ToggleFavoriteFolder(lists);

        Assert.Equal("Lists - Work added to Favorites.", message);
        Assert.Equal(saves + 1, accounts.Saves);
        var entry = Assert.Single(vm.Accounts.Single(a => a.Id == Work).FavoriteFolders);
        Assert.Equal("Lists", entry.FullName);
        Assert.Equal(0, entry.Order);
        Assert.True((DateTime.UtcNow - entry.AddedUtc).Duration() < TimeSpan.FromMinutes(1));
        var favorite = Assert.Single(FavoritesHeader(vm)!.Children);
        Assert.Same(NodeFor(vm, Work, "Lists").Folder, favorite.Folder);
        Assert.True(vm.IsFavoriteFolder(lists));
        Assert.True(vm.IsFavoriteFolder(favorite));
    }

    [Fact]
    public async Task ASecondToggle_RemovesIt_AndTheHeadingGoesWhenEmpty()
    {
        var (vm, _, _) = await MakeAsync();
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));

        Assert.Equal("Lists - Work removed from Favorites.", vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists")));
        Assert.Null(FavoritesHeader(vm));
        Assert.Empty(vm.Accounts.Single(a => a.Id == Work).FavoriteFolders);
    }

    [Fact]
    public async Task TogglingTheFavoriteItself_RemovesIt()
    {
        var (vm, _, _) = await MakeAsync();
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));
        var favorite = FavoritesHeader(vm)!.Children[0];

        Assert.True(vm.IsInFavoritesGroup(favorite));
        Assert.False(vm.IsInFavoritesGroup(NodeFor(vm, Work, "Lists")));
        Assert.Equal("Lists - Work removed from Favorites.", vm.ToggleFavoriteFolder(favorite));
        Assert.Null(FavoritesHeader(vm));
    }

    [Fact]
    public async Task TogglingASavedViewsCopy_ActsOnTheFolder()
    {
        var view = new SavedView
        {
            Name = "Both",
            Folders = [new ViewFolder { AccountId = Work, FolderFullName = "Notes" }, new ViewFolder { AccountId = Home, FolderFullName = "Notes" }],
        };
        var (vm, _, _) = await MakeAsync(views: new ViewsStub(view));
        var copy = Flatten(Root(vm, "Views").Children)
            .First(n => n.Folder is { FullName: "Notes" } f && f.AccountId == Work);

        Assert.Equal("Notes - Work added to Favorites.", vm.ToggleFavoriteFolder(copy));
        Assert.Equal(["Notes - Work"], FavoriteLabels(vm));
    }

    [Fact]
    public async Task TheToggleKeepsTheOtherNodes_SoFocusSurvives()
    {
        var (vm, _, _) = await MakeAsync();
        var inbox = NodeFor(vm, Work, "INBOX");
        var allMail = Root(vm, "All Mail");

        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Home, "Notes"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));

        Assert.Same(inbox, NodeFor(vm, Work, "INBOX"));
        Assert.Same(allMail, Root(vm, "All Mail"));
    }

    // ── Where, and in what order ────────────────────────────────────────────────

    [Fact]
    public async Task Favorites_SitsAfterCalendar_BeforeViewsAndAllMail()
    {
        var (vm, _, _) = await MakeAsync(views: new ViewsStub(new SavedView { Name = "V" }));
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));

        Assert.Equal(["Calendar", "Favorites", "Views", "All Mail", "Work", "Home"], RootLabels(vm));
    }

    [Fact]
    public async Task FavoritesAreInTheOrderAdded_AcrossAccounts_NotGroupedByAccount()
    {
        var (vm, _, _) = await MakeAsync();
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Home, "Notes"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "INBOX"));

        Assert.Equal(["Lists - Work", "Notes - Home", "Inbox - Work"], FavoriteLabels(vm));
    }

    [Fact]
    public async Task ReAddingPutsTheFolderLast()
    {
        var (vm, _, _) = await MakeAsync();
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Home, "Notes"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));

        Assert.Equal(["Notes - Home", "Lists - Work"], FavoriteLabels(vm));
    }

    [Fact]
    public async Task WithoutAnOrder_EntriesSortByWhenTheyWereAdded_AfterNumberedOnes()
    {
        // Written before favorites could be moved: no Order, just AddedUtc.
        var (vm, _, _) = await MakeAsync(arrange: Favorites(
            (Home, "Notes", 5, null), (Work, "Lists", 1, null), (Work, "INBOX", 9, 0)));

        Assert.Equal(["Inbox - Work", "Lists - Work", "Notes - Home"], FavoriteLabels(vm));
    }

    [Fact]
    public async Task ANewFavoriteGoesLast_AfterArrangedOnes()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Home, "Notes", 5, 1), (Work, "Lists", 1, 0)));

        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "INBOX"));

        Assert.Equal(["Lists - Work", "Notes - Home", "Inbox - Work"], FavoriteLabels(vm));
    }

    // ── Moving favorites (#1058) ────────────────────────────────────────────────

    [Fact]
    public async Task AFavoriteMovesAcrossAccounts_AndTheNewOrderIsSaved()
    {
        var (vm, accounts, _) = await MakeAsync();
        vm.ToggleFavoriteFolder(NodeFor(vm, Work, "Lists"));
        vm.ToggleFavoriteFolder(NodeFor(vm, Home, "Notes"));
        var saves = accounts.Saves;
        var notes = FavoritesHeader(vm)!.Children[1];

        Assert.Equal("Moved above Lists - Work.", vm.MoveFolderTreeNode(notes, TreeMove.Up));
        Assert.Equal(["Notes - Home", "Lists - Work"], FavoriteLabels(vm));
        Assert.Equal(saves + 1, accounts.Saves);
        Assert.Equal(0, vm.Accounts.Single(a => a.Id == Home).FavoriteFolders.Single().Order);
        Assert.Equal(1, vm.Accounts.Single(a => a.Id == Work).FavoriteFolders.Single().Order);
        Assert.Same(notes, FavoritesHeader(vm)!.Children[0]);   // the same node, so focus can go back to it
    }

    [Fact]
    public async Task AtTheEndsOfFavorites_ItSaysSo()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Work, "Lists", 1, 0), (Home, "Notes", 2, 1)));
        var first = FavoritesHeader(vm)!.Children[0];
        var last = FavoritesHeader(vm)!.Children[1];

        Assert.Equal("Already at the start of Favorites.", vm.MoveFolderTreeNode(first, TreeMove.Up));
        Assert.Equal("Already at the start of Favorites.", vm.MoveFolderTreeNode(first, TreeMove.ToStart));
        Assert.Equal("Already at the end of Favorites.", vm.MoveFolderTreeNode(last, TreeMove.Down));
        Assert.Equal("Already at the end of Favorites.", vm.MoveFolderTreeNode(last, TreeMove.ToEnd));
    }

    [Fact]
    public async Task WithOneFavorite_ThereIsNothingToReorder()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Work, "Lists", 1, 0)));

        Assert.Equal("There is only one favorite, so there is nothing to reorder.",
                     vm.MoveFolderTreeNode(FavoritesHeader(vm)!.Children[0], TreeMove.Down));
    }

    [Fact]
    public async Task AStaleFavorite_KeepsItsPlaceThroughAMove()
    {
        // "Gone" has no folder this session: not shown, but still between Lists and Notes.
        var (vm, _, _) = await MakeAsync(arrange: Favorites(
            (Work, "Lists", 1, 0), (Work, "Gone", 2, 1), (Home, "Notes", 3, 2)));
        Assert.Equal(["Lists - Work", "Notes - Home"], FavoriteLabels(vm));

        Assert.Equal("Moved above Lists - Work.", vm.MoveFolderTreeNode(FavoritesHeader(vm)!.Children[1], TreeMove.Up));

        var work = vm.Accounts.Single(a => a.Id == Work).FavoriteFolders;
        Assert.Equal(1, work.Single(f => f.FullName == "Lists").Order);
        Assert.Equal(2, work.Single(f => f.FullName == "Gone").Order);
        Assert.Equal(0, vm.Accounts.Single(a => a.Id == Home).FavoriteFolders.Single().Order);
    }

    [Fact]
    public async Task TheFavoritesSectionItselfMovesAmongTheTopLevel()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Work, "Lists", 1, 0)));

        Assert.Equal("Moved below Home.", vm.MoveFolderTreeNode(FavoritesHeader(vm), TreeMove.ToEnd));
        Assert.Equal(["Calendar", "All Mail", "Work", "Home", "Favorites"], RootLabels(vm));
    }

    // ── Labels ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithOneAccount_TheLabelIsTheFolderName()
    {
        var (vm, _, _) = await MakeAsync();
        vm.Accounts.Remove(vm.Accounts.Single(a => a.Id == Home));

        Assert.Equal("Inbox added to Favorites.", vm.ToggleFavoriteFolder(NodeFor(vm, Work, "INBOX")));
        Assert.Equal(["Inbox"], FavoriteLabels(vm));
    }

    [Fact]
    public async Task TheUnreadCountIsInTheAccessibleName_AndFollowsTheFolder()
    {
        var (vm, _, _) = await MakeAsync();
        var lists = NodeFor(vm, Work, "Lists");
        lists.Folder!.UnreadCount = 4;
        vm.ToggleFavoriteFolder(lists);
        var favorite = FavoritesHeader(vm)!.Children[0];

        Assert.Equal("Lists - Work, 4 unread", favorite.AutomationName);
        lists.Folder.UnreadCount = 3;   // one folder object, so one count
        Assert.Equal("Lists - Work, 3 unread", favorite.AutomationName);
        Assert.Equal("Favorites", FavoritesHeader(vm)!.AutomationName);
    }

    // ── What can be a favorite ──────────────────────────────────────────────────

    [Fact]
    public async Task TheInboxCanBeAFavorite()
    {
        var (vm, _, _) = await MakeAsync();
        Assert.Null(vm.FavoriteFolderRefusal(NodeFor(vm, Work, "INBOX")));
    }

    [Theory]
    [InlineData("null", "Select a folder in the folder tree first.")]
    [InlineData("account", "'Work' is an account, not a folder, so it cannot be a favorite.")]
    [InlineData("favorites", "'Favorites' only groups the folders inside it, so it cannot be a favorite.")]
    [InlineData("allmailheader", "'All Mail' only groups the folders inside it, so it cannot be a favorite.")]
    [InlineData("allinboxes", "'All Inboxes' is a view, not a folder, so it cannot be a favorite.")]
    [InlineData("accountallmail", "'All Mail — Work' is a view, not a folder, so it cannot be a favorite.")]
    [InlineData("calendar", "'Calendar' is a calendar, not a mail folder, so it cannot be a favorite.")]
    [InlineData("segment", "'Lists' only groups the folders inside it, so it cannot be a favorite.")]
    public async Task WhatIsNotAMailFolder_IsRefused_WithTheReason(string which, string expected)
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Work, "Notes", 1, 0)));
        FolderTreeNode? node = which switch
        {
            "null"           => null,
            "account"        => Root(vm, "Work"),
            "favorites"      => FavoritesHeader(vm),
            "allmailheader"  => Root(vm, "All Mail"),
            "allinboxes"     => Root(vm, "All Mail").Children.Single(c => c.Label == "All Inboxes"),
            "accountallmail" => Root(vm, "Work").Children[0],
            "calendar"       => Root(vm, "Calendar"),
            _                => new FolderTreeNode { Label = "Lists" },   // a path segment: no folder
        };
        var before = vm.Accounts.Sum(a => a.FavoriteFolders.Count);

        Assert.Equal(expected, vm.ToggleFavoriteFolder(node));
        Assert.Equal(before, vm.Accounts.Sum(a => a.FavoriteFolders.Count));
    }

    [Fact]
    public async Task TheOutboxIsRefused()
    {
        var (vm, _, _) = await MakeAsync();
        var outbox = new FolderTreeNode { Folder = MainViewModel.OutboxFolder, Label = "Outbox" };
        Assert.Equal("'Outbox' is a view, not a folder, so it cannot be a favorite.", vm.FavoriteFolderRefusal(outbox));
    }

    // ── Stale entries, moves, deletes, hiding ───────────────────────────────────

    [Fact]
    public async Task AnEntryWithNoLoadedFolder_IsNotShownButKept()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Home, "Notes", 1, 0)), connectHome: false);

        Assert.Null(FavoritesHeader(vm));
        Assert.Single(vm.Accounts.Single(a => a.Id == Home).FavoriteFolders);
    }

    [Fact]
    public async Task Forget_DropsTheFolderAndWhatIsInside_ButNotNamesThatMerelyStartTheSame()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites(
            (Work, "Archive", 1, 0), (Work, "Archive/2020", 2, 1), (Work, "Archives", 3, 2), (Work, "Archive2", 4, 3)));

        Assert.True(vm.ForgetFavoriteFolders(Work, "Archive"));

        Assert.Equal(["Archives", "Archive2"], vm.Accounts.Single(a => a.Id == Work).FavoriteFolders.Select(f => f.FullName));
        Assert.False(vm.ForgetFavoriteFolders(Work, "Nothing"));
    }

    [Fact]
    public async Task HidingAFavorite_KeepsItInFavorites_AndSaysSo()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Work, "Notes", 1, 0)));

        var message = vm.HideFolder(NodeFor(vm, Work, "Notes"));

        Assert.Equal("Notes hidden. It still shows in Favorites.", message);
        Assert.Equal(["Notes - Work"], FavoriteLabels(vm));
    }

    // ── Remapping (IMAP to Graph, settings import) ──────────────────────────────

    [Fact]
    public void TheRemapper_RewritesFavorites_KeepingWhenAddedAndOrder_AndDropsWhatCannotMatch()
    {
        var added = new DateTime(2026, 11, 21, 15, 0, 0, DateTimeKind.Utc);
        var favorites = new List<FavoriteFolder>
        {
            new() { FullName = "Notes", AddedUtc = added, Order = 3 },
            new() { FullName = "Gone", AddedUtc = added, Order = 4 },
        };
        var graph = new List<MailFolderModel> { F("graph-notes-id", "Notes", Work) };

        var report = FolderReferenceRemapper.Remap(Work, graph, [], [], new ConfigModel(), null, favorites);

        Assert.True(report.FavoriteFoldersChanged);
        Assert.False(report.AnythingChanged);   // the caller saves the account itself
        var only = Assert.Single(favorites);
        Assert.Equal(("graph-notes-id", added, (int?)3), (only.FullName, only.AddedUtc, only.Order));
    }

    [Fact]
    public void TheRemapper_LeavesAlreadyRemappedFavoritesAlone()
    {
        var favorites = new List<FavoriteFolder> { new() { FullName = "graph-notes-id" } };
        var report = FolderReferenceRemapper.Remap(Work, [F("graph-notes-id", "Notes", Work)], [], [], new ConfigModel(), null, favorites);
        Assert.False(report.FavoriteFoldersChanged);
    }

    // ── Persistence ─────────────────────────────────────────────────────────────

    [Fact]
    public void Favorites_RoundTripThroughAccountsJson_AndAMissingPropertyLoadsEmpty()
    {
        var account = new AccountModel { Id = Work, Username = "w@example.com" };
        account.FavoriteFolders.Add(new FavoriteFolder { FullName = "INBOX", AddedUtc = new DateTime(2026, 11, 21, 15, 2, 11, DateTimeKind.Utc), Order = 0 });

        var back = JsonSerializer.Deserialize<AccountModel>(JsonSerializer.Serialize(account))!;
        var entry = Assert.Single(back.FavoriteFolders);
        Assert.Equal(("INBOX", 0), (entry.FullName, entry.Order ?? -1));

        var old = JsonSerializer.Deserialize<AccountModel>("""{ "Username": "w@example.com" }""")!;
        Assert.Empty(old.FavoriteFolders);
    }

    // ── Focus helper (returning to the tree lands on the copy you left) ─────────

    [Fact]
    public async Task ReturningToTheTree_PrefersTheCopyTheUserLeftFrom()
    {
        var (vm, _, _) = await MakeAsync(arrange: Favorites((Work, "Notes", 1, 0)));
        var favorite = FavoritesHeader(vm)!.Children[0];
        var own = NodeFor(vm, Work, "Notes");

        Assert.Same(favorite, TreeViewFocusHelper.PreferredFolderTreeNode(vm.FolderTree!, favorite, own.Folder!));
        Assert.Same(own, TreeViewFocusHelper.PreferredFolderTreeNode(vm.FolderTree!, own, own.Folder!));
        // Nothing current, or a current node showing something else: the first match.
        Assert.Same(favorite, TreeViewFocusHelper.PreferredFolderTreeNode(vm.FolderTree!, null, own.Folder!));
        Assert.Same(favorite, TreeViewFocusHelper.PreferredFolderTreeNode(vm.FolderTree!, NodeFor(vm, Work, "Lists"), own.Folder!));
        // A current node no longer in the tree is not kept.
        var orphan = new FolderTreeNode { Folder = own.Folder, Label = "Notes" };
        Assert.Same(favorite, TreeViewFocusHelper.PreferredFolderTreeNode(vm.FolderTree!, orphan, own.Folder!));
    }

    // ── Entry points ────────────────────────────────────────────────────────────

    private static string Source(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new InvalidOperationException(relative + " not found.");
    }

    [Fact]
    public void TheCommandIsRegisteredOnCtrlD()
    {
        var source = Source(Path.Combine("QuickMail", "Views", "MainWindow.xaml.cs"));
        Assert.Contains("id: \"folder.toggleFavorite\", category: \"Mail\", title: \"Toggle Favorite Folder\"", source, StringComparison.Ordinal);
        Assert.Contains("defaultKey: Key.D, defaultModifiers: ModifierKeys.Control,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BothMenusHaveACheckableFavoriteItem_WithItsHandlers()
    {
        var xaml = Source(Path.Combine("QuickMail", "Views", "MainWindow.xaml"));
        Assert.Contains("Loaded=\"FolderContextMenu_FavoriteItem_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"FolderContextMenu_ToggleFavorite_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"MenuFavoriteItem_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"MenuToggleFavorite_Click\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("sc:CommandShortcut.Id=\"folder.toggleFavorite\" Header=\"Fa_vorite Folder\"").Length - 1);
    }

    [Theory]
    [InlineData("favorite")]
    [InlineData("add to favorites")]
    [InlineData("remove favorite")]
    public void ThePaletteFindsItFirst(string query)
    {
        var commands = new List<CommandDefinition>
        {
            new("folder.toggleFavorite", "Mail", "Toggle Favorite Folder", () => { },
                description: "Add the folder to Favorites, or remove it from Favorites"),
            new("folder.hide", "Mail", "Hide Folder", () => { },
                description: "Hide the selected folder, and the folders inside it, from the folder list"),
            new("folder.new", "Mail", "New Folder", () => { }),
            new("mail.flag", "Mail", "Flag Message", () => { }),
        };

        var ranked = CommandMatcher.Rank(commands, query);

        Assert.Equal("folder.toggleFavorite", ranked.First().Id);
    }
}
