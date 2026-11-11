// Hiding folders from the folder tree — issue #963.
//
// A user asked to take folders they never use out of QuickMail's folder list. Pinned here:
//   * the builder prunes a hidden folder with its whole branch, under both hierarchy models (IMAP
//     paths and Graph parent ids), and drops a path segment that hiding left empty;
//   * the destination pickers are unaffected — they call the builder without a hidden list, so a
//     hidden folder can still be filed into;
//   * Hide Folder refuses, and says why, for anything that is not an ordinary folder, and for the
//     Inbox, which on INBOX-rooted servers holds every other folder;
//   * the list is stored on the account and saved, survives a rebuild, and Show All Hidden Folders
//     brings everything back;
//   * Go to Folder's list agrees with the tree;
//   * all three entry points exist (context menu, Folder menu, Command Palette).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class HiddenFoldersTests
{
    private static readonly Guid AccountA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MailFolderModel F(
        string fullName, string display, string? parentId = null,
        SpecialFolderKind kind = SpecialFolderKind.None, Guid accountId = default)
        => new() { FullName = fullName, DisplayName = display, ParentId = parentId, Kind = kind, AccountId = accountId };

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private static List<string> FolderNames(IEnumerable<FolderTreeNode> roots) =>
        [.. Flatten(roots).Where(n => n.Folder != null).Select(n => n.Folder!.FullName)];

    // ── Builder ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithoutAHiddenList_KeepsEveryFolder()
    {
        // The destination pickers (Move/Copy Folder, rule targets) call Build this way.
        var flat = new List<MailFolderModel> { F("INBOX", "Inbox"), F("Archive", "Archive"), F("Archive/2020", "2020") };

        Assert.Equal(3, FolderNames(FolderTreeBuilder.Build(flat)).Count);
        Assert.Equal(3, FolderNames(FolderTreeBuilder.Build(flat, null, [])).Count);
    }

    [Fact]
    public void Build_Imap_HidingAParentHidesItsWholeBranch()
    {
        var flat = new List<MailFolderModel>
        {
            F("INBOX", "Inbox"), F("Archive", "Archive"), F("Archive/2020", "2020"),
            F("Archive/2020/Q1", "Q1"), F("Receipts", "Receipts"),
        };

        var names = FolderNames(FolderTreeBuilder.Build(flat, null, ["Archive"]));

        Assert.Equal(["INBOX", "Receipts"], names);
    }

    [Fact]
    public void Build_Imap_HidingALeafLeavesItsParentAndSiblings()
    {
        var flat = new List<MailFolderModel>
        {
            F("INBOX", "Inbox"), F("Archive", "Archive"), F("Archive/2020", "2020"), F("Archive/2021", "2021"),
        };

        var names = FolderNames(FolderTreeBuilder.Build(flat, null, ["Archive/2020"]));

        Assert.Equal(["INBOX", "Archive", "Archive/2021"], names);
    }

    [Fact]
    public void Build_Graph_HidingAParentHidesTheFoldersThatPointAtIt()
    {
        // Graph nests by ParentId, not by path, so the prune must work on the tree, not on names.
        var flat = new List<MailFolderModel>
        {
            F("id-inbox", "Inbox", parentId: "root", kind: SpecialFolderKind.Inbox),
            F("id-notes", "Notes", parentId: "root"),
            F("id-notes-old", "Old", parentId: "id-notes"),
            F("id-rss", "RSS Feeds", parentId: "root"),
        };

        var names = FolderNames(FolderTreeBuilder.Build(flat, null, ["id-notes"]));

        Assert.Equal(["id-inbox", "id-rss"], names);
    }

    [Fact]
    public void Build_APathSegmentLeftEmptyByHiding_IsDropped()
    {
        // "Lists" is not a folder of its own — it exists only because "Lists/Dev" does. With its one
        // folder hidden it would be an empty node that opens nothing.
        var flat = new List<MailFolderModel> { F("INBOX", "Inbox"), F("Lists/Dev", "Dev") };

        var roots = FolderTreeBuilder.Build(flat, null, ["Lists/Dev"]);

        Assert.DoesNotContain(Flatten(roots), n => n.Label == "Lists");
    }

    [Fact]
    public void Build_APathSegmentWithAFolderStillShowing_IsKept()
    {
        var flat = new List<MailFolderModel> { F("INBOX", "Inbox"), F("Lists/Dev", "Dev"), F("Lists/Ops", "Ops") };

        var roots = FolderTreeBuilder.Build(flat, null, ["Lists/Dev"]);

        var lists = Assert.Single(Flatten(roots), n => n.Label == "Lists");
        Assert.Equal("Lists/Ops", Assert.Single(lists.Children).Folder!.FullName);
    }

    [Fact]
    public void Build_MatchesFullNamesExactly()
    {
        // Ordinal: a Graph id differing only by case is a different folder, and a name that merely
        // starts the same way ("Arch") is not the folder.
        var flat = new List<MailFolderModel> { F("INBOX", "Inbox"), F("Archive", "Archive") };

        Assert.Equal(2, FolderNames(FolderTreeBuilder.Build(flat, null, ["archive"])).Count);
        Assert.Equal(2, FolderNames(FolderTreeBuilder.Build(flat, null, ["Arch"])).Count);
    }

    [Fact]
    public void VisibleFolders_KeepsTheOriginalOrder_AndDropsDescendants()
    {
        var flat = new List<MailFolderModel>
        {
            F("Receipts", "Receipts"), F("INBOX", "Inbox"), F("Archive/2020", "2020"), F("Archive", "Archive"), F("Zeta", "Zeta"),
        };

        var visible = FolderTreeBuilder.VisibleFolders(flat, ["Archive"]);

        Assert.Equal(["Receipts", "INBOX", "Zeta"], visible.Select(f => f.FullName));
    }

    [Fact]
    public void VisibleFolders_WithNothingHidden_IsTheWholeList()
    {
        var flat = new List<MailFolderModel> { F("INBOX", "Inbox"), F("Archive", "Archive") };

        Assert.Equal(flat, FolderTreeBuilder.VisibleFolders(flat, null));
        Assert.Equal(flat, FolderTreeBuilder.VisibleFolders(flat, []));
    }

    // ── ViewModel ────────────────────────────────────────────────────────────

    private sealed class RecordingAccountService : IAccountService
    {
        public int Saves { get; private set; }
        public List<AccountModel> LoadAccounts() => [];
        public void SaveAccounts(List<AccountModel> accounts) => Saves++;
        public void SetDefaultAccount(Guid accountId) { }
    }

    private sealed class ViewsStub(params SavedView[] views) : IViewService
    {
        public List<SavedView> Load() => [.. views];
        public void Save(List<SavedView> v) { }
    }

    private static Task<(MainViewModel Vm, RecordingAccountService Accounts)> MakeVmAsync(
        params string[] alreadyHidden) => MakeVmWithAsync(alreadyHidden, new StubViewService());

    private static async Task<(MainViewModel Vm, RecordingAccountService Accounts)> MakeVmWithAsync(
        string[] alreadyHidden, IViewService views, params MailFolderModel[] extraA)
    {
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountA] =
            [
                F("INBOX", "Inbox", kind: SpecialFolderKind.Inbox, accountId: AccountA),
                F("Archive", "Archive", accountId: AccountA),
                F("Archive/2020", "2020", accountId: AccountA),
                F("Archive/2021", "2021", accountId: AccountA),
                F("Notes", "Notes", accountId: AccountA),
                .. extraA,
            ],
            [AccountB] =
            [
                F("INBOX", "Inbox", kind: SpecialFolderKind.Inbox, accountId: AccountB),
                F("Notes", "Notes", accountId: AccountB),
            ],
        };

        var accounts = new RecordingAccountService();
        var vm = new MainViewModel(
            new FolderedMailService(folders, []), accounts, new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), views,
            new StubRuleService(), new StubSmtpService());

        var a = new AccountModel
        {
            Id = AccountA, AccountName = "Work", Username = "work@example.com",
            AuthType = AuthType.OAuth2Microsoft,
        };
        a.HiddenFolders.AddRange(alreadyHidden);
        vm.Accounts.Add(a);
        vm.Accounts.Add(new AccountModel
        {
            Id = AccountB, AccountName = "Home", Username = "home@example.com",
            AuthType = AuthType.OAuth2Microsoft,
        });
        await vm.ConnectAllAccountsAsync();
        vm.UpdateSavedViews();
        return (vm, accounts);
    }

    private static FolderTreeNode NodeFor(MainViewModel vm, Guid accountId, string fullName) =>
        Flatten(vm.FolderTree!).Single(n => n.Folder is { } f && f.AccountId == accountId && f.FullName == fullName);

    private static bool TreeShows(MainViewModel vm, Guid accountId, string fullName) =>
        Flatten(vm.FolderTree!).Any(n => n.Folder is { } f && f.AccountId == accountId && f.FullName == fullName);

    [Fact]
    public async Task HideFolder_TakesTheBranchOutOfTheTree_AndSavesItOnTheAccount()
    {
        var (vm, accounts) = await MakeVmAsync();
        var savesBefore = accounts.Saves;

        var message = vm.HideFolder(NodeFor(vm, AccountA, "Archive"));

        Assert.False(TreeShows(vm, AccountA, "Archive"));
        Assert.False(TreeShows(vm, AccountA, "Archive/2020"));
        Assert.False(TreeShows(vm, AccountA, "Archive/2021"));
        Assert.True(TreeShows(vm, AccountA, "Notes"));
        Assert.Equal(["Archive"], vm.Accounts.Single(x => x.Id == AccountA).HiddenFolders);
        Assert.Equal(savesBefore + 1, accounts.Saves);
        Assert.Equal("Archive and the 2 folders inside it hidden.", message);
    }

    [Fact]
    public async Task HideFolder_OnlyAffectsTheFoldersAccount()
    {
        // Both accounts have a "Notes"; hiding one must not hide the other.
        var (vm, _) = await MakeVmAsync();

        vm.HideFolder(NodeFor(vm, AccountA, "Notes"));

        Assert.False(TreeShows(vm, AccountA, "Notes"));
        Assert.True(TreeShows(vm, AccountB, "Notes"));
        Assert.Empty(vm.Accounts.Single(x => x.Id == AccountB).HiddenFolders);
    }

    [Fact]
    public async Task HideFolder_ALeaf_SaysJustItsName()
    {
        var (vm, _) = await MakeVmAsync();

        Assert.Equal("Notes hidden.", vm.HideFolder(NodeFor(vm, AccountA, "Notes")));
    }

    [Fact]
    public async Task HideFolder_TheSameFolderTwice_IsStoredOnce()
    {
        var (vm, _) = await MakeVmAsync();
        var node = NodeFor(vm, AccountA, "Notes");

        vm.HideFolder(node);
        vm.HideFolder(node);

        Assert.Equal(["Notes"], vm.Accounts.Single(x => x.Id == AccountA).HiddenFolders);
    }

    [Fact]
    public async Task AFolderHiddenBeforeStartup_IsNotInTheFirstTree()
    {
        var (vm, _) = await MakeVmAsync("Notes");

        Assert.False(TreeShows(vm, AccountA, "Notes"));
        Assert.True(TreeShows(vm, AccountB, "Notes"));
    }

    [Fact]
    public async Task TheInbox_CannotBeHidden()
    {
        var (vm, accounts) = await MakeVmAsync();
        var savesBefore = accounts.Saves;

        Assert.Equal("The Inbox cannot be hidden.", vm.HideFolder(NodeFor(vm, AccountA, "INBOX")));
        Assert.True(TreeShows(vm, AccountA, "INBOX"));
        Assert.Equal(savesBefore, accounts.Saves);
    }

    [Fact]
    public async Task TheInbox_IsRecognisedByNameWhenTheServerDidNotFlagIt()
    {
        var (vm, _) = await MakeVmAsync();
        var unflagged = new FolderTreeNode { Folder = F("Inbox", "Inbox", accountId: AccountA), Label = "Inbox" };

        Assert.Equal("The Inbox cannot be hidden.", vm.HideFolderRefusal(unflagged));
    }

    [Fact]
    public async Task AnAccountHeader_CannotBeHidden()
    {
        var (vm, _) = await MakeVmAsync();
        var header = vm.FolderTree!.Single(n => n.IsHeader && n.AccountId == AccountA);

        Assert.Contains("is an account", vm.HideFolderRefusal(header), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupHeader_CannotBeHidden()
    {
        var (vm, _) = await MakeVmAsync();
        var allMailGroup = vm.FolderTree!.Single(n => n.IsHeader && n.AccountId == null && n.Label == "All Mail");

        Assert.Contains("only groups", vm.HideFolderRefusal(allMailGroup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVirtualFolder_CannotBeHidden()
    {
        var (vm, _) = await MakeVmAsync();
        var node = new FolderTreeNode { Folder = MainViewModel.AllInboxesFolder, Label = "All Inboxes" };

        Assert.Contains("is a view", vm.HideFolderRefusal(node), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountsAllMailNode_CannotBeHidden()
    {
        // The per-account "All Mail" node injected under each account header is virtual too.
        var (vm, _) = await MakeVmAsync();
        var header = vm.FolderTree!.Single(n => n.IsHeader && n.AccountId == AccountA);
        var accountAllMail = header.Children[0];

        Assert.NotNull(vm.HideFolderRefusal(accountAllMail));
    }

    [Fact]
    public async Task AFolderOfNoKnownAccount_CannotBeHidden()
    {
        // An opened .eml file's folder (#939) carries a reserved account id that is in no account list.
        var (vm, _) = await MakeVmAsync();
        var node = new FolderTreeNode { Folder = F("Opened", "Opened", accountId: Guid.NewGuid()), Label = "Opened" };

        Assert.NotNull(vm.HideFolderRefusal(node));
    }

    [Fact]
    public async Task NothingSelected_SaysSo()
    {
        var (vm, _) = await MakeVmAsync();

        Assert.Equal("Select a folder in the folder tree first.", vm.HideFolder(null));
    }

    [Fact]
    public async Task ShowAllHiddenFolders_BringsEveryHiddenFolderBack()
    {
        var (vm, accounts) = await MakeVmAsync("Archive");
        vm.HideFolder(NodeFor(vm, AccountB, "Notes"));
        var savesBefore = accounts.Saves;

        var message = vm.ShowAllHiddenFolders();

        Assert.Equal("4 hidden folders shown again.", message);
        Assert.True(TreeShows(vm, AccountA, "Archive"));
        Assert.True(TreeShows(vm, AccountA, "Archive/2020"));
        Assert.True(TreeShows(vm, AccountB, "Notes"));
        Assert.All(vm.Accounts, a => Assert.Empty(a.HiddenFolders));
        Assert.Equal(savesBefore + 1, accounts.Saves);
    }

    [Fact]
    public async Task ShowAllHiddenFolders_OneFolder_IsSingular()
    {
        var (vm, _) = await MakeVmAsync("Notes");

        Assert.Equal("1 hidden folder shown again.", vm.ShowAllHiddenFolders());
    }

    [Fact]
    public async Task ShowAllHiddenFolders_WithNothingHidden_SaysSo_AndSavesNothing()
    {
        var (vm, accounts) = await MakeVmAsync();
        var savesBefore = accounts.Saves;

        Assert.Equal("No folders are hidden.", vm.ShowAllHiddenFolders());
        Assert.Equal(savesBefore, accounts.Saves);
    }

    [Fact]
    public async Task GoToFoldersList_LeavesOutHiddenFolders_ButTheCacheKeepsThem()
    {
        var (vm, _) = await MakeVmAsync("Archive");

        var visible = vm.VisibleCachedFolders[AccountA].Select(f => f.FullName).ToList();

        Assert.Equal(["INBOX", "Notes"], visible);
        Assert.Equal(2, vm.VisibleCachedFolders[AccountB].Count);
        // The cache itself — which the Move/Copy pickers read — still has every folder.
        Assert.Equal(5, vm.CachedFolders[AccountA].Count);
    }

    [Fact]
    public void HiddenFolders_RoundTripThroughAccountsJson()
    {
        var account = new AccountModel { Id = AccountA, AccountName = "Work" };
        account.HiddenFolders.Add("Archive");
        account.HiddenFolders.Add("Lists/Dev");

        var json = System.Text.Json.JsonSerializer.Serialize(account);
        var back = System.Text.Json.JsonSerializer.Deserialize<AccountModel>(json)!;

        Assert.Equal(["Archive", "Lists/Dev"], back.HiddenFolders);
    }

    [Fact]
    public void AnAccountSavedBeforeThisFeature_LoadsWithNothingHidden()
    {
        var back = System.Text.Json.JsonSerializer.Deserialize<AccountModel>(
            "{\"Id\":\"" + AccountA + "\",\"AccountName\":\"Work\"}")!;

        Assert.NotNull(back.HiddenFolders);
        Assert.Empty(back.HiddenFolders);
    }

    [Fact]
    public async Task HideFolder_KeepsTheSameNodeObjectsForEverythingElse()
    {
        // The window captures the node above before hiding and focuses it afterwards; that only works
        // because the rebuild merges into the live tree rather than replacing its nodes (#719).
        var (vm, _) = await MakeVmAsync();
        var inbox = NodeFor(vm, AccountA, "INBOX");
        var notes = NodeFor(vm, AccountB, "Notes");

        vm.HideFolder(NodeFor(vm, AccountA, "Archive"));

        Assert.Same(inbox, NodeFor(vm, AccountA, "INBOX"));
        Assert.Same(notes, NodeFor(vm, AccountB, "Notes"));
    }

    [Fact]
    public async Task TheLandingNode_SkipsAPathSegmentThatHidingEmpties()
    {
        // "Lists" is not a folder; it exists for "Lists/Dev" alone. Hiding Dev removes Lists too, so
        // focus must land above Lists, on a node still in the tree afterwards.
        var (vm, _) = await MakeVmWithAsync([], new StubViewService(), F("Lists/Dev", "Dev", accountId: AccountA));
        foreach (var n in Flatten(vm.FolderTree!)) n.IsExpanded = true;
        var dev = NodeFor(vm, AccountA, "Lists/Dev");
        var visible = Flatten(vm.FolderTree!).ToList();   // everything is expanded, so this is the visible order

        var landing = QuickMail.Views.MainWindow.HideLandingNode(visible, dev);
        vm.HideFolder(dev);

        Assert.NotNull(landing);
        Assert.NotEqual("Lists", landing!.Label);
        Assert.Contains(Flatten(vm.FolderTree!), n => ReferenceEquals(n, landing));
        Assert.DoesNotContain(Flatten(vm.FolderTree!), n => n.Label == "Lists");
    }

    [Fact]
    public async Task HidingAFolderListedInASavedView_HidesItFromTheAccount_AndSaysTheViewKeepsIt()
    {
        var view = new SavedView
        {
            Name = "Reading",
            Folders =
            [
                new ViewFolder { AccountId = AccountA, FolderFullName = "Notes" },
                new ViewFolder { AccountId = AccountA, FolderFullName = "Archive" },
            ],
        };
        var (vm, _) = await MakeVmWithAsync([], new ViewsStub(view));
        var viewsGroup = vm.FolderTree!.Single(n => n.IsHeader && n.Label == "Views");
        var viewCopy = Flatten(viewsGroup.Children).First(n => n.Folder?.FullName == "Archive");

        var message = vm.HideFolder(viewCopy);

        // Gone from under the account, with the two folders inside it, counted from the account's node.
        var header = vm.FolderTree!.Single(n => n.IsHeader && n.AccountId == AccountA);
        Assert.DoesNotContain(Flatten(header.Children), n => n.Folder?.FullName is "Archive" or "Archive/2020");
        Assert.Equal("Archive and the 2 folders inside it hidden. It still shows in the saved view, which lists it by name.", message);
        // Still in the view, which is why the window must not move focus off it.
        Assert.Contains(Flatten(viewsGroup.Children), n => ReferenceEquals(n, viewCopy));
        Assert.True(vm.IsInViewsGroup(viewCopy));
    }

    [Fact]
    public async Task DeletingAFolder_ForgetsItsHiddenEntries_AndThoseInsideIt()
    {
        var (vm, _) = await MakeVmAsync("Archive/2020", "Notes");

        vm.ForgetHiddenFolders(AccountA, "Archive");

        Assert.Equal(["Notes"], vm.Accounts.Single(a => a.Id == AccountA).HiddenFolders);
    }

    [Fact]
    public async Task ForgettingAFolder_DoesNotTouchANameThatMerelyStartsTheSame()
    {
        var (vm, _) = await MakeVmAsync("Archives", "Archive2");

        vm.ForgetHiddenFolders(AccountA, "Archive");

        Assert.Equal(["Archives", "Archive2"], vm.Accounts.Single(a => a.Id == AccountA).HiddenFolders);
    }

    // ── Remapping (IMAP to Microsoft 365, settings import) ───────────────────

    [Fact]
    public void Remap_RewritesHiddenPathsToGraphIds_AndDropsWhatItCannotMatch()
    {
        var graph = new List<MailFolderModel>
        {
            F("id-inbox", "Inbox", kind: SpecialFolderKind.Inbox), F("id-rss", "RSS Feeds"), F("id-notes", "Notes"),
        };
        var hidden = new List<string> { "RSS Feeds", "Gone Folder", "Notes" };

        var report = FolderReferenceRemapper.Remap(AccountA, graph, [], [], new ConfigModel(), hidden);

        Assert.Equal(["id-rss", "id-notes"], hidden);
        Assert.True(report.HiddenFoldersChanged);
        Assert.False(report.AnythingChanged);   // nothing to say aloud; the caller saves the account
    }

    [Fact]
    public void Remap_AlreadyGraphIds_IsNoChange()
    {
        var graph = new List<MailFolderModel> { F("id-rss", "RSS Feeds") };
        var hidden = new List<string> { "id-rss" };

        var report = FolderReferenceRemapper.Remap(AccountA, graph, [], [], new ConfigModel(), hidden);

        Assert.Equal(["id-rss"], hidden);
        Assert.False(report.HiddenFoldersChanged);
    }

    [Fact]
    public void ImportHeal_TranslatesHiddenIdsFromTheOtherComputer()
    {
        // The other computer's Graph id means nothing here; its recorded path is matched by name.
        var here = new List<MailFolderModel> { F("here-rss", "RSS Feeds"), F("here-inbox", "Inbox", kind: SpecialFolderKind.Inbox) };
        var paths = new Dictionary<string, string> { ["there-rss"] = "RSS Feeds" };
        var account = new AccountModel { Id = AccountA };
        account.HiddenFolders.Add("there-rss");

        var report = ImportFolderHeal.Heal(AccountA, here, paths, [], [], new ConfigModel(), account, out var archiveChanged);

        Assert.Equal(["here-rss"], account.HiddenFolders);
        Assert.True(report.HiddenFoldersChanged);
        Assert.False(archiveChanged);
    }

    // ── Entry points ─────────────────────────────────────────────────────────

    [Fact]
    public void TheDestinationPickers_StillOfferHiddenFolders()
    {
        // Out of scope by design: a hidden folder can still be filed into. Only Go to Folder filters.
        var source = Source("Views/MainWindow.xaml.cs");
        Assert.Single(Regex.Matches(source, "VisibleCachedFolders"));
        Assert.Contains("_vm.Accounts, _vm.CachedFolders, node!.Folder!, \"Move Folder To\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("folder.hide")]
    [InlineData("folder.showHidden")]
    public void EachActionIsRegisteredAsACommand(string commandId)
    {
        Assert.Contains("id: \"" + commandId + "\"", Source("Views/MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("folder.hide", "FolderContextMenu_HideFolder_Click", "MenuHideFolder_Click")]
    [InlineData("folder.showHidden", "FolderContextMenu_ShowHiddenFolders_Click", "MenuShowHiddenFolders_Click")]
    public void EachActionIsOnTheContextMenuAndTheFolderMenu(string commandId, string contextHandler, string menuHandler)
    {
        var xaml = Source("Views/MainWindow.xaml");

        var contextMenu = Regex.Match(xaml, "<ContextMenu x:Key=\"FolderContextMenu\"(?<body>.*?)</ContextMenu>", RegexOptions.Singleline);
        Assert.True(contextMenu.Success);
        Assert.Contains("sc:CommandShortcut.Id=\"" + commandId + "\"", contextMenu.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains(contextHandler, contextMenu.Groups["body"].Value, StringComparison.Ordinal);

        var folderMenu = Regex.Match(xaml,
            "<MenuItem Header=\"Fol_der\">(?<body>.*?)</MenuItem>\\s*\\r?\\n\\s*<!-- View -->", RegexOptions.Singleline);
        Assert.True(folderMenu.Success);
        Assert.Contains("sc:CommandShortcut.Id=\"" + commandId + "\"", folderMenu.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains(menuHandler, folderMenu.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void GoToFolder_ReadsTheVisibleFolders()
    {
        var source = Source("Views/MainWindow.xaml.cs");
        var body = Regex.Match(source, "private async void OpenFolderPicker\\(\\)(?<body>.*?)ShowDialog", RegexOptions.Singleline);
        Assert.True(body.Success);
        Assert.Contains("_vm.VisibleCachedFolders", body.Groups["body"].Value, StringComparison.Ordinal);
    }

    private static string Source(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), path + " not found.");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}
