// What survives a slow network, a noisy error body, and a window closing — issue #637.
//
// Round nineteen. Three of these pin failures that were silent by construction: a timeout that
// looked like the user cancelling, an error message that happened to contain the wrong words, and
// an orphaned row nobody was left to clear.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftUploadResilienceTests
{
    private static readonly Guid AccountId = Guid.Parse("9b9b9b9b-9b9b-9b9b-9b9b-9b9b9b9b9b9b");

    private static AccountModel Account() => new()
    {
        Id = AccountId, Username = "me@example.com", AccountName = "Work",
    };

    private static (SyncService Sync, Drafts Store, List<(AccountModel, string)> Blocked)
        MakeSync(Exception appendThrows)
    {
        var store = new Drafts();
        var sync = new SyncService(
            new RecordingMailService { AppendDraftFailure = _ => appendThrows },
            new StubLocalStoreService(), new StubConfigService(), new StubRuleService(),
            ui: new StubUiDispatcher(), probeMode: false, localDrafts: store);
        var blocked = new List<(AccountModel, string)>();
        sync.DraftUploadsBlocked += (a, r) => blocked.Add((a, r));
        return (sync, store, blocked);
    }

    // ── a timeout is not the user cancelling ─────────────────────────────────

    [Fact]
    public async Task ATimeoutOnOneDraft_DoesNotAbandonThePassOrTheSweep()
    {
        // HttpClient's own timeout throws a TaskCanceledException carrying a token that is not
        // ours. Rethrowing it aborted the pass on the draft it happened to hit -- and since the
        // replay is oldest-first, that draft was first on every later sweep too, so nothing behind
        // it ever went up, no row was marked and nothing was said. It also escaped
        // SyncAllAccountsAsync, whose caller reads a cancellation as "the user asked for this".
        var (sync, store, _) = MakeSync(new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException()));
        store.AddPending("local-1");
        store.AddPending("local-2");

        // Completes rather than throwing, which is the whole point.
        var uploaded = await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Equal(0, uploaded);
        Assert.Equal(2, store.Pending.Count);      // still queued, to be retried
        Assert.Empty(store.Failed);                // and not de-queued by a marking
    }

    [Fact]
    public async Task RealCancellation_StillStopsThePass()
    {
        // The other half: when the caller genuinely cancels, the pass must not swallow it.
        var (sync, store, _) = MakeSync(new InvalidOperationException("never reached"));
        store.AddPending("local-1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sync.UploadPendingDraftsAsync(Account(), cts.Token));
    }

    // ── the classifier reads exceptions, not prose ───────────────────────────

    [Fact]
    public async Task AServerErrorBodyMentioningDrafts_IsStillAVerdictAboutThatDraft()
    {
        // The no-Drafts-folder test matches on message TEXT, and ran before the verdict question,
        // so a server error body carrying the phrase stalled the whole account.
        var (sync, store, blocked) = MakeSync(new HttpRequestException(
            "Graph request failed (400 BadRequest): No Drafts folder is a phrase in this error body",
            inner: null, statusCode: HttpStatusCode.BadRequest));
        store.AddPending("local-1");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(blocked);
        Assert.Contains("Your mail server refused it", store.Failed["local-1"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASignInRefusalNestedInAnAggregate_IsStillAccountScope()
    {
        // Every predicate followed InnerException only, which is the FIRST of an aggregate's
        // several -- so a batch whose second failure was the refused sign-in classified on its
        // first, and the backlog was marked with the wrong sentence.
        var (sync, store, blocked) = MakeSync(new AggregateException(
            new InvalidOperationException("something else"),
            new HttpRequestException("401", inner: null, statusCode: HttpStatusCode.Unauthorized)));
        store.AddPending("local-1");

        await sync.UploadPendingDraftsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Empty(store.Failed);
        var (_, reason) = Assert.Single(blocked);
        Assert.Contains("sign in again", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNoDraftsFolderSentence_DoesNotAskForWhatHasJustBeenDone()
    {
        // It is raised only after a live connection has asked the server, so the account IS
        // connected: "connect the account once" told the user to do the thing they had just done.
        var (_, reason) = SendFailure.ClassifyUpload(
            new InvalidOperationException("No Drafts folder found for this account."), s => s);

        Assert.DoesNotContain("Connect the account", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("on their own", reason, StringComparison.Ordinal);
    }

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
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id) => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason)
        {
            Failed[id] = reason;
            Pending.RemoveAll(p => p.MessageId == id);
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
