using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace QuickMail.Helpers;

/// <summary>
/// Converts between Key+ModifierKeys and human-readable gesture strings like "Ctrl+Shift+8".
/// Centralizes the formatting logic used by the settings UI, key capture dialog, and file I/O.
/// </summary>
public static class GestureHelper
{
    private static readonly Dictionary<Key, string> _keyToName = BuildKeyToName();
    private static readonly Dictionary<string, Key> _nameToKey = BuildNameToKey();

    public static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return string.Empty;
        var parts = new List<string>(4);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Shift)   != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Alt)     != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(_keyToName.TryGetValue(key, out var name) ? name : key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>
    /// Converts legacy integer-format hotkey fields to a gesture string.
    /// Returns null when <paramref name="key"/> is 0 (no binding stored).
    /// </summary>
    public static string? MigrateFromLegacyIntegers(int key, int modifiers)
    {
        if (key == 0) return null;
        return Format((Key)key, (ModifierKeys)modifiers);
    }

    public static bool TryParse(string gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;

        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+');
        if (parts.Length < 2) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim())
            {
                case "Ctrl":  modifiers |= ModifierKeys.Control; break;
                case "Shift": modifiers |= ModifierKeys.Shift;   break;
                case "Alt":   modifiers |= ModifierKeys.Alt;     break;
                case "Win":   modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        if (modifiers == ModifierKeys.None) return false;

        var keyPart = parts[^1].Trim();
        if (_nameToKey.TryGetValue(keyPart, out key)) return true;

        // Fallback: try the enum name directly (covers F1–F24, letter keys, etc.)
        if (Enum.TryParse(keyPart, ignoreCase: true, out key) && key != Key.None) return true;

        key = Key.None;
        modifiers = ModifierKeys.None;
        return false;
    }

    private static Dictionary<Key, string> BuildKeyToName()
    {
        var d = new Dictionary<Key, string>();
        for (int i = 0; i <= 9; i++) d[(Key)((int)Key.D0 + i)] = i.ToString();
        for (int i = 0; i <= 9; i++) d[(Key)((int)Key.NumPad0 + i)] = $"Num{i}";
        d[Key.Add]      = "NumAdd";
        d[Key.Subtract] = "NumSub";
        d[Key.Multiply] = "NumMul";
        d[Key.Divide]   = "NumDiv";
        d[Key.Decimal]  = "Num.";
        d[Key.Space]    = "Space";
        d[Key.Return]   = "Enter";
        d[Key.Escape]   = "Escape";
        d[Key.Tab]      = "Tab";
        d[Key.Delete]   = "Delete";
        d[Key.Back]     = "Backspace";
        d[Key.Insert]   = "Insert";
        d[Key.Home]     = "Home";
        d[Key.End]      = "End";
        d[Key.Prior]    = "PageUp";
        d[Key.Next]     = "PageDown";
        d[Key.Up]       = "Up";
        d[Key.Down]     = "Down";
        d[Key.Left]     = "Left";
        d[Key.Right]    = "Right";
        d[Key.OemComma]  = ",";
        d[Key.OemPeriod] = ".";
        return d;
    }

    private static Dictionary<string, Key> BuildNameToKey()
    {
        var d = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i <= 9; i++) d[i.ToString()] = (Key)((int)Key.D0 + i);
        for (int i = 0; i <= 9; i++) d[$"Num{i}"] = (Key)((int)Key.NumPad0 + i);
        d["NumAdd"]    = Key.Add;
        d["NumSub"]    = Key.Subtract;
        d["NumMul"]    = Key.Multiply;
        d["NumDiv"]    = Key.Divide;
        d["Num."]      = Key.Decimal;
        d["Space"]     = Key.Space;
        d["Enter"]     = Key.Return;
        d["Return"]    = Key.Return;
        d["Escape"]    = Key.Escape;
        d["Esc"]       = Key.Escape;
        d["Tab"]       = Key.Tab;
        d["Delete"]    = Key.Delete;
        d["Del"]       = Key.Delete;
        d["Backspace"] = Key.Back;
        d["Insert"]    = Key.Insert;
        d["Home"]      = Key.Home;
        d["End"]       = Key.End;
        d["PageUp"]    = Key.Prior;
        d["PageDown"]  = Key.Next;
        d["Up"]        = Key.Up;
        d["Down"]      = Key.Down;
        d["Left"]      = Key.Left;
        d["Right"]     = Key.Right;
        d[","]         = Key.OemComma;
        d["."]         = Key.OemPeriod;
        return d;
    }
}
