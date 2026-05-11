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
