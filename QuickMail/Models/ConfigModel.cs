using System;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>Global application configuration plus optional per-account overrides.</summary>
public class ConfigModel
{
    // ── Global settings ──────────────────────────────────────────────────────────

    /// <summary>Number of body-preview lines shown in the message list. 0 = disabled.</summary>
    public int PreviewLines { get; set; } = 3;

    /// <summary>Whether to display the message-status column in the message list.</summary>
    public bool ShowMessageStatus { get; set; } = true;

    /// <summary>
    /// How to display the message list.
    /// Values: "messages" (flat list), "conversations" (grouped by subject), "from" (grouped by sender).
    /// </summary>
    public string ViewMode { get; set; } = "messages";

    /// <summary>
    /// How many days of mail to sync. 0 = sync all mail (no date filter).
    /// Supported values: 7, 30, 180, 365, or 0 (all).
    /// </summary>
    public int SyncDays { get; set; } = 30;

    // ── Custom hotkey overrides ──────────────────────────────────────────────────

    /// <summary>User-defined keyboard shortcut overrides, stored in hotkeys.json.</summary>
    public List<HotkeyBinding> CustomHotkeys { get; set; } = [];

    // ── Per-account overrides ─────────────────────────────────────────────────────

    public Dictionary<Guid, AccountOverrideConfig> Accounts { get; set; } = [];

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the effective PreviewLines value for the given account,
    /// applying the per-account override when present.
    /// </summary>
    public int GetPreviewLines(Guid accountId)
    {
        if (Accounts.TryGetValue(accountId, out var ovr) && ovr.PreviewLines.HasValue)
            return ovr.PreviewLines.Value;
        return PreviewLines;
    }
}

/// <summary>Per-account configuration overrides. Only set fields that differ from global defaults.</summary>
public class AccountOverrideConfig
{
    /// <summary>Override the global PreviewLines for this account. Null = use global setting.</summary>
    public int? PreviewLines { get; set; }
}
