using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

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
public sealed class ServerRuleModel
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Execution order on the server. Lower runs first.</summary>
    public int Sequence { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Server-set: the rule cannot be modified (edit/delete blocked).</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Server-set: the rule is in an error state on the server.</summary>
    public bool HasError { get; set; }

    // ── Conditions (editable subset) ────────────────────────────────────────

    public string? SenderContains { get; set; }
    public List<string> FromAddresses { get; set; } = [];
    public string? SubjectContains { get; set; }
    public string? BodyOrSubjectContains { get; set; }
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
        sb.Append(string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed rule" : DisplayName);
        sb.Append(IsEnabled ? " (enabled)" : " (disabled)");
        sb.AppendLine();

        var conditions = DescribeConditions();
        sb.AppendLine(conditions.Count == 0
            ? "Applies to: all messages."
            : "Applies when: " + string.Join("; ", conditions) + ".");

        var actions = DescribeActions();
        sb.AppendLine(actions.Count == 0
            ? "Does: nothing."
            : "Does: " + string.Join("; ", actions) + ".");

        if (IsReadOnly) sb.AppendLine("This rule is read-only on the server and cannot be changed.");
        if (HasError) sb.AppendLine("This rule is in an error state on the server.");

        if (!IsFullyEditable)
        {
            sb.Append("This rule uses ");
            sb.Append(UnsupportedFields.Count > 0
                ? "conditions or actions QuickMail can't edit yet (" + string.Join(", ", UnsupportedFields) + ")"
                : "conditions or actions QuickMail can't edit yet");
            sb.AppendLine(". You can enable, disable, or delete it here, or edit it in Outlook.");
        }

        return sb.ToString().TrimEnd();
    }

    private List<string> DescribeConditions()
    {
        var c = new List<string>();
        if (!string.IsNullOrWhiteSpace(SenderContains)) c.Add($"sender contains '{SenderContains}'");
        if (FromAddresses.Count > 0) c.Add($"from {string.Join(" or ", FromAddresses)}");
        if (!string.IsNullOrWhiteSpace(SubjectContains)) c.Add($"subject contains '{SubjectContains}'");
        if (!string.IsNullOrWhiteSpace(BodyOrSubjectContains)) c.Add($"subject or body contains '{BodyOrSubjectContains}'");
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
        if (MarkAsRead) a.Add("mark as read");
        if (!string.IsNullOrWhiteSpace(MarkImportance)) a.Add($"set importance to {MarkImportance}");
        if (Delete) a.Add("move to Deleted Items");
        if (ForwardTo.Count > 0) a.Add($"forward to {string.Join(", ", ForwardTo)}");
        if (StopProcessingRules) a.Add("stop processing more rules");
        return a;
    }
}
