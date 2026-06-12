using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Reads and writes the QuickMail configuration file.
///
/// File format — simple INI with comments placed on the line AFTER each setting:
///
///   [global]
///   PreviewLines = 3
///   # Number of body-preview lines shown in the message list. Set to 0 to disable.
///
///   [account:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx]
///   PreviewLines = 2
///   # Override preview lines for this account only.
///
/// Rules: lines starting with # or ; are comments and are ignored during parsing.
/// Blank lines are ignored. Section headers are [section-name].
/// Settings are key = value pairs.
/// </summary>
public class ConfigService : IConfigService
{
    private readonly string _dataFolder;
    private readonly string _configFile;
    private readonly string _hotkeysFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private ConfigModel? _cached;

    public ConfigService(ProfileContext profile)
    {
        _dataFolder   = profile.ProfileDir;
        _configFile   = Path.Combine(profile.ProfileDir, "config.ini");
        _hotkeysFile  = Path.Combine(profile.ProfileDir, "hotkeys.json");
    }

    public ConfigModel Load()
    {
        if (_cached != null) return _cached;

        if (!File.Exists(_configFile))
        {
            _cached = new ConfigModel();
            Save(_cached);          // write defaults on first run
            return _cached;
        }

        try
        {
            _cached = ParseFile(File.ReadAllLines(_configFile));
        }
        catch
        {
            _cached = new ConfigModel();
        }

        // Load custom hotkeys from separate JSON file
        if (File.Exists(_hotkeysFile))
        {
            try
            {
                var hotkeys = JsonSerializer.Deserialize<List<HotkeyBinding>>(File.ReadAllText(_hotkeysFile));
                if (hotkeys != null)
                    _cached.CustomHotkeys = ValidateAndMigrateHotkeys(hotkeys);
            }
            catch { /* malformed hotkeys.json — ignore and use defaults */ }
        }

        return _cached;
    }

    public void Save(ConfigModel config)
    {
        _cached = config;
        Directory.CreateDirectory(_dataFolder);
        File.WriteAllText(_configFile, BuildFileText(config), Encoding.UTF8);

        // Save custom hotkeys to separate JSON file (only write when non-empty)
        if (config.CustomHotkeys.Count > 0)
            File.WriteAllText(_hotkeysFile, JsonSerializer.Serialize(config.CustomHotkeys, JsonOptions), Encoding.UTF8);
        else if (File.Exists(_hotkeysFile))
            File.Delete(_hotkeysFile);

        Views.AccessibilityHelper.Configure(config);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static bool ParseBool(string v) =>
        v.Equals("on",   StringComparison.OrdinalIgnoreCase)
        || v.Equals("true", StringComparison.OrdinalIgnoreCase)
        || v == "1";

    // ── Hotkey helpers ────────────────────────────────────────────────────────────

    private static List<HotkeyBinding> ValidateAndMigrateHotkeys(List<HotkeyBinding> raw)
    {
        var result = new List<HotkeyBinding>(raw.Count);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var h in raw)
        {
            // Skip entries with no command id or duplicate command ids
            if (string.IsNullOrWhiteSpace(h.CommandId) || !seen.Add(h.CommandId))
                continue;

            // Migrate old integer format: if Gesture is absent but Key int is set, convert it
            if (string.IsNullOrEmpty(h.Gesture) && h.Key != 0)
                h.Gesture = GestureHelper.MigrateFromLegacyIntegers(h.Key, h.Modifiers) ?? string.Empty;

            // Skip entries whose gesture string can't be parsed into a valid key combination
            if (!GestureHelper.TryParse(h.Gesture, out _, out _))
                continue;

            result.Add(h);
        }

        return result;
    }

    // ── Parser ────────────────────────────────────────────────────────────────────

