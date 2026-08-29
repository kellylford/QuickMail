// The upload pass that carries offline drafts to the server once it is reachable — issue #637.
//
// Two rules are what this file exists to pin.
//
// Oldest first, so a chain of supersedes replays in the order it was written.
//
// And stop only when the account is UNREACHABLE, because then the rest would each spend a
// connection timeout and everything is retried on the next sweep anyway. A draft the server
// answered and refused is different: stopping there blocked every draft behind it forever, since
// this pass replays oldest-first and the refused one is first every time. That draft is marked so
// its row says so, and the pass carries on.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SyncServiceDraftUploadTests
{
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static AccountModel Account() => new() { Id = AccountId, Username = "me@example.com" };

    private static SyncService MakeSync(RecordingMailService imap, ILocalDraftService drafts)
        => new(imap, new StubLocalStoreService(), new StubConfigService(), new StubRuleService(),
               ui: new StubUiDispatcher(), probeMode: false, localDrafts: drafts);

    /// <summary>Draft service whose pending list and failure behaviour a test can dictate outright.</summary>
    private sealed class ScriptedDraftService : ILocalDraftService
    {
        private readonly List<MailMessageSummary> _pending = [];

        public List<string> Discarded { get; } = [];
        public Dictionary<string, string?> Supersedes { get; } = [];
        public HashSet<string> MissingBytes { get; } = [];

        public void AddPending(string id, DateTimeOffset written, string? supersedes = null)
        {
            _pending.Add(new MailMessageSummary
            {
                MessageId = id, AccountId = AccountId, FolderName = "Drafts",
                Date = written, IsPendingUpload = true,
            });
            Supersedes[id] = supersedes;
        }

        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid accountId)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(
                [.. _pending.Where(p => p.AccountId == accountId).OrderBy(p => p.Date)]);

        public Task<ComposeModel?> LoadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
            => Task.FromResult(MissingBytes.Contains(messageId)
                ? null
                : new ComposeModel { AccountId = accountId, Subject = messageId });

        public Task<string?> GetSupersededServerIdAsync(Guid accountId, string folderName, string messageId)
            => Task.FromResult(Supersedes.TryGetValue(messageId, out var s) ? s : null);

        /// <summary>Drafts the pass marked refused, and why (#637).</summary>
        public Dictionary<string, string> Failed { get; } = [];

        public Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason)
        {
            Failed[messageId] = reason;
            return Task.CompletedTask;
        }

        public Task DiscardAsync(Guid accountId, string folderName, string messageId)
        {
            Discarded.Add(messageId);
            _pending.RemoveAll(p => p.MessageId == messageId);
            return Task.CompletedTask;
        }

        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft, string folderName,
            string? previousMessageId, CancellationToken ct = default)
            => throw new NotSupportedException("the upload pass never saves");

        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
    }

    [Fact]
    public async Task UploadsEveryPendingDraft_AndDropsEachLocalCopy()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        drafts.AddPending("local-2", DateTimeOffset.UtcNow.AddMinutes(-5));

        var uploaded = await MakeSync(imap, drafts).UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(2, uploaded);
        Assert.Equal(2, imap.AppendDraftCalls);
        Assert.Equal(["local-1", "local-2"], drafts.Discarded);
    }

    /// <summary>
    /// Oldest first. Written order is the only order in which a sequence of edits to the same draft
    /// makes sense, and the store query is what guarantees it — this pins the pass to that guarantee.
    /// </summary>
    [Fact]
    public async Task ReplaysOldestFirst()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDraftService();
        drafts.AddPending("newer", DateTimeOffset.UtcNow);
        drafts.AddPending("older", DateTimeOffset.UtcNow.AddHours(-2));

        await MakeSync(imap, drafts).UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(["older", "newer"], drafts.Discarded);
    }

    /// <summary>
    /// A draft that supersedes a server one must upload as a replacement, or the stale server copy
    /// survives and the user is left with two drafts where they wrote one.
    /// </summary>
    [Fact]
    public async Task PassesTheSupersededServerId_SoTheUploadReplacesRatherThanDuplicates()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-1", DateTimeOffset.UtcNow, supersedes: "server-uid-77");

        await MakeSync(imap, drafts).UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal("server-uid-77", imap.LastReplaceMessageId);
    }

    /// <summary>
    /// A pending row whose bytes are gone can never upload, and must not be reported as though it
    /// had. Deleting it and counting it among the uploaded told the user their draft had reached
    /// the server when it had in fact just been destroyed, unread — the row is marked instead, so
    /// it stays, says what happened, and stops jamming the pass (#637).
    /// </summary>
    [Fact]
    public async Task MarksAPendingRowWhoseStoredBytesAreMissing_RatherThanCallingItUploaded()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-orphan", DateTimeOffset.UtcNow);
        drafts.MissingBytes.Add("local-orphan");

        var uploaded = await MakeSync(imap, drafts)
            .UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(0, imap.AppendDraftCalls);
        Assert.Equal(0, uploaded);
        Assert.Empty(drafts.Discarded);
        Assert.Contains("could not be read", drafts.Failed["local-orphan"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A draft the server REFUSES must not stop the ones behind it. The pass replays oldest-first,
    /// so a permanently-refused draft is first every time — stopping there meant every later draft
    /// silently never uploaded, for as long as that one stayed (#637).
    /// </summary>
    [Fact]
    public async Task ARefusedDraft_IsMarkedAndTheRestStillUpload()
    {
        var imap = new RecordingMailService
        {
            AppendDraftFailure = id => id == "local-bad"
                ? new InvalidOperationException("mailbox does not exist")
                : null,
        };
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-bad",  DateTimeOffset.UtcNow.AddHours(-2));
        drafts.AddPending("local-good", DateTimeOffset.UtcNow.AddHours(-1));

        var uploaded = await MakeSync(imap, drafts)
            .UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(1, uploaded);
        Assert.Equal(["local-good"], drafts.Discarded);
        Assert.Contains("mailbox does not exist", drafts.Failed["local-bad"], StringComparison.Ordinal);
    }

    /// <summary>
    /// An account that is merely unreachable still stops the pass, and nothing is marked: every
    /// draft is retried on the next sweep rather than each one spending a connection timeout.
    /// </summary>
    [Fact]
    public async Task AnUnreachableAccount_StopsThePass_WithoutMarkingAnything()
    {
        var imap = new RecordingMailService
        {
            AppendDraftFailure = _ => new System.Net.Sockets.SocketException(10060),
        };
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-a", DateTimeOffset.UtcNow.AddHours(-2));
        drafts.AddPending("local-b", DateTimeOffset.UtcNow.AddHours(-1));

        var uploaded = await MakeSync(imap, drafts)
            .UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(0, uploaded);
        Assert.Empty(drafts.Failed);
        Assert.Equal(2, (await drafts.GetPendingAsync(AccountId)).Count);
    }

    [Fact]
    public async Task DoesNothing_WhenNothingIsPending()
    {
        var imap = new RecordingMailService();

        var uploaded = await MakeSync(imap, new ScriptedDraftService())
            .UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(0, uploaded);
        Assert.Equal(0, imap.AppendDraftCalls);
    }

    /// <summary>
    /// Announcing the rows as removed is how the Drafts list drops the local copies — the same path
    /// it already uses for messages that stopped existing, rather than a second mechanism.
    /// </summary>
    [Fact]
    public async Task RaisesMessagesRemoved_ForTheUploadedRows()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-1", DateTimeOffset.UtcNow);
        var sync = MakeSync(imap, drafts);

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        await sync.UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal("local-1", Assert.Single(removed).MessageId);
    }
}
