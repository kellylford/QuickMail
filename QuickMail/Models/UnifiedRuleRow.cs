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
    // Items") here. Null when the VM chose not to resolve — no move action, an IMAP account (its
    // TargetFolder is already the readable path, deliberately left unresolved), or the folder isn't in
    // the cache.
    private readonly string? _targetFolderDisplay;

    // Whether the rule's folders (the one it moves to, the one it copies to) are opaque ids (a Graph account)
    // rather than readable paths (IMAP). When true and no display name resolved — the folder isn't cached, or its
    // id drifted (#366) — the summary must NOT fall back to the raw id; it says "another folder", as server rules
    // already do. False leaves the raw folder as the fallback, which reads fine for IMAP.
    private readonly bool _folderIsOpaque;

    // The copy-to target's human name, resolved the same way as the move-to target above (#682).
    private readonly string? _copyFolderDisplay;

    private UnifiedRuleRow(RuleRunsWhere runsWhere, ServerRuleModel? server, MailRule? client, bool showFieldLabels, string? targetFolderDisplay, bool folderIsOpaque, string? copyFolderDisplay)
    {
        RunsWhere = runsWhere;
        Server = server;
        Client = client;
        _showFieldLabels = showFieldLabels;
        _targetFolderDisplay = targetFolderDisplay;
        _folderIsOpaque = folderIsOpaque;
        _copyFolderDisplay = copyFolderDisplay;
    }

    public static UnifiedRuleRow ForServer(ServerRuleModel rule, bool showFieldLabels = false)
        => new(RuleRunsWhere.Server, rule, null, showFieldLabels, targetFolderDisplay: null, folderIsOpaque: false, copyFolderDisplay: null);
    public static UnifiedRuleRow ForClient(MailRule rule, bool showFieldLabels = false, string? targetFolderDisplay = null, bool folderIsOpaque = false, string? copyFolderDisplay = null)
        => new(RuleRunsWhere.Client, null, rule, showFieldLabels, targetFolderDisplay, folderIsOpaque, copyFolderDisplay);

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
            var summary = RunsWhere == RuleRunsWhere.Server ? Server!.OneLineSummary() : ClientSummary();
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
            AppendSection(sb, "Does:", ClientActions(), "nothing");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>"If subject contains 'x' → move to Archive" for a client rule — the one-line list-row
    /// summary, mirroring <see cref="ServerRuleModel.OneLineSummary"/> so both kinds read the same way.</summary>
    private string ClientSummary()
    {
        var conditions = ClientConditions(Client!);
        var actions = ClientActions();
        var lhs = conditions.Count == 0 ? "All messages" : "If " + string.Join(" and ", conditions);
        var rhs = actions.Count == 0 ? "do nothing" : string.Join(", ", actions);
        return $"{lhs} → {rhs}";
    }

    private static List<string> ClientConditions(MailRule r)
    {
        var conditions = new List<string>();
        // A rule may list several addresses, any one of which matches (#682); read through the model's own
        // accessor so the summary can't describe the condition differently from the engine that runs it.
        if (r.FromValues() is { Count: > 0 } from) conditions.Add($"from contains {Quoted(from)}");
        if (r.UseSenderCondition && !string.IsNullOrWhiteSpace(r.SenderContains))
            conditions.Add($"sender contains '{r.SenderContains}'");
        if (r.ToValues() is { Count: > 0 } to) conditions.Add($"to contains {Quoted(to)}");
        if (r.UseSubjectCondition && !string.IsNullOrWhiteSpace(r.SubjectContains))
            conditions.Add(r.SubjectAlsoMatchesBody
                ? $"subject or body contains '{r.SubjectContains}'"
                : $"subject contains '{r.SubjectContains}'");
        if (r.UseBodyCondition && !string.IsNullOrWhiteSpace(r.BodyContains)) conditions.Add($"body contains '{r.BodyContains}'");
        if (r.MustHaveAttachments) conditions.Add("has attachments");
        return conditions;
    }

    /// <summary>
    /// The values one condition accepts. A single value reads as it always has, "'a@x.com'"; several read
    /// as "any of 'a@x.com', 'b@y.com'". "a or b" would be shorter, but spoken next to the "and" that
    /// joins the conditions it leaves the grouping ambiguous — and the reading that wins is the one where
    /// the rule acts more widely than it does.
    /// </summary>
    private static string Quoted(IReadOnlyList<string> values)
        => values.Count == 1
            ? $"'{values[0]}'"
            : "any of " + string.Join(", ", values.Select(v => $"'{v}'"));

    /// <summary>What the rule does, in the order it does it.</summary>
    private List<string> ClientActions()
    {
        var r = Client!;
        return r.AllActions().Select(action => action switch
        {
            RuleAction.MarkAsRead => "mark as read",
            RuleAction.MarkAsUnread => "mark as unread",
            RuleAction.CopyToFolder => $"copy to {Folder(_copyFolderDisplay, r.CopyTargetFolder)}",
            RuleAction.MoveToFolder => $"move to {Folder(_targetFolderDisplay, r.TargetFolder)}",
            RuleAction.Delete => "move to Trash",
            _ => action.ToString(),
        }).ToList();

        // Prefer the VM-resolved folder name. Fall back to the raw folder only when it is readable (IMAP,
        // whose folder is the folder path); an opaque Graph id that didn't resolve (folder not cached, or its
        // id drifted per #366) reads "another folder" rather than the "AQMkAD…" blob, matching how a server
        // rule renders an unresolved target.
        string Folder(string? display, string? raw)
            => !string.IsNullOrWhiteSpace(display) ? display
             : _folderIsOpaque ? "another folder"
             : !string.IsNullOrWhiteSpace(raw) ? raw
             : "a folder";
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
