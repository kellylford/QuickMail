using System.Collections.Generic;
using System.Windows.Forms;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ICommandRegistry
{
    void Register(CommandDefinition command);
    IReadOnlyList<CommandDefinition> GetAll();
    CommandDefinition? FindById(string id);

    /// <summary>
    /// Finds the command matching the given combined key+modifier data
    /// (as provided by <see cref="KeyEventArgs.KeyData"/>).
    /// </summary>
    CommandDefinition? FindByGesture(Keys keyData);

    void ApplyUserOverrides(IEnumerable<HotkeyBinding> overrides);
}
