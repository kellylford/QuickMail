namespace QuickMail.Models;

/// <summary>
/// The content of a user-submitted bug report. Holds what the user typed plus a snapshot of
/// non-sensitive UI state (<see cref="Context"/>) captured when the report window opened —
/// no log content, by explicit product decision (see docs/planning/bug-reporting-pm-dev-spec.md §4.2).
/// </summary>
public sealed class BugReportModel
{
    public string Summary { get; set; } = string.Empty;
    public string WhatHappened { get; set; } = string.Empty;
    public string WhatExpected { get; set; } = string.Empty;
    public string StepsToReproduce { get; set; } = string.Empty;

    /// <summary>UI-state snapshot for the Environment section; null if unavailable.</summary>
    public BugReportContext? Context { get; set; }
}

/// <summary>
/// Non-sensitive UI-state snapshot included in a bug report's Environment section. Captured
/// when the report window opens (state can change while the user types), not read live at
/// format time. Contains no message content, addresses, or credentials.
/// </summary>
public sealed class BugReportContext
{
    public string Theme { get; set; } = string.Empty;
    public string View { get; set; } = string.Empty;
    public string Sort { get; set; } = string.Empty;

    /// <summary>
    /// Where messages open (Reading pane / Tab / Window) — the Windowing "Message Open Mode"
    /// setting. Included because it has no PII and is often central to reproducing a report
    /// (e.g. attachment behaviour differs by mode; see issue #350).
    /// </summary>
    public string MessageOpenMode { get; set; } = string.Empty;

    /// <summary>
    /// The user's own accounts as a count plus the distinct protocols they connect over, with any
    /// shared mailboxes counted after them: <c>"2 (IMAP, Microsoft 365)"</c>, or
    /// <c>"1 (Microsoft 365), plus 2 shared mailboxes"</c>. Behaviour now diverges by backend in
    /// draft handling, folder semantics, rules, and attachment fetch, so a report that does not say
    /// which one is in use costs a source read to triage (issue #639, found triaging #637).
    /// <para>Protocols are listed in <see cref="BackendKind"/> order, not account order, so one
    /// setup always renders one line and two reports from the same user are comparable. A second
    /// producer of this field must keep that, or the field stops being comparable across reports —
    /// which is the whole of its value. <see cref="ViewModels.MainViewModel.DescribeAccounts"/> is
    /// the reference implementation.</para>
    /// <para>Counts and protocol kinds only — never an address, host name, or display name. This
    /// text is published verbatim into a public issue.</para>
    /// </summary>
    public string Accounts { get; set; } = string.Empty;
}
