using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Models;

/// <summary>Where a rule executes: on the Microsoft 365 server, or inside QuickMail.</summary>
public enum RuleRunsWhere { Server, Client }

/// <summary>
/// One row in the unified per-account rules list (spec §20.7). Wraps exactly one of a server
/// (<see cref="ServerRuleModel"/>) or client (<see cref="MailRule"/>) rule and presents a single,
/// consistent accessible line that also states where the rule runs. The list is per-account, so the
/// row carries no account label.
/// </summary>
public sealed class UnifiedRuleRow
{
    private readonly bool _showFieldLabels;

    private UnifiedRuleRow(RuleRunsWhere runsWhere, ServerRuleModel? server, MailRule? client, bool showFieldLabels)
    {
        RunsWhere = runsWhere;
        Server = server;
        Client = client;
        _showFieldLabels = showFieldLabels;
    }

    public static UnifiedRuleRow ForServer(ServerRuleModel rule, bool showFieldLabels = false)
        => new(RuleRunsWhere.Server, rule, null, showFieldLabels);
    public static UnifiedRuleRow ForClient(MailRule rule, bool showFieldLabels = false)
        => new(RuleRunsWhere.Client, null, rule, showFieldLabels);

    public RuleRunsWhere RunsWhere { get; }

    /// <summary>The wrapped server rule, or null for a client row.</summary>
    public ServerRuleModel? Server { get; }

    /// <summary>The wrapped client rule, or null for a server row.</summary>
    public MailRule? Client { get; }

    public string Name => RunsWhere == RuleRunsWhere.Server ? Server!.DisplayName : Client!.Name;

    public bool IsEnabled => RunsWhere == RuleRunsWhere.Server ? Server!.IsEnabled : Client!.IsEnabled;

    /// <summary>
    /// The list row's accessible/display text: name, where it runs, enabled state, and a one-line
    /// summary. A screen reader reads a Selector item's name from <see cref="ToString"/>, so that
    /// forwards here (see CLAUDE.md).
    /// </summary>
    public string RowText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "Unnamed rule" : Name;
            var where = RunsWhere == RuleRunsWhere.Server ? "on server" : "in QuickMail";
            var state = IsEnabled ? "enabled" : "disabled";
            // "Show field labels in the rules list" (RuleListShowFieldLabels): off reads the values run
            // together, on prefixes each with its field name, mirroring the client window's "Rule …".
            var head = _showFieldLabels
                ? $"Rule {name}, runs {where}, status {state}"
                : $"{name}, {where}, {state}";
            var summary = RunsWhere == RuleRunsWhere.Server ? Server!.OneLineSummary() : ClientSummary(Client!);
            return string.IsNullOrEmpty(summary) ? head : $"{head}. {summary}";
        }
    }

    public override string ToString() => RowText;

    /// <summary>Fuller prose for the detail pane. Server rules reuse their rich detail; client rules
    /// get a short "runs in QuickMail" block with the same one-line summary.</summary>
    public string DetailText
    {
        get
        {
            if (RunsWhere == RuleRunsWhere.Server) return Server!.DetailText();
            var name = string.IsNullOrWhiteSpace(Name) ? "Unnamed rule" : Name;
            var state = IsEnabled ? "enabled" : "disabled";
            return $"{name} ({state})\nRuns in QuickMail (only while QuickMail is open).\n{ClientSummary(Client!)}";
        }
    }

    /// <summary>"If subject contains 'x' → move to Archive" for a client rule — mirrors
    /// <see cref="ServerRuleModel.OneLineSummary"/> so both kinds read the same way.</summary>
    private static string ClientSummary(MailRule r)
    {
        var conditions = new List<string>();
        if (r.UseFromCondition && !string.IsNullOrWhiteSpace(r.FromContains)) conditions.Add($"from contains '{r.FromContains}'");
        if (r.UseToCondition && !string.IsNullOrWhiteSpace(r.ToContains)) conditions.Add($"to contains '{r.ToContains}'");
        if (r.UseSubjectCondition && !string.IsNullOrWhiteSpace(r.SubjectContains)) conditions.Add($"subject contains '{r.SubjectContains}'");
        if (r.UseBodyCondition && !string.IsNullOrWhiteSpace(r.BodyContains)) conditions.Add($"body contains '{r.BodyContains}'");
        if (r.MustHaveAttachments) conditions.Add("has attachments");

        var action = r.Action switch
        {
            RuleAction.MarkAsRead => "mark as read",
            RuleAction.MarkAsUnread => "mark as unread",
            RuleAction.MoveToFolder => $"move to {(string.IsNullOrWhiteSpace(r.TargetFolder) ? "a folder" : r.TargetFolder)}",
            RuleAction.Delete => "move to Trash",
            _ => r.Action.ToString(),
        };

        var lhs = conditions.Count == 0 ? "All messages" : "If " + string.Join(" and ", conditions);
        return $"{lhs} → {action}";
    }
}
