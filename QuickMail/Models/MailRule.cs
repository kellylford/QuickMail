using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace QuickMail.Models;

/// <summary>
/// A user-defined rule that automatically performs an action on incoming messages
/// that match a set of conditions. All populated conditions are ANDed together.
/// An empty/null condition matches everything.
/// <para>
/// A condition on addresses may list several, and any one of them matching satisfies it (#682) — the
/// "or" is inside the one condition, never across two. Read those through <see cref="FromValues"/> and
/// <see cref="ToValues"/> rather than the fields, which also carry what a QuickMail from before #682
/// matches on.
/// </para>
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

    /// <summary>When true, the From condition is active — <see cref="FromAddresses"/> where the rule has
    /// them, otherwise <see cref="FromContains"/>.</summary>
    public bool UseFromCondition { get; set; } = true;

    /// <summary>
    /// Case-insensitive substring match against MailMessageSummary.From. When <see cref="FromAddresses"/>
    /// holds the condition, this carries its first address and is not read: it is what a QuickMail from
    /// before #682 matches on, and matching one of the addresses can only act on less mail than matching
    /// any of them.
    /// </summary>
    public string? FromContains { get; set; }

    /// <summary>
    /// The From addresses the rule matches, any one of which is enough (#682). Null — the common case —
    /// when <see cref="FromContains"/> alone says what the rule matches; set only when it cannot, which is
    /// more than one address, or one address alongside <see cref="SenderContains"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FromAddresses { get; set; }

    /// <summary>When true, the <see cref="SenderContains"/> condition is active.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSenderCondition { get; set; }

    /// <summary>
    /// The editor's "Sender contains": a substring match against From, ANDed with any
    /// <see cref="FromAddresses"/> the rule also lists (#682).
    /// <para>
    /// <b>Where it is set and <see cref="FromAddresses"/> is null, <see cref="FromContains"/> is this
    /// value's mirror</b> and is not read — <see cref="FromValues"/> is what enforces that. The mirror
    /// exists so a QuickMail from before #682, which has no sender field, still tests the sender rather
    /// than nothing at all.
    /// </para>
    /// <para>
    /// Alongside addresses there is no mirror to be had: the two are ANDed, and one old field cannot
    /// hold both. The addresses take it, so such a build tests the addresses and not the sender — the
    /// one shape in which it acts on mail this build would leave alone. It is a narrow one (a sender
    /// substring added to a list of addresses) and the alternative is worse: a mirror of the sender
    /// would drop the address list instead, which is the broader condition of the two.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SenderContains { get; set; }

    /// <summary>When true, the Sent-to condition is active — <see cref="SentToAddresses"/> where the rule
    /// has them, otherwise <see cref="ToContains"/>.</summary>
    public bool UseToCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.To. Carries the first of
    /// <see cref="SentToAddresses"/> when those hold the condition, as <see cref="FromContains"/> does.</summary>
    public string? ToContains { get; set; }

    /// <summary>The Sent-to addresses the rule matches, any one of which is enough (#682). Null unless the
    /// rule lists more than one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SentToAddresses { get; set; }

    /// <summary>When true, the SubjectContains condition is active.</summary>
    public bool UseSubjectCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.Subject.</summary>
    public string? SubjectContains { get; set; }

    /// <summary>
    /// When true, <see cref="SubjectContains"/> matches the body as well as the subject — the editor's
    /// "Subject or body contains" (#682). It rides on the subject condition rather than a field of its own
    /// so that a QuickMail from before #682 reads the rule as a subject match: narrower than the rule
    /// really is, which is the safe direction. That leaves one subject slot, so a rule cannot test the
    /// subject and the subject-or-body separately; the editor refuses to save one that tries.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SubjectAlsoMatchesBody { get; set; }

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

    /// <summary>
    /// What the From condition looks for — a message matches when From contains <b>any one</b> of these.
    /// Empty when the rule doesn't test From, and empty when <see cref="FromContains"/> is only mirroring
    /// <see cref="SenderContains"/> for an older build, which that condition covers instead. One
    /// definition for the engine, the rule summaries and the editor, so none of them can read these
    /// fields differently from the others.
    /// </summary>
    public IReadOnlyList<string> FromValues()
        => FromContainsMirrorsSender ? [] : ConditionValues(UseFromCondition, FromAddresses, FromContains);

    /// <summary>
    /// True when <see cref="FromContains"/> holds nothing but a copy of <see cref="SenderContains"/>,
    /// put there for a QuickMail from before #682 (see that field). The one place this build decides
    /// it, so the engine, the rule summaries and the editor cannot disagree about which condition a
    /// rule's oldest field belongs to.
    /// </summary>
    [JsonIgnore]
    public bool FromContainsMirrorsSender => SenderContains is not null && FromAddresses is not { Count: > 0 };

    /// <summary>What the Sent-to condition looks for, read as <see cref="FromValues"/> is.</summary>
    public IReadOnlyList<string> ToValues() => ConditionValues(UseToCondition, SentToAddresses, ToContains);

    private static IReadOnlyList<string> ConditionValues(bool use, List<string>? addresses, string? single)
    {
        if (!use) return [];
        if (addresses is { Count: > 0 }) return [.. addresses.Where(a => !string.IsNullOrWhiteSpace(a))];
        return string.IsNullOrEmpty(single) ? [] : [single];
    }

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
