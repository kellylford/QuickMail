using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

/// <summary>
/// A server-side (Exchange / Microsoft 365) Inbox rule as the UI sees it — the rules that run on
/// Microsoft's servers even when QuickMail is closed. Distinct from <see cref="MailRule"/>, which
/// runs inside QuickMail during sync. See <c>docs/planning/server-rules-pm-dev-spec.md</c>.
/// <para>
/// Holds the <b>editable common subset</b> of Graph's rule model as typed fields, plus the
/// <b>raw JSON</b> for conditions/actions/exceptions. Graph <c>PATCH</c> replaces those complex
/// objects wholesale, so editing is gated to rules we can fully represent
/// (<see cref="IsFullyEditable"/>) — otherwise saving would silently drop predicates the user set
/// in Outlook (spec §16, the central correctness risk).
/// </para>
/// </summary>
public sealed partial class ServerRuleModel : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Execution order on the server. Lower runs first.</summary>
    public int Sequence { get; set; }

    /// <summary>
    /// Observable so a list row's announced text updates in place when the rule is toggled — without
    /// re-inserting the item (which would disturb a screen reader's focus). <see cref="RowText"/> is
    /// what the list binds its accessible name to; notifying it fires the UIA change the reader needs.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowText))]
    private bool _isEnabled = true;

    /// <summary>The list row's accessible/display text (same as <see cref="ToString"/>), as a
    /// change-notifying property so a toggle re-announces the new state.</summary>
    public string RowText => ToString();

    /// <summary>Server-set: the rule cannot be modified (edit/delete blocked).</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Server-set: the rule is in an error state on the server.</summary>
    public bool HasError { get; set; }

    // ── Conditions (editable subset) ────────────────────────────────────────

    public string? SenderContains { get; set; }
    public List<string> FromAddresses { get; set; } = [];

    /// <summary>Recipients the message was sent to (Graph <c>sentToAddresses</c>).</summary>
    public List<string> SentToAddresses { get; set; } = [];

    public string? SubjectContains { get; set; }
    public string? BodyOrSubjectContains { get; set; }

    /// <summary>Text matched against the message body (Graph <c>bodyContains</c>).</summary>
    public string? BodyContains { get; set; }

    public bool SentToMe { get; set; }
    public bool SentOnlyToMe { get; set; }
    public bool HasAttachments { get; set; }

    /// <summary>"low", "normal", or "high". Null when not part of the rule.</summary>
    public string? Importance { get; set; }

    // ── Actions (editable subset) ───────────────────────────────────────────

    /// <summary>Graph folder ID (the same opaque ID QuickMail uses as a folder's FullName).</summary>
    public string? MoveToFolderId { get; set; }

    /// <summary>Display name for <see cref="MoveToFolderId"/>, resolved for prose. Not sent to Graph.</summary>
    public string? MoveToFolderName { get; set; }

    /// <summary>Graph folder ID for a copy action (copyToFolder).</summary>
    public string? CopyToFolderId { get; set; }

    /// <summary>Display name for <see cref="CopyToFolderId"/>, resolved for prose. Not sent to Graph.</summary>
    public string? CopyToFolderName { get; set; }

    public bool MarkAsRead { get; set; }

    /// <summary>"low", "normal", or "high". Null when the rule doesn't set importance.</summary>
    public string? MarkImportance { get; set; }

    /// <summary>Move to Deleted Items (Graph's <c>delete</c> action — not a permanent delete).</summary>
    public bool Delete { get; set; }

    public List<string> ForwardTo { get; set; } = [];
    public bool StopProcessingRules { get; set; }

    // ── Round-trip safety ───────────────────────────────────────────────────

    /// <summary>
    /// False when the rule uses any predicate or action outside the editable subset, or has
    /// exceptions. Such rules are view + toggle + delete only: editing them would replace the
    /// server's richer object with our narrower one and silently lose data (spec §16).
    /// </summary>
    public bool IsFullyEditable { get; set; } = true;

    /// <summary>
    /// Human-readable names of the predicates/actions that put this rule outside the editable
    /// subset. Surfaced to the user so they know what QuickMail can't represent yet.
    /// </summary>
    public List<string> UnsupportedFields { get; set; } = [];

    /// <summary>Raw Graph JSON, retained so a future version can merge rather than replace.</summary>
    public JsonElement? RawConditions { get; set; }
    public JsonElement? RawActions { get; set; }
    public JsonElement? RawExceptions { get; set; }

    // ── Presentation ────────────────────────────────────────────────────────

    /// <summary>
    /// A screen reader reads a data-bound Selector item's accessible name from <c>ToString()</c>
    /// (DisplayMemberPath only drives the visual), so this must carry the full list-row content:
    /// name, state, any markers, and a one-line rule summary. See CLAUDE.md.
    /// </summary>
    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed rule" : DisplayName;
        var parts = new List<string> { name, IsEnabled ? "enabled" : "disabled" };
        if (IsReadOnly) parts.Add("read-only");
        if (HasError) parts.Add("error");
        // Announced per row so a user arrowing the list hears which rules QuickMail can't fully edit
        // (they use conditions/actions outside the editable subset). Read-only rules already say so.
        if (!IsFullyEditable && !IsReadOnly) parts.Add("not editable in QuickMail");

        var summary = OneLineSummary();
        var head = string.Join(", ", parts);
        return string.IsNullOrEmpty(summary) ? head : $"{head}. {summary}";
    }

    /// <summary>"If subject contains 'invoice' → move to Archive" — the list-row summary.</summary>
    public string OneLineSummary()
    {
        var conditions = DescribeConditions();
        var actions = DescribeActions();
        if (conditions.Count == 0 && actions.Count == 0) return string.Empty;

        var lhs = conditions.Count == 0 ? "All messages" : "If " + string.Join(" and ", conditions);
        var rhs = actions.Count == 0 ? "do nothing" : string.Join(", then ", actions);
        return $"{lhs} → {rhs}";
    }

    /// <summary>
    /// Full prose for the detail region, including a note about anything outside the editable
    /// subset — "view fidelity, edit subset" (spec §6.3), so the user can see the whole rule even
    /// when QuickMail won't let them change it.
    /// </summary>
    public string DetailText()
    {
        var sb = new StringBuilder();
        var name = string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed rule" : DisplayName;
        sb.AppendLine($"{name} ({(IsEnabled ? "enabled" : "disabled")})");

        // Each condition / action on its own line under a header, so it's easy to read line by line
        // with a screen reader.
        AppendSection(sb, "Applies when:", DescribeConditions(), "all messages");
        AppendSection(sb, "Does:", DescribeActions(), "nothing");

        var reasons = new List<string>();
        if (IsReadOnly) reasons.Add("This rule is read-only on the server and cannot be changed.");
        if (HasError) reasons.Add("This rule is in an error state on the server.");
        if (!IsFullyEditable)
            reasons.Add(UnsupportedFields.Count > 0
                ? $"This rule uses conditions or actions QuickMail can't edit yet ({string.Join(", ", UnsupportedFields)}). You can enable, disable, or delete it here, or edit it in Outlook."
                : "This rule uses conditions or actions QuickMail can't edit yet. You can enable, disable, or delete it here, or edit it in Outlook.");

        if (reasons.Count > 0)
        {
            sb.AppendLine("Reason for block:");
            foreach (var r in reasons) sb.AppendLine(r);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Appends a titled section with one item per line. Non-empty: the header on its own line, then
    /// each item on its own line separated by ";" (no trailing ";" on the last). Empty: the header
    /// and the empty text on one line ("Applies when: all messages").
    /// </summary>
    private static void AppendSection(StringBuilder sb, string header, List<string> items, string emptyText)
    {
        if (items.Count == 0)
        {
            sb.AppendLine($"{header} {emptyText}");
            return;
        }

        sb.AppendLine(header);
        for (var i = 0; i < items.Count; i++)
            sb.AppendLine(i < items.Count - 1 ? $"{items[i]};" : items[i]);
    }

    private List<string> DescribeConditions()
    {
        var c = new List<string>();
        if (!string.IsNullOrWhiteSpace(SenderContains)) c.Add($"sender contains '{SenderContains}'");
        if (FromAddresses.Count > 0) c.Add($"from {string.Join(" or ", FromAddresses)}");
        if (SentToAddresses.Count > 0) c.Add($"sent to {string.Join(" or ", SentToAddresses)}");
        if (!string.IsNullOrWhiteSpace(SubjectContains)) c.Add($"subject contains '{SubjectContains}'");
        if (!string.IsNullOrWhiteSpace(BodyOrSubjectContains)) c.Add($"subject or body contains '{BodyOrSubjectContains}'");
        if (!string.IsNullOrWhiteSpace(BodyContains)) c.Add($"body contains '{BodyContains}'");
        if (SentToMe) c.Add("sent to me");
        if (SentOnlyToMe) c.Add("sent only to me");
        if (HasAttachments) c.Add("has attachments");
        if (!string.IsNullOrWhiteSpace(Importance)) c.Add($"importance is {Importance}");
        return c;
    }

    private List<string> DescribeActions()
    {
        var a = new List<string>();
        if (!string.IsNullOrWhiteSpace(MoveToFolderId))
            a.Add($"move to {(string.IsNullOrWhiteSpace(MoveToFolderName) ? "another folder" : MoveToFolderName)}");
        if (!string.IsNullOrWhiteSpace(CopyToFolderId))
            a.Add($"copy to {(string.IsNullOrWhiteSpace(CopyToFolderName) ? "another folder" : CopyToFolderName)}");
        if (MarkAsRead) a.Add("mark as read");
        if (!string.IsNullOrWhiteSpace(MarkImportance)) a.Add($"set importance to {MarkImportance}");
        if (Delete) a.Add("move to Deleted Items");
        if (ForwardTo.Count > 0) a.Add($"forward to {string.Join(", ", ForwardTo)}");
        if (StopProcessingRules) a.Add("stop processing more rules");
        return a;
    }
}
