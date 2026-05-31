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
    private List<MailRule> _cache = [];
    private bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RuleService(IMailService imap, ILocalStoreService store, string? dataDirectory = null)
    {
        _imap = imap;
        _store = store;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        _filePath = Path.Combine(dir, "rules.json");
    }

    // ── Load / Save ─────────────────────────────────────────────────────────

    public List<MailRule> LoadRules()
    {
        if (_loaded) return _cache;

        if (!File.Exists(_filePath))
        {
            _cache = [];
            _loaded = true;
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<MailRule>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }
        _loaded = true;
        return _cache;
    }

    public void SaveRules(List<MailRule> rules)
    {
        _cache = rules;
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        // Atomic write: write to temp file, then rename.
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(rules, JsonOptions));
        File.Move(tempPath, _filePath, overwrite: true);
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

        var affectedKeys = new HashSet<(uint Uid, Guid AccountId, string FolderName)>();
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
                    LogService.Debug($"    Match: From='{m.From}' Subject='{m.Subject}' UID={m.UniqueId} Folder={m.FolderName}");
            }
            if (matched.Count == 0) continue;

            foreach (var m in matched)
                affectedKeys.Add((m.UniqueId, m.AccountId, m.FolderName));

            try
            {
                await ExecuteActionAsync(rule, matched, accountId, ct);

                // Remove messages from incoming that were moved or deleted so the
                // UI doesn't show them in the original folder after FolderSynced fires.
                if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
                {
                    var matchedKeys = new HashSet<(uint Uid, Guid AccountId, string FolderName)>();
                    foreach (var m in matched)
                        matchedKeys.Add((m.UniqueId, m.AccountId, m.FolderName));
                    incoming.RemoveAll(m => matchedKeys.Contains((m.UniqueId, m.AccountId, m.FolderName)));
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
                await _imap.MarkReadAsync(msg.AccountId, msg.FolderName, msg.UniqueId, ct);
                msg.IsRead = true;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.UniqueId, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkRead failed for UID {msg.UniqueId}", ex);
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
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.UniqueId, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkUnread failed for UID {msg.UniqueId}", ex);
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
            var uids = group.Select(m => m.UniqueId).ToList();
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
            var uids = group.Select(m => m.UniqueId).ToList();
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
        CancellationToken ct)
    {
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0) return [];

        var removedMessages = new List<MailMessageSummary>();

        // Load all cached messages once
        var allMessages = await store.LoadAllSummariesAsync();
        LogService.Debug($"ApplyRulesToExisting: {allMessages.Count} cached messages, {enabledRules.Count} enabled rules");

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            var matched = allMessages.Where(m =>
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
                    var byFolder = matched.GroupBy(m => (m.AccountId, m.FolderName));
                    foreach (var group in byFolder)
                    {
                        await store.DeleteSummariesAsync(
                            group.Key.AccountId, group.Key.FolderName,
                            group.Select(m => m.UniqueId));
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
