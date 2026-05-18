using System;
using System.Windows.Input;
using QuickMail.Helpers;

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
    public Key DefaultKey         { get; }
    public ModifierKeys DefaultModifiers { get; }
    public Action Execute         { get; }
    public Func<bool>? IsAvailable { get; }

    public CommandDefinition(
        string id,
        string category,
        string title,
        Action execute,
        Key defaultKey = Key.None,
        ModifierKeys defaultModifiers = ModifierKeys.None,
        string? description = null,
        Func<bool>? isAvailable = null)
    {
        Id             = id;
        Category       = category;
        Title          = title;
        Execute        = execute;
        DefaultKey     = defaultKey;
        DefaultModifiers = defaultModifiers;
        Description    = description;
        IsAvailable    = isAvailable;
    }

    /// <summary>Human-readable shortcut string, e.g. "Ctrl+N" or "Delete".</summary>
    public string GestureText => GestureHelper.Format(DefaultKey, DefaultModifiers);
}
