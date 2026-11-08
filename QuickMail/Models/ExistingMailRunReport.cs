using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Models;

/// <summary>What one client rule did during Run on Existing (#949). A message is counted under the first rule that
/// took it and under no later one (#685), so the moved and deleted counts add up to the run's total.</summary>
/// <param name="RuleName">The rule's name as the user gave it.</param>
/// <param name="Moved">Messages the rule moved to another folder.</param>
/// <param name="Deleted">Messages the rule moved to Trash.</param>
/// <param name="Failed">Messages the rule matched but could not act on: a move or delete the server refused, or a
/// copy that failed and so stopped the rule. Without it a rule that failed reads as one that matched nothing.</param>
public sealed record ExistingMailRuleResult(string RuleName, int Moved, int Deleted, int Failed = 0)
{
    /// <summary>Whether the rule moved or deleted anything. Marking and copying leave the message in the Inbox and
    /// are not counted, as they never were.</summary>
    public bool TookAny => Moved > 0 || Deleted > 0;

    /// <summary>Whether the rule is worth naming in the report: it took something, or it tried and failed.</summary>
    public bool HasNews => TookAny || Failed > 0;
}

/// <summary>The outcome of running client rules on mail already in the local store (#949): how many stored Inbox
/// messages the run looked at, what each rule that could act on them did, and the messages that left the Inbox.</summary>
/// <param name="InspectedCount">Stored Inbox messages the rules ran on, across every account in the run.</param>
/// <param name="Rules">One entry per enabled rule that applied to an account in the run, in the order they ran,
/// including rules that took nothing.</param>
/// <param name="Removed">The messages moved or deleted, which the caller drops from its view.</param>
public sealed record ExistingMailRunReport(
    int InspectedCount,
    IReadOnlyList<ExistingMailRuleResult> Rules,
    IReadOnlyList<MailMessageSummary> Removed)
{
    /// <summary>A run that did nothing because there were no enabled rules.</summary>
    public static ExistingMailRunReport Empty { get; } = new(0, [], []);

    /// <summary>A run that did nothing because the caller could not give it any account's Inbox: the account is
    /// offline or has not connected yet. Told apart from <see cref="Empty"/> so the user is not told they have no
    /// rules when they do.</summary>
    public static ExistingMailRunReport NoInbox { get; } = new(0, [], []) { InboxUnavailable = true };

    /// <summary>True when no account in the run had an Inbox to run on, so no rule ran.</summary>
    public bool InboxUnavailable { get; init; }

    public int TotalMoved => Rules.Sum(r => r.Moved);
    public int TotalDeleted => Rules.Sum(r => r.Deleted);
    public int TotalFailed => Rules.Sum(r => r.Failed);
}
