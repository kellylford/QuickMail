using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class SyncService : ISyncService
{
    private readonly IMailService _imap;
    private readonly ILocalStoreService _store;
    private readonly IConfigService _config;
    private readonly IRuleService _rules;
    private readonly IUiDispatcher _ui;

    // In --ui-probe mode the mail backend is a no-op stub that lists zero server messages, while the
    // store is seeded with fixture mail. Reconcile would read that empty listing as "everything was
    // deleted remotely" and purge the fixtures, emptying every visual-QA capture. Suppress reconcile
    // in probe mode — mirrors the !probeMode guard the one-time cache rebuild uses (#366).
    private readonly bool _probeMode;

    public SyncService(IMailService imap, ILocalStoreService store, IConfigService config, IRuleService rules,
        IUiDispatcher? ui = null, bool probeMode = false, IConnectivityService? connectivity = null)
    {
        _imap   = imap;
        _store  = store;
        _config = config;
        _rules  = rules;
        _probeMode = probeMode;
        _connectivity = connectivity;
        // WpfUiDispatcher marshals only when the real QuickMail App is present, and runs inline
        // otherwise — a plain Application.Current null-check is NOT enough (tests create a pumpless
        // Application, so InvokeAsync would park forever).
        _ui     = ui ?? new WpfUiDispatcher();
    }

    public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
    public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
    public event Action<int>? RulesApplied;
    public event Action<int, int>? SyncProgressChanged;

    private readonly Dictionary<Guid, DateTimeOffset> _lastSyncedUtc = new();

    // Store-less (online) dedupe only. Message keys (account, folder, id) already run through client
    // rules this session, so a re-fetched last-50 batch doesn't re-run a rule. Persisted paths use
    // the local store as the first-sight authority instead (this dictionary would grow unbounded and
    // is redundant there). Seeded as a baseline on the first online fetch per folder — see the
    // chokepoint — so rules never fire retroactively on the initial batch.
    private readonly ConcurrentDictionary<(Guid Account, string Folder, string Id), byte> _rulesApplied = new();

    // (account, folder) pairs whose store-less baseline has been established (first online fetch seen).
    private readonly ConcurrentDictionary<(Guid Account, string Folder), byte> _onlineBaselined = new();

    // Accounts whose cache was wiped by the one-time immutable-id rebuild (#366): the first persisted
    // sync of each of their folders is a baseline (mark-seen, no rules) so pre-existing mail — already
    // processed when it first arrived — isn't re-run through rules on upgrade day. Seeded via
    // SeedRebuildBaseline; _rebuildBaselined tracks which (account, folder) pairs have been baselined.
    private readonly ConcurrentDictionary<Guid, byte> _rebuildAccounts = new();

    // Shared mailboxes whose kept-but-not-run client rules have been logged this session (#678).
    private readonly ConcurrentDictionary<Guid, byte> _sharedRulesSkipLogged = new();
    private readonly ConcurrentDictionary<(Guid Account, string Folder), byte> _rebuildBaselined = new();

    /// <summary>
    /// Marks the given accounts as freshly cache-wiped by the one-time immutable-id rebuild (#366).
    /// The first persisted sync of each of their folders then caches the fetched messages but does NOT
    /// run client rules on them — they are pre-existing mail (rules already ran when it first arrived),
    /// not new arrivals, and the wipe erased the store's "already seen" memory. Rules resume on
    /// genuinely-new mail from the next sync. No-op for accounts not passed here. Call once at startup,
    /// right after the rebuild clears the cache and before any sync runs.
    /// </summary>
    public void SeedRebuildBaseline(IEnumerable<Guid> accountIds)
    {
        foreach (var id in accountIds) _rebuildAccounts.TryAdd(id, 0);
    }

    /// <summary>
    /// Which folders this launch's sync covers, per <see cref="ConfigModel.StartupSyncScope"/>.
    /// Returns a predicate rather than a set so the two-pass loop stays untouched.
    ///
    /// <para><c>startupFolder</c> — the default — syncs exactly what the startup folder shows. That
    /// means a real folder syncs alone, All Inboxes syncs every Inbox, and All Mail syncs
    /// everything, because All Mail spans everything and a narrower sync would put stale rows on
    /// screen. So a user who has not chosen a startup folder still gets today's full sweep: the
    /// saving is opted into by choosing a narrower place to start, not imposed.</para>
    ///
    /// <para>All Archive is approximated by <see cref="SpecialFolderKind.Archive"/>; resolving a
    /// per-account archive override lives in the VM and is not worth reaching for here, since
    /// guessing wide only costs one extra folder. A <c>view:{guid}</c> startup folder syncs
    /// everything for the same reason — this layer has no view service to resolve it.</para>
    /// </summary>
    private static Func<AccountModel, MailFolderModel, bool> BuildStartupScopeFilter(
        ConfigModel cfg, string scope,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders)
    {
        if (scope == ConfigModel.StartupSyncScopeAll)
            return static (_, _) => true;

        if (scope == ConfigModel.StartupSyncScopeInboxes)
            return static (_, f) => f.Kind == SpecialFolderKind.Inbox;

        // startupFolder: mirror whatever the startup folder covers.
        var key = cfg.StartupFolder;
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("view:", StringComparison.Ordinal))
            return static (_, _) => true;                       // All Mail, or a view we cannot resolve

        if (!string.IsNullOrWhiteSpace(cfg.StartupFolderAccount))
        {
            if (!Guid.TryParse(cfg.StartupFolderAccount, out var accountId))
                return static (_, _) => true;                   // unreadable — sync wide rather than nothing
            // One real folder. Its account must still be one we know about; if the folder itself has
            // gone, startup falls back to All Mail, so sync wide rather than sync nothing.
            var known = cachedFolders.TryGetValue(accountId, out var fl) &&
                        fl.Any(f => string.Equals(f.FullName, key, StringComparison.OrdinalIgnoreCase));
            if (!known) return static (_, _) => true;
            return (a, f) => a.Id == accountId &&
                             string.Equals(f.FullName, key, StringComparison.OrdinalIgnoreCase);
        }

        return key switch
        {
            "AllInboxes" => static (_, f) => f.Kind == SpecialFolderKind.Inbox,
            "AllDrafts"  => static (_, f) => f.Kind == SpecialFolderKind.Drafts,
            "AllSent"    => static (_, f) => f.Kind == SpecialFolderKind.Sent,
            "AllTrash"   => static (_, f) => f.Kind == SpecialFolderKind.Trash,
            "AllArchive" => static (_, f) => f.Kind == SpecialFolderKind.Archive,
            _            => static (_, _) => true,              // AllMail, AllFlagged, AllWatched, unknown
        };
    }

    public async Task SyncAllAccountsAsync(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct)
    {
        var previewJobs = new ConcurrentBag<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)>();
        var accountList = accounts.ToList();

        // Startup sync scope (#516). This method has exactly one caller — MainViewModel's startup
        // pass — so it IS the startup sync, and reading the setting here keeps it off the interface
        // and out of five test stubs. InScope decides which folders this launch covers; everything
        // it skips is still picked up by the periodic sweep, which visits every folder, and by the
        // IMAP IDLE / Graph delta watchers, which cover every account's Inbox live. Nothing is
        // skipped permanently, and new-mail notifications are unaffected.
        var startupCfg = _config.Load();
        var scope      = ConfigModel.ParseStartupSyncScope(startupCfg.StartupSyncScope);
        var inScope    = BuildStartupScopeFilter(startupCfg, scope, cachedFolders);

        int totalFolders = accountList.Sum(a =>
            cachedFolders.TryGetValue(a.Id, out var fl)
                ? fl.Count(f => !f.ExcludeFromAllMail && inScope(a, f)) : 0);

        // int[] so Interlocked.Increment works inside async lambdas (can't use ref locals there).
        int[] completedFolders = { 0 };

        // Group accounts by incoming host — the server each backend actually receives from, so a
        // POP3 account groups by its POP3 host, not its (empty) ImapHost. Accounts on the same
        // server sync sequentially within their group to avoid hitting per-IP connection limits
        // (which trigger "Server shutting down" BYEs on shared hosting) — and, for POP3, the
        // RFC 1939 exclusive maildrop lock. Grouping by ImapHost here put every POP3 and Graph
        // account into one "" bucket (serialized against each other, parallel with an IMAP account
        // on the same real host — both wrong). Same rationale as MainViewModel's connect grouping.
        var accountsByHost = accountList
            .GroupBy(a => a.IncomingHost, StringComparer.OrdinalIgnoreCase)
            .ToList();

        async Task SyncPassAsync(Func<MailFolderModel, bool> folderFilter)
        {
            await Task.WhenAll(accountsByHost.Select(async hostGroup =>
            {
                foreach (var account in hostGroup)
                {
                    if (!cachedFolders.TryGetValue(account.Id, out var folders)) continue;
                    foreach (var folder in folders)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (folder.ExcludeFromAllMail || !folderFilter(folder)) continue;
                        if (!inScope(account, folder)) continue;
                        try
                        {
                            var incoming = await SyncFolderAsync(account, folder, ct);
                            var previewLines = _config.Load().GetPreviewLines(account.Id);
                            if (incoming.Count > 0 && previewLines > 0
                                && incoming.Any(s => string.IsNullOrEmpty(s.Preview)))
                                previewJobs.Add((account, folder, incoming));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Log($"Sync {account.AccountLabel}/{folder.DisplayName}", ex);
                        }

                        var count = Interlocked.Increment(ref completedFolders[0]);
                        _ui.Post(() => SyncProgressChanged?.Invoke(count, totalFolders));
                    }
                }
            }));
        }

        // NOOP: one per host group in parallel, sequential within each group.
        await Task.WhenAll(accountsByHost.Select(async hostGroup =>
        {
            foreach (var account in hostGroup)
            {
                try { await _imap.NoOpAsync(account.Id, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { LogService.Log($"NoOp {account.AccountLabel}", ex); }
            }
        }));

        // Pass 1: Inbox folders first, all accounts in parallel — fastest path to new-mail visibility.
        await SyncPassAsync(f => f.Kind == SpecialFolderKind.Inbox);
        ct.ThrowIfCancellationRequested();
        // Pass 2: All remaining non-excluded folders, all accounts in parallel.
        await SyncPassAsync(f => f.Kind != SpecialFolderKind.Inbox);

        foreach (var account in accountList)
            _lastSyncedUtc[account.Id] = DateTimeOffset.UtcNow;

        // Fetch previews only after ALL folder syncs complete so preview IMAP calls
        // don't race with the sync IMAP calls on the same shared client.
        // They run sequentially — fire-and-forget the whole batch so SyncAllAccounts
        // returns promptly and the status bar updates, while previews trickle in.
        // The offline-bodies pass (#637) follows the previews on the same trickle, so it never
        // competes with them or with the sync for background leases.
        RunPostSyncTrickleAsync(previewJobs.ToList(), accountList, cachedFolders, ct)
            .LogFaults("sync: post-sync trickle");
    }

    private async Task RunPostSyncTrickleAsync(
        List<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)> previewJobs,
        List<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        CancellationToken ct)
    {
        if (previewJobs.Count > 0)
            await FetchAllPreviewsAsync(previewJobs, ct);
        await DownloadOfflineBodiesAsync(accounts, cachedFolders, ct);
    }

    private async Task FetchAllPreviewsAsync(
        List<(AccountModel Account, MailFolderModel Folder, List<MailMessageSummary> Incoming)> jobs,
        CancellationToken ct)
    {
        foreach (var (account, folder, incoming) in jobs)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await FetchAndApplyPreviewsAsync(account, folder, incoming, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One folder's preview failure must not kill the rest of the batch.
                LogService.Log($"Preview fetch failed for {account.AccountLabel}/{folder.DisplayName}", ex);
            }
        }
    }

    /// <summary>
    /// The client-side rules, or none when rules.json can't be read (#700). Arriving mail still has to be stored
    /// and shown, so an unreadable rules file must not stop the sync; RuleService has logged why. Mail that arrives
    /// meanwhile is not filed later by itself — Run on Existing Mail is the way to apply the rules to it.
    /// </summary>
    private List<MailRule> RulesOrNone()
    {
        try { return _rules.LoadRules(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Where client mail rules run on mail as it arrives. The sync paths — the initial and periodic full sync, and
    /// the live IDLE/change-notifier syncs — hand their fetched batch here: it is cached, and rules then run on every
    /// Inbox message that is cached, not yet run through them, and arriving — whichever path cached it.
    ///
    /// <para>That last part is #712. This used to decide what was new by asking whether the store already held a
    /// message, believing every path funnelled its batch through here. They do not: opening a folder, All Mail, All
    /// Inboxes and saved views fetch from the server and cache what they get, without running rules. A message one of
    /// those cached first read as already known here, and no rule ever ran on it — with nothing in the log to say so.
    /// So being cached and having had rules run are now separate facts: the store holds each message it caches as
    /// waiting for rules, and <see cref="SettlePendingRulesAsync"/> records them done as it runs them. The view paths
    /// ask for a pass straight after caching, through <see cref="ApplyPendingRulesAsync"/>.</para>
    ///
    /// <para>The store-less online path keeps its in-session guard, and treats its first fetch per folder as a
    /// baseline (marked seen, never run) so rules can't fire retroactively on the reconciliation batch.</para>
    ///
    /// Persists the batch, runs rules, deletes rule-moved/deleted messages from the store, raises
    /// <see cref="RulesApplied"/> / <see cref="MessagesRemoved"/>, and returns the batch with those messages stripped
    /// so the UI never shows them in the origin folder. Callers own <see cref="FolderSynced"/>.
    /// </summary>
    private async Task<List<MailMessageSummary>> ApplyRulesToArrivalsAsync(
        AccountModel account, MailFolderModel folder,
        List<MailMessageSummary> fetched, bool persisted, bool consumeRebuildBaseline, CancellationToken ct)
    {
        if (persisted)
        {
            if (fetched.Count > 0)
                await _store.UpsertSummariesAsync(fetched);

            // Even for an empty batch: mail another path cached is waiting whether or not this fetch found anything,
            // and an empty batch still consumes a pending #366 rebuild baseline (F4).
            var (settledMatched, settledRemoved) =
                await SettlePendingRulesAsync(account, folder, fetched, consumeRebuildBaseline, ct);
            return StripAndRaise(fetched, settledMatched, settledRemoved);
        }

        // ── Store-less (online) ──────────────────────────────────────────────────────────────────────────────
        if (fetched.Count == 0) return fetched;

        // No enabled rules → no guard bookkeeping; a rule-less profile pays nothing.
        if (!RulesOrNone().Any(r => r.IsEnabled)) return fetched;

        if (!IsInbox(folder)) return fetched;

        // The first fetch per folder is the last-50 reconciliation batch, not new mail. Mark it seen WITHOUT running
        // rules, so a move/delete/mark-read rule never rewrites up-to-50 pre-existing messages on a delete or archive
        // reconciliation. Retroactive application has its own user-invoked home in ApplyRulesToExistingAsync.
        if (_onlineBaselined.TryAdd((account.Id, folder.FullName), 0))
        {
            foreach (var m in fetched)
                _rulesApplied.TryAdd((account.Id, folder.FullName, m.MessageId), 0);
            return fetched;
        }

        var newArrivals = fetched
            .Where(m => _rulesApplied.TryAdd((account.Id, folder.FullName, m.MessageId), 0))
            .ToList();
        if (newArrivals.Count == 0) return fetched;

        if (account.IsShared)
        {
            LogSharedMailboxSkipOnce(account);
            return fetched;
        }

        int matchedCount;
        List<MailMessageSummary> removedMessages;
        try
        {
            LogService.Debug($"ApplyRules: {account.AccountLabel}/{folder.FullName} — {newArrivals.Count} new of {fetched.Count} fetched");
            (matchedCount, removedMessages) = await _rules.ApplyRulesAsync(newArrivals, account.Id, ct);
            LogService.Debug($"ApplyRules: done — {matchedCount} matched, {removedMessages.Count} removed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"Applying rules for {account.AccountLabel}/{folder.FullName} failed", ex);
            // Un-mark the guard so the next poll retries instead of skipping these forever.
            foreach (var m in newArrivals)
                _rulesApplied.TryRemove((account.Id, folder.FullName, m.MessageId), out _);
            return fetched;
        }

        return StripAndRaise(fetched, matchedCount, removedMessages);
    }

    /// <inheritdoc />
    public async Task ApplyPendingRulesAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        var (matched, removed) = await SettlePendingRulesAsync(account, folder, [], consumeRebuildBaseline: false, ct);
        RaiseRuleOutcome(matched, removed);
    }

    // One rules pass at a time per folder. Two paths can finish caching the same message moments apart — opening the
    // Inbox and the new-mail sync — and each would otherwise find it waiting and run the rules on it twice: two
    // copies, or a second move of a message that has already gone.
    private readonly ConcurrentDictionary<(Guid Account, string Folder), SemaphoreSlim> _rulePassGates = new();

    // Messages client rules have run on this session. A view's write can land after a rule has already moved a message
    // and its row been deleted, caching it again as waiting; without this, the next pass would run the rules on mail that
    // has gone. Only arrivals go in, so it grows by the mail that arrives while QuickMail is open.
    private readonly ConcurrentDictionary<(Guid Account, string Folder, string Id), byte> _rulesRanThisSession = new();

    /// <summary>
    /// Runs client rules on the folder's cached mail that is waiting for them, and records it done (#712). Only an
    /// Inbox's mail goes through rules (#336); any other folder's waiting mail is simply recorded done, which also
    /// keeps the store's index of waiting mail down to the Inboxes.
    /// <para><paramref name="justFetched"/> is the batch the caller has in hand. Where a waiting message is one of
    /// those, that instance is what the rules act on, so a rule's effect on it (marking it read) reaches the batch
    /// the caller goes on to show. A waiting message the caller did not fetch is already on screen, cached by another
    /// path: its read-state change is raised through <see cref="FolderReadStatesReconciled"/>, and a move or delete
    /// comes back with the rest to be raised as <see cref="MessagesRemoved"/>.</para>
    /// </summary>
    private async Task<(int Matched, List<MailMessageSummary> Removed)> SettlePendingRulesAsync(
        AccountModel account, MailFolderModel folder, IReadOnlyList<MailMessageSummary> justFetched,
        bool consumeRebuildBaseline, CancellationToken ct)
    {
        if (!IsInbox(folder))
        {
            await _store.MarkFolderRulesAppliedAsync(account.Id, folder.FullName);
            return (0, []);
        }

        var gate = _rulePassGates.GetOrAdd((account.Id, folder.FullName), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Persisted rebuild baseline (#366/N5): after the one-time immutable-id cache wipe the store is empty, so
            // everything re-fetched is cached as waiting — pre-existing mail that rules already ran on when it first
            // arrived. While a wiped account's folder is not yet baselined, run no rules on it.
            //
            // F2 (race): the delta poll's IDLE last-50 fetch runs concurrently with the full sync's larger window on
            // upgrade launches, and nothing serializes them. Only the FULL sync consumes the baseline, recording every
            // waiting message done; the IDLE path leaves them waiting. If IDLE could consume, it would baseline the
            // folder on 50 messages, and the full sync's larger remainder, cached afterwards, would go through rules.
            // Once the full sync consumes, rules resume normally on both paths.
            if (_rebuildAccounts.ContainsKey(account.Id)
                && !_rebuildBaselined.ContainsKey((account.Id, folder.FullName)))
            {
                if (consumeRebuildBaseline)
                {
                    await _store.MarkFolderRulesAppliedAsync(account.Id, folder.FullName);
                    _rebuildBaselined.TryAdd((account.Id, folder.FullName), 0);
                }
                return (0, []);
            }

            // Client-side rules do not run on a shared mailbox (#678): its rules belong in Outlook, and a client rule
            // there would move or delete, from one person's machine, mail everyone else reads. Its mail is recorded
            // done rather than left waiting, so no rule acts on it later either.
            if (account.IsShared)
            {
                LogSharedMailboxSkipOnce(account);
                await _store.MarkFolderRulesAppliedAsync(account.Id, folder.FullName);
                return (0, []);
            }

            // No enabled rules — none written yet, or rules.json can't be read (#700). The waiting mail is still
            // recorded done: a rule written later acts on mail that arrives after it, not on what was already here.
            // For an unreadable file that keeps #700's promise as written — mail that arrived meanwhile is left to
            // Run on Existing Mail.
            if (!RulesOrNone().Any(r => r.IsEnabled))
            {
                await _store.MarkFolderRulesAppliedAsync(account.Id, folder.FullName);
                return (0, []);
            }

            var waiting = await _store.LoadRulesPendingSummariesAsync(account.Id, folder.FullName);
            if (waiting.Count == 0) return (0, []);

            var inHand = new Dictionary<string, MailMessageSummary>(StringComparer.Ordinal);
            foreach (var m in justFetched) inHand.TryAdd(m.MessageId, m);

            // Waiting means cached for the first time, which is not the same as arriving: a view that fetches a wider
            // window than the sync has cached — the sync range just widened, or All on IMAP — caches older mail too.
            // Rules act on the arrivals only; the rest is recorded done without them.
            var boundary = await _store.GetRulesSettledBoundaryAsync(account.Id, folder.FullName);
            if (boundary.MaxDateTicks is null && waiting.Any(m => !inHand.ContainsKey(m.MessageId)))
            {
                // No line yet, and mail waits that the caller didn't fetch itself, so the caller's batch can't stand in for
                // one: draw it from the server. If the server can't be asked, leave everything waiting for the next pass.
                if (await DrawFirstLineAsync(account, folder, waiting, ct) is not { } drawn) return (0, []);
                boundary = drawn;
            }
            var batch = new List<MailMessageSummary>();
            var notArriving = new List<string>();
            foreach (var m in waiting)
            {
                if (_rulesRanThisSession.ContainsKey((account.Id, folder.FullName, m.MessageId))
                    || !IsArrival(account, m, boundary, inHand))
                    notArriving.Add(m.MessageId);
                else
                    batch.Add(inHand.TryGetValue(m.MessageId, out var held) ? held : m);
            }
            if (notArriving.Count > 0)
            {
                await _store.MarkRulesAppliedAsync(account.Id, folder.FullName, notArriving);
                LogService.Debug($"ApplyRules: {account.AccountLabel}/{folder.FullName} — {notArriving.Count} cached but not arriving, recorded done without rules");
            }
            if (batch.Count == 0) return (0, []);

            var cachedElsewhere = batch.Where(m => !inHand.ContainsKey(m.MessageId)).ToList();
            var readBefore = cachedElsewhere.ToDictionary(m => m.MessageId, m => m.IsRead, StringComparer.Ordinal);

            // Recorded done BEFORE the rules act, as caching the batch used to be: a message whose rules fail part way
            // is not retried by the next pass, where running a copy or a move again could act on it twice.
            await _store.MarkRulesAppliedAsync(account.Id, folder.FullName, batch.Select(m => m.MessageId));
            foreach (var m in batch)
                _rulesRanThisSession.TryAdd((account.Id, folder.FullName, m.MessageId), 0);

            int matched;
            List<MailMessageSummary> removed;
            try
            {
                LogService.Debug($"ApplyRules: {account.AccountLabel}/{folder.FullName} — {batch.Count} waiting ({batch.Count - cachedElsewhere.Count} from this fetch, {cachedElsewhere.Count} cached by another path)");
                (matched, removed) = await _rules.ApplyRulesAsync(batch, account.Id, ct);
                LogService.Debug($"ApplyRules: done — {matched} matched, {removed.Count} removed");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Applying rules for {account.AccountLabel}/{folder.FullName} failed", ex);
                return (0, []);
            }

            // Delete rule-moved/deleted messages from the store so they don't reappear on cache load.
            foreach (var group in removed.GroupBy(m => (m.AccountId, m.FolderName)))
            {
                try
                {
                    await _store.DeleteSummariesAsync(
                        group.Key.AccountId, group.Key.FolderName, group.Select(m => m.MessageId));
                }
                catch (Exception ex)
                {
                    LogService.Log($"Rule cleanup: failed to delete {group.Count()} summaries from {group.Key.FolderName}", ex);
                }
            }

            var removedIds = removed.Select(m => m.MessageId).ToHashSet(StringComparer.Ordinal);
            var readChanged = cachedElsewhere
                .Where(m => !removedIds.Contains(m.MessageId) && m.IsRead != readBefore[m.MessageId])
                .ToList();
            if (readChanged.Count > 0)
                _ui.Post(() => FolderReadStatesReconciled?.Invoke(readChanged));

            return (matched, removed);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Draws an Inbox's first arrival line when rules settle it with mail waiting that the caller didn't fetch itself — a
    /// view cached it — and it has no line yet: a new account, or an Inbox empty when this version first ran or after its
    /// cache was wiped (#712 second review). Everything the server holds in the Inbox apart from the mail waiting here was
    /// there before it, so the line sits at the newest of that. Null when the server can't be asked: guessing would either
    /// run rules over older mail or skip mail arriving.
    /// <para>Nothing newer than the newest waiting message counts, though. A folder with no line has nothing in the store
    /// recorded done — recording mail done draws the line — so server mail that isn't waiting isn't cached at all, and the
    /// view that cached the waiting mail fetched the newest. Mail past that reached the server after the view fetched, and
    /// may be cached by a sync while the listing is still coming back: it is arriving, not already there.</para>
    /// <para>POP3 has no server listing, and its first collection brings a mailbox's whole backlog. What the account has already
    /// collected tells the two apart: mail it recorded collecting besides what is waiting now, or any downloaded mail of
    /// its recorded done (an account that deletes from the server forgets a message's collection once the server stops
    /// listing it, but a rule that filed it keeps it cached). Mail the user wrote — Sent, Drafts — doesn't count. Either way what it downloads is arriving, so the line is drawn at the start;
    /// otherwise no line yet, and only the caller's own batch counts.</para>
    /// </summary>
    private async Task<(long? MaxNumericId, long? MaxDateTicks)?> DrawFirstLineAsync(
        AccountModel account, MailFolderModel folder, List<MailMessageSummary> waiting, CancellationToken ct)
    {
        var waitingIds = waiting.Select(m => m.MessageId).ToHashSet(StringComparer.Ordinal);

        if (account.BackendKind == BackendKind.Pop3Smtp)
        {
            var collected = await _store.LoadPop3CollectedUidlsAsync(account.Id);
            if (!collected.Any(id => !waitingIds.Contains(id)) && !await _store.AccountHasRulesSettledMailAsync(account.Id, Pop3MailService.LocalIdPrefix))
                return (null, null);
            await _store.EnsureRulesWatermarkAsync(account.Id, folder.FullName, 0, 0);
            return await _store.GetRulesSettledBoundaryAsync(account.Id, folder.FullName);
        }

        IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)> listing;
        try
        {
            listing = await _imap.GetFolderMessageIdDatesAsync(account.Id, folder.FullName, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"ApplyRules: couldn't list {account.AccountLabel}/{folder.FullName} to tell arriving mail from older mail; its waiting mail is left for the next pass", ex);
            return null;
        }

        var newestWaitingUid = waiting.Select(m => long.TryParse(m.MessageId, out var u) ? u : 0).DefaultIfEmpty().Max();
        // On IMAP the dates compared are the server's receive date against the Date header, but only the UID part of an IMAP
        // line is ever read.
        var newestWaitingTicks = waiting.Select(m => m.Date.UtcTicks).DefaultIfEmpty().Max();
        long maxNumeric = 0, maxTicks = 0;
        foreach (var (id, received, _) in listing)
        {
            if (waitingIds.Contains(id)) continue;
            if (long.TryParse(id, out var uid) && uid > maxNumeric && uid < newestWaitingUid) maxNumeric = uid;
            if (received.UtcTicks > maxTicks && received.UtcTicks < newestWaitingTicks) maxTicks = received.UtcTicks;
        }
        await _store.EnsureRulesWatermarkAsync(account.Id, folder.FullName, maxNumeric, maxTicks);
        return await _store.GetRulesSettledBoundaryAsync(account.Id, folder.FullName);   // what stands, if another pass drew it first
    }

    // How far before the newest settled receive time a Microsoft 365 message can still be arriving. Exchange stamps the
    // receive time as mail comes in, but scanning can keep a message out of the mailbox for minutes after that, so it can
    // show up after mail stamped later has already been settled. Mail a wider window brings in is days older, and mail
    // from the hour before the newest settled message is already cached, so this lets no older mail through.
    private static readonly long GraphArrivalToleranceTicks = TimeSpan.FromHours(1).Ticks;

    /// <summary>
    /// Whether a message waiting for rules is arriving mail, or older mail cached for the first time by a view that
    /// fetched a wider window than the sync — the sync range widened, say, or All on IMAP. Measured against the folder's
    /// stored arrival line, in the order that account's server gives arrivals:
    /// <list type="bullet">
    /// <item>IMAP: a higher UID. UIDs follow arrival; the Date header does not, and mail delivered late carries an old one.</item>
    /// <item>Microsoft 365: a receive time no more than an hour before the newest. Graph's date is when the server
    /// received the message. A message moved into the Inbox from another program keeps its receive time, so rules don't
    /// run on it — as Outlook's own Inbox rules don't.</item>
    /// <item>POP3: always. Everything a POP3 account caches is mail it has just downloaded; there is no wider window.</item>
    /// </list>
    /// A folder reaches here without a line only when all its waiting mail is the caller's own batch — a sync settling a
    /// folder for the first time — or on a POP3 account's first collection. Then the caller's batch counts, as it always has.
    /// </summary>
    private static bool IsArrival(
        AccountModel account, MailMessageSummary message,
        (long? MaxNumericId, long? MaxDateTicks) boundary, Dictionary<string, MailMessageSummary> inHand)
    {
        if (boundary.MaxDateTicks is not long newestSettled)
            return inHand.ContainsKey(message.MessageId);

        return account.BackendKind switch
        {
            BackendKind.Pop3Smtp => true,
            BackendKind.MicrosoftGraph => message.Date.UtcTicks >= newestSettled - GraphArrivalToleranceTicks,
            _ => long.TryParse(message.MessageId, out var uid)
                ? uid > (boundary.MaxNumericId ?? 0)
                : message.Date.UtcTicks >= newestSettled,
        };
    }

    /// <summary>
    /// Whether client rules act on this folder. #336: client rules fire ONLY on the Inbox. Other folders are still
    /// fetched and cached — rules just don't run against them. This is the classic mail-rules model (rules process
    /// mail as it arrives in the Inbox) and it prevents double-processing: a server-side rule (or a manual move) that
    /// files a message into another folder must not then be re-acted on by a matching client rule when QuickMail
    /// syncs that folder, and a rule must never yank back mail the user filed elsewhere.
    /// <para>IMPORTANT (review L5): for Graph accounts folder.FullName is an opaque id that never equals "INBOX", so
    /// folder.Kind == Inbox is the ONLY thing keeping client rules alive on a Graph inbox. Every current caller
    /// resolves the inbox model from _cachedFolders (where Kind is set), so this holds — but any new entry point that
    /// hands this a Graph inbox with Kind == None would silently stop running rules on it. Pinned by
    /// GraphInbox_ByKind_RunsRules.</para>
    /// </summary>
    private static bool IsInbox(MailFolderModel folder)
        => folder.Kind == SpecialFolderKind.Inbox
           || string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Logs, once a session per account, that a shared mailbox's saved client rules are kept but not run (#678).</summary>
    private void LogSharedMailboxSkipOnce(AccountModel account)
    {
        if (_sharedRulesSkipLogged.TryAdd(account.Id, 0)
            && RulesOrNone().Any(r => r.AccountId == account.Id && r.IsEnabled))
            LogService.Log($"Client-side rules saved for the shared mailbox {account.AccountLabel} are kept but not run: a shared mailbox's rules are managed in Outlook (#678).");
    }

    /// <summary>Strips rule-moved/deleted messages from a fetched batch, so the UI doesn't show them in the origin
    /// folder, and raises what the rules did.</summary>
    private List<MailMessageSummary> StripAndRaise(
        List<MailMessageSummary> fetched, int matchedCount, List<MailMessageSummary> removedMessages)
    {
        if (removedMessages.Count > 0)
        {
            var removedKeys = removedMessages
                .Select(m => (m.MessageId, m.AccountId, m.FolderName)).ToHashSet();
            fetched.RemoveAll(m => removedKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));
        }
        RaiseRuleOutcome(matchedCount, removedMessages);
        return fetched;
    }

    private void RaiseRuleOutcome(int matchedCount, List<MailMessageSummary> removedMessages)
    {
        if (matchedCount == 0 && removedMessages.Count == 0) return;
        _ui.Post(() =>
        {
            if (matchedCount > 0) RulesApplied?.Invoke(matchedCount);
            if (removedMessages.Count > 0) MessagesRemoved?.Invoke(removedMessages);
        });
    }

    public async Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // IDLE-triggered sync in non-online (SQLite cache) mode.
        //
        // We intentionally mirror SyncOneFolderOnlineAsync rather than calling
        // SyncFolderAsync here.  SyncFolderAsync queries the max message key from the store
        // and fetches only messages *after* that key — but by the time IDLE fires,
        // RefreshFolderFromServerAsync has usually already stored the new messages and
        // advanced the max key.  That causes SyncFolderAsync to see incoming.Count == 0,
        // skip FolderSynced, and produce no announcement.
        //
        // Fetching the last 50 by count (sinceMessageId: "0") guarantees FolderSynced fires
        // whenever the server has messages.  OnFolderSynced deduplicates by message id so
        // already-visible messages are discarded; only genuinely new arrivals are inserted.
        LogService.Log($"IDLE targeted sync: fetching {account.AccountLabel}/{folder.FullName}");
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, sinceMessageId: "0", initialCount: 50, ct);
        LogService.Log($"IDLE targeted sync: {incoming.Count} messages fetched from {account.AccountLabel}/{folder.FullName}");
        if (incoming.Count > 0)
        {
            // Upsert + client rules happen inside the shared chokepoint so live-arriving mail is
            // subject to rules exactly like the full sync. POP3 caches its downloads inside the fetch;
            // they are cached as waiting for rules, so the chokepoint still runs rules on them (#712).
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: true, consumeRebuildBaseline: false, ct);
            QueueArrivalBodies(account, folder, incoming, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }
        return incoming;
    }

    public async Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // Fetch the last 50 messages. OnFolderSynced deduplicates by UID so already-visible
        // messages are harmlessly skipped; only truly new arrivals are inserted.
        LogService.Log($"IDLE targeted sync: fetching {account.AccountLabel}/{folder.FullName}");
        var incoming = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, sinceMessageId: "0", initialCount: 50, ct);
        LogService.Log($"IDLE targeted sync: {incoming.Count} messages fetched from {account.AccountLabel}/{folder.FullName}");
        if (incoming.Count > 0)
        {
            // Online mode keeps no local store, so rules dedupe via the in-session guard only.
            incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: false, consumeRebuildBaseline: false, ct);
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        }
        return incoming;
    }

    /// <summary>
    /// Full sync of a single folder: fetches messages newer than the local high-water mark (raising
    /// <see cref="FolderSynced"/> so the current view merges them in) and then reconciles remote
    /// deletions (raising <see cref="MessagesRemoved"/>). This is the same work the startup full sync
    /// does per folder, exposed for the periodic all-folder sweep — non-Inbox folders have no live
    /// watcher (Graph delta and IMAP IDLE cover only the Inbox), so mail a server-side rule files into
    /// a custom folder is otherwise invisible until the folder is opened or the app restarts (#366).
    /// Returns the genuinely-new arrivals (empty when none).
    /// </summary>
    public async Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
        => await SyncFolderAsync(account, folder, ct);

    private async Task<List<MailMessageSummary>> SyncFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // ── New messages ─────────────────────────────────────────────────────────
        var maxKey   = await _store.GetMaxMessageKeyAsync(account.Id, folder.FullName);
        var cfg      = _config.Load();

        // maxKey is "0" for a genuinely-fresh folder AND for every Graph folder (Graph message ids are
        // non-numeric, so GetMaxMessageKeyAsync's CAST-to-integer high-water mark is always 0). Without
        // a guard, a fully-cached Graph folder re-fetched its whole SyncDays window on EVERY periodic
        // sweep — ~all its recent mail, every cycle (#462). The id-diff path below fetches only when the
        // server actually holds something new. (IMAP folders with mail have a real numeric maxKey and
        // take the incremental branch below, so this changes nothing for them.)
        if (maxKey == "0" && cfg.SyncDays > 0)
            return await SyncFolderByIdDiffAsync(account, folder, cfg, ct);

        var incoming = await _imap.GetMessagesSinceAsync(
            account.Id, folder.FullName, maxKey, cfg.InitialSyncCount, ct);

        // Upsert + client rules run inside the shared chokepoint (the same path the live IDLE
        // syncs use). It strips rule-moved/deleted messages from the batch and raises
        // RulesApplied / MessagesRemoved; here we just surface the survivors to the UI —
        // immediately, without waiting for body preview fetches. Called even when nothing new came
        // back, so Inbox mail another path cached and left waiting for rules is settled (#712).
        incoming = await ApplyRulesToArrivalsAsync(account, folder, incoming, persisted: true, consumeRebuildBaseline: true, ct);
        if (incoming.Count > 0)
            _ui.Post(() => FolderSynced?.Invoke(incoming));

        // ── Remote deletions ─────────────────────────────────────────────────────
        await ReconcileFolderAsync(account, folder, ct);

        return incoming;
    }

    /// <summary>
    /// Sync path for a folder whose numeric high-water mark is always "0" — every Graph folder (Graph
    /// ids are non-numeric) and any genuinely-fresh folder. Instead of re-fetching the whole SyncDays
    /// window on every periodic sweep (#462), it lists the server ids WITH their received dates ONCE and
    /// gates on that: it fetches the window only when the server holds a <em>within-window</em> id the
    /// cache lacks, so an unchanged folder costs one id listing and no message fetch.
    ///
    /// Why window-scoped and not a plain id diff: the local cache only ever holds mail inside the
    /// SyncDays window (the fetch is date-filtered), while the server lists mail of every age. A naïve
    /// "server has an id the cache lacks" test is therefore true forever on any folder containing mail
    /// older than the window, and would re-fetch every cycle — defeating the fix. Filtering the server
    /// ids to the window before diffing compares like with like.
    ///
    /// Why the fetch pulls the WHOLE window (not "newest-cached-date forward"): that still surfaces mail
    /// filed into the folder with an <em>older</em> receivedDateTime than the newest we already hold — a
    /// server-side rule batch-filing older mail, or old mail moved in from another client — as long as it
    /// falls inside the window. A date-forward filter would silently miss it. The single date-bearing
    /// listing also drives the deletion reconcile (cached ids missing from the FULL set), so there is no
    /// second round-trip.
    /// </summary>
    private async Task<List<MailMessageSummary>> SyncFolderByIdDiffAsync(
        AccountModel account, MailFolderModel folder, ConfigModel cfg, CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-cfg.SyncDays);

        // Probe mode: the fixture stub lists no server ids, so an id-diff would skip the seed fetch and
        // (via reconcile) delete the seeded fixture mail. Fetch the window and skip reconcile, exactly
        // as this branch did before #462.
        if (_probeMode)
        {
            var seeded = await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, windowStart, ct);
            return await SurfaceArrivalsAsync(account, folder, seeded, ct);
        }

        // id → is_read for everything cached in this folder. The key set is the folder's cached-id set
        // (drives the addition/deletion diff), and the values let us reconcile read/unread changed by
        // another client — so this one query replaces a separate GetAllMessageIdsAsync here.
        var cacheReadStates = await _store.LoadFolderReadStatesAsync(account.Id, folder.FullName);

        // Fresh/empty cache: fetch the full initial window and skip the id listing entirely — there is
        // nothing to diff against and nothing to reconcile (both no-op on an empty cache), so the listing
        // would be a wasted round-trip on each folder's first sync.
        if (cacheReadStates.Count == 0)
        {
            var initial = await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, windowStart, ct);
            return await SurfaceArrivalsAsync(account, folder, initial, ct);
        }

        var serverIdDates = await _imap.GetFolderMessageIdDatesAsync(account.Id, folder.FullName, ct);

        // Fetch only when the server lists a WITHIN-WINDOW id we don't yet hold — old mail the cache never
        // captured (older than the window) is not a reason to fetch.
        var hasNew = serverIdDates.Any(m => m.ReceivedUtc >= windowStart && !cacheReadStates.ContainsKey(m.Id));

        var fetched = hasNew
            ? await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, windowStart, ct)
            : new List<MailMessageSummary>();

        // Always run the (possibly empty) batch through the chokepoint. Even an empty batch settles Inbox mail
        // another path cached that is still waiting for rules (#712), and consumes a pending #366 rebuild
        // baseline (F4) — preserving the pre-#462 behavior where every sweep passed through
        // ApplyRulesToArrivalsAsync.
        var incoming = await SurfaceArrivalsAsync(account, folder, fetched, ct);

        // ── Read/unread reconcile ── the old full-window re-fetch refreshed read state from the server as
        // a side effect (UpsertSummariesAsync carries is_read = excluded.is_read); with the fetch now
        // skipped, do it explicitly from the same listing. A cached message the server now reports with a
        // different read state — read (or unread) elsewhere, e.g. Outlook on the phone — gets its cache
        // row updated and the change surfaced. Messages just fetched above already carry current read
        // state, so exclude them.
        var fetchedIds = fetched.Count == 0 ? null : new HashSet<string>(fetched.Select(m => m.MessageId));
        await ReconcileReadStatesAsync(account, folder, serverIdDates, cacheReadStates, fetchedIds);

        // ── Remote deletions ── the FULL server id set (any age) vs the cache; reuse the listing we
        // already have (no second server round-trip).
        var localIds = new HashSet<string>(cacheReadStates.Keys);
        var serverIds = serverIdDates.Select(m => m.Id).ToList();
        await ReconcileDeletionsAsync(account, folder, localIds, serverIds);

        return incoming;
    }

    /// <summary>
    /// Updates the cache and the UI for messages whose read/unread state changed on the server since we
    /// last saw them (#462). Diffs the server's read state (from the id listing) against the cached
    /// state; for the rows that differ it updates only <c>is_read</c> in the store (never touching other
    /// columns) and raises <see cref="FolderReadStatesReconciled"/> with minimal summaries so the view
    /// can refresh the matching rows and folder counts. Deliberately NOT routed through FolderSynced: a
    /// read change must not fire a new-mail toast or reconcile flag state.
    /// </summary>
    private async Task ReconcileReadStatesAsync(
        AccountModel account, MailFolderModel folder,
        IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)> serverIdDates,
        Dictionary<string, bool> cacheReadStates,
        HashSet<string>? justFetchedIds)
    {
        var toRead   = new List<(Guid, string, string)>();
        var toUnread = new List<(Guid, string, string)>();
        var changed  = new List<MailMessageSummary>();

        foreach (var m in serverIdDates)
        {
            if (justFetchedIds != null && justFetchedIds.Contains(m.Id)) continue; // already current from the fetch
            if (!cacheReadStates.TryGetValue(m.Id, out var cachedRead)) continue;  // not cached (or a new arrival)
            if (cachedRead == m.IsRead) continue;

            (m.IsRead ? toRead : toUnread).Add((account.Id, folder.FullName, m.Id));
            changed.Add(new MailMessageSummary
            {
                MessageId  = m.Id,
                AccountId  = account.Id,
                FolderName = folder.FullName,
                IsRead     = m.IsRead,
            });
        }

        if (changed.Count == 0) return;

        // is_read-only updates — never an upsert, which would blank the row's other columns.
        if (toRead.Count   > 0) await _store.UpdateIsReadBatchAsync(toRead,   isRead: true);
        if (toUnread.Count > 0) await _store.UpdateIsReadBatchAsync(toUnread, isRead: false);

        LogService.Log($"Read-state reconcile {account.AccountLabel}/{folder.FullName}: {changed.Count} changed");
        _ui.Post(() => FolderReadStatesReconciled?.Invoke(changed));
    }

    /// <summary>
    /// Runs a fetched batch through the shared rules/upsert chokepoint and raises
    /// <see cref="FolderSynced"/> for the survivors. An empty batch is a cheap no-op (aside from
    /// consuming a pending rebuild baseline).
    /// </summary>
    private async Task<List<MailMessageSummary>> SurfaceArrivalsAsync(
        AccountModel account, MailFolderModel folder, List<MailMessageSummary> fetched, CancellationToken ct)
    {
        var incoming = await ApplyRulesToArrivalsAsync(account, folder, fetched, persisted: true, consumeRebuildBaseline: true, ct);
        if (incoming.Count > 0)
            _ui.Post(() => FolderSynced?.Invoke(incoming));
        return incoming;
    }

    /// <summary>
    /// Reconciles a single folder's local cache against the server: any message id we hold locally
    /// but the server no longer lists (deleted or moved away by another client — Outlook web/desktop/
    /// mobile, a server-side rule, or Exchange Online rebalancing) is removed from the store and
    /// raised via <see cref="MessagesRemoved"/> so the UI drops the ghost row.
    ///
    /// Backend-agnostic — <see cref="IMailService.GetFolderMessageIdsAsync"/> routes to IMAP or Graph
    /// (Graph reads with immutable ids, #366). Add-only sync paths (live IDLE, Graph delta poll, the
    /// periodic fallback) do NOT reconcile, so this is the piece that catches deletions made elsewhere
    /// while the app is running. Cheap: one id-only listing plus a set difference. No-ops (returns 0)
    /// when the folder has no local data yet. Returns the number of ghosts removed.
    /// </summary>
    public async Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
    {
        // Never reconcile against the probe stub: its empty server listing would delete the seeded
        // fixture mail and blank the visual-QA captures (see _probeMode).
        if (_probeMode) return 0;

        // Only meaningful when we already have local data for this folder.
        var localIds = await _store.GetAllMessageIdsAsync(account.Id, folder.FullName);
        if (localIds.Count == 0) return 0;

        var serverIds  = await _imap.GetFolderMessageIdsAsync(account.Id, folder.FullName, ct);
        return await ReconcileDeletionsAsync(account, folder, localIds, serverIds);
    }

    /// <summary>
    /// Deletion half of the reconcile, given id sets that have already been listed — removes cached ids
    /// the server no longer lists and raises <see cref="MessagesRemoved"/>. Split out of
    /// <see cref="ReconcileFolderAsync"/> so the Graph sweep path (<see cref="SyncFolderByIdDiffAsync"/>)
    /// can drive both addition detection and deletion from a single server-id listing (#462). Callers own
    /// the _probeMode guard and the empty-cache no-op.
    /// </summary>
    private async Task<int> ReconcileDeletionsAsync(
        AccountModel account, MailFolderModel folder, HashSet<string> localIds, IList<string> serverIds)
    {
        // A backend whose listing is not authoritative for deletions (POP3: the server drops a
        // message from its listing the moment it is collected, and the cache is then the only copy)
        // makes this whole reconcile unsafe, not merely unnecessary. Asked here, once, rather than
        // left to each backend to shape its listing so the arithmetic happens to come out empty.
        if (!_imap.ListingIsAuthoritativeForDeletions(account.Id)) return 0;

        var serverSet  = new HashSet<string>(serverIds);
        var deletedIds = localIds.Where(id => !serverSet.Contains(id)).ToList();

        if (deletedIds.Count == 0) return 0;

        LogService.Log($"Reconcile {account.AccountLabel}/{folder.FullName}: {deletedIds.Count} remote deletion(s)");
        await _store.DeleteSummariesAsync(account.Id, folder.FullName, deletedIds);

        var removed = deletedIds
            .Select(id => new MailMessageSummary
            {
                MessageId  = id,
                AccountId  = account.Id,
                FolderName = folder.FullName,
            })
            .ToList();

        _ui.Post(() => MessagesRemoved?.Invoke(removed));

        return deletedIds.Count;
    }

    private async Task FetchAndApplyPreviewsAsync(
        AccountModel account, MailFolderModel folder,
        List<MailMessageSummary> incoming, CancellationToken ct)
    {
        try
        {
            // Only fetch bodies for messages the server didn't fill via IMAP PREVIEW.
            var ids = incoming
                .Where(s => string.IsNullOrEmpty(s.Preview))
                .OrderByDescending(s => s.Date)
                .Take(100)
                .Select(s => s.MessageId)
                .ToList();
            if (ids.Count == 0) return;

            var previewLines = _config.Load().GetPreviewLines(account.Id);
            if (previewLines <= 0) return;
            var previews = await _imap.FetchPreviewsAsync(
                account.Id, folder.FullName, ids, previewLines, ct);

            // Match each summary in 'incoming' to its preview, building both the
            // UI-apply list and the persistence list in one pass.
            var updates = new List<(string MessageId, string Preview)>(previews.Count);
            var uiApply = new List<(MailMessageSummary Summary, string Preview)>(previews.Count);
            foreach (var s in incoming)
            {
                if (!previews.TryGetValue(s.MessageId, out var p)) continue;
                uiApply.Add((s, p));
                updates.Add((s.MessageId, p));
            }
            if (uiApply.Count == 0) return;

            // One dispatcher hop for the whole batch instead of N — N dispatcher
            // invocations during a fast sync flood the UI thread with continuations.
            _ui.Post(() =>
            {
                foreach (var (s, p) in uiApply) s.Preview = p;
            });

            // One transaction for the whole batch instead of N opens/commits.
            await _store.UpdatePreviewsBatchAsync(account.Id, folder.FullName, updates);
        }
        catch (OperationCanceledException) { /* sync cancelled — normal */ }
        catch (Exception ex)
        {
            LogService.Log($"FetchAndApplyPreviews {account.AccountLabel}/{folder.FullName}", ex);
        }
    }

    public DateTimeOffset? LastSyncedUtc(Guid accountId) =>
        _lastSyncedUtc.TryGetValue(accountId, out var t) ? t : null;
}
