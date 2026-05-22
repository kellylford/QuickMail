using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IRuleService
{
    /// <summary>Load all rules from rules.json. Returns empty list if file is missing or corrupted.</summary>
    List<MailRule> LoadRules();

    /// <summary>Persist all rules to rules.json. Creates the data directory if needed.</summary>
    void SaveRules(List<MailRule> rules);

    /// <summary>
    /// Apply enabled rules to a batch of incoming messages for a specific account.
    /// Rules are evaluated in list order. Each rule is tested against every message;
    /// matching messages have the rule's action executed.
    /// Returns the number of messages that matched at least one rule, and the list
    /// of messages that were moved or deleted (removed from the incoming list).
    /// </summary>
    Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct);

    /// <summary>
    /// Test a rule against a set of messages without executing any actions.
    /// Returns the subset of messages that would match.
    /// </summary>
    List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages);

    /// <summary>
    /// Apply all enabled rules to messages already in the local store.
    /// Used after rules are created/edited so existing mail is processed.
    /// Returns messages that were moved or deleted (should be removed from UI).
    /// </summary>
    Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(
        ILocalStoreService store,
        CancellationToken ct);
}
