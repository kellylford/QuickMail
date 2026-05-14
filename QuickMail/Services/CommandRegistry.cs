using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Holds all registered <see cref="CommandDefinition"/> instances and resolves
/// keyboard gestures to commands, respecting user-defined overrides.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HotkeyBinding> _userOverrides = [];

    public void Register(CommandDefinition command) =>
        _byId[command.Id] = command;

    public IReadOnlyList<CommandDefinition> GetAll() =>
        _byId.Values.OrderBy(c => c.Category).ThenBy(c => c.Title).ToList();

    public CommandDefinition? FindById(string id) =>
        _byId.TryGetValue(id, out var cmd) ? cmd : null;

    public CommandDefinition? FindByGesture(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.None) return null;

        // User overrides take precedence
        foreach (var binding in _userOverrides)
        {
            if ((Keys)binding.Shortcut == keyData)
                return FindById(binding.CommandId);
        }

        // Fall back to default gestures
        foreach (var cmd in _byId.Values)
        {
            if (cmd.Shortcut == keyData)
                return cmd;
        }

        return null;
    }

    public void ApplyUserOverrides(IEnumerable<HotkeyBinding> overrides)
    {
        _userOverrides.Clear();
        _userOverrides.AddRange(overrides);
    }
}
