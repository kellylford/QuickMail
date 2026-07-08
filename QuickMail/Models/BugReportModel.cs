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
}
