using System.Text.Json.Serialization;

namespace QuickMail.Models;

/// <summary>
/// A user-defined override that maps a keyboard shortcut to a command by ID.
/// Stored in hotkeys.json. The <see cref="Gesture"/> field is the authoritative format,
/// e.g. "Ctrl+Shift+8". The legacy integer fields below are read-only for migration
/// and are never written by current code.
/// </summary>
public sealed class HotkeyBinding
{
    public string CommandId { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="Gesture"/> of an explicit "no key" override: the command has no shortcut at all,
    /// not even its default. Written when the user gives a command's default key to another command
    /// (#1044). Older versions cannot parse it and drop the entry, which restores the default there.
    /// </summary>
    public const string NoKeyGesture = "None";

    /// <summary>Human-readable gesture, e.g. "Ctrl+Shift+8" or "Ctrl+Alt+F5", or <see cref="NoKeyGesture"/>.</summary>
    public string Gesture { get; set; } = string.Empty;

    /// <summary>True when this override takes the command's key away rather than giving it one.</summary>
    [JsonIgnore]
    public bool IsNoKey => string.Equals(Gesture, NoKeyGesture, System.StringComparison.OrdinalIgnoreCase);

    // Legacy fields kept for migrating old hotkeys.json files that stored raw integers.
    // JsonIgnore(WhenWritingDefault) means these are never written when saving (value stays 0).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Key { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Modifiers { get; set; }
}
