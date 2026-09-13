using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class RuleService : IRuleService
{
    private readonly string _filePath;
    private readonly IMailService _imap;
    private readonly ILocalStoreService _store;
    private readonly IAccountService? _accountService;
    private List<MailRule> _cache = [];
    private bool _loaded;
    private string? _loggedLoadError;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RuleService(IMailService imap, ILocalStoreService store, string? dataDirectory = null,
        IAccountService? accountService = null)
    {
        _imap = imap;
        _store = store;
        // Optional: supplied in the app (App.xaml.cs) to drive the D1 "All accounts" → per-account
        // migration. When absent (unit tests that don't exercise migration), no migration runs.
        _accountService = accountService;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        _filePath = Path.Combine(dir, "rules.json");
    }

    // ── Load / Save ─────────────────────────────────────────────────────────

    /// <summary>
    /// The rules in rules.json, cached after the first successful read. A missing file, or one holding
    /// nothing, is no rules.
    /// <para>
    /// A file that is there but can't be read or parsed throws <see cref="RulesFileUnreadableException"/>
    /// instead of reading as empty (#700). Every writer loads the whole list, changes it and saves it back,
    /// so an empty read turned the next save into one that replaced every rule in the file. The failure is
    /// not cached: a file that was only locked for a moment reads normally on the next call.
    /// </para>
    /// </summary>
    public List<MailRule> LoadRules()
    {
        if (_loaded) return _cache;

        List<MailRule> rules;
        var missing = false;
        try
        {
            var json = File.ReadAllText(_filePath);
            rules = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<MailRule>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                   || (ex is DirectoryNotFoundException && DriveIsThere()))
        {
            // Not there yet, so no rules. File.Exists, checked here before, answered false for any error at all — a
            // permission problem, a drive that had dropped — and that read as no rules and let the next save replace
            // the file (#700). A missing folder counts as "not there" only on a drive that is there: a profile on a
            // drive that has disconnected reports the same missing folder, and its rules are not gone.
            rules = [];
            missing = true;
        }
        catch (Exception ex)
        {
            var unreadable = RulesFileUnreadableException.For(_filePath, ex);
            // Sync reads the rules on every Inbox poll, so log a failure when it starts or changes, not each time.
            if (_loggedLoadError != unreadable.Message)
            {
                _loggedLoadError = unreadable.Message;
                LogService.Log($"Client-side rules file {_filePath} can't be read; it is left as it is, and no client-side rules run until it can be.", ex);
            }
            throw unreadable;
        }

        if (_loggedLoadError is not null)
        {
            _loggedLoadError = null;
            LogService.Log(missing
                ? "Client-side rules file that couldn't be read is no longer there; there are no client-side rules."
                : "Client-side rules file can be read again.");
        }
        _cache = rules;
        _loaded = true;
        MigrateAllAccountRules();
        return _cache;
    }

    /// <summary>Whether the drive (or network share) the rules file lives on is reachable at all.</summary>
    private bool DriveIsThere()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_filePath));
        return !string.IsNullOrEmpty(root) && Directory.Exists(root);
    }

    /// <summary>
    /// One-time migration (#333, D1): the "All accounts" rule scope is retired, so every rule must
    /// belong to exactly one account. Each unscoped rule (null <see cref="MailRule.AccountId"/>) is
    /// duplicated into one rule per <b>non-Graph</b> account; Graph accounts receive none — server
    /// rules replace client rules there, and an all-account client rule must not silently run on a
    /// Graph mailbox (D2).
    /// <para>
    /// <b>Idempotent by construction.</b> The migration is defined as "eliminate null AccountId", so
    /// the absence of any unscoped rule <i>is</i> the completion signal — a second run finds nothing
    /// to do. It runs here in <see cref="LoadRules"/>, before any consumer sees the list, so no caller
    /// can observe a null AccountId. It persists once at the end via the atomic <see cref="SaveRules"/>,
    /// so the file is either fully migrated or untouched — never half-duplicated.
    /// </para>
    /// </summary>
    private void MigrateAllAccountRules()
    {
        if (_accountService is null) return;                    // no account context (some unit tests)
        if (_cache.All(r => r.AccountId is not null)) return;   // already migrated / nothing unscoped

        var accounts = _accountService.LoadAccounts();
        // Never migrate against an EMPTY account list: it would drop every unscoped rule, and an empty
        // read can be transient (startup ordering, a locked/corrupt accounts.json). Defer until
        // accounts exist. A genuine Graph-only profile still drops below (accounts present, but none
        // non-Graph) — the drop path only fires when there is real account context. (Review of #364.)
        if (accounts.Count == 0) return;

        // Never onto a shared mailbox either (#678): its rules are managed in Outlook, and a client-side
        // rule there would be kept but never run.
        var targets = accounts.Where(a => a.BackendKind != BackendKind.MicrosoftGraph && !a.IsShared).ToList();

        var migrated = new List<MailRule>(_cache.Count);
        int converted = 0, dropped = 0;

        foreach (var rule in _cache)
        {
            if (rule.AccountId is not null) { migrated.Add(rule); continue; }

            converted++;
            if (targets.Count == 0)
            {
                // Graph-only profile: an all-account CLIENT rule has no valid target (it must not run
                // on a Graph account, D2), so it is dropped. Destructive, hence logged per rule — the
                // release notes call this out. Affected population: a Microsoft-only profile carrying
                // legacy all-account client rules.
                dropped++;
                LogService.Log($"Rules migration: dropped all-account rule '{rule.Name}' — no non-Graph account to assign it to.");
                continue;
            }

            foreach (var account in targets)
                migrated.Add(CloneForAccount(rule, account.Id));
        }

        _cache = migrated;
        SaveRules(_cache);   // atomic write; also refreshes _cache/_loaded
        LogService.Log($"Rules migration: converted {converted} all-account rule(s) across {targets.Count} non-Graph account(s); dropped {dropped}.");
    }

    /// <summary>
    /// Copies a rule and binds it to one account. The copy gets a <b>fresh Id</b> — reusing the
    /// source id across N per-account copies would collide, breaking selection and delete-by-id.
    /// </summary>
    private static MailRule CloneForAccount(MailRule source, Guid accountId) => new()
    {
        Id = Guid.NewGuid(),
        Name = source.Name,
        IsEnabled = source.IsEnabled,
        UseFromCondition = source.UseFromCondition,
        FromContains = source.FromContains,
        UseToCondition = source.UseToCondition,
        ToContains = source.ToContains,
        UseSubjectCondition = source.UseSubjectCondition,
        SubjectContains = source.SubjectContains,
        UseBodyCondition = source.UseBodyCondition,
        BodyContains = source.BodyContains,
        MustHaveAttachments = source.MustHaveAttachments,
        AccountId = accountId,
        Action = source.Action,
        TargetFolder = source.TargetFolder,
    };

    public void SaveRules(List<MailRule> rules)
    {
        // Read the file first if this instance hasn't (#700), so a save made before anything was read throws for a
        // file that can't be read instead of replacing it. It does not protect a READABLE file from a caller that
        // saves a list it didn't load: that list replaces the file, as it always has.
        if (!_loaded) LoadRules();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            Helpers.AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(rules, JsonOptions));
        }
        catch
        {
            // Callers change the cached list in place before saving it, so after a failed write the cache holds
            // a change the file doesn't. Read the file again next time rather than go on reporting that change.
            _loaded = false;
            throw;
        }
        _cache = rules;
        _loaded = true;
    }

    // ── Rule Execution ──────────────────────────────────────────────────────

    public async Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct)
    {
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        LogService.Debug($"ApplyRulesAsync: {enabledRules.Count} enabled rules, {incoming.Count} incoming messages for account {accountId}");
        if (enabledRules.Count == 0) return (0, []);

        var affectedKeys = new HashSet<(string MessageId, Guid AccountId, string FolderName)>();
        var removedMessages = new List<MailMessageSummary>();

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            // Account scope check
            if (rule.AccountId.HasValue && rule.AccountId.Value != accountId)
            {
                LogService.Debug($"  Rule '{rule.Name}': skipped (account {rule.AccountId} != {accountId})");
                continue;
            }

            var matched = incoming.Where(m => MatchesRule(rule, m)).ToList();
            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched (action={rule.Action}, from='{rule.FromContains}', subject='{rule.SubjectContains}')");
            if (matched.Count > 0)
            {
                foreach (var m in matched.Take(3))
                    LogService.Debug($"    Match: From='{m.From}' Subject='{m.Subject}' UID={m.MessageId} Folder={m.FolderName}");
            }
            if (matched.Count == 0) continue;

            foreach (var m in matched)
                affectedKeys.Add((m.MessageId, m.AccountId, m.FolderName));

            try
            {
                await ExecuteActionAsync(rule, matched, accountId, ct);

                // Remove messages from incoming that were moved or deleted so the
                // UI doesn't show them in the original folder after FolderSynced fires.
                if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
                {
                    var matchedKeys = new HashSet<(string MessageId, Guid AccountId, string FolderName)>();
                    foreach (var m in matched)
                        matchedKeys.Add((m.MessageId, m.AccountId, m.FolderName));
                    incoming.RemoveAll(m => matchedKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));
                    removedMessages.AddRange(matched);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Rule '{rule.Name}' action failed", ex);
            }
        }

        return (affectedKeys.Count, removedMessages);
    }

    public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
    {
        return messages.Where(m => MatchesRule(rule, m)).ToList();
    }

    // ── Condition Matching ──────────────────────────────────────────────────

    private static bool MatchesRule(MailRule rule, MailMessageSummary msg)
    {
        if (rule.UseFromCondition
            && !string.IsNullOrEmpty(rule.FromContains)
            && !msg.From.Contains(rule.FromContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.UseToCondition
            && !string.IsNullOrEmpty(rule.ToContains)
            && !msg.To.Contains(rule.ToContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.UseSubjectCondition
            && !string.IsNullOrEmpty(rule.SubjectContains)
            && !msg.Subject.Contains(rule.SubjectContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.UseBodyCondition
            && !string.IsNullOrEmpty(rule.BodyContains)
            && (msg.Preview == null || !msg.Preview.Contains(rule.BodyContains, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.MustHaveAttachments && !msg.HasAttachments)
            return false;

        return true;
    }

    // ── Action Execution ────────────────────────────────────────────────────

    private async Task ExecuteActionAsync(
        MailRule rule,
        List<MailMessageSummary> matched,
        Guid accountId,
        CancellationToken ct)
    {
        switch (rule.Action)
        {
            case RuleAction.MarkAsRead:
                await MarkAsReadAsync(matched, ct);
                break;

            case RuleAction.MarkAsUnread:
                await MarkAsUnreadAsync(matched, ct);
                break;

            case RuleAction.MoveToFolder:
                if (string.IsNullOrEmpty(rule.TargetFolder)) break;
                await MoveToFolderAsync(matched, rule.TargetFolder, ct);
                break;

            case RuleAction.Delete:
                await DeleteAsync(matched, ct);
                break;
        }
    }

    private async Task MarkAsReadAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _imap.MarkReadAsync(msg.AccountId, msg.FolderName, msg.MessageId, ct);
                msg.IsRead = true;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.MessageId, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkRead failed for UID {msg.MessageId}", ex);
            }
        }
    }

    private async Task MarkAsUnreadAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // IMailService has no MarkUnreadAsync yet — we update the local store
                // only. Full server-side unread will be added in a follow-up.
                msg.IsRead = false;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.MessageId, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkUnread failed for UID {msg.MessageId}", ex);
            }
        }
    }

    private async Task MoveToFolderAsync(
        List<MailMessageSummary> messages, string targetFolder, CancellationToken ct)
    {
        // Group messages by (AccountId, FolderName) so we issue one MOVE per source folder.
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.MessageId).ToList();
            try
            {
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, targetFolder, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MoveToFolder failed for {uids.Count} messages to '{targetFolder}'", ex);
            }
        }
    }

    private async Task DeleteAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.MessageId).ToList();
            try
            {
                await _imap.MoveToTrashBatchAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Delete (move to trash) failed for {uids.Count} messages", ex);
            }
        }
    }

    // ── Apply to existing messages ──────────────────────────────────────────

    public async Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(
        ILocalStoreService store,
        IReadOnlyDictionary<Guid, string> inboxFolderByAccount,
        CancellationToken ct)
    {
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0) return [];

        var removedMessages = new List<MailMessageSummary>();

        // Load all cached messages once, then keep only Inbox mail (issue #346 follow-up).
        // Client rules act on the Inbox only, so drop everything in Sent/Archive/Junk/Trash/custom
        // folders — and any account we weren't given an Inbox for (fail-closed). FolderName holds the
        // folder's FullName (set at sync time), matched Ordinal against the caller-supplied Inbox
        // FullName from the same folder enumeration.
        var allMessages = await store.LoadAllSummariesAsync();
        var inboxMessages = allMessages.Where(m =>
            inboxFolderByAccount.TryGetValue(m.AccountId, out var inbox) &&
            string.Equals(m.FolderName, inbox, StringComparison.Ordinal)).ToList();
        LogService.Debug($"ApplyRulesToExisting: {allMessages.Count} cached, {inboxMessages.Count} in Inbox, {enabledRules.Count} enabled rules");

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            var matched = inboxMessages.Where(m =>
            {
                if (rule.AccountId.HasValue && rule.AccountId.Value != m.AccountId)
                    return false;
                return MatchesRule(rule, m);
            }).ToList();

            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched in existing mail (action={rule.Action})");
            if (matched.Count == 0) continue;

            try
            {
                await ExecuteActionAsync(rule, matched, matched[0].AccountId, ct);

                if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
                {
                    // Out of the running for the rules after this one, as arriving mail is in
                    // ApplyRulesAsync (#685): a later rule would otherwise act again on a message that is
                    // already moved or deleted, and it would be counted twice. Straight after the action, so a
                    // failure updating the local store below can't leave the message in the running.
                    var gone = new HashSet<MailMessageSummary>(matched);
                    inboxMessages.RemoveAll(gone.Contains);

                    var byFolder = matched.GroupBy(m => (m.AccountId, m.FolderName));
                    foreach (var group in byFolder)
                    {
                        await store.DeleteSummariesAsync(
                            group.Key.AccountId, group.Key.FolderName,
                            group.Select(m => m.MessageId));
                    }
                    removedMessages.AddRange(matched);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"ApplyRulesToExisting: rule '{rule.Name}' failed", ex);
            }
        }

        return removedMessages;
    }
}
