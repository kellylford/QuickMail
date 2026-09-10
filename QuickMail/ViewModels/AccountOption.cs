using System;

namespace QuickMail.ViewModels;

/// <summary>
/// An entry in the rules window's account picker. Selector-bound, so <see cref="ToString"/> is its
/// accessible name (CLAUDE.md; pinned in SelectorItemAccessibilityTests).
/// </summary>
public class AccountOption
{
    public Guid? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}
