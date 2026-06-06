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
    /// How to sort the message list or groups.
    /// Values: "dateDesc", "dateAsc", "alphaAsc", "alphaDesc", "countDesc", "countAsc".
    /// </summary>
    public string Sort { get; set; } = "dateDesc";

    /// <summary>
    /// How many days of mail to sync. 0 = sync all mail (no date filter).
    /// Supported values: 7, 30, 180, 365, or 0 (all).
    /// </summary>
    public int SyncDays { get; set; } = 30;

    /// <summary>Maximum simultaneous IMAP connections QuickMail may open per account.</summary>
    public int MaxImapConnectionsPerAccount { get; set; } = 6;

    /// <summary>
    /// Number of messages to fetch on the initial sync of a folder (when no local UID exists).
    /// Default is 500. Set to 0 to fetch all messages in the folder.
    /// </summary>
    public int InitialSyncCount { get; set; } = 500;

    // ── Screen reader announcement settings ──────────────────────────────────────

    /// <summary>Master switch for all custom screen reader announcements from QuickMail code.</summary>
    public bool CustomAnnouncements { get; set; } = true;

    /// <summary>Announce instructional hints (e.g. how to use the search box).</summary>
    public bool AnnounceHints { get; set; } = true;

    /// <summary>Announce background loading and sync progress.</summary>
    public bool AnnounceStatus { get; set; } = true;

    /// <summary>Announce action results (search counts, move/delete confirmations).</summary>
    public bool AnnounceResults { get; set; } = true;

    /// <summary>Announce spelling errors while typing (before the word is complete). Default off.</summary>
    public bool AnnounceSpellingWhileTyping { get; set; } = false;

    /// <summary>Announce spelling errors when the caret moves into a misspelled word during navigation.</summary>
    public bool AnnounceSpellingWhileNavigating { get; set; } = true;

    /// <summary>Announce spelling suggestions when a misspelling is announced.</summary>
    public bool AnnounceSpellingSuggestions { get; set; } = true;

    // ── Advanced ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// How log lines are formatted.
    /// "actionFirst" (default): message then timestamp — easier to scan with a screen reader.
    /// "timeFirst": timestamp then message — the historical format.
    /// </summary>
    public string LogFormat { get; set; } = "actionFirst";

    // ── Tutorial ──────────────────────────────────────────────────────────────────

    /// <summary>Show a confirmation dialog before emptying trash. Default on.</summary>
    public bool ConfirmEmptyTrash { get; set; } = true;

    /// <summary>Whether the user has completed the first-run keyboard tutorial.</summary>
    public bool TutorialCompleted { get; set; } = false;

    // ── Custom hotkey overrides ──────────────────────────────────────────────────

    /// <summary>User-defined keyboard shortcut overrides, stored in hotkeys.json.</summary>
    public List<HotkeyBinding> CustomHotkeys { get; set; } = [];

    // ── Feature flags ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Raw key/value pairs from the config.ini [features] section (e.g. "GraphBackend" = "true").
    /// Resolved through <see cref="QuickMail.Services.IFeatureGate"/>; absent keys fall back to built-in defaults.
    /// </summary>
    public Dictionary<string, string> Features { get; set; } = [];

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

    // ── ViewMode / Sort serialization helpers ─────────────────────────────────────

    /// <summary>Converts a config-string ViewMode to the enum (case-insensitive).</summary>
    public static Models.ViewMode ParseViewMode(string? s) => (s?.ToLowerInvariant()) switch
    {
        "conversations" => Models.ViewMode.Conversations,
        "from"          => Models.ViewMode.From,
        "to"            => Models.ViewMode.To,
        _               => Models.ViewMode.Messages,
    };

    /// <summary>Converts a ViewMode enum to its config-string representation.</summary>
    public static string ToConfigString(Models.ViewMode mode) => mode switch
    {
        Models.ViewMode.Conversations => "conversations",
        Models.ViewMode.From          => "from",
        Models.ViewMode.To            => "to",
        _                             => "messages",
    };

    /// <summary>Converts a config-string Sort to the enum (case-insensitive).</summary>
    public static MessageSort ParseSort(string? s) => (s?.ToLowerInvariant()) switch
    {
        "dateasc"   => MessageSort.DateAscending,
        "alphaasc"  => MessageSort.AlphaAscending,
        "alphadesc" => MessageSort.AlphaDescending,
        "countdesc" => MessageSort.CountDescending,
        "countasc"  => MessageSort.CountAscending,
        _           => MessageSort.DateDescending,
    };

    /// <summary>Converts a MessageSort enum to its config-string representation.</summary>
    public static string ToConfigString(MessageSort sort) => sort switch
    {
        MessageSort.DateAscending   => "dateAsc",
        MessageSort.AlphaAscending  => "alphaAsc",
        MessageSort.AlphaDescending => "alphaDesc",
        MessageSort.CountDescending => "countDesc",
        MessageSort.CountAscending  => "countAsc",
        _                           => "dateDesc",
    };
}

/// <summary>Per-account configuration overrides. Only set fields that differ from global defaults.</summary>
public class AccountOverrideConfig
{
    /// <summary>Override the global PreviewLines for this account. Null = use global setting.</summary>
    public int? PreviewLines { get; set; }
}
