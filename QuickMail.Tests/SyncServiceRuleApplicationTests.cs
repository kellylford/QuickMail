using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pins the fix for the "client rules never ran on live-arriving mail" bug: mail that arrives while
/// QuickMail is running comes in through the IDLE / change-notifier path
/// (<see cref="SyncService.SyncOneFolderAsync"/> / <see cref="SyncService.SyncOneFolderOnlineAsync"/>),
/// which previously stored and displayed the message but never invoked the rule engine. Only the
/// full sync applied rules, so a message first seen live slipped past every client rule permanently.
/// <para>
/// The fix routes all sync paths through one rule-application chokepoint. These tests drive the two
/// live paths directly and assert the rule engine is invoked exactly once per genuinely-new message,
/// and never on a message already known to the store or already processed this session.
/// </para>
/// </summary>
public class SyncServiceRuleApplicationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MailFolderModel _inbox = new() { FullName = "INBOX", DisplayName = "Inbox" };

    public SyncServiceRuleApplicationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-sync-rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private AccountModel Account() => new() { Id = _accountId, AccountName = "Test", ImapHost = "host" };

    private MailMessageSummary Message(string id, bool read = true) => new()
    {
        MessageId = id,
        AccountId = _accountId,
        FolderName = "INBOX",
        From = "Tim Spaulding <tim.spaulding@bits-acb.org>",
        To = "me@example.com",
        Subject = "hello",
        IsRead = read,
    };

    // ── IRuleService that records every batch handed to the engine ───────────────
    private sealed class CapturingRuleService : IRuleService
    {
        public List<List<MailMessageSummary>> Calls { get; } = [];

        // Ids the simulated rule "moves/deletes" — returned as RemovedMessages so the chokepoint's
        // store-delete / batch-strip / MessagesRemoved path runs under test.
        public HashSet<string> RemoveIds { get; } = [];

        // The chokepoint skips the engine entirely when there are no enabled rules, so report one.
        private static readonly List<MailRule> OneEnabledRule = [new MailRule { Name = "t", IsEnabled = true }];

        public Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesAsync(
            List<MailMessageSummary> incoming, Guid accountId, CancellationToken ct)
        {
            Calls.Add(incoming.ToList());
            var removed = incoming.Where(m => RemoveIds.Contains(m.MessageId)).ToList();
            // Survivors: simulate a local-only mark-as-unread.
            foreach (var m in incoming.Where(m => !RemoveIds.Contains(m.MessageId))) m.IsRead = false;
            return Task.FromResult((incoming.Count, removed));
        }

        public List<MailRule> LoadRules() => OneEnabledRule;
        public void SaveRules(List<MailRule> rules) { }
        public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages) => [];
        public Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(ILocalStoreService store, CancellationToken ct)
            => Task.FromResult(new List<MailMessageSummary>());
    }

    // ── IMailService that returns a scripted batch from GetMessagesSinceAsync ─────
    private sealed class FetchStubMailService : IMailService
    {
        // Mutable so a test can add a later "arrival" between fetches.
        public List<MailMessageSummary> Batch { get; }
        public FetchStubMailService(List<MailMessageSummary> batch) => Batch = batch;

        // The only method the IDLE paths call to fetch.
        public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid a, string f, string sinceId, int count, CancellationToken ct = default)
            => Task.FromResult(Batch.Select(Clone).ToList());

        private static MailMessageSummary Clone(MailMessageSummary m) => new()
        {
            MessageId = m.MessageId, AccountId = m.AccountId, FolderName = m.FolderName,
            From = m.From, To = m.To, Subject = m.Subject, IsRead = m.IsRead, Preview = m.Preview,
        };

        // ── Everything else: inert ───────────────────────────────────────────────
        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected(Guid accountId) => true;
        public Task<List<MailFolderModel>> GetFoldersAsync(Guid a, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid a, string f, int max, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid a, string f, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<MailMessageDetail> GetMessageDetailAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task MarkReadAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReadBatchAsync(Guid a, string f, IList<string> ids, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMessageFlaggedAsync(Guid a, string f, string id, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashBatchAsync(Guid a, string f, IList<string> ids, CancellationToken ct = default) => Task.CompletedTask;
        public Task PermanentlyDeleteBatchAsync(Guid a, string f, IList<string> ids, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoOpAsync(Guid a, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountTrashMessagesAsync(Guid a, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EmptyTrashAsync(Guid a, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IList<string>> GetFolderMessageIdsAsync(Guid a, string f, CancellationToken ct = default) => Task.FromResult<IList<string>>([]);
        public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid a, string f, IList<string> ids, int maxLines, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<int> PollAsync(Guid a, string f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid a, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<string?> FindDraftsFolderNameAsync(Guid a, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string> AppendDraftAsync(Guid a, ComposeModel draft, string? replaceId, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task AppendToSentAsync(Guid a, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> DownloadAttachmentAsync(Guid a, string f, string id, string part, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task CopyMessagesAsync(Guid a, string f, IList<string> ids, string dest, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveMessagesAsync(Guid a, string f, IList<string> ids, string dest, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateFolderAsync(Guid a, string? parent, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(Guid a, string f, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameFolderAsync(Guid a, string f, string newName, string? newParent, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFolderAsync(Guid a, string f, string? destParent, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private SyncService Build(FetchStubMailService imap, CapturingRuleService rules)
        => new(imap, _store, new StubConfigService(), rules);

    // ── The verifications ────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveIdleSync_AppliesRulesToNewArrival()
    {
        // The exact scenario that was broken: a fresh message arrives while the app is running.
        var msg = Message("100");
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([msg]), rules);

        var forwarded = await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);          // the rule engine WAS invoked …
        Assert.Equal("100", Assert.Single(batch).MessageId);   // … with the new message
        Assert.False(Assert.Single(forwarded).IsRead);   // rule's mark-unread reached the UI batch
    }

    [Fact]
    public async Task LiveIdleSync_DoesNotReapplyRulesToAnAlreadyStoredMessage()
    {
        // Message already cached (e.g. a prior sync saw it). Rules must not fire again — otherwise a
        // move/delete rule would re-execute every poll.
        await _store.UpsertSummariesAsync([Message("200")]);
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([Message("200")]), rules);

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);   // known message → skipped entirely
    }

    [Fact]
    public async Task LiveIdleSync_NewAndKnownMixed_OnlyNewGoesToRules()
    {
        await _store.UpsertSummariesAsync([Message("300")]);          // known
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([Message("300"), Message("301")]), rules);

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);
        Assert.Equal("301", Assert.Single(batch).MessageId);   // only the genuinely-new one
    }

    [Fact]
    public async Task OnlineIdleSync_FirstFetchIsBaseline_RulesFireOnlyOnLaterArrivals()
    {
        // Online mode keeps no store and re-fetches the last 50 every fire — and that path is also
        // delete/archive reconciliation. So the FIRST fetch per folder must be a baseline (marked
        // seen, no rules), or a move/delete rule would retroactively rewrite up to 50 pre-existing
        // messages. Rules fire only on messages that appear in a LATER fetch.
        var rules = new CapturingRuleService();
        var mail = new FetchStubMailService([Message("400")]);
        var sync = Build(mail, rules);

        await sync.SyncOneFolderOnlineAsync(Account(), _inbox, CancellationToken.None);
        Assert.Empty(rules.Calls);   // baseline — msg 400 is pre-existing, not touched

        // A genuinely new message shows up in the next fetch.
        mail.Batch.Add(Message("401"));
        await sync.SyncOneFolderOnlineAsync(Account(), _inbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);          // rules ran once, only for the new arrival
        Assert.Equal("401", Assert.Single(batch).MessageId);
    }

    [Fact]
    public async Task LiveIdleSync_RuleRemovedMessage_StrippedFromBatch_DeletedFromStore_AndRaisesMessagesRemoved()
    {
        // A move/delete rule returns the message in RemovedMessages. The chokepoint must drop it
        // from the returned batch (so the UI doesn't show it in the origin folder), delete it from
        // the store, and raise MessagesRemoved.
        var rules = new CapturingRuleService();
        rules.RemoveIds.Add("500");
        var sync = Build(new FetchStubMailService([Message("500"), Message("501")]), rules);

        var removedRaised = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removedRaised.AddRange(list);

        var forwarded = await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        Assert.DoesNotContain(forwarded, m => m.MessageId == "500");   // stripped from batch
        Assert.Contains(forwarded, m => m.MessageId == "501");         // survivor kept
        Assert.Equal("500", Assert.Single(removedRaised).MessageId);   // MessagesRemoved fired
        var stored = await _store.GetAllMessageIdsAsync(_accountId, "INBOX");
        Assert.DoesNotContain("500", stored);                          // deleted from store
        Assert.Contains("501", stored);                                // survivor persisted
    }
}
