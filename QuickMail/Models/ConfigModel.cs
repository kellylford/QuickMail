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
    /// Read messages as plain text: render each message from its original text/plain part
    /// (falling back to text extracted from the HTML when the sender sent no plain-text part)
    /// instead of the HTML body. Sticky preference; applies to the reading pane, message tabs,
    /// and the standalone message window. Default off (issue #34).
    /// </summary>
    public bool ReadAsPlainText { get; set; } = false;

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

    /// <summary>
    /// Poll interval (seconds) for the Microsoft Graph new-mail delta watcher. Default 60, clamped
    /// to 30–600. IMAP accounts use a held IDLE connection instead and ignore this setting.
    /// </summary>
    public int GraphPollSeconds { get; set; } = 60;

    /// <summary>
    /// Fallback mail-sync interval in minutes. Primary new-mail detection is IMAP IDLE (server push);
    /// this periodic inbox sync is a safety net for when a server never pushes, the held IDLE
    /// connection dies quietly, or read/flag state changed in another client (which IDLE never
    /// reports). Default 5. Set to 0 to disable and rely on IDLE alone. Clamped to 0 or 1–120.
    /// </summary>
    public int MailSyncPollMinutes { get; set; } = 5;

    // ── Appearance ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The active theme id. "system" follows the OS light/dark setting and yields
    /// entirely to Windows High Contrast; other values name a built-in ("parchment",
    /// "dark", "ember", "fjord", "heather") or a user theme id.
    /// </summary>
    public string AppearanceThemeId { get; set; } = "system";

    /// <summary>App-wide text scale. Discrete steps 1.0/1.1/1.25/1.5/1.75/2.0.</summary>
    public double AppearanceTextScale { get; set; } = 1.0;

    /// <summary>Font family override applied app-wide. Empty = use the theme's font.</summary>
    public string AppearanceFontFamily { get; set; } = string.Empty;

    /// <summary>Always underline hyperlinks in message content. Default off.</summary>
    public bool AppearanceUnderlineLinks { get; set; } = false;

    /// <summary>Thicker keyboard focus indicators (2px → 4px). Default off.</summary>
    public bool AppearanceThickFocus { get; set; } = false;

    /// <summary>Override sender HTML colors/fonts with theme colors in the reading pane. Default off.</summary>
    public bool AppearanceForceMessageTheme { get; set; } = false;

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

    /// <summary>Controls how suggestions are worded: "justSuggestions" or "numbersWithSuggestions".</summary>
    public string SpellingSuggestionsVerbosity { get; set; } = "numbersWithSuggestions";

    /// <summary>Announce the block type (heading level, list item, normal text) when the caret moves to a different paragraph in HTML compose mode.</summary>
    public bool AnnounceFormattingWhileNavigating { get; set; } = true;

    /// <summary>
    /// Include the flag name before read status in each message row's accessible name.
    /// Default on so screen reader users hear flag state immediately.
    /// </summary>
    public bool AnnounceFlagStatus { get; set; } = true;

    /// <summary>
    /// When true, the address-book contact list speaks field labels in each row's accessible name
    /// ("Name … email … account …"); when false (default) it speaks concise field data only
    /// ("Kelly Ford, kelly@example.com, Local address book"), matching how the message list reads.
    /// </summary>
    public bool ContactListShowFieldLabels { get; set; } = false;

    /// <summary>
    /// When true, the calendar event list speaks field labels in each row's accessible name
    /// ("Subject … when … status …"); when false (default) it speaks concise data only, matching
    /// how the message and contact lists read. Mirrors <see cref="ContactListShowFieldLabels"/>.
    /// </summary>
    public bool CalendarListShowFieldLabels { get; set; } = false;

    /// <summary>
    /// Fire a notification before each appointment. OFF by default (opt-in, per the
    /// announcement-infra convention for potentially intrusive features; full-calendar spec Q6).
    /// </summary>
    public bool CalendarReminders { get; set; } = false;

    /// <summary>Minutes before an appointment's start that the reminder fires. Default 10.</summary>
    public int CalendarReminderMinutes { get; set; } = 10;

    // ── Flagging ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Guid string of the named flag that the K key applies.
    /// Defaults to the built-in "Flagged" flag (FlagDefinition.BuiltInFlagId).
    /// If the stored Guid does not match any defined flag, FlagService falls back to the built-in.
    /// </summary>
    public string DefaultFlagId { get; set; } = "00000000-0000-0000-0000-000000000001";

    // ── Compose ───────────────────────────────────────────────────────────────────

    /// <summary>Editing mode new compose windows start in. Drafts reopen in the mode they were saved in; templates always reopen in plain text.</summary>
    public ComposeMode DefaultComposeMode { get; set; } = ComposeMode.PlainText;

    /// <summary>Automatically save composes as drafts while editing.</summary>
    public bool AutoSaveDrafts { get; set; } = true;

    /// <summary>Seconds between automatic draft saves. Clamped to 30–600.</summary>
    public int AutoSaveIntervalSeconds { get; set; } = 120;

    // ── Advanced ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// How log lines are formatted.
    /// "actionFirst" (default): message then timestamp — easier to scan with a screen reader.
    /// "timeFirst": timestamp then message — the historical format.
    /// </summary>
    public string LogFormat { get; set; } = "actionFirst";

    /// <summary>
    /// Whether to write log entries to quickmail.log. Default off.
    /// When off and /debug is not active, Log() is a no-op.
    /// /debug always overrides this setting and forces logging on.
    /// </summary>
    public bool EnableLogging { get; set; } = false;

    // ── Tutorial ──────────────────────────────────────────────────────────────────

    /// <summary>Show a confirmation dialog before emptying trash. Default on.</summary>
    public bool ConfirmEmptyTrash { get; set; } = true;

    // ── Notifications ─────────────────────────────────────────────────────────────

    /// <summary>Show a Windows toast notification when new mail arrives in an inbox. Default off —
    /// the user opts in from Settings.</summary>
    public bool NotifyOnNewMail { get; set; } = false;

    /// <summary>
    /// When closing the main window, hide QuickMail to the notification area (system tray) instead
    /// of exiting, so new-mail watchers keep running and notifications keep arriving. Default off
    /// (existing behaviour: closing the window exits the app).
    /// </summary>
    public bool CloseToTray { get; set; } = false;

    /// <summary>Whether the one-time "still running in the notification area" hint has been shown.
    /// Maintained automatically the first time the window hides to the tray.</summary>
    public bool TrayHintShown { get; set; } = false;

    /// <summary>Whether the user has completed the first-run keyboard tutorial.</summary>
    public bool TutorialCompleted { get; set; } = false;

    /// <summary>Whether the one-time desktop shortcut offer has been shown (installed copies only).
    /// The shortcut itself is not stored here — the .lnk file on the desktop is the source of truth.</summary>
    public bool DesktopShortcutPrompted { get; set; } = false;

    // ── Updates ───────────────────────────────────────────────────────────────────

    /// <summary>Download and install updates automatically (installed copies). When off, the
    /// Help menu still reports available updates but nothing is downloaded or installed.
    /// Read at the startup check, so a change takes effect the next time QuickMail starts.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Show the "QuickMail Update Installed" dialog on the first launch after an
    /// update has been applied.</summary>
    public bool ShowUpdateInstalledAlerts { get; set; } = true;

    /// <summary>The app version recorded on the previous run. A mismatch on an installed copy
    /// means an update was applied since; used to trigger the update-installed dialog.</summary>
    public string LastRunVersion { get; set; } = string.Empty;

    // ── Windowing (Phase 6) ───────────────────────────────────────────────────────

    /// <summary>Tab and window management preferences.</summary>
    public WindowingPreferences Windowing { get; set; } = new();

    // ── Calendar ─────────────────────────────────────────────────────────────────

    /// <summary>Whether declined calendar events appear in the calendar list. Default off.</summary>
    public bool ShowDeclinedEvents { get; set; } = false;

    /// <summary>
    /// Obsolete: the calendar is now a folder in the folder tree, not a toggle pane.
    /// Retained only so older config.ini files do not break on parse.
    /// </summary>
    public bool CalendarPaneOpen { get; set; } = false;

    // ── Custom hotkey overrides ──────────────────────────────────────────────────

    /// <summary>User-defined keyboard shortcut overrides, stored in hotkeys.json.</summary>
    public List<HotkeyBinding> CustomHotkeys { get; set; } = [];

    // ── Google OAuth client credentials ──────────────────────────────────────────

    /// <summary>
    /// Google OAuth client ID from the Google Cloud Console app registration.
    /// Set in the [google] section of config.ini. Never committed to source control.
    /// </summary>
    public string GoogleClientId { get; set; } = string.Empty;

    /// <summary>
    /// Google OAuth client secret from the Google Cloud Console app registration.
    /// Set in the [google] section of config.ini. Never committed to source control.
    /// </summary>
    public string GoogleClientSecret { get; set; } = string.Empty;

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
        "dateasc"      => MessageSort.DateAscending,
        "alphaasc"     => MessageSort.AlphaAscending,
        "alphadesc"    => MessageSort.AlphaDescending,
        "countdesc"    => MessageSort.CountDescending,
        "countasc"     => MessageSort.CountAscending,
        "flaggedfirst" => MessageSort.FlaggedFirst,
        _              => MessageSort.DateDescending,
    };

    /// <summary>Converts a MessageSort enum to its config-string representation.</summary>
    public static string ToConfigString(MessageSort sort) => sort switch
    {
        MessageSort.DateAscending   => "dateAsc",
        MessageSort.AlphaAscending  => "alphaAsc",
        MessageSort.AlphaDescending => "alphaDesc",
        MessageSort.CountDescending => "countDesc",
        MessageSort.CountAscending  => "countAsc",
        MessageSort.FlaggedFirst    => "flaggedFirst",
        _                           => "dateDesc",
    };
}

/// <summary>Per-account configuration overrides. Only set fields that differ from global defaults.</summary>
public class AccountOverrideConfig
{
    /// <summary>Override the global PreviewLines for this account. Null = use global setting.</summary>
    public int? PreviewLines { get; set; }
}
