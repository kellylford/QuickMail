using System.Runtime.InteropServices;

namespace EditorKeyTests;

/// <summary>
/// Real keystrokes through SendInput, with scan codes, to whatever has the keyboard. Nothing here
/// brings a window to the front: a person does not re-activate the window between keys, and a
/// harness that does repairs exactly the focus faults these tests exist to find.
/// </summary>
internal static class Keyboard
{
    /// <summary>Pause after each key, so the app (and a screen reader) keeps up.</summary>
    public static int KeyDelayMs { get; set; } = 60;

    /// <summary>
    /// How long a key is held down, and how long its modifiers stay down after it is released.
    /// A person holds Ctrl for tens of milliseconds around the letter; software that passes a
    /// key combination on (a screen reader, a remote desktop) can release it within a few.
    /// Both are worth testing: the editor ignored Ctrl+A when Ctrl came up too soon.
    /// </summary>
    public static int HoldMs { get; set; } = 15;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = 0x0D, ["TAB"] = 0x09, ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B, ["SPACE"] = 0x20,
        ["BACKSPACE"] = 0x08, ["BS"] = 0x08, ["DELETE"] = 0x2E, ["DEL"] = 0x2E, ["INSERT"] = 0x2D,
        ["HOME"] = 0x24, ["END"] = 0x23, ["PGUP"] = 0x21, ["PGDN"] = 0x22,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["APPS"] = 0x5D,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74, ["F6"] = 0x75,
        ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    // Keys whose scan codes need the "extended" flag, or Windows takes them for the number pad's.
    private static readonly HashSet<ushort> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5D };

    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    /// <summary>
    /// Sends keys written the way SendKeys writes them: <c>^</c> Ctrl, <c>+</c> Shift, <c>%</c>
    /// Alt before a key; <c>{RIGHT}</c>, <c>{F7}</c>, <c>{RIGHT 3}</c> for named keys and
    /// repeats; anything else is that character's key.
    /// </summary>
    public static void Send(string keys)
    {
        int i = 0;
        while (i < keys.Length)
        {
            var modifiers = new List<ushort>();
            while (i < keys.Length && keys[i] is '^' or '+' or '%')
            {
                modifiers.Add(keys[i] switch { '^' => VK_CONTROL, '+' => VK_SHIFT, _ => VK_MENU });
                i++;
            }
            if (i >= keys.Length) throw new FormatException($"Keys end after a modifier: {keys}");

            ushort vk;
            int repeat = 1;
            bool shiftForChar = false;
            if (keys[i] == '{')
            {
                int close = keys.IndexOf('}', i + 1);
                if (close < 0) throw new FormatException($"Unclosed brace in keys: {keys}");
                var parts = keys.Substring(i + 1, close - i - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                i = close + 1;
                if (parts.Length == 0) throw new FormatException($"Empty braces in keys: {keys}");
                if (parts.Length > 1) repeat = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                if (Named.TryGetValue(parts[0], out var named)) vk = named;
                else if (parts[0].Length == 1) (vk, shiftForChar) = KeyFor(parts[0][0]);
                else throw new FormatException($"Unknown key name '{parts[0]}' in: {keys}");
            }
            else
            {
                (vk, shiftForChar) = KeyFor(keys[i]);
                i++;
            }

            if (shiftForChar && !modifiers.Contains(VK_SHIFT)) modifiers.Add(VK_SHIFT);
            for (int n = 0; n < repeat; n++) Chord(modifiers, vk);
        }
    }

    /// <summary>Types text, each character as its own key press.</summary>
    public static void Type(string text)
    {
        foreach (char c in text)
        {
            if (c == '\n') { Chord(Array.Empty<ushort>(), 0x0D); continue; }
            var (vk, shift) = KeyFor(c);
            Chord(shift ? new[] { VK_SHIFT } : Array.Empty<ushort>(), vk);
        }
    }

    private static (ushort Vk, bool Shift) KeyFor(char c)
    {
        short scan = VkKeyScanW(c);
        if (scan == -1) throw new FormatException($"No key on this keyboard layout types '{c}'.");
        if ((scan & 0x0600) != 0) throw new FormatException($"'{c}' needs Ctrl or Alt on this layout; not supported.");
        return ((ushort)(scan & 0xFF), (scan & 0x0100) != 0);
    }

    private static void Chord(IReadOnlyList<ushort> modifiers, ushort vk)
    {
        foreach (var m in modifiers) Key(m, up: false);
        Key(vk, up: false);
        Thread.Sleep(HoldMs);
        Key(vk, up: true);
        if (modifiers.Count > 0 && HoldMs > 15) Thread.Sleep(HoldMs);
        for (int i = modifiers.Count - 1; i >= 0; i--) Key(modifiers[i], up: true);
        Thread.Sleep(KeyDelayMs);
    }

    private static void Key(ushort vk, bool up)
    {
        var input = new INPUT { type = 1 };
        input.ki.wVk = vk;
        input.ki.wScan = (ushort)MapVirtualKeyW(vk, 0);
        input.ki.dwFlags = (up ? 2u : 0u) | (Extended.Contains(vk) ? 1u : 0u);
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException($"SendInput was refused (error {Marshal.GetLastWin32Error()}). Is the session locked?");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short VkKeyScanW(char c);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
