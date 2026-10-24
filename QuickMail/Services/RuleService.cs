using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
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
                LogService.Log($"Client-side rules file {_filePath} can't be read; it is left as it is, and no client-side rules run until it can be", ex);
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
        FromAddresses = source.FromAddresses is null ? null : [.. source.FromAddresses],
        UseSenderCondition = source.UseSenderCondition,
        SenderContains = source.SenderContains,
        FromAddressesOff = source.FromAddressesOff is null ? null : [.. source.FromAddressesOff],
        UseToCondition = source.UseToCondition,
        ToContains = source.ToContains,
        SentToAddresses = source.SentToAddresses is null ? null : [.. source.SentToAddresses],
        UseSubjectCondition = source.UseSubjectCondition,
        SubjectContains = source.SubjectContains,
        SubjectAlsoMatchesBody = source.SubjectAlsoMatchesBody,
        UseBodyCondition = source.UseBodyCondition,
        BodyContains = source.BodyContains,
        MustHaveAttachments = source.MustHaveAttachments,
        AccountId = accountId,
        Action = source.Action,
        TargetFolder = source.TargetFolder,
        Actions = source.Actions is null ? null : [.. source.Actions],
        CopyTargetFolder = source.CopyTargetFolder,
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
            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched (actions={string.Join(", ", rule.AllActions())}, from='{string.Join("|", rule.FromValues())}', sender='{(rule.UseSenderCondition ? rule.SenderContains : null)}', subject='{rule.SubjectContains}')");
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
                var taken = await ExecuteActionsAsync(rule, matched, accountId, ct);

                // Remove messages from incoming that were moved or deleted so the
                // UI doesn't show them in the original folder after FolderSynced fires.
                // Only the ones that really went: the caller deletes these from the local store, and
                // for a POP3 account that row is the only copy of the message (#794).
                if (taken.Count > 0)
                {
                    var takenKeys = new HashSet<(string MessageId, Guid AccountId, string FolderName)>();
                    foreach (var m in taken)
                        takenKeys.Add((m.MessageId, m.AccountId, m.FolderName));
                    incoming.RemoveAll(m => takenKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));
                    removedMessages.AddRange(taken);
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

    /// <summary>
    /// Whether a sender condition's <paramref name="value"/> matches <paramref name="msg"/>'s sender. A value that is
    /// an address or part of one ("service@paypal.com", "@company.com") is looked for in the sender's real address
    /// alone where the row has it, as a whole address or domain, never in the display name, which the sender writes: otherwise mail from
    /// <c>"service@paypal.com" &lt;x@evil.example&gt;</c> matches a rule Create Rule from Message made from real PayPal
    /// mail (review of #806). A whole "Name &lt;address&gt;", as Create Rule from Message put in a Microsoft rule before
    /// #801, is split the same way: the address must be in the real address and the name in the display name, or
    /// <c>"PayPal &lt;service@paypal.com&gt;" &lt;x@evil.example&gt;</c> would match it too. Anything else, a name above
    /// all, is looked for in the name and address together. A row not yet backfilled has no separate address, so
    /// every value is looked for in what it has, as before #803.
    /// </summary>
    internal static bool SenderMatches(MailMessageSummary msg, string value)
    {
        if (string.IsNullOrEmpty(msg.FromEmail) || !value.Contains('@', StringComparison.Ordinal))
            return msg.FromWithAddress.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (value.Contains('<', StringComparison.Ordinal))
        {
            if (!MimeKit.MailboxAddress.TryParse(value, out var mailbox))
                return msg.FromWithAddress.Contains(value, StringComparison.OrdinalIgnoreCase);
            return AddressMatch.Names(msg.FromEmail, mailbox.Address)
                   && (string.IsNullOrEmpty(mailbox.Name)
                       || msg.From.Contains(mailbox.Name, StringComparison.OrdinalIgnoreCase));
        }

        // A whole address or domain ("service@paypal.com", "@paypal.com": a dot after the '@') is matched on address
        // boundaries, not as a substring: "service@paypal.com" must not match service@paypal.com.evil.example, which
        // anyone owning a domain can send from. A fragment that is neither ("@acme", "john.smith@") can only mean a
        // substring, so it stays one, as it always was.
        var trimmed = value.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return trimmed.IndexOf('.', at + 1) > at
            ? AddressMatch.Names(msg.FromEmail, trimmed)
            : msg.FromEmail.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRule(MailRule rule, MailMessageSummary msg)
    {
        // A rule may list several From (or Sent-to) addresses, and matching any one of them is enough
        // (#682) — the same "or" a server rule's address list means. Every condition is still ANDed with
        // the others, so the "or" is inside one condition, never across two.
        // Both sender conditions go through SenderMatches (#803): From on an IMAP or POP3 row is the display name
        // alone, so an address condition had nothing to match there. A value holding an address is matched against
        // the real address, and a name against the name and address together, since a rule from before #682 may
        // hold a name in its From condition.
        if (rule.FromValues() is { Count: > 0 } fromValues
            && !fromValues.Any(v => SenderMatches(msg, v)))
            return false;

        // ANDed with the addresses above, where a rule uses both.
        if (rule.UseSenderCondition
            && !string.IsNullOrEmpty(rule.SenderContains)
            && !SenderMatches(msg, rule.SenderContains))
            return false;

        if (rule.ToValues() is { Count: > 0 } toValues
            && !toValues.Any(v => msg.To.Contains(v, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.UseSubjectCondition && !string.IsNullOrEmpty(rule.SubjectContains))
        {
            // "Subject or body contains" is the subject condition widened to the body as well (#682), so
            // either place matching is enough. The body here is the stored preview, as BodyContains uses —
            // the only body text a rule can count on having.
            var hit = msg.Subject.Contains(rule.SubjectContains, StringComparison.OrdinalIgnoreCase)
                      || (rule.SubjectAlsoMatchesBody
                          && msg.Preview is not null
                          && msg.Preview.Contains(rule.SubjectContains, StringComparison.OrdinalIgnoreCase));
            if (!hit) return false;
        }

        if (rule.UseBodyCondition
            && !string.IsNullOrEmpty(rule.BodyContains)
            && (msg.Preview == null || !msg.Preview.Contains(rule.BodyContains, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.MustHaveAttachments && !msg.HasAttachments)
            return false;

        return true;
    }

    // ── Action Execution ────────────────────────────────────────────────────

    /// <summary>Runs each of the rule's actions in turn, in the order <see cref="MailRule.AllActions"/> gives:
    /// moving or deleting comes last, because after either the message is no longer where the others look.
    /// Returns the messages a move or delete actually took out of their folder — not every message the
    /// rule matched. A move that failed left its messages where they were, and a caller that treated
    /// them as gone deleted them from the local store: for a POP3 account, the only copy (#794).</summary>
    private async Task<List<MailMessageSummary>> ExecuteActionsAsync(
        MailRule rule,
        List<MailMessageSummary> matched,
        Guid accountId,
        CancellationToken ct)
    {
        var taken = new List<MailMessageSummary>();
        foreach (var action in rule.AllActions())
        {
            switch (action)
            {
                case RuleAction.MarkAsRead:
                    await MarkAsReadAsync(matched, ct);
                    break;

                case RuleAction.MarkAsUnread:
                    await MarkAsUnreadAsync(matched, ct);
                    break;

                case RuleAction.CopyToFolder:
                    if (string.IsNullOrEmpty(rule.CopyTargetFolder)) break;
                    await CopyToFolderAsync(matched, rule.CopyTargetFolder, ct);
                    break;

                case RuleAction.MoveToFolder:
                    if (string.IsNullOrEmpty(rule.TargetFolder)) break;
                    taken.AddRange(await MoveToFolderAsync(matched, rule.TargetFolder, ct));
                    break;

                case RuleAction.Delete:
                    taken.AddRange(await DeleteAsync(matched, ct));
                    break;
            }
        }
        return taken.Distinct().ToList();
    }

    private async Task CopyToFolderAsync(
        List<MailMessageSummary> messages, string targetFolder, CancellationToken ct)
    {
        // One COPY per source folder, as for a move. A failure here is deliberately NOT swallowed the way the other
        // actions' are: copying runs before moving and deleting, and a rule that keeps a copy and then files the
        // message must not file it when no copy was made — that loses the message from the Inbox with nothing kept
        // anywhere. Throwing leaves it where it is and skips the rule's remaining actions. Messages from more than one
        // source folder are copied group by group, so a failure part-way through leaves the earlier groups copied; the
        // rule's later actions are then skipped for every group, not only the one that failed.
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.MessageId).ToList();

            // Copying into the folder the message is already in makes a second copy that the next sync reads as new
            // mail, which this same rule matches and copies again, and so on until the mailbox fills. Client rules run
            // on the Inbox, so this is a rule that copies to the Inbox. The Rules Manager refuses to save one; this is
            // the backstop for a rule that acquires such a target afterwards (a folder rename, a Graph conversion).
            if (string.Equals(group.Key.FolderName, targetFolder, StringComparison.Ordinal))
            {
                LogService.Log($"Rule copy skipped: '{targetFolder}' is the folder the {uids.Count} message(s) are already in.");
                continue;
            }

            try
            {
                await _imap.CopyMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, targetFolder, ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // A request that timed out arrives as TaskCanceledException even though nothing asked us to stop. Left
                // as a cancellation it would travel past every later rule — the callers rethrow those untouched — and
                // abort the whole pass. It is a failed copy, and only this rule should stop.
                LogService.Log($"Rule copy to '{targetFolder}' timed out for {uids.Count} message(s); the rule's remaining actions were skipped", ex);
                throw new IOException($"Copying {uids.Count} message(s) to '{targetFolder}' timed out.", ex);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Rule copy to '{targetFolder}' failed for {uids.Count} message(s); the rule's remaining actions were skipped", ex);
                throw;
            }
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

    /// <summary>Moves the messages, one MOVE per source folder, and returns the ones that moved.</summary>
    private async Task<List<MailMessageSummary>> MoveToFolderAsync(
        List<MailMessageSummary> messages, string targetFolder, CancellationToken ct)
    {
        var moved = new List<MailMessageSummary>();
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
                moved.AddRange(group);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A timeout arrives as a cancellation nobody asked for; like any other failure it leaves
                // these messages where they were, and the rest of the pass carries on.
                LogService.Log($"MoveToFolder failed for {uids.Count} messages to '{targetFolder}'", ex);
            }
        }
        return moved;
    }

    /// <summary>Moves the messages to Trash, one batch per source folder, and returns the ones that went.</summary>
    private async Task<List<MailMessageSummary>> DeleteAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        var deleted = new List<MailMessageSummary>();
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.MessageId).ToList();
            try
            {
                await _imap.MoveToTrashBatchAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, ct);
                deleted.AddRange(group);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Delete (move to trash) failed for {uids.Count} messages", ex);
            }
        }
        return deleted;
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

        // Everything this pass looks at has now had the rules run on it, so record it. A message a view cached a moment
        // ago is still waiting for a sync pass (#712), and that pass would otherwise run the rules on it again — with
        // Copy, a second copy. Recorded before acting, as the sync pass does. That closes the gap for a pass that comes
        // later, not for one already acting on the same message as this loads the store; that race was always there.
        foreach (var group in inboxMessages.GroupBy(m => (m.AccountId, m.FolderName)))
            await store.MarkRulesAppliedAsync(group.Key.AccountId, group.Key.FolderName, group.Select(m => m.MessageId));

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            var matched = inboxMessages.Where(m =>
            {
                if (rule.AccountId.HasValue && rule.AccountId.Value != m.AccountId)
                    return false;
                return MatchesRule(rule, m);
            }).ToList();

            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched in existing mail (actions={string.Join(", ", rule.AllActions())})");
            if (matched.Count == 0) continue;

            try
            {
                // Only what a move or delete really took: a message whose move failed is still in the
                // Inbox, and deleting its row below would, on a POP3 account, destroy the only copy (#794).
                var taken = await ExecuteActionsAsync(rule, matched, matched[0].AccountId, ct);

                if (taken.Count > 0)
                {
                    // Out of the running for the rules after this one, as arriving mail is in
                    // ApplyRulesAsync (#685): a later rule would otherwise act again on a message that is
                    // already moved or deleted, and it would be counted twice. Straight after the action, so a
                    // failure updating the local store below can't leave the message in the running.
                    var gone = new HashSet<MailMessageSummary>(taken);
                    inboxMessages.RemoveAll(gone.Contains);

                    var byFolder = taken.GroupBy(m => (m.AccountId, m.FolderName));
                    foreach (var group in byFolder)
                    {
                        await store.DeleteSummariesAsync(
                            group.Key.AccountId, group.Key.FolderName,
                            group.Select(m => m.MessageId));
                    }
                    removedMessages.AddRange(taken);
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
