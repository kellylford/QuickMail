// Rename Folder (#1083): MainViewModel's half. What can be renamed and what is refused, with the
// sentence for each; the server call; and everything carried across afterwards — rules, views, the
// startup folder, hidden folders and favorites, the remembered list views, the message cache, the
// folder tree, messages held in memory — or, where it cannot be, said by name. Also the deferral
// for a rename made inside a modal folder picker, and the two small ViewModels that follow a rename.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class RenameFolderTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Home = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class ViewsStore(params SavedView[] views) : IViewService
    {
        public List<SavedView> Saved { get; private set; } = [.. views];
        public int Saves { get; private set; }
        public List<SavedView> Load() => Saved.Select(Clone).ToList();
        public void Save(List<SavedView> v) { Saves++; Saved = v.Select(Clone).ToList(); }

        private static SavedView Clone(SavedView s) => new()
        {
            Id = s.Id, Name = s.Name, VirtualFolderKey = s.VirtualFolderKey,
            Folders = s.Folders.Select(f => new ViewFolder
            {
                AccountId = f.AccountId, FolderFullName = f.FolderFullName,
                FolderDisplayName = f.FolderDisplayName, AccountDisplayName = f.AccountDisplayName,
            }).ToList(),
        };
    }

    private sealed class Accounts : IAccountService
    {
        public int Saves { get; private set; }
        public List<AccountModel> LoadAccounts() => [];
        public void SaveAccounts(List<AccountModel> accounts) => Saves++;
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MailFolderModel F(string fullName, string display, Guid account,
                                     SpecialFolderKind kind = SpecialFolderKind.None, string? parentId = null) =>
        new() { FullName = fullName, DisplayName = display, AccountId = account, Kind = kind, ParentId = parentId };

    private sealed record Rig(
        MainViewModel Vm, FolderedMailService Mail, StubRuleService Rules, ViewsStore Views,
        StubConfigService Config, StubLocalStoreService Store, StubFolderViewStateService FolderViews, Accounts AccountStore);

    private static async Task<Rig> MakeAsync(
        IEnumerable<MailRule>? rules = null, ViewsStore? views = null, Action<AccountModel>? arrangeWork = null,
        bool graphWork = false)
    {
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [Work] = graphWork
                ?
                [
                    F("inbox-id", "Inbox", Work, SpecialFolderKind.Inbox, parentId: "root"),
                    F("p-id", "Projects", Work, parentId: "root"),
                    F("c-id", "2026", Work, parentId: "p-id"),
                ]
                :
                [
                    F("INBOX", "Inbox", Work, SpecialFolderKind.Inbox),
                    F("Sent", "Sent", Work, SpecialFolderKind.Sent),
                    F("Projects", "Projects", Work),
                    F("Projects/2026", "2026", Work),
                    F("Lists/News", "News", Work),
                    F("Notes", "Notes", Work),
                ],
            [Home] =
            [
                F("INBOX", "Inbox", Home, SpecialFolderKind.Inbox),
                F("Projects", "Projects", Home),
            ],
        };
        var mail = new FolderedMailService(folders, []);
        var ruleService = new StubRuleService { LoadedRules = rules?.ToList() ?? [] };
        views ??= new ViewsStore();
        var config = new StubConfigService();
        var store = new StubLocalStoreService();
        var folderViews = new StubFolderViewStateService();
        var accountStore = new Accounts();

        var vm = new MainViewModel(
            mail, accountStore, new StubCredentialService(), store, new StubOAuthService(), new StubSyncService(),
            config, new StubCommandRegistry(), views, ruleService, new StubSmtpService(),
            folderViewState: folderViews);

        var work = new AccountModel
        {
            Id = Work, AccountName = "Work", Username = "work@example.com",
            BackendKind = graphWork ? BackendKind.MicrosoftGraph : BackendKind.ImapSmtp,
        };
        var home = new AccountModel { Id = Home, AccountName = "Home", Username = "home@example.com" };
        arrangeWork?.Invoke(work);
        vm.Accounts.Add(work);
        vm.Accounts.Add(home);
        await vm.ConnectAllAccountsAsync();
        vm.UpdateSavedViews();
        return new Rig(vm, mail, ruleService, views, config, store, folderViews, accountStore);
    }

    private static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private static FolderTreeNode NodeFor(MainViewModel vm, Guid accountId, string fullName) =>
        Flatten(vm.FolderTree.Single(r => r.IsHeader && r.AccountId == accountId).Children)
            .Single(n => n.Folder is { } f && f.AccountId == accountId && f.FullName == fullName);

    private static MailFolderModel Cached(MainViewModel vm, Guid accountId, string fullName) =>
        vm.CachedFolders[accountId].Single(f => f.FullName == fullName);

    // ── What can be renamed ──────────────────────────────────────────────────

    [Fact]
    public async Task AnOrdinaryFolder_CanBeRenamed()
    {
        var rig = await MakeAsync();
        Assert.Null(rig.Vm.RenameFolderRefusal(NodeFor(rig.Vm, Work, "Projects")));
        Assert.Null(rig.Vm.RenameFolderRefusal(NodeFor(rig.Vm, Work, "Projects/2026")));
    }

    [Theory]
    [InlineData("INBOX", "Inbox is a standard folder, so it cannot be renamed.")]
    [InlineData("Sent", "Sent is a standard folder, so it cannot be renamed.")]
    public async Task StandardFolders_AreRefused_WithTheReason(string fullName, string reason)
    {
        var rig = await MakeAsync();
        Assert.Equal(reason, rig.Vm.RenameFolderRefusal(NodeFor(rig.Vm, Work, fullName)));

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, fullName), "Other");
        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.Message);
        Assert.Empty(rig.Mail.Renames);
    }

    [Fact]
    public async Task AnIncomingFolderNamedInbox_IsRefusedEvenWithoutAKind()
    {
        var rig = await MakeAsync();
        var bare = F("inbox", "inbox", Work);
        Assert.Equal("inbox is a standard folder, so it cannot be renamed.", rig.Vm.RenameFolderRefusal(bare));
    }

    [Fact]
    public async Task AccountsHeadingsViewsAndPathSegments_SayWhatTheyAre()
    {
        var rig = await MakeAsync();
        var vm = rig.Vm;
        var account = vm.FolderTree.Single(r => r.IsHeader && r.AccountId == Work);
        Assert.Equal("'Work' is an account, not a folder. To rename an account, use Manage Accounts.", vm.RenameFolderRefusal(account));

        var allMail = vm.FolderTree.Single(r => r.SectionKey == FolderTreeOrder.AllMail);
        Assert.Equal("'All Mail' is a heading, not a folder, so it cannot be renamed.", vm.RenameFolderRefusal(allMail));

        var allInboxes = allMail.Children.Single(n => n.Folder == MainViewModel.AllInboxesFolder);
        Assert.Equal($"'{allInboxes.Label}' is a view, not a folder, so it cannot be renamed.", vm.RenameFolderRefusal(allInboxes));

        var lists = Flatten(account.Children).Single(n => n.Label == "Lists" && n.Folder == null);
        Assert.Equal("'Lists' is part of a folder path on the server, not a folder, so it cannot be renamed.", vm.RenameFolderRefusal(lists));

        Assert.Equal("Select a folder in the folder tree first.", vm.RenameFolderRefusal((FolderTreeNode?)null));
    }

    [Fact]
    public async Task ASavedView_PointsAtManageViews()
    {
        var rig = await MakeAsync();
        var view = new MailFolderModel { FullName = MainViewModel.ViewPrefix + Guid.NewGuid(), DisplayName = "Mine" };
        Assert.Equal("'Mine' is a saved view, not a folder. To rename a view, use Manage Views on the View menu.", rig.Vm.RenameFolderRefusal(view));
    }

    [Fact]
    public async Task ABadName_IsRefusedBeforeTheServerIsAsked()
    {
        var rig = await MakeAsync();
        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "notes");
        Assert.False(result.Succeeded);
        Assert.Equal("There is already a folder named notes there.", result.Message);
        Assert.Empty(rig.Mail.Renames);
    }

    // ── A rename and everything it carries along ─────────────────────────────

    [Fact]
    public async Task Rename_AsksTheServer_AndTheTreeShowsTheNewNames()
    {
        var rig = await MakeAsync();
        var renamed = new List<FolderRename>();
        rig.Vm.FolderRenamed += renamed.Add;

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), " Work items ");

        Assert.True(result.Succeeded);
        Assert.Equal((Work, "Projects", "Work items"), Assert.Single(rig.Mail.Renames));
        Assert.Equal("Renamed Projects to Work items.", result.Message);
        Assert.Equal(string.Empty, result.Announcement);
        var rename = Assert.Single(renamed);
        Assert.Equal(("Projects", "Work items", (char?)'/'), (rename.OldFullName, rename.NewFullName, rename.Separator));

        var node = NodeFor(rig.Vm, Work, "Work items");
        Assert.Equal("Work items", node.Label);
        Assert.Contains(node.Children, c => c.Folder?.FullName == "Work items/2026");
        Assert.DoesNotContain(rig.Vm.CachedFolders[Work], f => f.FullName.StartsWith("Projects", StringComparison.Ordinal));
        // The other account's Projects is another folder.
        Assert.Equal("Projects", NodeFor(rig.Vm, Home, "Projects").Label);
        Assert.Equal("Renamed Projects to Work items.", rig.Vm.StatusText);
    }

    [Fact]
    public async Task Rename_RewritesRulesAndViews_AndSaysSo()
    {
        var rule = new MailRule { Name = "File it", AccountId = Work, Action = RuleAction.MoveToFolder, TargetFolder = "Projects/2026" };
        var homeRule = new MailRule { Name = "Home's", AccountId = Home, Action = RuleAction.MoveToFolder, TargetFolder = "Projects" };
        var view = new SavedView
        {
            Name = "Current",
            Folders = [new ViewFolder { AccountId = Work, FolderFullName = "Projects", FolderDisplayName = "Projects" }],
        };
        var rig = await MakeAsync(rules: [rule, homeRule], views: new ViewsStore(view));

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.Equal("1 rule and 1 saved view updated.", result.Announcement);
        Assert.Equal("Renamed Projects to Clients. 1 rule and 1 saved view updated.", result.Message);
        Assert.Equal(1, rig.Rules.SaveCount);
        Assert.Equal("Clients/2026", rig.Rules.LoadedRules.Single(r => r.Name == "File it").TargetFolder);
        Assert.Equal("Projects", rig.Rules.LoadedRules.Single(r => r.Name == "Home's").TargetFolder);
        var saved = Assert.Single(rig.Views.Saved).Folders.Single();
        Assert.Equal(("Clients", "Clients"), (saved.FolderFullName, saved.FolderDisplayName));
        // The views in memory, which the tree and the view commands use, follow too.
        Assert.Equal("Clients", rig.Vm.SavedViews.Single().Folders.Single().FolderFullName);
    }

    [Fact]
    public async Task Rename_CarriesTheStartupFolderDestinationsHiddenFavoritesAndArchive()
    {
        var rig = await MakeAsync(arrangeWork: work =>
        {
            work.HiddenFolders.Add("Projects/2026");
            work.FavoriteFolders.Add(new FavoriteFolder { FullName = "Projects", AddedUtc = DateTime.UtcNow, Order = 0 });
            work.ArchiveFolderFullName = "Projects";
        });
        var cfg = rig.Config.Load();
        cfg.StartupFolder = "Projects";
        cfg.StartupFolderAccount = Work.ToString();
        cfg.StartupFolderLabel = "Projects";
        cfg.Accounts[Work] = new AccountOverrideConfig { LastMoveFolder = "Projects/2026" };
        rig.Config.Save(cfg);
        var accountSaves = rig.AccountStore.Saves;

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.Equal("The startup folder updated.", result.Announcement);
        var after = rig.Config.Load();
        Assert.Equal(("Clients", "Clients"), (after.StartupFolder, after.StartupFolderLabel));
        Assert.Equal("Clients/2026", after.Accounts[Work].LastMoveFolder);
        var work = rig.Vm.Accounts.Single(a => a.Id == Work);
        Assert.Equal(["Clients/2026"], work.HiddenFolders);
        Assert.Equal("Clients", work.FavoriteFolders.Single().FullName);
        Assert.Equal("Clients", work.ArchiveFolderFullName);
        Assert.True(rig.AccountStore.Saves > accountSaves);
    }

    [Fact]
    public async Task Rename_RefilesTheCache_AndMovesRememberedListViews()
    {
        var rig = await MakeAsync();
        var state = new ListState(ViewMode.Conversations, MessageFilter.Unread, null, MessageSort.DateAscending, null);
        rig.FolderViews.Remember(Work, "Projects/2026", state);
        rig.FolderViews.Remember(Home, "Projects", state);

        await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.Equal((Work, "Projects", "Clients", '/'), Assert.Single(rig.Store.FolderRenames));
        Assert.Equal(state, rig.FolderViews.Recall(Work, "Clients/2026"));
        Assert.Null(rig.FolderViews.Recall(Work, "Projects/2026"));
        Assert.Equal(state, rig.FolderViews.Recall(Home, "Projects"));
    }

    [Fact]
    public async Task Rename_UpdatesMessagesAlreadyInHand_AndTheOpenFolder()
    {
        var rig = await MakeAsync();
        var inSub = new MailMessageSummary { AccountId = Work, FolderName = "Projects/2026", MessageId = "1" };
        var elsewhere = new MailMessageSummary { AccountId = Home, FolderName = "Projects", MessageId = "2" };
        rig.Vm.MessageClipboard.Copy([inSub, elsewhere]);
        rig.Vm.SelectedFolder = Cached(rig.Vm, Work, "Projects/2026");

        await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.Equal("Clients/2026", inSub.FolderName);
        Assert.Equal("Projects", elsewhere.FolderName);
        Assert.Equal("Clients/2026", rig.Vm.SelectedFolder!.FullName);
        Assert.Same(Cached(rig.Vm, Work, "Clients/2026"), rig.Vm.SelectedFolder);
    }

    [Fact]
    public async Task Rename_ByIdOnMicrosoft365_ChangesOnlyTheName()
    {
        var rule = new MailRule { Name = "File it", AccountId = Work, Action = RuleAction.MoveToFolder, TargetFolder = "p-id" };
        var rig = await MakeAsync(rules: [rule], graphWork: true);

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "p-id"), "Clients");

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.Announcement);
        Assert.Equal("Clients", Cached(rig.Vm, Work, "p-id").DisplayName);
        Assert.Equal("p-id", rig.Rules.LoadedRules.Single().TargetFolder);
        Assert.Equal(0, rig.Rules.SaveCount);
        Assert.Empty(rig.Store.FolderRenames);
        Assert.Equal("Clients", NodeFor(rig.Vm, Work, "p-id").Label);
    }

    // ── What goes wrong ──────────────────────────────────────────────────────

    [Fact]
    public async Task AServerRefusal_IsReported_AndNothingChanges()
    {
        var rule = new MailRule { Name = "File it", AccountId = Work, Action = RuleAction.MoveToFolder, TargetFolder = "Projects" };
        var rig = await MakeAsync(rules: [rule]);
        rig.Mail.RenameFailure = new InvalidOperationException("Mailbox already exists.");

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.False(result.Succeeded);
        Assert.Equal("Could not rename Projects. Mailbox already exists.", result.Message);
        Assert.Equal("Projects", rule.TargetFolder);
        Assert.Equal(0, rig.Rules.SaveCount);
        Assert.Empty(rig.Store.FolderRenames);
        Assert.NotNull(NodeFor(rig.Vm, Work, "Projects"));
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact]
    public async Task UnreadableRules_TheRenameStands_AndTheUserIsTold()
    {
        var rig = await MakeAsync();
        rig.Rules.ThrowOnLoad = new IOException("locked");

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.True(result.Succeeded);
        Assert.Equal("But your rules could not be read, so a rule that files mail into it must have its folder set again.",
                     result.Announcement);
        Assert.Equal(0, rig.Rules.SaveCount);
        Assert.NotNull(NodeFor(rig.Vm, Work, "Clients"));
    }

    [Fact]
    public async Task RulesThatCannotBeSaved_AreNamed()
    {
        var rule = new MailRule { Name = "File it", AccountId = Work, Action = RuleAction.MoveToFolder, TargetFolder = "Projects" };
        var rig = await MakeAsync(rules: [rule]);
        rig.Rules.ThrowOnSave = new IOException("disk full");

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.True(result.Succeeded);
        Assert.Equal("But the rule File it could not be saved, so set its folder again.", result.Announcement);
    }

    [Fact]
    public async Task ACacheThatCannotBeRefiled_IsSaid()
    {
        var rig = await MakeAsync();
        rig.Store.RenameFolderFailure = new IOException("database is locked");

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients");

        Assert.True(result.Succeeded);
        Assert.Equal("But the messages saved on this computer for it will download again.", result.Announcement);
    }

    // ── Inside a modal picker ────────────────────────────────────────────────

    [Fact]
    public async Task InsideAPicker_TheTreeAndListenersWait_ForThePickerToClose()
    {
        var rig = await MakeAsync();
        var renamed = new List<FolderRename>();
        rig.Vm.FolderRenamed += renamed.Add;
        var treeBefore = rig.Vm.FolderTree;

        var result = await rig.Vm.RenameFolderAsync(Cached(rig.Vm, Work, "Projects"), "Clients", insideModal: true);

        Assert.True(result.Succeeded);
        // The picker gets the account's new list to rebuild its own tree from.
        Assert.Contains(result.AccountFolders!, f => f.FullName == "Clients/2026");
        // Files are written now ...
        Assert.Single(rig.Store.FolderRenames);
        // ... but the main window is left alone while the picker's loop runs.
        Assert.Same(treeBefore, rig.Vm.FolderTree);
        Assert.Empty(renamed);

        rig.Vm.CommitPendingFolderTreeRebuild();

        Assert.NotSame(treeBefore, rig.Vm.FolderTree);
        Assert.Equal("Clients", NodeFor(rig.Vm, Work, "Clients").Label);
        Assert.Single(renamed);

        // Said once: a second commit has nothing left to announce.
        rig.Vm.CommitPendingFolderTreeRebuild();
        Assert.Single(renamed);
    }

    // ── The ViewModels that follow a rename ──────────────────────────────────

    [Fact]
    public void RuleEditor_FollowsTheRename_SoSavingCannotWriteTheOldNameBack()
    {
        var rename = new FolderRename(Work, "Projects", "Clients", '/', "Projects", "Clients");
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.AccountId = Work;
        editor.MoveToFolderId = "Projects/2026";
        editor.MoveToFolderName = "Projects/2026";
        editor.CopyToFolderId = "Projects";
        editor.CopyToFolderName = "Projects";

        editor.ApplyFolderRename(rename);

        Assert.Equal(("Clients/2026", "Clients/2026"), (editor.MoveToFolderId, editor.MoveToFolderName));
        Assert.Equal(("Clients", "Clients"), (editor.CopyToFolderId, editor.CopyToFolderName));
    }

    [Fact]
    public void RuleEditor_ById_TakesTheNewName_AndAnotherAccountsEditorIsLeftAlone()
    {
        var byId = new FolderRename(Work, "p-id", "p-id", null, "Projects", "Clients");
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.AccountId = Work;
        editor.MoveToFolderId = "p-id";
        editor.MoveToFolderName = "Projects";
        editor.ApplyFolderRename(byId);
        Assert.Equal(("p-id", "Clients"), (editor.MoveToFolderId, editor.MoveToFolderName));

        var other = ServerRuleEditorViewModel.ForNew();
        other.AccountId = Home;
        other.MoveToFolderId = "Projects";
        other.MoveToFolderName = "Projects";
        other.ApplyFolderRename(new FolderRename(Work, "Projects", "Clients", '/', "Projects", "Clients"));
        Assert.Equal("Projects", other.MoveToFolderId);
    }

    [Fact]
    public void MessageWindow_FollowsTheRename()
    {
        var shown = new MailMessageSummary { AccountId = Work, FolderName = "Projects", MessageId = "1" };
        var vm = new MessageWindowViewModel { OriginalSummary = shown };
        vm.MessageList.Add(new MailMessageSummary { AccountId = Work, FolderName = "Projects/2026", MessageId = "2" });

        vm.ApplyFolderRename(new FolderRename(Work, "Projects", "Clients", '/', "Projects", "Clients"));

        Assert.Equal("Clients", shown.FolderName);
        Assert.Equal("Clients/2026", vm.MessageList[0].FolderName);
    }

    [Fact]
    public async Task RenameWindowViewModel_ARefusalIsSaid_AndTheNameIsSelectedAgain()
    {
        var heard = new List<(string, AnnouncementCategory)>();
        var selects = 0;
        FolderRenameResult? renamed = null;
        var vm = new RenameFolderViewModel("Projects", _ => Task.FromResult(FolderRenameResult.Refused("Type a name for the folder.")));
        vm.AnnouncementRequested += (t, c) => heard.Add((t, c));
        vm.SelectNameRequested += () => selects++;
        vm.Renamed += r => renamed = r;

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal([("Type a name for the folder.", AnnouncementCategory.Result)], heard);
        Assert.Equal(1, selects);
        Assert.Null(renamed);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task RenameWindowViewModel_Success_HandsBackTheResult()
    {
        string? asked = null;
        var done = new FolderRenameResult(true, "Renamed Projects to Clients.", string.Empty);
        var vm = new RenameFolderViewModel("to_do", name => { asked = name; return Task.FromResult(done); });
        FolderRenameResult? renamed = null;
        vm.Renamed += r => renamed = r;

        Assert.Equal("to_do", vm.NewName);
        Assert.Equal("New name for to__do:", vm.Prompt);   // a single underscore would be an access key
        vm.NewName = "Clients";
        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal("Clients", asked);
        Assert.Same(done, renamed);
    }

    [Fact]
    public async Task RenameWindowViewModel_AnUnforeseenFailure_IsStillSaid()
    {
        var heard = new List<string>();
        var vm = new RenameFolderViewModel("Projects", _ => throw new InvalidOperationException("boom"));
        vm.AnnouncementRequested += (t, _) => heard.Add(t);

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal(["Could not rename Projects. boom"], heard);
        Assert.False(vm.IsWorking);
    }
}
