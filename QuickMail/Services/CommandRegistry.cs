using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Holds all registered <see cref="CommandDefinition"/> instances and resolves
/// keyboard gestures to commands, respecting user-defined overrides.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ParsedOverride> _userOverrides = [];

    private readonly record struct ParsedOverride(string CommandId, Key Key, ModifierKeys Modifiers);

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
            if (binding.Key == key && binding.Modifiers == modifiers)
                return FindById(binding.CommandId);
        }

        // A command's default gesture is suppressed if the user remapped that command elsewhere.
        var suppressed = _userOverrides.Select(o => o.CommandId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fall back to default gestures
        foreach (var cmd in _byId.Values)
        {
            if (suppressed.Contains(cmd.Id)) continue;
            if (cmd.DefaultKey == key && cmd.DefaultModifiers == modifiers)
                return cmd;
        }

        return null;
    }

    public void ApplyUserOverrides(IEnumerable<HotkeyBinding> overrides)
    {
        _userOverrides.Clear();
        foreach (var b in overrides)
        {
            if (string.IsNullOrEmpty(b.CommandId)) continue;

            // Prefer the authoritative Gesture string written by current code.
            if (!string.IsNullOrEmpty(b.Gesture)
                && GestureHelper.TryParse(b.Gesture, out var key, out var mods))
            {
                _userOverrides.Add(new ParsedOverride(b.CommandId, key, mods));
                continue;
            }

            // Migrate any pre-existing legacy integer-format entries.
            if (b.Key != 0)
                _userOverrides.Add(new ParsedOverride(b.CommandId, (Key)b.Key, (ModifierKeys)b.Modifiers));
        }
    }
}
