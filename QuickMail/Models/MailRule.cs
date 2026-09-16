using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace QuickMail.Models;

/// <summary>
/// A user-defined rule that automatically performs an action on incoming messages
/// that match a set of conditions. All populated conditions are ANDed together.
/// An empty/null condition matches everything.
/// </summary>
public class MailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-visible name for this rule. Required — validated on save.</summary>
    public string Name { get; set; } = string.Empty;

    // A screen reader reads a data-bound Selector item's UIA Name from ToString()
    // (DisplayMemberPath only sets the visual), so without this any Selector bound to MailRule
    // would announce "QuickMail.Models.MailRule" for every row. See CLAUDE.md. The rules window
    // itself lists UnifiedRuleRow, which carries its own row name.
    public override string ToString() => Name;

    /// <summary>When false, the rule is skipped during evaluation.</summary>
    public bool IsEnabled { get; set; } = true;

    // ── Conditions (all ANDed) ──────────────────────────────────────────────

    /// <summary>When true, the FromContains condition is active.</summary>
    public bool UseFromCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.From.</summary>
    public string? FromContains { get; set; }

    /// <summary>When true, the ToContains condition is active.</summary>
    public bool UseToCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.To.</summary>
    public string? ToContains { get; set; }

    /// <summary>When true, the SubjectContains condition is active.</summary>
    public bool UseSubjectCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.Subject.</summary>
    public string? SubjectContains { get; set; }

    /// <summary>When true, the BodyContains condition is active.</summary>
    public bool UseBodyCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.Preview.</summary>
    public string? BodyContains { get; set; }

    /// <summary>When true, only messages with HasAttachments == true match.</summary>
    public bool MustHaveAttachments { get; set; }

    /// <summary>
    /// Scope the rule to one account. Null means the rule applies to all accounts.
    /// </summary>
    public Guid? AccountId { get; set; }

    // ── Actions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule's main action, and its only one unless <see cref="Actions"/> lists more. With several, it is
    /// the one that runs last (see <see cref="AllActions"/>) — moving or deleting, where the rule does
    /// either — so a QuickMail from before #682, which reads only this field, still does what matters most.
    /// </summary>
    public RuleAction Action { get; set; } = RuleAction.MarkAsRead;

    /// <summary>
    /// Every action, when the rule has more than one (#682); null when <see cref="Action"/> is all it does.
    /// Read the rule's actions through <see cref="AllActions"/>, which covers both.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RuleAction>? Actions { get; set; }

    /// <summary>
    /// Destination folder full name (e.g. "INBOX/Priority") for MoveToFolder; ignored when the rule
    /// doesn't move. A Graph account stores the folder's id instead.
    /// </summary>
    public string? TargetFolder { get; set; }

    /// <summary>Destination folder for CopyToFolder, stored the same way as <see cref="TargetFolder"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CopyTargetFolder { get; set; }

    /// <summary>
    /// What the rule does, in the order it does it: marking read or unread first, then copying, then moving
    /// or deleting. Once a message is moved or deleted it is no longer in the folder the others act on.
    /// </summary>
    public IReadOnlyList<RuleAction> AllActions()
        => (Actions is { Count: > 0 } ? Actions : [Action]).Distinct().OrderBy(RunOrder).ToList();

    public bool Does(RuleAction action) => AllActions().Contains(action);

    /// <summary>True when the rule takes matching mail out of the folder: it moves or deletes it.</summary>
    public bool RemovesFromFolder() => Does(RuleAction.MoveToFolder) || Does(RuleAction.Delete);

    private static int RunOrder(RuleAction action) => action switch
    {
        RuleAction.MarkAsRead or RuleAction.MarkAsUnread => 0,
        RuleAction.CopyToFolder => 1,
        _ => 2,
    };
}

/// <summary>A client-side rule's action. Stored in rules.json by number, so a new one goes on the end.</summary>
public enum RuleAction
{
    /// <summary>Mark the message as read (IMAP \Seen flag).</summary>
    MarkAsRead,

    /// <summary>Mark the message as unread (remove IMAP \Seen flag).</summary>
    MarkAsUnread,

    /// <summary>Move the message to TargetFolder via IMAP MOVE.</summary>
    MoveToFolder,

    /// <summary>Move the message to the Trash folder.</summary>
    Delete,

    /// <summary>Copy the message to CopyTargetFolder, leaving it where it is.</summary>
    CopyToFolder,
}
