// Where focus goes after a command that rebuilds a grouped view, and what counts as a draft —
// issue #637.
//
// Two things the branch got wrong by asking the wrong object:
//
//   - whether the rebuild has happened was inferred from the awaited command having returned. It is
//     built on the thread pool and posted back, so for an all-local draft delete — no network leg,
//     SQLite completing synchronously — the command returns first and the landing listener was torn
//     off before it could fire. The row went and focus was left on nothing.
//   - whether a message is a draft was asked of the FOLDER on screen, which is the wrong message
//     whenever a window navigates Prev/Next, a notification opens mail from elsewhere, or an
//     aggregate view merges drafts in beside ordinary mail.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class GroupRebuildLandingTests
{
    private static readonly Guid AccountId = Guid.Parse("1a1a1a1a-1a1a-1a1a-1a1a-1a1a1a1a1a1a");

    private static MailFolderModel Folder(string full, string display, SpecialFolderKind kind) =>
        new() { AccountId = AccountId, FullName = full, DisplayName = display, Kind = kind };

    private static MailMessageSummary Row(string id, string folder, bool pending = false) => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = folder,
        Subject = $"Message {id}", IsRead = true, IsPendingUpload = pending,
        Date = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
    };

    private sealed class OneAccount : IAccountService
    {
        public List<AccountModel> LoadAccounts() =>
            [new AccountModel { Id = AccountId, AccountName = "Work", Username = "k@work.example" }];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MainViewModel Vm(IUiDispatcher? ui = null, StubLocalStoreService? store = null,
                                    IMailService? mail = null) =>
        new(mail ?? new StubImapMailService(), new OneAccount(), new StubCredentialService(),
            store ?? new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(),
            uiDispatcher: ui ?? new StubUiDispatcher());

    // ── the wait ─────────────────────────────────────────────────────────────

    [Fact]
    public void WithNothingScheduled_TheWaitIsAlreadyOver()
    {
        // A command that refused, or that rebuilt nothing, must not park the caller.
        Assert.True(Vm().GroupRebuildSettledAsync().IsCompleted);
    }

    [Fact]
    public async Task TheWaitLastsUntilTheRebuildHasBeenAppliedToTheUi()
    {
        // The whole point. Completing when the thread-pool build finished — or when the command
        // that triggered it returned — is exactly the too-early disarm this replaced.
        var ui = new HoldsThePost();
        var vm = Vm(ui);
        vm.Messages.Add(Row("1", "INBOX"));

        vm.ViewMode = ViewMode.Conversations;          // schedules a rebuild
        var settled = vm.GroupRebuildSettledAsync();

        // The build runs off the UI thread; wait for it to reach the post rather than assume it has.
        while (!ui.HasPending)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.False(settled.IsCompleted);             // built, but not landed

        ui.RunPending();

        await settled;
        Assert.NotEmpty(vm.Conversations);
    }

    [Fact]
    public async Task ARebuildSupersededByAnother_HandsItsWaitersOverRatherThanEndingTheWait()
    {
        // A background sweep scheduling its own rebuild between the command's and the command's
        // post. The superseded post changes nothing, so completing the wait there disarmed the
        // landing listener and left the REAL rebuild to land with nobody listening -- the same
        // focus-on-nothing this wait exists to prevent.
        var ui = new HoldsThePost();
        var vm = Vm(ui);
        vm.Messages.Add(Row("1", "INBOX"));

        vm.ViewMode = ViewMode.Conversations;                 // rebuild A
        var settled = vm.GroupRebuildSettledAsync();
        while (!ui.HasPending)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        vm.Messages.Add(Row("2", "INBOX"));
        vm.RebuildActiveGroupViewForTests();                  // rebuild B supersedes A
        while (ui.PendingCount < 2)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        ui.RunOldestPending();                                // A lands as a no-op
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(settled.IsCompleted);

        ui.RunPending();                                      // B lands for real
        await settled;
        Assert.Equal(2, vm.Conversations.Count);
    }

    private sealed class HoldsThePost : IUiDispatcher
    {
        private readonly List<Action> _pending = [];
        public bool HasPending { get { lock (_pending) return _pending.Count > 0; } }
        public int PendingCount { get { lock (_pending) return _pending.Count; } }
        public void RunOldestPending()
        {
            Action? due;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                due = _pending[0];
                _pending.RemoveAt(0);
            }
            due();
        }
        public void Invoke(Action action) => action();
        public void Post(Action action) { lock (_pending) _pending.Add(action); }
        public void RunPending()
        {
            List<Action> due;
            lock (_pending) { due = [.. _pending]; _pending.Clear(); }
            foreach (var a in due) a();
        }
    }

    // ── what counts as a draft ───────────────────────────────────────────────

    [Fact]
    public void ADraftThisComputerIsStillHolding_IsADraftWhereverItIsShown()
    {
        // All Mail and All Inboxes merge local-only drafts in beside ordinary mail, so the folder
        // on screen says nothing about the row.
        var vm = Vm();
        Assert.True(vm.IsDraftRow(Row("local-1", "Drafts", pending: true)));
    }

    [Fact]
    public void OrdinaryMail_IsNotADraftJustBecauseTheDraftsFolderIsSelected()
    {
        // The regression this replaced: activating a new-mail notification while Drafts was open
        // told the user their DRAFT could not be recovered, about somebody else's message.
        var vm = Vm();
        vm.SelectedFolder = MainViewModel.AllDraftsFolder;

        Assert.False(vm.IsDraftRow(Row("41", "INBOX")));
    }

    [Fact]
    public void ARefusedDraftSittingInAllMail_IsStillADraft()
    {
        // All Mail is where the app starts, and it lists local-only drafts beside ordinary mail --
        // it reads the whole cache rather than merging folder by folder, so nothing filters them
        // out. This is the answer the open routing depends on: the row says "not uploaded" and the
        // guide says open it and fix what the server objected to, which needs a compose window.
        var vm = Vm();
        vm.SelectedFolder = MainViewModel.AllMailFolder;
        var refused = Row("local-1", "Drafts", pending: true);
        refused.SendFailedReason = "Your mail server refused it: over quota.";

        Assert.True(vm.IsDraftRow(refused));
        Assert.False(vm.IsSelectedFolderDrafts);      // which is why asking the folder was wrong
    }

    [Fact]
    public void AnUploadedServerDraft_IsADraftButNotOneThisComputerIsHolding()
    {
        // The two questions, and why routing needs the narrow one. Sending an already-uploaded
        // server draft down the compose path -- which skips the local cache to read the server --
        // meant that offline, in All Mail, Enter opened nothing at all.
        var vm = Vm();
        vm.SelectedFolder = MainViewModel.AllMailFolder;
        var uploaded = Row("41", MainViewModel.AllDraftsFolder.FullName);

        Assert.True(vm.IsDraftRow(uploaded));
        Assert.False(vm.IsLocalDraftRow(uploaded));
    }

    [Fact]
    public void ADraftStillHeldHere_IsBothKindsOfDraft()
    {
        var vm = Vm();
        var held = Row("local-1", "Drafts", pending: true);

        Assert.True(vm.IsDraftRow(held));
        Assert.True(vm.IsLocalDraftRow(held));
    }

    [Fact]
    public async Task AServerDraftThatWillNotLoad_SaysTheSameThingOnBothChannels()
    {
        // The channel that matters is the one the user reads with announcements off. Handing the
        // network failure to the store placeholder made the reading pane say the saved copy was
        // damaged and the draft unrecoverable -- about a draft sitting intact on the server --
        // while the status line correctly said the server could not be reached.
        var vm = Vm(mail: new UnreachableMailService());
        vm.SelectedAccount = vm.Accounts.FirstOrDefault()
                             ?? new AccountModel { Id = AccountId, Username = "me@example.com" };
        vm.SelectedFolder = Folder("Drafts", "Drafts", SpecialFolderKind.Drafts);
        var serverDraft = Row("41", "Drafts");          // no local id, not pending: lives on the server
        vm.SelectedMessage = serverDraft;

        await vm.OpenDraftCommand.ExecuteAsync(null);

        Assert.NotNull(vm.MessageDetail);
        Assert.Equal(MainViewModel.DraftCouldNotBeOpened, vm.MessageDetail!.PlainTextBody);
        Assert.Contains("could not be opened", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("damaged", vm.MessageDetail.PlainTextBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnreachableMailService : StubImapMailServiceBase
    {
        public override Task<MailMessageDetail> GetMessageDetailAsync(
            Guid accountId, string folderName, string messageId, CancellationToken ct = default)
            => throw new System.Net.Sockets.SocketException(10060);
    }

    [Fact]
    public void TheCatchAllSentence_DoesNotNameACauseItCannotKnow()
    {
        // It is reached for ANY failure, including ones that never touched the network, so it must
        // not assert the server was unreachable -- and it must not be the store-damage sentence
        // either, which was what the reading pane showed for a draft sitting intact on the server.
        Assert.DoesNotContain("server could not be reached", MainViewModel.DraftCouldNotBeOpened,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("damaged", MainViewModel.DraftCouldNotBeOpened,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing has been discarded", MainViewModel.DraftCouldNotBeOpened,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnreadableStoreSentence_DoesNotPromiseTheDraftIsSafe()
    {
        // It used to end "Nothing has been lost — try again in a moment." The catch that produces
        // it is broad: a saved copy with truncated MIME throws here too, and the upload sweep calls
        // that permanent in as many words. Promising recovery every time, for ever, is the silent
        // loss this feature exists to remove.
        Assert.DoesNotContain("Nothing has been lost", MainViewModel.StoreUnreadable,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing has been lost", MainViewModel.StoreUnreadableMessage,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be recovered", MainViewModel.StoreUnreadable, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowInTheAllDraftsView_IsADraft()
    {
        Assert.True(Vm().IsDraftRow(Row("41", MainViewModel.AllDraftsFolder.FullName)));
    }

    [Fact]
    public async Task AServerDraftInTheAccountsRealDraftsFolder_IsADraft()
    {
        // Nothing local about it — no pending flag, an ordinary server id — so the folder it lives
        // in is the only thing that can say what it is.
        var store = new StubLocalStoreService();
        store.SeededFolders[AccountId] =
        [
            Folder("INBOX", "Inbox", SpecialFolderKind.Inbox),
            Folder("Drafts", "Drafts", SpecialFolderKind.Drafts),
        ];
        var vm = Vm(store: store);
        vm.LoadAccountList();      // App does this before the first load; the cache is account-scoped
        await vm.InitialLoadAsync();

        Assert.True(vm.IsDraftRow(Row("41", "Drafts")));
        Assert.False(vm.IsDraftRow(Row("42", "INBOX")));
    }
}
