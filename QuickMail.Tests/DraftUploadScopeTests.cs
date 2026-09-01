// Whose problem an upload failure is, and what the pass does about it — issue #637.
//
// The pass had two answers: unreachable (stop, keep everything queued) and everything else (mark
// this draft, carry on). "Everything else" swallowed the account-scope failures, and marking is
// what de-queues: LoadPendingDraftsAsync excludes any row with a send_failed_reason. So one expired
// token walked the whole backlog, marked every draft with a sentence telling the user to edit and
// save it, and signing in again brought none of it back.
//
// These pin the third answer, and the sentence each failure produces at the SITE that chooses it —
// the predicate had unit tests, and reverting the branch in the sweep that calls it left the suite
// green.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class DraftUploadScopeTests
{
    private static readonly Guid AccountId = Guid.Parse("4d4d4d4d-4d4d-4d4d-4d4d-4d4d4d4d4d4d");

    private static AccountModel Account() => new()
    {
        Id = AccountId, Username = "me@example.com", AccountName = "Work",
    };

    private static (SyncService Sync, Drafts Store, List<(AccountModel, string)> Blocked)
        MakeSync(Exception appendThrows)
    {
        var store = new Drafts();
        var sync = new SyncService(
            new RecordingMailService { AppendDraftFailure = _ => appendThrows }, new StubLocalStoreService(),
            new StubConfigService(), new StubRuleService(),
            ui: new StubUiDispatcher(), probeMode: false, localDrafts: store);
        var blocked = new List<(AccountModel, string)>();
        sync.DraftUploadsBlocked += (a, r) => blocked.Add((a, r));
        return (sync, store, blocked);
    }

    // ── account-scope: stop, keep the queue, say it once ─────────────────────

    [Fact]
    public async Task ARefusedSignIn_LeavesEveryDraftQueuedAndSaysWhatToDo()
    {
        // The measured failure: three drafts, one rejected login, all three marked and excluded
        // from the pending query for ever. Editing them is not the fix and never was.
        var (sync, store, blocked) = MakeSync(new AuthenticationException("Invalid credentials"));
        store.AddPending("local-1");
        store.AddPending("local-2");
        store.AddPending("local-3");

        var uploaded = await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Equal(0, uploaded);
        Assert.Empty(store.Failed);                       // nothing de-queued
        Assert.Equal(3, store.Pending.Count);
        var (account, reason) = Assert.Single(blocked);
        Assert.Equal(AccountId, account.Id);
        Assert.Contains("sign in again", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Edit the draft", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailedSecureConnection_IsNotBlamedOnTheDraft()
    {
        // Neither the draft's fault nor fixable by saving it again, which is what it used to say.
        var (sync, store, blocked) = MakeSync(new SslHandshakeException("The remote certificate is invalid"));
        store.AddPending("local-1");
        store.AddPending("local-2");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(store.Failed);
        Assert.Equal(2, store.Pending.Count);
        var (_, reason) = Assert.Single(blocked);
        Assert.Contains("secure connection", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAccountWithNoDraftsFolder_StopsWithoutDeQueuingAnything()
    {
        // The user guide used to give this as its example of "what the server said". No draft can
        // put it right, so no draft should be marked with it.
        var (sync, store, blocked) = MakeSync(
            new InvalidOperationException("No Drafts folder found for this account."));
        store.AddPending("local-1");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(store.Failed);
        Assert.Single(store.Pending);
        var (_, reason) = Assert.Single(blocked);
        Assert.Contains("no Drafts folder", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── message-scope: mark this one, carry on, and say whose fault it is ────

    [Fact]
    public async Task AServerVerdict_MarksThatDraftInTheServersName()
    {
        var (sync, store, blocked) = MakeSync(new SmtpCommandException(
            SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxUnavailable, "over quota"));
        store.AddPending("local-1");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(blocked);
        Assert.Contains("Your mail server refused it", store.Failed["local-1"], StringComparison.Ordinal);
        Assert.Contains("over quota", store.Failed["local-1"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickMailsOwnFailure_IsNotReportedAsTheServersVerdict()
    {
        // The defect this pins, at the site that chooses the sentence. Reverting the branch here
        // left the whole suite green: the predicate was tested, the wiring was not.
        var (sync, store, blocked) = MakeSync(new NullReferenceException());
        store.AddPending("local-1");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(blocked);
        Assert.Contains("QuickMail could not upload it", store.Failed["local-1"], StringComparison.Ordinal);
        Assert.DoesNotContain("mail server refused", store.Failed["local-1"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableAccount_StopsWithoutMarkingOrSayingAnything()
    {
        // Unchanged, and pinned here beside the other two so the three answers stay distinct.
        var (sync, store, blocked) = MakeSync(new System.Net.Sockets.SocketException(10060));
        store.AddPending("local-1");
        store.AddPending("local-2");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(store.Failed);
        Assert.Empty(blocked);
        Assert.Equal(2, store.Pending.Count);
    }

    // ── stubs ────────────────────────────────────────────────────────────────

    private sealed class Drafts : ILocalDraftService
    {
        public List<MailMessageSummary> Pending { get; } = [];
        public Dictionary<string, string> Failed { get; } = [];

        public void AddPending(string id) => Pending.Add(new MailMessageSummary
        {
            MessageId = id, AccountId = AccountId, FolderName = "Drafts",
            Date = DateTimeOffset.UtcNow, IsPendingUpload = true,
        });

        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid accountId)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([.. Pending.OrderBy(p => p.Date)]);
        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(new ComposeModel { AccountId = a, Subject = id });
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id)
            => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason)
        {
            Failed[id] = reason;
            Pending.RemoveAll(p => p.MessageId == id);   // the pending query excludes a marked row
            return Task.CompletedTask;
        }
        public Task DiscardAsync(Guid a, string f, string id)
        {
            Pending.RemoveAll(p => p.MessageId == id);
            return Task.CompletedTask;
        }
        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft, string folderName,
            string? previousMessageId, CancellationToken ct = default)
            => throw new NotSupportedException("the upload pass never saves");
        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id) => Task.FromResult(string.Empty);
    }
}
