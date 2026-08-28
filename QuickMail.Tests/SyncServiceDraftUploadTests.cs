// The upload pass that carries offline drafts to the server once it is reachable — issue #637.
//
// Two rules are what this file exists to pin. Oldest first, so a chain of supersedes replays in the
// order it was written. And stop on the first failure, because the overwhelmingly likely reason a
// draft will not upload is that the account is still unreachable, and trying the rest would spend a
// connection timeout each — everything still pending is retried on the next sweep, so nothing is
// lost by waiting.

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

    [Fact]
    public async Task StopsAtTheFirstFailure_AndLeavesTheRestPending()
    {
        var imap = new RecordingMailService { AppendDraftThrows = true };
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        drafts.AddPending("local-2", DateTimeOffset.UtcNow.AddMinutes(-5));

        var uploaded = await MakeSync(imap, drafts).UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(0, uploaded);
        Assert.Empty(drafts.Discarded);
        Assert.Equal(2, (await drafts.GetPendingAsync(AccountId)).Count);
    }

    /// <summary>
    /// A pending row whose bytes are gone can never upload. Leaving it would mark Drafts as having
    /// something to send forever, so the row goes rather than the pass jamming on it.
    /// </summary>
    [Fact]
    public async Task DropsAPendingRowWhoseStoredBytesAreMissing()
    {
        var imap = new RecordingMailService();
        var drafts = new ScriptedDraftService();
        drafts.AddPending("local-orphan", DateTimeOffset.UtcNow);
        drafts.MissingBytes.Add("local-orphan");

        await MakeSync(imap, drafts).UploadPendingDraftsAsync(Account(), CancellationToken.None);

        Assert.Equal(0, imap.AppendDraftCalls);
        Assert.Equal(["local-orphan"], drafts.Discarded);
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
