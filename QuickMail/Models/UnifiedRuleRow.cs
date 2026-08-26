using System.Collections.Generic;
using System.Linq;
using System.Text;

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

    // The move-to target's human name, resolved by the VM from the account's folder cache. A Graph
    // client rule stores an opaque folder id in MailRule.TargetFolder (e.g. "AQMkAD…"), so printing that
    // raw makes the summary unreadable; the VM looks the id up and passes the DisplayName ("Deleted
    // Items") here. Null when there is nothing to resolve (no move action, or the folder is unknown), and
    // the summary then falls back to the raw TargetFolder — still right for IMAP, whose FullName reads.
    private readonly string? _targetFolderDisplay;

    private UnifiedRuleRow(RuleRunsWhere runsWhere, ServerRuleModel? server, MailRule? client, bool showFieldLabels, string? targetFolderDisplay)
    {
        RunsWhere = runsWhere;
        Server = server;
        Client = client;
        _showFieldLabels = showFieldLabels;
        _targetFolderDisplay = targetFolderDisplay;
    }

    public static UnifiedRuleRow ForServer(ServerRuleModel rule, bool showFieldLabels = false)
        => new(RuleRunsWhere.Server, rule, null, showFieldLabels, targetFolderDisplay: null);
    public static UnifiedRuleRow ForClient(MailRule rule, bool showFieldLabels = false, string? targetFolderDisplay = null)
        => new(RuleRunsWhere.Client, null, rule, showFieldLabels, targetFolderDisplay);

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
            var where = RunsWhere == RuleRunsWhere.Server ? "on server" : "on client";
            var state = IsEnabled ? "enabled" : "disabled";
            // "Show field labels in the rules list" (RuleListShowFieldLabels): off reads the values run
            // together, on prefixes each with its field name, mirroring the client window's "Rule …".
            var head = _showFieldLabels
                ? $"Rule {name}, runs {where}, status {state}"
                : $"{name}, {where}, {state}";
            var summary = RunsWhere == RuleRunsWhere.Server ? Server!.OneLineSummary() : ClientSummary(Client!, _targetFolderDisplay);
            return string.IsNullOrEmpty(summary) ? head : $"{head}. {summary}";
        }
    }

    public override string ToString() => RowText;

    /// <summary>Fuller prose for the detail pane. A client rule reads with the same "Applies when: /
    /// Does:" section structure as a server rule (<see cref="ServerRuleModel.DetailText"/>), so the two
    /// panes feel the same. It carries no "runs client-side" line: where the rule runs is already spoken
    /// as the row label and as the account hint, and the server pane has no equivalent line either.</summary>
    public string DetailText
    {
        get
        {
            if (RunsWhere == RuleRunsWhere.Server) return Server!.DetailText();

            var name = string.IsNullOrWhiteSpace(Name) ? "Unnamed rule" : Name;
            var sb = new StringBuilder();
            sb.AppendLine($"{name} ({(IsEnabled ? "enabled" : "disabled")})");
            AppendSection(sb, "Applies when:", ClientConditions(Client!), "all messages");
            AppendSection(sb, "Does:", ClientActions(Client!, _targetFolderDisplay), "nothing");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>"If subject contains 'x' → move to Archive" for a client rule — the one-line list-row
    /// summary, mirroring <see cref="ServerRuleModel.OneLineSummary"/> so both kinds read the same way.</summary>
    private static string ClientSummary(MailRule r, string? targetFolderDisplay)
    {
        var conditions = ClientConditions(r);
        var actions = ClientActions(r, targetFolderDisplay);
        var lhs = conditions.Count == 0 ? "All messages" : "If " + string.Join(" and ", conditions);
        var rhs = actions.Count == 0 ? "do nothing" : string.Join(", ", actions);
        return $"{lhs} → {rhs}";
    }

    private static List<string> ClientConditions(MailRule r)
    {
        var conditions = new List<string>();
        if (r.UseFromCondition && !string.IsNullOrWhiteSpace(r.FromContains)) conditions.Add($"from contains '{r.FromContains}'");
        if (r.UseToCondition && !string.IsNullOrWhiteSpace(r.ToContains)) conditions.Add($"to contains '{r.ToContains}'");
        if (r.UseSubjectCondition && !string.IsNullOrWhiteSpace(r.SubjectContains)) conditions.Add($"subject contains '{r.SubjectContains}'");
        if (r.UseBodyCondition && !string.IsNullOrWhiteSpace(r.BodyContains)) conditions.Add($"body contains '{r.BodyContains}'");
        if (r.MustHaveAttachments) conditions.Add("has attachments");
        return conditions;
    }

    private static List<string> ClientActions(MailRule r, string? targetFolderDisplay)
    {
        // Prefer the VM-resolved folder name (a Graph target is stored as an opaque id); fall back to the
        // raw TargetFolder, which still reads for IMAP (its FullName is the path).
        var target = !string.IsNullOrWhiteSpace(targetFolderDisplay) ? targetFolderDisplay
                   : !string.IsNullOrWhiteSpace(r.TargetFolder) ? r.TargetFolder
                   : "a folder";
        var action = r.Action switch
        {
            RuleAction.MarkAsRead => "mark as read",
            RuleAction.MarkAsUnread => "mark as unread",
            RuleAction.MoveToFolder => $"move to {target}",
            RuleAction.Delete => "move to Trash",
            _ => r.Action.ToString(),
        };
        return [action];
    }

    // Mirrors ServerRuleModel.AppendSection so the client detail pane reads with the same shape: a
    // non-empty section is the header on its own line then one item per line (";"-separated, none
    // trailing); an empty section is "header emptyText" on one line ("Applies when: all messages").
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
}
