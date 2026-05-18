using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
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
    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");

    private static readonly string ConfigFile   = Path.Combine(DataFolder, "config.ini");
    private static readonly string HotkeysFile  = Path.Combine(DataFolder, "hotkeys.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private ConfigModel? _cached;

    public ConfigModel Load()
    {
        if (_cached != null) return _cached;

        if (!File.Exists(ConfigFile))
        {
            _cached = new ConfigModel();
            Save(_cached);          // write defaults on first run
            return _cached;
        }

        try
        {
            _cached = ParseFile(File.ReadAllLines(ConfigFile));
        }
        catch
        {
            _cached = new ConfigModel();
        }

        // Load custom hotkeys from separate JSON file
        if (File.Exists(HotkeysFile))
        {
            try
            {
                var hotkeys = JsonSerializer.Deserialize<List<HotkeyBinding>>(File.ReadAllText(HotkeysFile));
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
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(ConfigFile, BuildFileText(config), Encoding.UTF8);

        // Save custom hotkeys to separate JSON file (only write when non-empty)
        if (config.CustomHotkeys.Count > 0)
            File.WriteAllText(HotkeysFile, JsonSerializer.Serialize(config.CustomHotkeys, JsonOptions), Encoding.UTF8);
        else if (File.Exists(HotkeysFile))
            File.Delete(HotkeysFile);
    }

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
                h.Gesture = GestureHelper.Format((Key)h.Key, (ModifierKeys)h.Modifiers);

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
                else if (header.StartsWith("account:") &&
                         Guid.TryParse(header["account:".Length..], out var g))
                {
                    section  = "account";
                    acctGuid = g;
                    if (!config.Accounts.ContainsKey(acctGuid))
                        config.Accounts[acctGuid] = new AccountOverrideConfig();
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

            var key   = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();

            if (section == "global")
            {
                switch (key)
                {
                    case "previewlines":
                        if (int.TryParse(value, out var pl)) config.PreviewLines = Math.Max(0, pl);
                        break;
                    case "showmessagestatus":
                        config.ShowMessageStatus = value.Equals("on", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || value == "1";
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
                    case "initialsynccount":
                        if (int.TryParse(value, out var isc)) config.InitialSyncCount = Math.Max(0, isc);
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

        sb.AppendLine($"InitialSyncCount = {config.InitialSyncCount}");
        sb.AppendLine("# Number of messages to fetch on the initial sync of a folder.");
        sb.AppendLine("# Default is 500. Set to 0 to fetch all messages in the folder.");
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

        return sb.ToString();
    }
}
