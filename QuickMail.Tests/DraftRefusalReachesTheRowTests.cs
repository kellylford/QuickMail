// A refused draft has to change the row the user is looking at — issue #637.
//
// When the upload pass succeeds it raises MessagesRemoved and the row disappears, which is signal
// enough. When it FAILS the row stays, so nothing else would ever tell the open list: the refusal
// was written to SQLite and the summary in Messages went on saying "not on server" — which the row
// defines as ON ITS WAY — about a draft nothing would retry, until the folder was reopened or the
// app restarted.
//
// This exercises the production path end to end: the sweep's event, the view model's handler, and
// the change notification the row field depends on. Two earlier tests claimed to cover this and did
// not — they set the property from the test and asserted that setting it raised PropertyChanged,
// which is the source generator's behaviour and would have passed with no production code at all.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftRefusalReachesTheRowTests
{
    private static readonly Guid AccountId = Guid.Parse("7f7f7f7f-7f7f-7f7f-7f7f-7f7f7f7f7f7f");

    /// <summary>A sync service whose refusal event a test can raise.</summary>
    private sealed class RefusingSync : ISyncService
    {
#pragma warning disable CS0067 // not raised by this fake
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? DraftUploadsRefused;
        public event Action<int>? DraftsUploaded;

        public void RaiseRefused(params MailMessageSummary[] rows) => DraftUploadsRefused?.Invoke(rows);
        public void RaiseUploaded(int count) => DraftsUploaded?.Invoke(count);

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> a,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> f, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<int> ReconcileFolderAsync(AccountModel a, MailFolderModel f, CancellationToken ct) => Task.FromResult(0);
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
    }

    private static MailMessageSummary Row(string id = "local-1") => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = "Airport thoughts", IsPendingUpload = true, IsRead = true,
    };

    private static (MainViewModel vm, RefusingSync sync) MakeVm(params MailMessageSummary[] rows)
    {
        var sync = new RefusingSync();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService())
        {
            Messages = new BatchObservableCollection<MailMessageSummary>(rows),
        };
        return (vm, sync);
    }

    [Fact]
    public void ARefusalChangesWhatTheOpenRowSays()
    {
        var live = Row();
        var (vm, sync) = MakeVm(live);
        Assert.Equal("not on server", live.LocationLabel);

        // The sweep hands over rows it read from the store — NOT the instances the list holds,
        // which is why the handler matches them up rather than assigning them in.
        var fromStore = Row();
        fromStore.SendFailedReason = "Your mail server refused it: over quota.";
        sync.RaiseRefused(fromStore);

        Assert.Equal("not uploaded", live.LocationLabel);
        Assert.Equal("Not uploaded", live.StatusDisplay);
    }

    [Fact]
    public void TheRowFieldIsToldToRefresh()
    {
        // The field binds LocationLabel, so the refusal has to raise it — the previous binding did,
        // and moving the binding without moving the notification left the row stale even once the
        // state had changed underneath it.
        var live = Row();
        var (vm, sync) = MakeVm(live);
        var raised = new List<string?>();
        live.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var fromStore = Row();
        fromStore.SendFailedReason = "Your mail server refused it: over quota.";
        sync.RaiseRefused(fromStore);

        Assert.Contains(nameof(MailMessageSummary.LocationLabel), raised);
        Assert.Contains(nameof(MailMessageSummary.DeliveryNotice), raised);
    }

    [Fact]
    public void SavingTheDraftAgain_TakesTheRefusalBackOffTheRow()
    {
        // The user is told to open the draft, fix it and save. He does -- and the row went on
        // saying "not uploaded", the wording this feature defines as stuck until you act. Offline
        // that never corrects itself, so the one durable channel lied in the opposite direction
        // from the bug the refusal event was added to fix.
        var live = Row();
        var (vm, sync) = MakeVm(live);

        var fromStore = Row();
        fromStore.SendFailedReason = "Your mail server refused it: over quota.";
        sync.RaiseRefused(fromStore);
        Assert.Equal("not uploaded", live.LocationLabel);

        vm.OnDraftStored(AccountId, "Drafts", "local-1", "Airport thoughts, again");

        Assert.Null(live.SendFailedReason);
        Assert.Equal("not on server", live.LocationLabel);
        // An offline edit that changed the subject has to reach the list too: the subject is what
        // the row is announced by.
        Assert.Equal("Airport thoughts, again", live.Subject);
    }

    [Fact]
    public void AnUploadSaysSoOnTheStatusLine()
    {
        // The rows go through MessagesRemoved, whose handler ends by setting the status line to the
        // folder's message count -- so on its own an upload is indistinguishable from the list
        // reordering itself: the draft the user was sitting on is simply not there any more.
        var (vm, sync) = MakeVm(Row());

        sync.RaiseUploaded(1);
        Assert.Equal("1 draft uploaded.", vm.StatusText);

        sync.RaiseUploaded(3);
        Assert.Equal("3 drafts uploaded.", vm.StatusText);
    }

    [Fact]
    public void OnceTheUploadTakesIt_TheRowGoes()
    {
        // OnDraftStored marks the row pending; the save's server leg then uploads and deletes the
        // local copy. With nothing telling the list, the row went on saying "not on server" about a
        // draft that was ON the server -- Enter on it answered that its saved copy was missing, and
        // Delete offered to destroy the only copy of something that no longer existed.
        var live = Row();
        var (vm, _) = MakeVm(live);
        vm.OnDraftStored(AccountId, "Drafts", "local-1", "Airport thoughts");
        Assert.Contains(live, vm.Messages);

        vm.OnDraftRowDropped(AccountId, "Drafts", "local-1", DraftRowDropReason.Uploaded);

        Assert.DoesNotContain(live, vm.Messages);
    }

    [Fact]
    public void ASenderChangeIsNotReportedAsAnUpload()
    {
        // DraftRowDropped has three raisers and only one of them is an upload. Announcing all of
        // them as one told the user, offline, that a draft had reached a server it had not been
        // anywhere near -- on the one channel that reaches him (#637).
        var (vm, _) = MakeVm(Row());

        vm.OnDraftRowDropped(AccountId, "Drafts", "local-1", DraftRowDropReason.MovedToAnotherAccount);

        Assert.Equal("Draft moved to another account.", vm.StatusText);
    }

    [Fact]
    public void DecliningToKeepADraft_SaysNothingAndStillDropsTheRow()
    {
        // The row going IS the outcome the user asked for, so there is nothing to report -- but the
        // row must still go, or it points at a message that no longer exists.
        var live = Row();
        var (vm, _) = MakeVm(live);
        vm.StatusText = "Ready";

        vm.OnDraftRowDropped(AccountId, "Drafts", "local-1", DraftRowDropReason.Discarded);

        Assert.DoesNotContain(live, vm.Messages);
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public void ARowDroppedElsewhere_LeavesThisOneAlone()
    {
        var live = Row();
        var (vm, _) = MakeVm(live);

        vm.OnDraftRowDropped(Guid.NewGuid(), "Drafts", "local-1", DraftRowDropReason.Uploaded);
        vm.OnDraftRowDropped(AccountId, "Sent", "local-1", DraftRowDropReason.Uploaded);
        vm.OnDraftRowDropped(AccountId, "Drafts", "local-2", DraftRowDropReason.Uploaded);

        Assert.Contains(live, vm.Messages);
    }

    [Fact]
    public void ADraftStoredElsewhere_LeavesThisRowAlone()
    {
        var live = Row();
        var (vm, sync) = MakeVm(live);
        var fromStore = Row();
        fromStore.SendFailedReason = "Your mail server refused it: over quota.";
        sync.RaiseRefused(fromStore);

        vm.OnDraftStored(Guid.NewGuid(), "Drafts", "local-1", "Somewhere else");

        Assert.Equal("not uploaded", live.LocationLabel);
        Assert.Equal("Airport thoughts", live.Subject);
    }

    [Fact]
    public void ARefusalForADifferentRowLeavesThisOneAlone()
    {
        // Rows key on account, folder AND id. Matching on fewer has been the recurring source of
        // bugs in this feature, and here it would mark the wrong draft as refused.
        var live = Row("local-1");
        var (vm, sync) = MakeVm(live);

        var other = Row("local-2");
        other.SendFailedReason = "Your mail server refused it: over quota.";
        sync.RaiseRefused(other);

        Assert.Equal("not on server", live.LocationLabel);
        Assert.Null(live.SendFailedReason);
    }

    [Fact]
    public void ARefusalForTheSameIdInAnotherAccountLeavesThisOneAlone()
    {
        var live = Row();
        var (vm, sync) = MakeVm(live);

        var elsewhere = Row();
        elsewhere.AccountId = Guid.NewGuid();
        elsewhere.SendFailedReason = "Your mail server refused it: over quota.";
        sync.RaiseRefused(elsewhere);

        Assert.Equal("not on server", live.LocationLabel);
    }
}
