using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuickMail.Tests;

/// <summary>
/// Real keyboard and mouse input through Windows' <c>SendInput</c>, for the few tests whose claim
/// is about what WPF does with input from an actual device: which device it records as the most
/// recent one, and the <c>WM_CONTEXTMENU</c> that <c>DefWindowProc</c> raises for Shift+F10. A
/// synthesized <c>KeyEventArgs</c> raised inside WPF goes through neither. Only for tests gated by
/// <see cref="InputTests"/>: the input goes to whatever window is in the foreground.
/// </summary>
internal static class OsInput
{
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_F10 = 0x79;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static INPUT Mouse(int x, int y, uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags } },
    };

    private static void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Presses and releases <paramref name="vk"/>, holding Shift around it if asked.</summary>
    public static void PressKey(ushort vk, bool shift = false)
    {
        if (shift)
            Send(Key(VK_SHIFT, false), Key(vk, false), Key(vk, true), Key(VK_SHIFT, true));
        else
            Send(Key(vk, false), Key(vk, true));
    }

    /// <summary>Moves the pointer to <paramref name="screen"/> (physical pixels).</summary>
    public static void MoveTo(Point screen)
    {
        // Absolute coordinates are 0..65535 across the virtual desktop.
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        var x = (int)Math.Round((screen.X - left) * 65535.0 / (width - 1));
        var y = (int)Math.Round((screen.Y - top) * 65535.0 / (height - 1));
        Send(Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));
    }

    /// <summary>Right-clicks at <paramref name="screen"/> (physical pixels).</summary>
    public static void RightClickAt(Point screen)
    {
        MoveTo(screen);
        Send(Mouse(0, 0, MOUSEEVENTF_RIGHTDOWN), Mouse(0, 0, MOUSEEVENTF_RIGHTUP));
    }
}
