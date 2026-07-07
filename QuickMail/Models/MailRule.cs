using System;

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
    // (DisplayMemberPath only sets the visual). Without this the Rules list announces
    // "QuickMail.Models.MailRule" for every row. See CLAUDE.md.
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

    // ── Action ──────────────────────────────────────────────────────────────

    public RuleAction Action { get; set; } = RuleAction.MarkAsRead;

    /// <summary>
    /// Destination folder full name (e.g. "INBOX/Priority"). Required when
    /// Action == MoveToFolder; ignored otherwise.
    /// </summary>
    public string? TargetFolder { get; set; }
}

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
}
