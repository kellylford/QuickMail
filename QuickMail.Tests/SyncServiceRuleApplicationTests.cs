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
/// and never on a message rules have already run on — recorded in the store (#712), or this session in online mode.
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
        Date = DateTimeOffset.UtcNow,   // recent → inside the sweep's SyncDays window
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

        /// <summary>Set to make <see cref="LoadRules"/> throw, as it does for a rules file that can't be read (#700).</summary>
        public Exception? ThrowOnLoad { get; set; }

        /// <summary>Set to report no rules at all, as a profile does before any rule is written.</summary>
        public bool NoRules { get; set; }

        public List<MailRule> LoadRules() => ThrowOnLoad is { } ex ? throw ex : NoRules ? [] : OneEnabledRule;
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

        // POP3's fetches persist every download into the store BEFORE returning (the store is the
        // only copy). Setting this reproduces that shape: those downloads must still go through rules.
        public Func<List<MailMessageSummary>, Task>? PersistOnFetch { get; set; }

        // The only method the IDLE paths call to fetch.
        public async Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid a, string f, string sinceId, int count, CancellationToken ct = default)
        {
            var batch = Batch.Select(Clone).ToList();
            if (PersistOnFetch is not null) await PersistOnFetch(batch);
            return batch;
        }

        private static MailMessageSummary Clone(MailMessageSummary m) => new()
        {
            MessageId = m.MessageId, AccountId = m.AccountId, FolderName = m.FolderName,
            From = m.From, To = m.To, Subject = m.Subject, IsRead = m.IsRead, Preview = m.Preview,
            Date = m.Date,   // a fetched message carries its date — Microsoft 365 arrival is measured by it
        };

        // ── Everything else: inert ───────────────────────────────────────────────
        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected(Guid accountId) => true;
        public Task<List<MailFolderModel>> GetFoldersAsync(Guid a, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid a, string f, int max, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        // The full-sync path uses the since-date fetch on a fresh store; return the same batch.
        // LastSinceDate stays null until GetMessagesSinceDateAsync is actually called, so a test can
        // assert the id-diff sweep skips the window fetch entirely when nothing is new, and pulls the
        // whole SyncDays window when there IS a new server id (#462).
        public DateTime? LastSinceDate { get; private set; }
        public async Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid a, string f, DateTime since, CancellationToken ct = default)
        {
            LastSinceDate = since;
            var batch = Batch.Select(Clone).ToList();
            if (PersistOnFetch is not null) await PersistOnFetch(batch);
            return batch;
        }
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
        // The server's id listing. Defaults to the batch ids (so the full sync's remote-deletion pass
        // doesn't treat the just-synced messages as deleted), but a test can set ServerIds to present a
        // listing that differs from what a window fetch would return — e.g. a new id the cache lacks, or
        // a cached id the server has dropped.
        public List<string>? ServerIds { get; set; }

        // Cleared to stand in for a POP3 account, whose server drops a message from its listing as
        // soon as it is collected — after which the local store is the only copy.
        public bool DeletionAuthority { get; set; } = true;
        public bool ListingIsAuthoritativeForDeletions(Guid accountId) => DeletionAuthority;

        public Task<IList<string>> GetFolderMessageIdsAsync(Guid a, string f, CancellationToken ct = default)
            => Task.FromResult<IList<string>>(ServerIds ?? ServerIdDates?.Select(x => x.Id).ToList()
                                              ?? Batch.Select(m => m.MessageId).ToList());

        // The server's id+date+read listing that the #462 sweep diffs against. A test sets ServerIdDates
        // to control exactly which ids the server reports, at what received date, and with what read state
        // (so it can present a within-window new id, an out-of-window old id, a read-state change, etc.).
        // Defaults to the batch ids with their dates and read state.
        public List<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>? ServerIdDates { get; set; }
        public int IdDatesListings { get; private set; }   // how many times the sweep listed server ids+dates
        public Exception? ThrowOnListing { get; set; }      // the server can't be asked
        public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid a, string f, CancellationToken ct = default)
        {
            IdDatesListings++;
            if (ThrowOnListing is not null) throw ThrowOnListing;
            return Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>(
                ServerIdDates ?? Batch.Select(m => (m.MessageId, m.Date, m.IsRead)).ToList());
        }
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

    /// <summary>
    /// Caches mail as a previous sync leaves it: stored, with client rules already run on it. Writing straight to the
    /// store leaves a message waiting for rules (#712) — what mail cached by a path that runs no rules looks like, not
    /// steady-state mail — so a test seeding mail a sync has already handled goes through here.
    /// </summary>
    private async Task CacheAsProcessedAsync(List<MailMessageSummary> messages)
    {
        await _store.UpsertSummariesAsync(messages);
        foreach (var group in messages.GroupBy(m => (m.AccountId, m.FolderName)))
            await _store.MarkRulesAppliedAsync(group.Key.AccountId, group.Key.FolderName, group.Select(m => m.MessageId));
    }

    // ── The verifications ────────────────────────────────────────────────────────

    // ── POP3-shaped fetches (#128): the backend persists inside the fetch ─────────
    // A backend that stores its downloads before returning (POP3 — the store is its only copy) once made every arrival
    // read as already-known at the chokepoint, and client rules silently never ran on that account. Cached mail now waits
    // for rules whichever path cached it (#712); these pin that POP3's own downloads still go through them.

    [Fact]
    public async Task Pop3ShapedSweep_PersistsInsideTheFetch_RulesStillRunOnTheArrival()
    {
        var known = Message("p0");
        await CacheAsProcessedAsync([known]);   // steady state: the folder has cached mail

        var arrival = Message("p1", read: false);
        var imap = new FetchStubMailService([known, arrival])
        {
            PersistOnFetch = batch => _store.UpsertSummariesAsync(batch),
            ServerIdDates  = [(known.MessageId, known.Date, known.IsRead), (arrival.MessageId, arrival.Date, false)],
        };
        var rules = new CapturingRuleService();

        await Build(imap, rules).SyncFolderFullAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        // Only the arrival reaches the engine — not the cached message the window re-returned.
        var call = Assert.Single(rules.Calls);
        Assert.Equal([arrival.MessageId], call.Select(m => m.MessageId));
    }

    [Fact]
    public async Task ASharedMailbox_RunsNoClientRules()   // #678
    {
        // The same arrival that reaches the engine for a normal account (the test below) must not reach
        // it for a shared mailbox: its rules belong in Outlook, and a client rule there would act from
        // one person's machine on mail everyone reads.
        var arrival = Message("s1", read: false);
        var imap = new FetchStubMailService([arrival])
        {
            PersistOnFetch = batch => _store.UpsertSummariesAsync(batch),
        };
        var rules = new CapturingRuleService();
        var shared = Account();
        shared.IsShared = true;

        await Build(imap, rules).SyncFolderFullAsync(shared, _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);
    }

    [Fact]
    public async Task Pop3ShapedSweep_FirstSyncOfAnEmptyFolder_TreatsTheWholeFetchAsArrivals()
    {
        var arrival = Message("p1", read: false);
        var imap = new FetchStubMailService([arrival])
        {
            PersistOnFetch = batch => _store.UpsertSummariesAsync(batch),
        };
        var rules = new CapturingRuleService();

        await Build(imap, rules).SyncFolderFullAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        var call = Assert.Single(rules.Calls);
        Assert.Equal([arrival.MessageId], call.Select(m => m.MessageId));
    }

    [Fact]
    public async Task Pop3ShapedIdleSync_RunsRulesOnItsFreshDownloads()
    {
        // The targeted-sync path (delete/archive reconciliation): POP3's incremental fetch returns
        // ONLY what it just downloaded and stored, so the whole batch is new by construction.
        var arrival = Message("p1", read: false);
        var imap = new FetchStubMailService([arrival])
        {
            PersistOnFetch = batch => _store.UpsertSummariesAsync(batch),
        };
        var rules = new CapturingRuleService();
        var account = Account();
        account.BackendKind = BackendKind.Pop3Smtp;

        await Build(imap, rules).SyncOneFolderAsync(account, _inbox, CancellationToken.None);

        var call = Assert.Single(rules.Calls);
        Assert.Equal([arrival.MessageId], call.Select(m => m.MessageId));
    }

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
    public async Task LiveIdleSync_WhenTheRulesFileCantBeRead_StillStoresAndShowsTheArrival() // #700
    {
        // LoadRules now throws for an unreadable rules file rather than reading as empty. The sync must carry on
        // as it did with no rules: the message is stored and shown, and no rule runs.
        var msg = Message("200");
        var rules = new CapturingRuleService { ThrowOnLoad = RulesFileUnreadableException.For("rules.json", new IOException("locked")) };
        var sync = Build(new FetchStubMailService([msg]), rules);

        var forwarded = await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal("200", Assert.Single(forwarded).MessageId);
        Assert.Empty(rules.Calls);
        Assert.Contains("200", await _store.GetAllMessageIdsAsync(_accountId, "INBOX"));
    }

    [Fact]
    public async Task LiveIdleSync_DoesNotReapplyRulesToAnAlreadyStoredMessage()
    {
        // Message already cached (e.g. a prior sync saw it). Rules must not fire again — otherwise a
        // move/delete rule would re-execute every poll.
        await CacheAsProcessedAsync([Message("200")]);
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([Message("200")]), rules);

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);   // known message → skipped entirely
    }

    [Fact]
    public async Task LiveIdleSync_NewAndKnownMixed_OnlyNewGoesToRules()
    {
        await CacheAsProcessedAsync([Message("300")]);          // known
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
        await CacheAsProcessedAsync([Message("A"), Message("B"), Message("C")]);
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
        await CacheAsProcessedAsync([Message("A"), Message("B")]);
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
        await CacheAsProcessedAsync([Message("ghost")]);          // present locally, gone on server
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
    public async Task Reconcile_InProbeMode_NeverDeletes_EvenWhenServerListsNothing()
    {
        // Probe-mode guard (#366 review): the --ui-probe mail stub lists zero server messages while the
        // store holds seeded fixtures. Reconcile must NOT read that empty listing as "all deleted" and
        // purge them — that would blank every visual-QA capture.
        await CacheAsProcessedAsync([Message("A"), Message("B")]);
        var imap = new FetchStubMailService([]);            // server lists nothing
        var sync = new SyncService(imap, _store, new StubConfigService(), new CapturingRuleService(), probeMode: true);

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        Assert.Equal(0, await sync.ReconcileFolderAsync(Account(), _inbox, CancellationToken.None));
        Assert.Empty(removed);
        var stored = await _store.GetAllMessageIdsAsync(_accountId, "INBOX");
        Assert.Contains("A", stored);
        Assert.Contains("B", stored);
    }

    [Fact]
    public async Task Reconcile_NeverDeletes_WhenTheBackendsListingIsNotAuthoritative()
    {
        // POP3 (#128). Its server stops listing a message the moment it is collected, and by then the
        // local store holds the only copy in existence — so "missing from the listing" must never
        // mean "delete it". Pop3MailService also unions its cached ids into the listing so the
        // arithmetic comes out empty either way, but that is the backend defending itself against a
        // caller that cannot see the invariant. This is the sweep honouring it directly: even handed
        // a listing that genuinely omits cached mail, it removes nothing.
        await CacheAsProcessedAsync([Message("A"), Message("B")]);
        var imap = new FetchStubMailService([]) { ServerIds = [], DeletionAuthority = false };
        var sync = Build(imap, new CapturingRuleService());

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        Assert.Equal(0, await sync.ReconcileFolderAsync(Account(), _inbox, CancellationToken.None));
        Assert.Empty(removed);
        var stored = await _store.GetAllMessageIdsAsync(_accountId, "INBOX");
        Assert.Contains("A", stored);
        Assert.Contains("B", stored);
    }

    [Fact]
    public async Task Reconcile_StillDeletes_WhenTheBackendsListingIsAuthoritative()
    {
        // The other half of the gate: an ordinary IMAP/Graph account must keep losing cached mail
        // that was deleted elsewhere. A capability that turns the reconcile off everywhere would
        // pass the test above and break every account that is not POP3.
        await CacheAsProcessedAsync([Message("A"), Message("B")]);
        var imap = new FetchStubMailService([]) { ServerIds = ["A"] };   // B deleted on the server
        var sync = Build(imap, new CapturingRuleService());

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        Assert.Equal(1, await sync.ReconcileFolderAsync(Account(), _inbox, CancellationToken.None));
        Assert.Equal(["B"], removed.Select(m => m.MessageId));
    }

    [Fact]
    public async Task SyncFolderFull_OnNonInboxFolder_DoesNotRunClientRules()
    {
        // #427 seam: the all-folder sweep pulls new mail into custom folders, but client rules stay
        // Inbox-only — a rule must not fire on mail the sweep brought into a non-Inbox folder.
        var rules  = new CapturingRuleService();
        var custom = new MailFolderModel { FullName = "Archive", DisplayName = "Archive" };
        var msg    = new MailMessageSummary
        {
            MessageId = "x", AccountId = _accountId, FolderName = "Archive",
            From = "a@b.com", To = "me@b.com", Subject = "hi",
        };
        var sync = Build(new FetchStubMailService([msg]), rules);

        await sync.SyncFolderFullAsync(Account(), custom, CancellationToken.None);

        Assert.Empty(rules.Calls);   // rules never ran on the non-Inbox folder
    }

    [Fact]
    public async Task SyncFolderFull_GraphCachedFolder_NoNewServerIds_SkipsFetchEntirely()
    {
        // #462: a Graph folder's numeric high-water mark is always "0" (ids aren't numeric → CAST to 0),
        // so without the fix the sweep re-fetched the whole SyncDays window every cycle even when nothing
        // changed. The fix lists the server ids+dates and, finding no within-window id the cache lacks,
        // must not fetch messages at all — that is the whole point of #462.
        await CacheAsProcessedAsync([new MailMessageSummary
        {
            MessageId = "AAMkGraphNonNumericId==",   // non-numeric → GetMaxMessageKeyAsync returns "0"
            AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "cached", Date = DateTimeOffset.UtcNow.AddHours(-2),
        }]);

        // Server lists exactly what we already hold — nothing new.
        var imap = new FetchStubMailService([])
        {
            ServerIdDates = [("AAMkGraphNonNumericId==", DateTimeOffset.UtcNow.AddHours(-2), false)],
        };
        var sync = Build(imap, new CapturingRuleService());

        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.Null(imap.LastSinceDate);   // no window fetch happened
    }

    [Fact]
    public async Task SyncFolderFull_FreshEmptyCache_FetchesInitialWindow_WithoutListingIds()
    {
        // First sync of a folder (empty cache): fetch the initial window directly and skip the id listing
        // entirely — there is nothing to diff or reconcile against, so the listing would be a wasted
        // round-trip.
        var imap = new FetchStubMailService([Message("first")]);
        var sync = Build(imap, new CapturingRuleService());

        var incoming = await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.NotNull(imap.LastSinceDate);                       // it fetched the initial window …
        Assert.Equal(0, imap.IdDatesListings);                    // … without listing server ids+dates
        Assert.Contains(incoming, m => m.MessageId == "first");
    }

    [Fact]
    public async Task SyncFolderFull_GraphFolder_OldServerMailBeyondWindow_DoesNotForceRefetch()
    {
        // The crucial robustness property (this is where a plain full-listing id-diff failed): the local
        // cache only holds mail inside the SyncDays window, but the server lists mail of every age. Old
        // mail the cache never captured (older than the window) must NOT read as "new" and re-trigger a
        // full-window fetch every cycle — the exact thrash #462 is about.
        await CacheAsProcessedAsync([new MailMessageSummary
        {
            MessageId = "AAMkRecentCached==", AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "recent", Date = DateTimeOffset.UtcNow.AddHours(-2),
        }]);

        // Server lists the recent cached message PLUS an ancient one (60 days old, well outside the
        // 30-day window) that the cache legitimately never held.
        var imap = new FetchStubMailService([])
        {
            ServerIdDates =
            [
                ("AAMkRecentCached==", DateTimeOffset.UtcNow.AddHours(-2), false),
                ("AAMkAncientNeverCached==", DateTimeOffset.UtcNow.AddDays(-60), false),
            ],
        };
        var sync = Build(imap, new CapturingRuleService());

        var removed = new List<MailMessageSummary>();
        sync.MessagesRemoved += list => removed.AddRange(list);

        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.Null(imap.LastSinceDate);   // out-of-window old mail did NOT force a fetch
        Assert.Empty(removed);             // and the ancient server id is not mistaken for a deletion
    }

    [Fact]
    public async Task SyncFolderFull_GraphCachedFolder_NewWithinWindowId_FetchesWholeWindow_NotNewestCachedForward()
    {
        // The review's HIGH finding: when the sweep DOES see a new within-window server id, it must fetch
        // the whole SyncDays window — NOT "newest-cached-date forward". A narrower newest-forward fetch
        // would silently miss mail filed into the folder with an OLDER receivedDateTime than what we
        // already hold (a rule batch-filing older mail, or old mail moved in from another client, still
        // inside the window), which the pre-#462 full-window fetch caught and reconcile never adds back.
        var newest = DateTimeOffset.UtcNow.AddHours(-2);
        await CacheAsProcessedAsync([new MailMessageSummary
        {
            MessageId = "AAMkCached==", AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "cached", Date = newest,
        }]);

        // Server lists a SECOND id the cache lacks, dated a day BEFORE the newest cached but still inside
        // the window → the sweep must fetch, and the window fetch returns that older-dated arrival.
        var olderArrival = new MailMessageSummary
        {
            MessageId = "AAMkNewButOlder==", AccountId = _accountId, FolderName = "INBOX",
            From = "c@d.com", To = "me@b.com", Subject = "older, late-filed",
            Date = newest.AddDays(-1),
        };
        var imap = new FetchStubMailService([olderArrival])
        {
            ServerIdDates = [("AAMkCached==", newest, false), ("AAMkNewButOlder==", newest.AddDays(-1), false)],
        };
        var sync = Build(imap, new CapturingRuleService());

        var synced = new List<MailMessageSummary>();
        sync.FolderSynced += list => synced.AddRange(list);

        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        // It fetched, and fetched the WHOLE window (~30-day SyncDays), not narrowed to the 2h-ago newest.
        Assert.NotNull(imap.LastSinceDate);
        Assert.True(imap.LastSinceDate!.Value <= DateTime.UtcNow.AddDays(-25),
            $"expected the full SyncDays window (~30d), got {imap.LastSinceDate:o}");
        // And the older-dated arrival was actually surfaced + cached — not dropped.
        Assert.Contains(synced, m => m.MessageId == "AAMkNewButOlder==");
        Assert.Contains("AAMkNewButOlder==", await _store.GetAllMessageIdsAsync(_accountId, "INBOX"));
    }

    [Fact]
    public async Task SyncFolderFull_ReadStateChangedOnServer_ReconcilesLocally_WithoutFetch_NotAsNewMail()
    {
        // #462 review finding A: removing the re-fetch also removed the read-state refresh it did as a
        // side effect. The cache holds a message marked UNREAD; the server now reports it READ (same id,
        // within window, nothing new). The sweep must update the cached read state and raise
        // FolderReadStatesReconciled — WITHOUT a message fetch, and WITHOUT FolderSynced (which would risk
        // a new-mail toast for a mere read change).
        await CacheAsProcessedAsync([new MailMessageSummary
        {
            MessageId = "AAMkReadElsewhere==", AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "cached", IsRead = false,
            Date = DateTimeOffset.UtcNow.AddHours(-2),
        }]);

        var imap = new FetchStubMailService([])
        {
            ServerIdDates = [("AAMkReadElsewhere==", DateTimeOffset.UtcNow.AddHours(-2), true)],  // server: read
        };
        var sync = Build(imap, new CapturingRuleService());

        var reconciled = new List<MailMessageSummary>();
        sync.FolderReadStatesReconciled += list => reconciled.AddRange(list);
        var synced = new List<MailMessageSummary>();
        sync.FolderSynced += list => synced.AddRange(list);

        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.Null(imap.LastSinceDate);                          // no message fetch
        Assert.Empty(synced);                                     // not surfaced as new mail
        var m = Assert.Single(reconciled);
        Assert.Equal("AAMkReadElsewhere==", m.MessageId);
        Assert.True(m.IsRead);                                    // reconciled to read
        var states = await _store.LoadFolderReadStatesAsync(_accountId, "INBOX");
        Assert.True(states["AAMkReadElsewhere=="]);               // cache row updated
    }

    [Fact]
    public async Task SyncFolderFull_ReadStateUnchanged_RaisesNoReadReconcile()
    {
        // Server read state matches the cache → nothing to do, no event, no fetch.
        await CacheAsProcessedAsync([new MailMessageSummary
        {
            MessageId = "AAMkStable==", AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "cached", IsRead = true,
            Date = DateTimeOffset.UtcNow.AddHours(-2),
        }]);
        var imap = new FetchStubMailService([])
        {
            ServerIdDates = [("AAMkStable==", DateTimeOffset.UtcNow.AddHours(-2), true)],
        };
        var sync = Build(imap, new CapturingRuleService());

        var reconciled = new List<MailMessageSummary>();
        sync.FolderReadStatesReconciled += list => reconciled.AddRange(list);

        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.Empty(reconciled);
        Assert.Null(imap.LastSinceDate);
    }

    [Fact]
    public async Task SyncFolderFull_ReadChangeOnAMessageAlsoRefetched_IsNotDoubleReconciled()
    {
        // When a within-window NEW id forces a fetch, the fetched batch carries current read state and
        // flows through FolderSynced. A read change on a message that is also in that fetched batch must
        // NOT additionally raise FolderReadStatesReconciled — the fetch already covers it.
        await CacheAsProcessedAsync([new MailMessageSummary
        {
            MessageId = "AAMkRefetched==", AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "cached", IsRead = false,
            Date = DateTimeOffset.UtcNow.AddHours(-2),
        }]);

        // The window fetch returns the (now-read) cached message plus a genuinely new one.
        var refetched = new MailMessageSummary
        {
            MessageId = "AAMkRefetched==", AccountId = _accountId, FolderName = "INBOX",
            From = "a@b.com", To = "me@b.com", Subject = "cached", IsRead = true,
            Date = DateTimeOffset.UtcNow.AddHours(-2),
        };
        var brandNew = new MailMessageSummary
        {
            MessageId = "AAMkBrandNew==", AccountId = _accountId, FolderName = "INBOX",
            From = "c@d.com", To = "me@b.com", Subject = "new", IsRead = false,
            Date = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        var imap = new FetchStubMailService([refetched, brandNew])
        {
            ServerIdDates =
            [
                ("AAMkRefetched==", DateTimeOffset.UtcNow.AddHours(-2), true),
                ("AAMkBrandNew==",  DateTimeOffset.UtcNow.AddMinutes(-5), false),
            ],
        };
        var sync = Build(imap, new CapturingRuleService());

        var reconciled = new List<MailMessageSummary>();
        sync.FolderReadStatesReconciled += list => reconciled.AddRange(list);

        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        Assert.NotNull(imap.LastSinceDate);                                   // the new id forced a fetch
        Assert.DoesNotContain(reconciled, m => m.MessageId == "AAMkRefetched=="); // not double-handled
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

    // ── #712: mail another path cached before the rules step saw it ─────────────────────────────────────────────

    /// <summary>An account of the given kind: what counts as arriving mail depends on it.</summary>
    private AccountModel Account(BackendKind kind)
    {
        var account = Account();
        account.BackendKind = kind;
        return account;
    }

    /// <summary>A message with the given date — for Microsoft 365, the time the server received it.</summary>
    private MailMessageSummary MessageAt(string id, DateTimeOffset when, bool read = true)
    {
        var m = Message(id, read);
        m.Date = when;
        return m;
    }

    [Fact]
    public async Task IdleSync_RunsRulesOnMailAnotherPathCachedFirst() // #712
    {
        // The reported case. Opening the Inbox fetched the new message from the server and cached it seven seconds
        // before the new-mail sync arrived; the sync found it already in the store, took it for mail it had handled,
        // and no rule ever ran on it.
        await CacheAsProcessedAsync([Message("100")]);                      // the Inbox as the last sync left it
        await _store.UpsertSummariesAsync([Message("101", read: false)]);   // the new message, cached by opening the Inbox
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([Message("100"), Message("101", read: false)]), rules);

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);
        Assert.Equal("101", Assert.Single(batch).MessageId);
    }

    [Fact]
    public async Task SweepThatFindsNothingNewOnTheServer_StillRunsRulesOnWaitingMail() // #712
    {
        // Once another path has cached a message, the server holds nothing the cache lacks, so the sweep fetches nothing.
        // The waiting message must still be settled, not left until some unrelated mail happens to arrive.
        var settled = DateTimeOffset.UtcNow.AddHours(-1);
        var arrived = DateTimeOffset.UtcNow.AddMinutes(-1);
        await CacheAsProcessedAsync([MessageAt("AAMkSettled==", settled)]);
        await _store.UpsertSummariesAsync([MessageAt("AAMkCachedByTheView==", arrived)]);
        var imap = new FetchStubMailService([])
        {
            ServerIdDates = [("AAMkSettled==", settled, true), ("AAMkCachedByTheView==", arrived, true)],
        };
        var rules = new CapturingRuleService();

        await Build(imap, rules).SyncFolderFullAsync(Account(BackendKind.MicrosoftGraph), _inbox, CancellationToken.None);

        Assert.Null(imap.LastSinceDate);                    // nothing was fetched…
        var batch = Assert.Single(rules.Calls);             // …and the rules still ran on the waiting message
        Assert.Equal("AAMkCachedByTheView==", Assert.Single(batch).MessageId);
    }

    [Fact]
    public async Task ImapFullSync_WithNothingNew_StillRunsRulesOnWaitingMail() // #712
    {
        // The IMAP full sync fetches only what is newer than the highest cached id, so once another path has cached the
        // newest message the fetch comes back empty. The waiting message must still be settled.
        await CacheAsProcessedAsync([Message("40")]);
        await _store.UpsertSummariesAsync([Message("41")]);   // cached by a view
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules).SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);

        var batch = Assert.Single(rules.Calls);
        Assert.Equal("41", Assert.Single(batch).MessageId);
    }

    [Fact]
    public async Task ApplyPendingRules_RunsTheRules_AndRaisesTheMove() // #712
    {
        // What opening a folder asks for straight after caching: the message is on screen, and a rule that moves it takes
        // it off again.
        await CacheAsProcessedAsync([Message("100")]);
        await _store.UpsertSummariesAsync([Message("101", read: false)]);
        var rules = new CapturingRuleService();
        rules.RemoveIds.Add("101");
        var sync = Build(new FetchStubMailService([]), rules);
        var removed = new List<MailMessageSummary>();
        var matched = 0;
        sync.MessagesRemoved += list => removed.AddRange(list);
        sync.RulesApplied += n => matched += n;

        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal("101", Assert.Single(removed).MessageId);
        Assert.Equal(1, matched);
        Assert.DoesNotContain("101", await _store.GetAllMessageIdsAsync(_accountId, "INBOX"));
    }

    [Fact]
    public async Task ApplyPendingRules_ARuleChangingMailAlreadyOnScreen_IsRaisedAsAReadChange() // #712
    {
        // The message is already in the list, cached by the view, so no fetched batch carries the rule's effect back.
        await CacheAsProcessedAsync([Message("100")]);
        await _store.UpsertSummariesAsync([Message("101", read: true)]);
        var sync = Build(new FetchStubMailService([]), new CapturingRuleService());   // it marks survivors unread
        var readChanges = new List<MailMessageSummary>();
        sync.FolderReadStatesReconciled += list => readChanges.AddRange(list);

        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        var changed = Assert.Single(readChanges);
        Assert.Equal("101", changed.MessageId);
        Assert.False(changed.IsRead);
    }

    [Fact]
    public async Task WaitingMail_GoesThroughTheRulesOnce_NotOnEveryPass() // #712
    {
        await CacheAsProcessedAsync([Message("100")]);
        await _store.UpsertSummariesAsync([Message("101")]);
        var rules = new CapturingRuleService();
        var sync = Build(new FetchStubMailService([]), rules);

        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);
        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Single(rules.Calls);
    }

    [Fact]
    public async Task OlderMailAViewCaches_WhenTheWindowWidens_IsNotRunThroughRules_Imap() // #712 review
    {
        // The sync range went from 30 days to a year, so opening the Inbox fetched a year of mail — most of it new to the
        // cache, and older than anything the rules have seen. Only the message that has just arrived goes through rules.
        await CacheAsProcessedAsync([Message("500"), Message("501")]);
        await _store.UpsertSummariesAsync([Message("12"), Message("340"), Message("502", read: false)]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules).ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["502"], Assert.Single(rules.Calls).Select(m => m.MessageId));
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX"));   // the older mail is recorded done
    }

    [Fact]
    public async Task OlderMailAViewCaches_WhenTheWindowWidens_IsNotRunThroughRules_Graph() // #712 review
    {
        var now = DateTimeOffset.UtcNow;
        await CacheAsProcessedAsync([MessageAt("AAMkRecent==", now.AddDays(-2))]);
        await _store.UpsertSummariesAsync([MessageAt("AAMkLastYear==", now.AddDays(-300)), MessageAt("AAMkJustIn==", now)]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.MicrosoftGraph), _inbox, CancellationToken.None);

        Assert.Equal(["AAMkJustIn=="], Assert.Single(rules.Calls).Select(m => m.MessageId));
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX"));
    }

    [Fact]
    public async Task ImapMailDeliveredLate_WithAnOldDateHeader_StillCountsAsArriving() // #712 review
    {
        // IMAP arrival is told by UID, not date: the Date header is when the sender sent it, and late delivery is common.
        await CacheAsProcessedAsync([MessageAt("900", DateTimeOffset.UtcNow)]);
        await _store.UpsertSummariesAsync([MessageAt("901", DateTimeOffset.UtcNow.AddDays(-3))]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules).ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["901"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task Pop3_EveryWaitingMessageIsArriving_WhateverItsDate() // #712 review
    {
        // Everything a POP3 account caches is mail it has just downloaded, and a message downloaded today can carry an
        // older Date header — late delivery, resent mail.
        await CacheAsProcessedAsync([MessageAt("UIDL-A", DateTimeOffset.UtcNow)]);
        await _store.UpsertSummariesAsync([MessageAt("UIDL-B", DateTimeOffset.UtcNow.AddDays(-5))]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        Assert.Equal(["UIDL-B"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task AnInboxRulesKeptEmpty_AtUpgrade_StillRunsRulesOnTheFirstMailAViewCaches() // #712 second review
    {
        // Rules had filed everything, so the Inbox was empty when this version first ran and no line was drawn for it. The
        // first message afterwards is cached by opening the Inbox. A line is drawn from the server then, and nothing else is
        // in the Inbox, so the message is arriving.
        await _store.UpsertSummariesAsync([Message("101")]);
        var imap = new FetchStubMailService([]) { ServerIdDates = [("101", DateTimeOffset.UtcNow, false)] };
        var rules = new CapturingRuleService();

        await Build(imap, rules).ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["101"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task AnInboxWithNoLineYet_TakesServerMailThatIsntWaitingAsAlreadyThere() // #712 second review
    {
        // Everything the server holds in the Inbox apart from the mail waiting here was there before it, so the first line
        // sits at the newest of that. Waiting mail past it is arriving; older waiting mail, brought in by a wider window, is not.
        await _store.UpsertSummariesAsync([Message("40"), Message("101")]);
        var imap = new FetchStubMailService([])
        {
            ServerIdDates = [("40", DateTimeOffset.UtcNow.AddDays(-9), true), ("50", DateTimeOffset.UtcNow.AddDays(-1), true), ("101", DateTimeOffset.UtcNow, false)],
        };
        var rules = new CapturingRuleService();

        await Build(imap, rules).ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["101"], Assert.Single(rules.Calls).Select(m => m.MessageId));
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX"));   // the older mail is recorded done
    }

    [Fact]
    public async Task ASyncSettlingOnlyItsOwnFirstBatch_DoesNotAskTheServerForALine() // #712 second review
    {
        // A fresh account's first sync: everything waiting is what that sync fetched, which has always counted as arriving.
        // Listing the server would be a wasted round trip on every folder's first sync (#462).
        var imap = new FetchStubMailService([Message("8")]);
        var rules = new CapturingRuleService();

        await Build(imap, rules).SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["8"], Assert.Single(rules.Calls).Select(m => m.MessageId));
        Assert.Equal(0, imap.IdDatesListings);
    }

    [Fact]
    public async Task MailReachingTheServerWhileTheFirstLineIsDrawn_StillCountsAsArriving() // #712 third review
    {
        // Listing a large Inbox takes a while. Mail that reaches the server after the view fetched is in the listing, and
        // a sync can cache it before the listing comes back. It is newer than everything waiting, so the line stops short
        // of it and the sync's pass runs rules on it.
        await _store.UpsertSummariesAsync([Message("101")]);
        var now = DateTimeOffset.UtcNow;
        var imap = new FetchStubMailService([])
        {
            ServerIdDates = [("50", now.AddDays(-1), true), ("101", now.AddMinutes(-1), false), ("205", now, false)],
        };
        var rules = new CapturingRuleService();
        var sync = Build(imap, rules);

        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);
        await _store.UpsertSummariesAsync([Message("205")]);                  // IDLE caches the new message
        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["101"], rules.Calls[0].Select(m => m.MessageId));
        Assert.Equal(["205"], rules.Calls[1].Select(m => m.MessageId));
    }

    [Fact]
    public async Task WhenTheServerCantBeAskedForAFirstLine_WaitingMailWaitsForTheNextPass() // #712 second review
    {
        // Guessing would either run rules over older mail or skip mail arriving, so leave it all waiting and try again.
        await _store.UpsertSummariesAsync([Message("101")]);
        var imap = new FetchStubMailService([]) { ThrowOnListing = new IOException("offline") };
        var rules = new CapturingRuleService();
        var sync = Build(imap, rules);

        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);
        Assert.Equal(["101"], (await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX")).Select(m => m.MessageId));

        imap.ThrowOnListing = null;                                           // back online
        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);
        Assert.Equal(["101"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task Pop3FirstCollection_ABacklogAViewDownloaded_IsNotRunThroughRules() // #712 second review
    {
        // POP3's first collection brings a mailbox's whole backlog. With nothing collected before it, only the sync's own
        // batch counts, as it always has; what opening the Inbox downloaded first is left alone.
        await _store.AddPop3CollectedUidlsAsync(_accountId, ["UIDL-1", "UIDL-2"]);
        await _store.UpsertSummariesAsync([Message("UIDL-1"), Message("UIDL-2")]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX"));
    }

    [Fact]
    public async Task Pop3DeletingFromTheServer_WithNoLineYet_RunsRulesOnWhatItDownloads() // #712 third review
    {
        // An account that deletes from the server forgets it collected a message once the server stops listing it, so the
        // record holds only what was just downloaded. Mail a rule filed earlier is still cached, and tells a first
        // collection apart.
        await _store.AddPop3CollectedUidlsAsync(_accountId, ["UIDL-NEW"]);
        var filed = Message("UIDL-FILED");
        filed.FolderName = "Receipts";
        await CacheAsProcessedAsync([filed]);
        await _store.UpsertSummariesAsync([Message("UIDL-NEW")]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        Assert.Equal(["UIDL-NEW"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task Pop3FirstCollection_AfterSendingMail_StillLeavesTheBacklogAlone() // #712 fourth review
    {
        // Sent mail is stored and recorded done, but it was written here, not collected: the backlog opening the Inbox
        // downloads is still a first collection.
        var sent = Message("local-sent-1");
        sent.FolderName = "Sent";
        await CacheAsProcessedAsync([sent]);
        await _store.AddPop3CollectedUidlsAsync(_accountId, ["UIDL-1"]);
        await _store.UpsertSummariesAsync([Message("UIDL-1")]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);
    }

    [Fact]
    public async Task Pop3AfterItsFirstCollection_WithNoLineYet_RunsRulesOnWhatItDownloads() // #712 second review
    {
        // An Inbox rules keep empty had no line when this version first ran. The account has collected mail before, so what
        // it downloads now is arriving.
        await _store.AddPop3CollectedUidlsAsync(_accountId, ["UIDL-OLD", "UIDL-NEW"]);
        await _store.UpsertSummariesAsync([Message("UIDL-NEW")]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.Pop3Smtp), _inbox, CancellationToken.None);

        Assert.Equal(["UIDL-NEW"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task AMessageCachedAgainAfterItsRuleMovedIt_IsNotRunThroughRulesTwice() // #712 review
    {
        // The view and the sync fetched the same arrival. The sync's rule moved it and its row was deleted; then the view's
        // write landed and cached it again, waiting. It has gone from the Inbox, so the rules must not run on it again.
        await CacheAsProcessedAsync([Message("100")]);
        var rules = new CapturingRuleService();
        rules.RemoveIds.Add("101");
        var sync = Build(new FetchStubMailService([Message("100"), Message("101")]), rules);

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);   // ruled, moved, row deleted
        await _store.UpsertSummariesAsync([Message("101")]);                          // the view's late write
        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Single(rules.Calls);
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX"));
    }

    [Fact]
    public async Task WithNoRules_WaitingMailIsRecordedDone_SoARuleWrittenLaterLeavesItAlone() // #712
    {
        // A rule acts on mail that arrives after it exists, never on what was already in the Inbox.
        await CacheAsProcessedAsync([Message("100")]);
        await _store.UpsertSummariesAsync([Message("101")]);
        var rules = new CapturingRuleService { NoRules = true };
        var sync = Build(new FetchStubMailService([]), rules);

        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);
        rules.NoRules = false;                                      // now the user writes a rule
        await sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);
    }

    [Fact]
    public async Task WaitingMailOutsideTheInbox_IsRecordedDone_WithoutRules() // #712, #336
    {
        var filed = Message("filed");
        filed.FolderName = "Archive";
        await _store.UpsertSummariesAsync([filed]);
        var rules = new CapturingRuleService();
        var archive = new MailFolderModel { FullName = "Archive", DisplayName = "Archive" };

        await Build(new FetchStubMailService([]), rules).ApplyPendingRulesAsync(Account(), archive, CancellationToken.None);

        Assert.Empty(rules.Calls);
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "Archive"));
    }

    [Fact]
    public async Task WaitingMailInASharedMailbox_IsRecordedDone_WithoutRules() // #712, #678
    {
        await CacheAsProcessedAsync([Message("100")]);
        await _store.UpsertSummariesAsync([Message("101")]);
        var rules = new CapturingRuleService();
        var shared = Account();
        shared.IsShared = true;

        await Build(new FetchStubMailService([]), rules).ApplyPendingRulesAsync(shared, _inbox, CancellationToken.None);

        Assert.Empty(rules.Calls);
        Assert.Empty(await _store.LoadRulesPendingSummariesAsync(_accountId, "INBOX"));
    }

    /// <summary>
    /// Waiting mail held in memory, with a pause in the first rules pass between reading what is waiting and recording it
    /// done — exactly the window two passes could both fall into.
    /// </summary>
    private sealed class PausingWaitingStore : StubLocalStoreService
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, MailMessageSummary> _waiting = [];
        private readonly TaskCompletionSource _secondPassReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public void AddWaiting(MailMessageSummary m) { lock (_lock) _waiting[m.MessageId] = m; }

        // A folder the rules have settled before, up to UID 100, so a higher UID is arriving mail.
        public override Task<(long? MaxNumericId, long? MaxDateTicks)> GetRulesSettledBoundaryAsync(Guid accountId, string folderName)
            => Task.FromResult<(long?, long?)>((100, 0));

        public override async Task<List<MailMessageSummary>> LoadRulesPendingSummariesAsync(Guid accountId, string folderName)
        {
            List<MailMessageSummary> snapshot;
            int read;
            lock (_lock) { snapshot = [.. _waiting.Values]; read = ++_reads; }

            if (read == 1)
                // Give a second pass the chance to read the same waiting mail before this one records it done. When
                // passes on a folder are serialized, the second can't get here while the first holds it, so this times out.
                await Task.WhenAny(_secondPassReading.Task, Task.Delay(500));
            else
                _secondPassReading.TrySetResult();

            return snapshot;
        }

        public override Task MarkRulesAppliedAsync(Guid accountId, string folderName, IEnumerable<string> messageIds)
        {
            lock (_lock) foreach (var id in messageIds) _waiting.Remove(id);
            return Task.CompletedTask;
        }

        public override Task MarkFolderRulesAppliedAsync(Guid accountId, string folderName)
        {
            lock (_lock) _waiting.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TwoPassesAtOnce_RunTheRulesOnWaitingMailOnlyOnce() // #712
    {
        // Opening the Inbox and the new-mail sync can settle the same freshly cached message moments apart. With Copy now
        // a rule action, running the rules twice means two copies.
        var store = new PausingWaitingStore();
        store.AddWaiting(Message("712"));
        var rules = new CapturingRuleService();
        var sync = new SyncService(new FetchStubMailService([]), store, new StubConfigService(), rules);

        await Task.WhenAll(
            Task.Run(() => sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None)),
            Task.Run(() => sync.ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None)));

        Assert.Equal(["712"], rules.Calls.SelectMany(c => c).Select(m => m.MessageId));
    }

    [Fact]
    public async Task AnInboxRulesKeepEmpty_StillRunsRulesOnMailAViewCaches() // #712 review
    {
        // Every message this Inbox has had was filed by a rule, so no settled row is left in it. Mail a view caches next
        // is still arriving mail.
        await CacheAsProcessedAsync([Message("100")]);
        await _store.DeleteSummariesAsync(_accountId, "INBOX", ["100"]);    // the rule moved it out
        await _store.UpsertSummariesAsync([Message("101")]);                // the next arrival, cached by opening the Inbox
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules).ApplyPendingRulesAsync(Account(), _inbox, CancellationToken.None);

        Assert.Equal(["101"], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }

    [Fact]
    public async Task MailSettledWhileThereWereNoRules_StillSetsTheLine_SoALaterRuleLeavesOlderMailAlone() // #712 review
    {
        // Settled with no rules to run, the Inbox still gets its line. Without one, the first sweep after a rule is written
        // would take the folder for never settled and run the rule over everything its window fetch returned. The second
        // sync is a new service over the same store, as after a restart: the line is kept in the store, not in memory.
        var now = DateTimeOffset.UtcNow;
        var recent = MessageAt("AAMkRecent==", now.AddDays(-1));
        var graph = Account(BackendKind.MicrosoftGraph);

        await Build(new FetchStubMailService([recent]), new CapturingRuleService { NoRules = true })
            .SyncFolderFullAsync(graph, _inbox, CancellationToken.None);          // a fresh cache, settled with no rules

        var older = MessageAt("AAMkOlder==", now.AddDays(-20));                   // shown once the sync range widened
        await _store.UpsertSummariesAsync([older]);
        var arrived = MessageAt("AAMkArrived==", now);
        var imap = new FetchStubMailService([recent, older, arrived])
        {
            ServerIdDates = [(recent.MessageId, recent.Date, true), (older.MessageId, older.Date, true), (arrived.MessageId, arrived.Date, true)],
        };
        var rules = new CapturingRuleService();                                    // a rule has been written since

        await Build(imap, rules).SyncFolderFullAsync(graph, _inbox, CancellationToken.None);

        Assert.NotNull(imap.LastSinceDate);                                        // the new id brought the whole window back…
        Assert.Equal(["AAMkArrived=="], Assert.Single(rules.Calls).Select(m => m.MessageId));   // …and only the arrival was ruled
    }

    [Fact]
    public async Task Graph_AMessageScanningKeptBackMinutes_StillCountsAsArriving() // #712 review
    {
        // Exchange stamped it twenty minutes before a message that has already been settled, but scanning held it back, so
        // it showed up afterwards. It is still mail arriving.
        var now = DateTimeOffset.UtcNow;
        await CacheAsProcessedAsync([MessageAt("AAMkStampedLater==", now)]);
        await _store.UpsertSummariesAsync([MessageAt("AAMkHeldBack==", now.AddMinutes(-20))]);
        var rules = new CapturingRuleService();

        await Build(new FetchStubMailService([]), rules)
            .ApplyPendingRulesAsync(Account(BackendKind.MicrosoftGraph), _inbox, CancellationToken.None);

        Assert.Equal(["AAMkHeldBack=="], Assert.Single(rules.Calls).Select(m => m.MessageId));
    }
}
