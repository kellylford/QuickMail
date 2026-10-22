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
        GestureText    = DefaultGestureText;
    }

    /// <summary>The default shortcut as text, e.g. "Ctrl+N" or "Delete"; empty when there is none.</summary>
    public string DefaultGestureText => GestureHelper.Format(DefaultKey, DefaultModifiers);

    /// <summary>
    /// The shortcut this command answers to now: the user's binding from Settings if there is one,
    /// otherwise <see cref="DefaultGestureText"/>. The registry the command is registered with keeps
    /// it current. Menus, tooltips, screen readers and the command palette show this one (#849).
    /// </summary>
    public string GestureText { get; internal set; }
}
