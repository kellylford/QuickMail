using System.Windows.Forms;

namespace QuickMail.Models;

/// <summary>
/// A user-defined override that maps a keyboard shortcut to a command by ID.
/// Stored in hotkeys.json.
/// </summary>
public sealed class HotkeyBinding
{
    public string CommandId { get; set; } = string.Empty;

    /// <summary>Integer value of the combined <see cref="Keys"/> shortcut (key + modifiers).</summary>
    public int Shortcut { get; set; }
}
