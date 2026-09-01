// Tests for the Watched Conversations virtual folder and the Ctrl+Shift+W toggle.
//
// The folder is a predicate aggregate over IWatchService, in the same family as All Flagged and the
// contact-mail results view. The two behaviours worth pinning hardest are the ones that would fail
// silently: the live-arrival branch in OnFolderSynced (a reply joining the open folder during sync
// IS the feature), and the RowFieldCatalog ordering (the catalog array is the positional binding
// order for spoken rows, so a misplaced entry would corrupt every row's speech invisibly).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class WatchedConversationsTests
{
    private static readonly Guid AccountA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid AccountB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static MailMessageSummary Msg(
        string id, string subject, int daysAgo = 0, Guid? account = null, string folder = "INBOX") => new()
    {
        MessageId  = id,
        AccountId  = account ?? AccountA,
        FolderName = folder,
        From       = "someone@example.com",
        Subject    = subject,
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    // Sync service whose FolderSynced event a test can raise, to exercise OnFolderSynced.
    private sealed class RaisableSync : ISyncService
    {
#pragma warning disable CS0067 // not raised by this fake
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? DraftUploadsRefused;
        public event Action<AccountModel, string>? DraftUploadsBlocked;
        public event Action<int>? DraftsUploaded;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;

        public void Raise(params MailMessageSummary[] messages) => FolderSynced?.Invoke(messages);

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<int> ReconcileFolderAsync(AccountModel a, MailFolderModel f, CancellationToken ct) => Task.FromResult(0);
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
    }

    private static (MainViewModel Vm, StubWatchService Watch, RaisableSync Sync) MakeVm(
        IEnumerable<MailMessageSummary>? cached = null,
        ICommandRegistry? registry = null)
    {
        var watch = new StubWatchService();
        var sync  = new RaisableSync();
        ILocalStoreService store = cached != null
            ? new FilterableStoreForFlags(cached)
            : new StubLocalStoreService();

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), sync, new StubConfigService(),
            registry ?? new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), watchService: watch);

        return (vm, watch, sync);
    }

    /// <summary>
    /// Stands in for what the View does before invoking the toggle: it resolves which conversation
    /// the user is actually looking at (which is NOT always the selected message — a group header
    /// selection leaves SelectedMessage stale) and pushes it into the VM.
    /// </summary>
    private static void SetWatchTarget(MainViewModel vm, MailMessageSummary message)
    {
        vm.SelectedMessage    = message;
        vm.WatchTargetSubject = message.Subject;
    }

    private static Task SelectWatchedFolder(MainViewModel vm) =>
        vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllWatchedFolder);

    // ── Sentinel and placement ───────────────────────────────────────────────

    [Fact]
    public void AllWatchedFolder_UsesANulPrefixedSentinel()
    {
        // The NUL prefix is what marks a folder as virtual everywhere in the app — notably
        // ViewManagerViewModel.IsRealImapFolder, which keeps sentinels out of saved views' folder
        // lists. A sentinel written with C#'s greedy \x escape would silently not start with NUL.
        Assert.StartsWith("\u0000", MainViewModel.AllWatchedFolder.FullName, StringComparison.Ordinal);
        Assert.Equal("\u0000AllWatched", MainViewModel.AllWatchedFolder.FullName);
        Assert.Equal("Watched Conversations", MainViewModel.AllWatchedFolder.DisplayName);
    }

    // The flat Folders list and the tree are both built from the folder cache, so an account has to
    // be connected before either exists.
    private static async Task<MainViewModel> MakeConnectedVmAsync()
    {
        var (vm, _, _) = MakeVm();
        vm.Accounts.Add(new AccountModel
        {
            Id          = AccountA,
            AccountName = "Work",
            Username    = "work@example.com",
            AuthType    = AuthType.OAuth2Microsoft,
        });
        await vm.ConnectAllAccountsAsync();
        return vm;
    }

    [Fact]
    public async Task Folders_ContainsTheAllWatchedSentinel()
    {
        var vm = await MakeConnectedVmAsync();
        Assert.Contains(vm.Folders, f => f.FullName == MainViewModel.AllWatchedFolder.FullName);
    }

    [Fact]
    public async Task FolderTree_AllMailGroup_ListsWatchedConversationsLast()
    {
        var vm = await MakeConnectedVmAsync();

        var group = vm.FolderTree.FirstOrDefault(n => n.IsHeader && n.Label == "All Mail");
        Assert.NotNull(group);
        Assert.Equal("Watched Conversations", group!.Children.Last().Label);
    }

    [Fact]
    public void ViewManager_NamesTheSavedViewsVirtualFolder()
    {
        // A saved view stores the sentinel with the NUL stripped; without a map entry the View
        // Manager would label it the generic "Virtual folder".
        var view = new SavedView { Name = "Watching", VirtualFolderKey = "AllWatched" };
        var vm = new ViewManagerViewModel(
            new StubViewService(), new StubConfigService(), new StubCommandRegistry(),
            savedViews: [view],
            currentFolder: MainViewModel.AllWatchedFolder,
            currentAccount: null,
            currentViewMode: ViewMode.Messages,
            currentFilter: MessageFilter.All,
            currentSort: MessageSort.DateDescending)
        {
            SelectedView = view,
        };

        Assert.Equal("Watched Conversations", vm.SelectedFoldersSummary);
    }

    // ── Fetch ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectWatchedFolder_ListsEveryMessageOfEveryWatchedConversation_NewestFirst()
    {
        var corpus = new[]
        {
            Msg("1", "QuickMail 1.4 released",          daysAgo: 5),
            Msg("2", "Re: QuickMail 1.4 released",      daysAgo: 1, account: AccountB, folder: "Archive"),
            Msg("3", "Unrelated thread",                daysAgo: 2),
            Msg("4", "Fwd: quickmail 1.4 RELEASED",     daysAgo: 3),
        };
        var (vm, watch, _) = MakeVm(corpus);
        watch.Watch("QuickMail 1.4 released");

        await SelectWatchedFolder(vm);

        // Every member across both accounts and folders, newest first; the unrelated thread stays out.
        Assert.Equal(new[] { "2", "4", "1" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public async Task SelectWatchedFolder_WithNoWatches_ShowsTheEmptyStateStatus()
    {
        var (vm, _, _) = MakeVm(new[] { Msg("1", "Anything") });

        await SelectWatchedFolder(vm);

        Assert.Empty(vm.Messages);
        Assert.Contains("No watched conversations", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+W", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectWatchedFolder_StampsIsWatchedOnTheLoadedRows()
    {
        var (vm, watch, _) = MakeVm(new[] { Msg("1", "Budget Review") });
        watch.Watch("Budget Review");

        await SelectWatchedFolder(vm);

        Assert.All(vm.Messages, m => Assert.True(m.IsWatched));
    }

    // ── Live arrivals — this branch IS the feature ───────────────────────────

    [Fact]
    public async Task FolderSynced_AReplyToAWatchedConversationJoinsTheOpenFolder()
    {
        var (vm, watch, sync) = MakeVm(new[] { Msg("1", "QuickMail 1.4 released", daysAgo: 5) });
        watch.Watch("QuickMail 1.4 released");
        await SelectWatchedFolder(vm);
        Assert.Single(vm.Messages);

        sync.Raise(Msg("2", "Re: QuickMail 1.4 released", daysAgo: 0));

        Assert.Equal(new[] { "2", "1" }, vm.Messages.Select(m => m.MessageId).ToArray());
        Assert.True(vm.Messages.First(m => m.MessageId == "2").IsWatched);
    }

    [Fact]
    public async Task FolderSynced_AMessageInAnUnwatchedConversationIsIgnored()
    {
        var (vm, watch, sync) = MakeVm(new[] { Msg("1", "QuickMail 1.4 released", daysAgo: 5) });
        watch.Watch("QuickMail 1.4 released");
        await SelectWatchedFolder(vm);

        sync.Raise(Msg("99", "Some other thread"));

        Assert.DoesNotContain(vm.Messages, m => m.MessageId == "99");
    }

    // ── Toggle command ───────────────────────────────────────────────────────

    [Fact]
    public void ToggleWatchCommand_IsRegisteredOnCtrlShiftW()
    {
        var registry = new CommandRegistry();
        var (vm, _, _) = MakeVm(registry: registry);
        Assert.NotNull(vm);

        var cmd = registry.FindById("mail.toggleWatch");
        Assert.NotNull(cmd);
        Assert.Equal("Mail", cmd!.Category);
        Assert.Equal(Key.W, cmd.DefaultKey);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, cmd.DefaultModifiers);

        // Ctrl+W must keep resolving to something else — this feature deliberately did not take it.
        var ctrlW = registry.FindByGesture(Key.W, ModifierKeys.Control);
        Assert.True(ctrlW == null || ctrlW.Id != "mail.toggleWatch");
    }

    [Fact]
    public void ToggleWatch_WatchesThenUnwatchesTheWholeConversation()
    {
        var (vm, watch, _) = MakeVm();
        SetWatchTarget(vm, Msg("1", "Re: Budget Review"));

        vm.ToggleWatchConversation();
        Assert.True(watch.IsWatched("Budget Review"));
        Assert.True(vm.IsWatchTargetWatched);

        // Toggling from a *different* message of the same conversation turns the same watch off.
        SetWatchTarget(vm, Msg("2", "Fwd: budget review"));
        Assert.True(vm.IsWatchTargetWatched);
        vm.ToggleWatchConversation();

        Assert.False(watch.IsWatched("Budget Review"));
        Assert.Empty(watch.GetAll());
    }

    [Fact]
    public void ToggleWatch_UsesTheViewsTarget_NotTheSelectedMessage()
    {
        // Regression: selecting a conversation group header does NOT update SelectedMessage
        // (GroupedMessageTreeController.OnSelectedItemChanged only assigns for MailMessageSummary),
        // so acting on SelectedMessage would watch whatever thread was selected before the header —
        // and announce that wrong subject as though it were what the user asked for.
        var (vm, watch, _) = MakeVm();
        vm.SelectedMessage = Msg("1", "Budget Review");     // stale: what was selected earlier
        vm.WatchTargetResolver = () => "Trip to Dublin";    // what the group tree actually shows

        vm.ToggleWatchConversation();

        Assert.True(watch.IsWatched("Trip to Dublin"));
        Assert.False(watch.IsWatched("Budget Review"));
    }

    [Fact]
    public void ToggleWatch_WhenTheViewReportsNoTarget_DoesNothing()
    {
        // A From/To group header spans many conversations, so it is not a watch target. The command
        // must be inert there rather than falling back to the stale selected message.
        var (vm, watch, _) = MakeVm();
        vm.SelectedMessage = Msg("1", "Budget Review");
        vm.WatchTargetResolver = () => null;

        vm.RefreshWatchTarget();          // what the View does as the Message menu opens
        Assert.False(vm.HasWatchTarget);  // so the menu item dims

        vm.ToggleWatchConversation();

        Assert.Empty(watch.GetAll());
    }

    [Fact]
    public void ToggleWatch_OnABlankSubject_StoresNothingAndSaysWhy()
    {
        var (vm, watch, _) = MakeVm();
        var announcements = new List<string>();
        vm.AnnouncementRequested += (_, e) => announcements.Add(e.Text);
        SetWatchTarget(vm, Msg("1", "   "));

        vm.ToggleWatchConversation();

        Assert.Empty(watch.GetAll());
        Assert.Contains(announcements, a => a.Contains("no subject", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToggleWatch_AnnouncesBothDirections()
    {
        var (vm, _, _) = MakeVm();
        var announcements = new List<string>();
        vm.AnnouncementRequested += (_, e) => announcements.Add(e.Text);
        SetWatchTarget(vm, Msg("1", "Budget Review"));

        vm.ToggleWatchConversation();
        vm.ToggleWatchConversation();

        Assert.Equal(2, announcements.Count);
        Assert.Contains("Watching conversation: Budget Review", announcements[0], StringComparison.Ordinal);
        Assert.Contains("Stopped watching: Budget Review", announcements[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleWatch_InsideTheWatchedFolder_RemovesTheWholeConversationFromTheList()
    {
        var corpus = new[]
        {
            Msg("1", "Budget Review",     daysAgo: 3),
            Msg("2", "Re: Budget Review", daysAgo: 2),
            Msg("3", "Trip to Dublin",    daysAgo: 1),
        };
        var (vm, watch, _) = MakeVm(corpus);
        watch.Watch("Budget Review");
        watch.Watch("Trip to Dublin");
        await SelectWatchedFolder(vm);
        Assert.Equal(3, vm.Messages.Count);

        SetWatchTarget(vm, vm.Messages.First(m => m.MessageId == "2"));
        vm.ToggleWatchConversation();

        // Both members of the conversation leave, not just the selected one.
        Assert.Equal(new[] { "3" }, vm.Messages.Select(m => m.MessageId).ToArray());
        Assert.NotNull(vm.SelectedMessage);
        Assert.Equal("3", vm.SelectedMessage!.MessageId);
    }

    [Fact]
    public async Task ToggleWatch_UnwatchingTheLastConversation_LeavesTheEmptyStateStatus()
    {
        var (vm, watch, _) = MakeVm(new[] { Msg("1", "Budget Review") });
        watch.Watch("Budget Review");
        await SelectWatchedFolder(vm);

        SetWatchTarget(vm, vm.Messages[0]);
        vm.ToggleWatchConversation();

        Assert.Empty(vm.Messages);
        Assert.Null(vm.SelectedMessage);
        Assert.Contains("No watched conversations", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleWatch_OutsideTheWatchedFolder_UnwatchesButKeepsTheRow()
    {
        // Unwatching only prunes the list in the watched folder, whose membership IS the predicate.
        // Anywhere else the message still belongs where it is — only its watch state changes.
        var (vm, watch, _) = MakeVm(new[] { Msg("1", "Budget Review") });
        watch.Watch("Budget Review");

        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllMailFolder);
        var message = Assert.Single(vm.Messages);
        // Rows in ordinary folders carry watch state too, so the spoken "watched" field reads
        // correctly wherever the message appears.
        Assert.True(message.IsWatched);

        SetWatchTarget(vm, message);
        vm.ToggleWatchConversation();

        Assert.False(watch.IsWatched("Budget Review"));
        Assert.False(message.IsWatched);
        Assert.Contains(message, vm.Messages);
    }

    // ── Row speech catalog ───────────────────────────────────────────────────

    [Fact]
    public void RowFieldCatalog_WatchedFieldIsAppendedLastAndShipsDisabled()
    {
        // The MessageFields array order is the positional binding order used by Views.RowSpeech —
        // inserting anywhere but the end would misalign every field after it, silently corrupting
        // spoken row text while looking perfectly correct on screen.
        //
        // Asserted as "watched comes after everything that predates it" rather than "watched is
        // last": the rule is that a NEW field is appended, so the last entry is whichever field
        // was added most recently, and pinning watched to that position made this test fail for
        // the one change that obeys the rule (#637).
        var fields = RowFieldCatalog.For(RowKind.Message);
        var ids = fields.Select(f => f.Id).ToList();
        foreach (var predecessor in new[] { "flag", "status", "attachments", "from", "subject",
                                            "preview", "date", "unread", "replied", "forwarded",
                                            "to", "folder", "mailinglist" })
            Assert.True(ids.IndexOf("watched") > ids.IndexOf(predecessor),
                $"watched must be appended after {predecessor}, not inserted before it");

        var watched = RowFieldCatalog.Find(RowKind.Message, "watched");
        Assert.NotNull(watched);
        Assert.Equal("IsWatched", watched!.BindingPath);
        Assert.Equal(RowFieldFormat.State, watched.Format);
        Assert.Equal("watched", watched.TrueWord);
        Assert.Null(watched.FalseWord);   // silent when not watched

        // Ships disabled: an existing user's spoken rows are unchanged until they opt in.
        var layout = RowFieldCatalog.DefaultLayout(RowKind.Message);
        Assert.False(layout.Single(f => f.Id == "watched").Enabled);
    }

    [Fact]
    public void RowFieldCatalog_PreExistingMessageFieldOrderIsUnchanged()
    {
        // Pins the binding order that shipped before this feature. If this fails, the new field was
        // inserted rather than appended, and every row's speech is now wrong.
        Assert.Equal(
            new[]
            {
                "flag", "status", "attachments", "from", "subject", "preview", "date",
                "unread", "replied", "forwarded", "to", "folder", "mailinglist",
            },
            RowFieldCatalog.For(RowKind.Message).Select(f => f.Id).Take(13).ToArray());
    }

    [Fact]
    public void ReconcileMessageState_DoesNotClearIsWatched()
    {
        // A freshly fetched summary has never been stamped, so if IsWatched were copied in
        // ReconcileMessageState the flag would be wiped on every aggregate merge. Exercised through
        // the sync path, which is where the merge actually happens.
        var (vm, watch, sync) = MakeVm(new[] { Msg("1", "Budget Review", daysAgo: 1) });
        watch.Watch("Budget Review");
        SelectWatchedFolder(vm).GetAwaiter().GetResult();
        var shown = vm.Messages.Single();
        Assert.True(shown.IsWatched);

        // The same message arriving again from the server, unstamped.
        sync.Raise(Msg("1", "Budget Review", daysAgo: 1));

        Assert.True(shown.IsWatched);
    }
}
