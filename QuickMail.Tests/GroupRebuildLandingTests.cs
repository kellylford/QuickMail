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

    private static MainViewModel Vm(IUiDispatcher? ui = null, StubLocalStoreService? store = null) =>
        new(new StubImapMailService(), new OneAccount(), new StubCredentialService(),
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

    private sealed class HoldsThePost : IUiDispatcher
    {
        private readonly List<Action> _pending = [];
        public bool HasPending { get { lock (_pending) return _pending.Count > 0; } }
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
