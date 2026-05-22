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

        // User overrides take precedence — but only when their CommandId still resolves
        // to a registered command. Orphan overrides (e.g. saved-view hotkeys whose view
        // was later deleted) used to swallow the keypress by returning null from this
        // method instead of falling through to defaults.  When two overrides claim the
        // same gesture (one orphan, one live), the live one must still fire.
        // We also need to know which *live* command IDs have overrides, so a default
        // gesture isn't suppressed by an orphan binding for a deleted command.
        CommandDefinition? overrideHit = null;
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in _userOverrides)
        {
            var cmd = FindById(binding.CommandId);
            if (cmd == null) continue;            // orphan binding — ignore entirely
            suppressed.Add(binding.CommandId);    // live override — suppress this command's default

            if (overrideHit == null && binding.Key == key && binding.Modifiers == modifiers)
                overrideHit = cmd;
        }
        if (overrideHit != null) return overrideHit;

        // Fall back to default gestures (skipping any command that has a live override).
        foreach (var cmd in _byId.Values)
        {
            if (suppressed.Contains(cmd.Id)) continue;
            if (cmd.DefaultKey == key && cmd.DefaultModifiers == modifiers)
                return cmd;
        }

        return null;
    }

    /// <summary>
    /// Returns the set of CommandIds in the user-override list that don't correspond to
    /// any registered command. Used by the app to prune stale entries from hotkeys.json
    /// after all commands (including saved-view commands) have been registered.
    /// </summary>
    public IReadOnlyList<string> GetOrphanOverrideCommandIds() =>
        _userOverrides
            .Select(o => o.CommandId)
            .Where(id => !_byId.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
