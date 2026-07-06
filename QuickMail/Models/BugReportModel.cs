namespace QuickMail.Models;

/// <summary>
/// The content of a user-submitted bug report. Contains only what the user typed —
/// no log content, by explicit product decision (see docs/planning/bug-reporting-pm-dev-spec.md §4.2).
/// </summary>
public sealed class BugReportModel
{
    public string Summary { get; set; } = string.Empty;
    public string WhatHappened { get; set; } = string.Empty;
    public string WhatExpected { get; set; } = string.Empty;
    public string StepsToReproduce { get; set; } = string.Empty;
}
