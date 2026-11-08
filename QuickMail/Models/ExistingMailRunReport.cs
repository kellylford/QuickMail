using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Models;

/// <summary>What one client rule took out of the Inbox during Run on Existing (#949). A message is counted under the
/// first rule that took it and under no later one (#685), so these add up to the run's total.</summary>
/// <param name="RuleName">The rule's name as the user gave it.</param>
/// <param name="Moved">Messages the rule moved to another folder.</param>
/// <param name="Deleted">Messages the rule moved to Trash.</param>
public sealed record ExistingMailRuleResult(string RuleName, int Moved, int Deleted)
{
    /// <summary>Whether the rule moved or deleted anything. Marking and copying leave the message in the Inbox and
    /// are not counted, as they never were.</summary>
    public bool TookAny => Moved > 0 || Deleted > 0;
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
    /// <summary>A run that did nothing: no enabled rules, or nothing stored to run them on.</summary>
    public static ExistingMailRunReport Empty { get; } = new(0, [], []);

    public int TotalMoved => Rules.Sum(r => r.Moved);
    public int TotalDeleted => Rules.Sum(r => r.Deleted);
}
