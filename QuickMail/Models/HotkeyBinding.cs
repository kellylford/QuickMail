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

    /// <summary>Human-readable gesture, e.g. "Ctrl+Shift+8" or "Ctrl+Alt+F5".</summary>
    public string Gesture { get; set; } = string.Empty;

    // Legacy fields kept for migrating old hotkeys.json files that stored raw integers.
    // JsonIgnore(WhenWritingDefault) means these are never written when saving (value stays 0).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Key { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Modifiers { get; set; }
}
