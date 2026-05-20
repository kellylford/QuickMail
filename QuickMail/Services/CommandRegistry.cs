using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
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

    public void Unregister(string id) => _byId.Remove(id);

    public IReadOnlyList<CommandDefinition> GetAll() =>
        _byId.Values.OrderBy(c => c.Category).ThenBy(c => c.Title).ToList();

    public CommandDefinition? FindById(string id) =>
        _byId.TryGetValue(id, out var cmd) ? cmd : null;

    public CommandDefinition? FindByGesture(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return null;

        // User overrides take precedence
        foreach (var binding in _userOverrides)
        {
            if ((Key)binding.Key == key && (ModifierKeys)binding.Modifiers == modifiers)
                return FindById(binding.CommandId);
        }

        // Fall back to default gestures
        foreach (var cmd in _byId.Values)
        {
            if (cmd.DefaultKey == key && cmd.DefaultModifiers == modifiers)
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
