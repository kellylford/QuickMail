using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace QuickMail.Models;

/// <summary>
/// Metadata for a single named action that can appear in the command palette
/// and optionally be triggered by a keyboard shortcut.
/// </summary>
public sealed class CommandDefinition
{
    public string Id              { get; }
    public string Category        { get; }
    public string Title           { get; }
    public string? Description    { get; }

    /// <summary>
    /// Combined key + modifier flags (e.g. <c>Keys.Control | Keys.N</c>).
    /// <c>Keys.None</c> means no default shortcut.
    /// </summary>
    public Keys Shortcut          { get; }
    public Action Execute         { get; }
    public Func<bool>? IsAvailable { get; }

    public CommandDefinition(
        string id,
        string category,
        string title,
        Action execute,
        Keys shortcut = Keys.None,
        string? description = null,
        Func<bool>? isAvailable = null)
    {
        Id           = id;
        Category     = category;
        Title        = title;
        Execute      = execute;
        Shortcut     = shortcut;
        Description  = description;
        IsAvailable  = isAvailable;
    }

    public override string ToString() =>
        GestureText.Length > 0 ? $"{Title} ({GestureText})" : Title;

    /// <summary>Human-readable shortcut string, e.g. "Ctrl+N" or "Delete".</summary>
    public string GestureText
    {
        get
        {
            var keyCode = Shortcut & Keys.KeyCode;
            if (keyCode == Keys.None) return string.Empty;

            var parts = new List<string>();
            if ((Shortcut & Keys.Control) != 0) parts.Add("Ctrl");
            if ((Shortcut & Keys.Shift)   != 0) parts.Add("Shift");
            if ((Shortcut & Keys.Alt)     != 0) parts.Add("Alt");

            var keyStr = keyCode.ToString();
            // D0–D9 stringify as "D0"–"D9"; show just the digit.
            if (keyStr.Length == 2 && keyStr[0] == 'D' && char.IsDigit(keyStr[1]))
                keyStr = keyStr[1..];
            parts.Add(keyStr);
            return string.Join("+", parts);
        }
    }
}
