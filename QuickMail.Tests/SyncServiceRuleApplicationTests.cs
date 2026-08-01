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
        public Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(ILocalStoreService store, IReadOnlyDictionary<Guid, string> inboxFolderByAccount, CancellationToken ct)
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
        // The full-sync path uses the since-date fetch on a fresh store; return the same batch.
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid a, string f, DateTime since, CancellationToken ct = default) => Task.FromResult(Batch.Select(Clone).ToList());
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
        // Report the batch as still present on the server so the full sync's remote-deletion pass
        // doesn't treat the just-synced messages as deleted.
        public Task<IList<string>> GetFolderMessageIdsAsync(Guid a, string f, CancellationToken ct = default) => Task.FromResult<IList<string>>(Batch.Select(m => m.MessageId).ToList());
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
    public async Task FullSync_AppliesRulesToNewArrivals()
    {
        // The initial/periodic full sync (SyncFolderAsync, reached via SyncAllAccountsAsync) must run
        // rules too — it's the path that historically held this logic, and where a regression is worst.
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([Message("700")]), rules);
        var folder = new MailFolderModel { FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };
        var cached = new Dictionary<Guid, List<MailFolderModel>> { [_accountId] = [folder] };

        await sync.SyncAllAccountsAsync([Account()], cached, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);
        Assert.Equal("700", Assert.Single(batch).MessageId);
    }

    [Fact]
    public async Task NonInboxFolder_DoesNotRunRules_ButStillCachesTheMessages()
    {
        // #336: client rules fire only on the Inbox. Syncing a non-Inbox folder must NOT invoke the
        // rule engine (so a message a server rule / manual move already filed there isn't double-acted
        // on), but the message must still be fetched and cached.
        var archive = new MailFolderModel { FullName = "Archive", DisplayName = "Archive" }; // Kind = None
        var msg = new MailMessageSummary
        {
            MessageId = "900", AccountId = _accountId, FolderName = "Archive",
            From = "Tim <tim@bits-acb.org>", To = "me@example.com", Subject = "hello", IsRead = true,
        };
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([msg]), rules);

        var forwarded = await sync.SyncOneFolderAsync(Account(), archive, CancellationToken.None);

        Assert.Empty(rules.Calls);                                  // engine never invoked outside Inbox
        Assert.Contains(forwarded, m => m.MessageId == "900");      // message still surfaced to the UI
        var stored = await _store.GetAllMessageIdsAsync(_accountId, "Archive");
        Assert.Contains("900", stored);                             // and still cached
    }

    [Fact]
    public async Task GraphInbox_ByKind_RunsRules_EvenWithOpaqueFolderId()
    {
        // #336 gate is (Kind == Inbox || FullName == "INBOX"). For Graph the FullName is an opaque
        // folder id, never the literal "INBOX", so Kind is the ONLY thing keeping client rules alive
        // on a Graph inbox. This pins that: a future simplification to a FullName-only check would
        // silently stop running rules on every Graph inbox, and this test would go red.
        var graphInbox = new MailFolderModel
        {
            FullName = "AAMkADRmODc0NTk2LWI5ZGIt", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox,
        };
        var msg = new MailMessageSummary
        {
            MessageId = "42", AccountId = _accountId, FolderName = "AAMkADRmODc0NTk2LWI5ZGIt",
            From = "Amy <amy@x.com>", To = "me@example.com", Subject = "hello", IsRead = true,
        };
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([msg]), rules);

        await sync.SyncOneFolderAsync(Account(), graphInbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);                     // engine WAS invoked (via Kind)
        Assert.Equal("42", Assert.Single(batch).MessageId);
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

    // ── ReconcileFolderAsync (#366): live removal of mail deleted/moved by another client ─────────

    [Fact]
    public async Task Reconcile_RemovesGhost_WhenLocalIdMissingFromServer()
    {
        // Store holds A, B, C; the server now lists only A, C (B deleted/moved elsewhere). Reconcile
        // must delete B from the store and raise MessagesRemoved([B]).
        await _store.UpsertSummariesAsync([Message("A"), Message("B"), Message("C")]);
        var imap = new FetchStubMailService([]) ;
        imap.Batch.AddRange([Message("A"), Message("C")]); // server view = A, C
        var sync = Build(imap, new CapturingRuleService());

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        var count = await sync.ReconcileFolderAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal("B", Assert.Single(removed).MessageId);
        var stored = await _store.GetAllMessageIdsAsync(_accountId, "INBOX");
        Assert.DoesNotContain("B", stored);
        Assert.Contains("A", stored);
        Assert.Contains("C", stored);
    }

    [Fact]
    public async Task Reconcile_NoOp_WhenServerMatchesStore()
    {
        await _store.UpsertSummariesAsync([Message("A"), Message("B")]);
        var imap = new FetchStubMailService([]);
        imap.Batch.AddRange([Message("A"), Message("B")]); // server view identical
        var sync = Build(imap, new CapturingRuleService());

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        Assert.Equal(0, await sync.ReconcileFolderAsync(Account(), _inbox, CancellationToken.None));
        Assert.Empty(removed);
    }

    [Fact]
    public async Task SyncFolderFull_FetchesNewArrivals_AndReconcilesDeletions()
    {
        // The periodic all-folder sweep (#366) relies on this doing both halves in one call: pull new
        // mail (a server rule filed into a custom folder) AND drop a message deleted elsewhere.
        await _store.UpsertSummariesAsync([Message("ghost")]);          // present locally, gone on server
        var imap = new FetchStubMailService([Message("fresh")]);        // server view = [fresh]
        var sync = Build(imap, new CapturingRuleService());

        var synced = new List<MailMessageSummary>();
        sync.FolderSynced    += list => synced.AddRange(list);
        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        var incoming = await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.Contains(incoming, m => m.MessageId == "fresh");         // new arrival returned
        Assert.Contains(synced,   m => m.MessageId == "fresh");         // and surfaced via FolderSynced
        Assert.Equal("ghost", Assert.Single(removed).MessageId);       // deletion reconciled
        var stored = await _store.GetAllMessageIdsAsync(_accountId, "INBOX");
        Assert.Contains("fresh", stored);
        Assert.DoesNotContain("ghost", stored);
    }

    [Fact]
    public async Task Reconcile_NoOp_WhenNoLocalData()
    {
        // Nothing cached yet → nothing to reconcile, and (importantly) no false deletions even if the
        // server id listing is empty.
        var imap = new FetchStubMailService([]);
        var sync = Build(imap, new CapturingRuleService());

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        Assert.Equal(0, await sync.ReconcileFolderAsync(Account(), _inbox, CancellationToken.None));
        Assert.Empty(removed);
    }

    [Fact]
    public async Task RebuildBaseline_IdleSkipsWithoutConsuming_FullSyncConsumes_ThenRulesResume()
    {
        // #366/N5 + F2: after the one-time cache wipe the IDLE last-50 path skips rules on a wiped
        // account's folder but must NOT consume the baseline — only the full sync consumes. Otherwise a
        // race where IDLE wins would baseline the Inbox on 50 messages and let the full sync's larger
        // remainder re-fire rules over pre-existing mail. Afterward, rules resume on genuinely-new mail.
        var rules = new CapturingRuleService();
        var imap  = new FetchStubMailService([Message("old1"), Message("old2")]);
        var sync  = Build(imap, rules);
        sync.SeedRebuildBaseline(new[] { _accountId });
        var folders = new Dictionary<Guid, List<MailFolderModel>> { [_accountId] = new() { _inbox } };

        // IDLE path: caches, skips rules, does NOT consume the baseline…
        var first = await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);
        Assert.Empty(rules.Calls);
        Assert.Contains(first, m => m.MessageId == "old1");
        Assert.Contains("old1", await _store.GetAllMessageIdsAsync(_accountId, "INBOX"));

        // …so a message arriving during the upgrade window still skips rules via IDLE (unbaselined).
        imap.Batch.Add(Message("during-upgrade"));
        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);
        Assert.Empty(rules.Calls);

        // The full sync consumes the baseline (skipping its whole batch)…
        await sync.SyncAllAccountsAsync(new[] { Account() }, folders, CancellationToken.None);
        Assert.Empty(rules.Calls);

        // …after which rules resume on a genuinely-new message.
        imap.Batch.Add(Message("genuinely-new"));
        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);
        var batch = Assert.Single(rules.Calls);
        Assert.Equal("genuinely-new", Assert.Single(batch).MessageId);
    }

    [Fact]
    public async Task RebuildBaseline_OnlyAffectsSeededAccounts()
    {
        // An account NOT seeded for rebuild runs rules on its first sync as usual — the baseline is not
        // a blanket first-sync skip.
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([Message("100")]), rules);
        sync.SeedRebuildBaseline(new[] { Guid.NewGuid() }); // a different account

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);            // rules ran (this account wasn't baselined)
        Assert.Equal("100", Assert.Single(batch).MessageId);
    }
}
