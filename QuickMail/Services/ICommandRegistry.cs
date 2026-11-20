using System;
using System.Collections.Generic;
using System.Windows.Input;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ICommandRegistry
{
    void Register(CommandDefinition command);
    void Unregister(string id);
    IReadOnlyList<CommandDefinition> GetAll();
    CommandDefinition? FindById(string id);
    CommandDefinition? FindByGesture(Key key, ModifierKeys modifiers);
    void ApplyUserOverrides(IEnumerable<HotkeyBinding> bindings);

    /// <summary>
    /// CommandIds in the user-override list that don't correspond to any registered command.
    /// Used by the app to prune stale entries from hotkeys.json after startup (e.g. saved-view
    /// hotkeys whose underlying view was deleted).
    /// </summary>
    IReadOnlyList<string> GetOrphanOverrideCommandIds();

    /// <summary>
    /// The shortcut a command answers to now, as text ("Ctrl+R"), honouring the user's bindings;
    /// empty when the id is not registered or the command has no shortcut (#849).
    /// </summary>
    string GetGestureText(string id);

    /// <summary>
    /// Raised after the shortcuts may have changed: user bindings re-applied, or a command
    /// registered or removed. Whatever shows a shortcut re-reads it from <see cref="GetGestureText"/>.
    /// </summary>
    event EventHandler? BindingsChanged;
}