    private static ConfigModel ParseFile(string[] lines)
    {
        var config       = new ConfigModel();
        string? section  = null;
        Guid    acctGuid = Guid.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Comments and blank lines
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            // Section headers
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var header = line[1..^1].Trim().ToLowerInvariant();
                if (header == "global")
                {
                    section  = "global";
                    acctGuid = Guid.Empty;
                }
                else if (header == "windowing")
                {
                    section  = "windowing";
                    acctGuid = Guid.Empty;
                }
                else if (header.StartsWith("account:") &&
                         Guid.TryParse(header["account:".Length..], out var g))
                {
                    section  = "account";
                    acctGuid = g;
                    if (!config.Accounts.ContainsKey(acctGuid))
                        config.Accounts[acctGuid] = new AccountOverrideConfig();
                }
                else if (header == "features")
                {
                    section  = "features";
                    acctGuid = Guid.Empty;
                }
                else
                {
                    section = null;
                }
                continue;
            }

            // Key = Value
            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var rawKey = line[..eq].Trim();
            var key    = rawKey.ToLowerInvariant();
            var value  = line[(eq + 1)..].Trim();

            if (section == "global")
            {
                switch (key)
                {
                    case "previewlines":
                        if (int.TryParse(value, out var pl)) config.PreviewLines = Math.Max(0, pl);
                        break;
                    case "showmessagestatus":
                        config.ShowMessageStatus = ParseBool(value);
                        break;
                    case "viewmode":
                        config.ViewMode = value.ToLowerInvariant() switch
                        {
                            "conversations" => "conversations",
                            "from"          => "from",
                            "to"            => "to",
                            _               => "messages",
                        };
                        break;
                    case "syncdays":
                        if (int.TryParse(value, out var sd)) config.SyncDays = Math.Max(0, sd);
                        break;
                    case "maximapconnectionsperaccount":
                        if (int.TryParse(value, out var maxConn))
                            config.MaxImapConnectionsPerAccount = Math.Clamp(maxConn, 1, 15);
                        break;
                    case "initialsynccount":
                        if (int.TryParse(value, out var isc)) config.InitialSyncCount = Math.Max(0, isc);
                        break;
                    case "customannouncements":  config.CustomAnnouncements = ParseBool(value); break;
                    case "announcehints":        config.AnnounceHints        = ParseBool(value); break;
                    case "announcestatus":       config.AnnounceStatus       = ParseBool(value); break;
                    case "announceresults":      config.AnnounceResults      = ParseBool(value); break;
                    case "announcespellingwhiletyping":      config.AnnounceSpellingWhileTyping      = ParseBool(value); break;
                    case "announcespellingwhilenavigating": config.AnnounceSpellingWhileNavigating = ParseBool(value); break;
                    case "announcespellingsuggestions":     config.AnnounceSpellingSuggestions     = ParseBool(value); break;
                    case "announceformattingwhilenavigating": config.AnnounceFormattingWhileNavigating = ParseBool(value); break;
                    case "confirmemptytrash":    config.ConfirmEmptyTrash    = ParseBool(value); break;
                    case "logformat":
                        config.LogFormat = value.ToLowerInvariant() == "timefirst" ? "timeFirst" : "actionFirst";
                        break;
                    case "tutorialcompleted":    config.TutorialCompleted    = ParseBool(value); break;
                    case "defaultcomposemode":
                        config.DefaultComposeMode = value.ToLowerInvariant() switch
                        {
                            "markdown" => Models.ComposeMode.Markdown,
                            "html"     => Models.ComposeMode.Html,
                            _          => Models.ComposeMode.PlainText,
                        };
                        break;
                    case "autosavedrafts":
                        config.AutoSaveDrafts = ParseBool(value);
                        break;
                    case "autosaveintervalseconds":
                        if (int.TryParse(value, out var asi))
                            config.AutoSaveIntervalSeconds = Math.Clamp(asi, 30, 600);
                        break;
                }
            }
            else if (section == "windowing")
            {
                switch (key)
                {
                    case "messageopenmode":
                        config.Windowing.MessageOpenMode = value.ToLowerInvariant() switch
                        {
                            "tab"    => Models.MessageOpenMode.Tab,
                            "window" => Models.MessageOpenMode.Window,
                            _        => Models.MessageOpenMode.ReadingPane,
                        };
                        break;
                    case "confirmclosetabwithunsaved":
                        config.Windowing.ConfirmCloseTabWithUnsaved = ParseBool(value);
                        break;
                    case "tabsrememberacrossrestart":
                        config.Windowing.TabsRememberAcrossRestart = ParseBool(value);
                        break;
                }
            }
            else if (section == "account" && acctGuid != Guid.Empty)
            {
                var ovr = config.Accounts[acctGuid];
                switch (key)
                {
                    case "previewlines":
                        if (int.TryParse(value, out var pl)) ovr.PreviewLines = Math.Max(0, pl);
                        break;
                }
            }
            else if (section == "features")
            {
                // Preserve the original key case so IFeatureGate matches FeatureFlag.ToString().
                config.Features[rawKey] = value;
            }
        }

        return config;
    }

    // ── Writer ────────────────────────────────────────────────────────────────────

    private static string BuildFileText(ConfigModel config)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# QuickMail Configuration File");
        sb.AppendLine("# Edit this file with any text editor while QuickMail is closed.");
        sb.AppendLine("# Settings take effect on next launch.");
        sb.AppendLine();

        // ── [global] ─────────────────────────────────────────────────────────────

        sb.AppendLine("[global]");
        sb.AppendLine();

        sb.AppendLine($"PreviewLines = {config.PreviewLines}");
        sb.AppendLine("# Number of body-preview lines shown in the message list.");
        sb.AppendLine("# Set to 0 to disable previews.");
        sb.AppendLine();

        sb.AppendLine($"ShowMessageStatus = {(config.ShowMessageStatus ? "on" : "off")}");
        sb.AppendLine("# Show a status column in the message list.");
        sb.AppendLine("# When on, the first column shows the message status: New, Replied, Fwd, or blank (read).");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"ViewMode = {config.ViewMode}");
        sb.AppendLine("# How to display the message list.");
        sb.AppendLine("# Values: messages (flat list), conversations (grouped by subject), from (grouped by sender).");
        sb.AppendLine();

        sb.AppendLine($"SyncDays = {config.SyncDays}");
        sb.AppendLine("# How many days of mail to sync when opening a folder.");
        sb.AppendLine("# Set to 0 to sync all mail (no date filter).");
        sb.AppendLine("# Supported values: 7, 30, 180, 365, or 0 (all).");
        sb.AppendLine();

        sb.AppendLine($"MaxImapConnectionsPerAccount = {Math.Clamp(config.MaxImapConnectionsPerAccount, 1, 15)}");
        sb.AppendLine("# Maximum simultaneous IMAP connections per account.");
        sb.AppendLine("# Background sync is limited below this value so foreground message opens keep reserved capacity.");
        sb.AppendLine("# Increase only if your provider allows it. Values: 1-15.");
        sb.AppendLine();

        sb.AppendLine($"InitialSyncCount = {config.InitialSyncCount}");
        sb.AppendLine("# Number of messages to fetch on the initial sync of a folder.");
        sb.AppendLine("# Default is 500. Set to 0 to fetch all messages in the folder.");
        sb.AppendLine();

        sb.AppendLine($"CustomAnnouncements = {(config.CustomAnnouncements ? "on" : "off")}");
        sb.AppendLine("# Master switch for all custom screen reader announcements from QuickMail.");
        sb.AppendLine("# When off, only native control announcements (focus, selection) are spoken.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceHints = {(config.AnnounceHints ? "on" : "off")}");
        sb.AppendLine("# Announce instructional hints, e.g. how to use the search box.");
        sb.AppendLine("# Turn off once you are familiar with the interface. Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceStatus = {(config.AnnounceStatus ? "on" : "off")}");
        sb.AppendLine("# Announce background loading and sync progress updates. Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceResults = {(config.AnnounceResults ? "on" : "off")}");
        sb.AppendLine("# Announce action outcomes such as search result counts and move confirmations.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceSpellingWhileTyping = {(config.AnnounceSpellingWhileTyping ? "on" : "off")}");
        sb.AppendLine("# Announce spelling errors when typing. Default off.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceSpellingWhileNavigating = {(config.AnnounceSpellingWhileNavigating ? "on" : "off")}");
        sb.AppendLine("# Announce spelling errors when the caret moves into a misspelled word during navigation.");
        sb.AppendLine("# F7/Shift+F7 always announce regardless of this setting.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceSpellingSuggestions = {(config.AnnounceSpellingSuggestions ? "on" : "off")}");
        sb.AppendLine("# Announce spelling suggestions when a misspelling is announced.");
        sb.AppendLine("# When off, only the misspelled word is spoken without suggestions.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AnnounceFormattingWhileNavigating = {(config.AnnounceFormattingWhileNavigating ? "on" : "off")}");
        sb.AppendLine("# Announce block type (heading level, list item, normal text) when the caret");
        sb.AppendLine("# moves to a different paragraph in HTML compose mode. Default on.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"ConfirmEmptyTrash = {(config.ConfirmEmptyTrash ? "on" : "off")}");
        sb.AppendLine("# Show a confirmation dialog before permanently deleting all messages in trash.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"LogFormat = {config.LogFormat}");
        sb.AppendLine("# How log lines are formatted.");
        sb.AppendLine("# actionFirst: message then timestamp — easier to scan with a screen reader.");
        sb.AppendLine("# timeFirst: timestamp then message (historical format).");
        sb.AppendLine("# Values: actionFirst, timeFirst.");
        sb.AppendLine();

        sb.AppendLine($"TutorialCompleted = {(config.TutorialCompleted ? "on" : "off")}");
        sb.AppendLine("# Whether the first-run keyboard tutorial has been completed.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"DefaultComposeMode = {config.DefaultComposeMode switch
        {
            Models.ComposeMode.Markdown => "markdown",
            Models.ComposeMode.Html     => "html",
            _                           => "plain",
        }}");
        sb.AppendLine("# Editing mode new compose windows start in.");
        sb.AppendLine("# Drafts and templates always reopen in plain text.");
        sb.AppendLine("# Values: plain, markdown, html.");
        sb.AppendLine();

        sb.AppendLine($"AutoSaveDrafts = {(config.AutoSaveDrafts ? "on" : "off")}");
        sb.AppendLine("# Automatically save the message as a draft while composing.");
        sb.AppendLine("# Auto-save is quiet: success shows in the compose status row without an announcement;");
        sb.AppendLine("# a failure is announced once. Values: on, off.");
        sb.AppendLine();

        sb.AppendLine($"AutoSaveIntervalSeconds = {Math.Clamp(config.AutoSaveIntervalSeconds, 30, 600)}");
        sb.AppendLine("# Seconds between automatic draft saves. Values: 30-600.");
        sb.AppendLine();

        // ── [windowing] ──────────────────────────────────────────────────────────

        sb.AppendLine("[windowing]");
        sb.AppendLine();

        var modeStr = config.Windowing.MessageOpenMode switch
        {
            Models.MessageOpenMode.Tab    => "tab",
            Models.MessageOpenMode.Window => "window",
            _                             => "readingPane",
        };
        sb.AppendLine($"MessageOpenMode = {modeStr}");
        sb.AppendLine("# Where Enter / click on a message opens it.");
        sb.AppendLine("# Values: readingPane (default), tab, window.");
        sb.AppendLine();

        sb.AppendLine($"ConfirmCloseTabWithUnsaved = {(config.Windowing.ConfirmCloseTabWithUnsaved ? "on" : "off")}");
        sb.AppendLine("# Confirm before closing a tab with unsaved changes.");
        sb.AppendLine("# Values: on, off.");
        sb.AppendLine();

        // ── [account:guid] overrides ─────────────────────────────────────────────

        foreach (var (guid, ovr) in config.Accounts)
        {
            sb.AppendLine($"[account:{guid}]");
            sb.AppendLine("# Per-account overrides. Remove this section to use global defaults.");
            sb.AppendLine();

            if (ovr.PreviewLines.HasValue)
            {
                sb.AppendLine($"PreviewLines = {ovr.PreviewLines.Value}");
                sb.AppendLine("# Override preview lines for this account. Remove to use global setting.");
                sb.AppendLine();
            }
        }

        // ── [features] ─────────────────────────────────────────────────────────────
        // Only written when at least one flag has been set, so the default config stays clean.
        if (config.Features.Count > 0)
        {
            sb.AppendLine("[features]");
            sb.AppendLine("# Experimental feature flags. Values: true, false.");
            sb.AppendLine();
            foreach (var (name, value) in config.Features)
                sb.AppendLine($"{name} = {value}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
