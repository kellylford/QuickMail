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
}
