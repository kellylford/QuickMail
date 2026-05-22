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
    void ApplyUserOverrides(IEnumerable<HotkeyBinding> overrides);

    /// <summary>
    /// CommandIds in the user-override list that don't correspond to any registered command.
    /// Used by the app to prune stale entries from hotkeys.json after startup (e.g. saved-view
    /// hotkeys whose underlying view was deleted).
    /// </summary>
    IReadOnlyList<string> GetOrphanOverrideCommandIds();
}
