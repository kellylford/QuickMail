using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuickMail.Tests;

/// <summary>
/// A real mouse click, delivered through <c>SendInput</c> so it enters the system input queue the
/// way the physical mouse does: Windows hit-tests the point, decides which window gets it, posts
/// WM_LBUTTONDOWN / WM_LBUTTONUP, and WPF turns those into routed events with real handled-flags.
///
/// <para>This is the one rung above raising routed events by hand. Everything below it has to
/// assume something: that the point maps to the row, that the container does not mark the
/// button-up handled on its way past, that the click reaches the handler declared on the list at
/// all. <see cref="MouseActivationTests"/> raises <c>Mouse.MouseUpEvent</c> on an element it picked
/// itself, which cannot answer any of those - and the folder bug (#601) lived precisely there: every
/// piece worked and the click still did nothing.</para>
///
/// <para><b>This moves the machine's real pointer.</b> Nothing here should be called directly by a
/// test: go through <c>MouseClickInputTests.AppUnderMouse</c>, which moves first, confirms both
/// Windows and WPF agree the intended element is under the cursor, and only then presses - so a
/// coordinate mismatch fails the test instead of clicking whatever was really there. It also pairs
/// every press with a release in a <c>finally</c>, and puts the pointer back where the user left
/// it.</para>
/// </summary>
internal static class RealMouse
{
    private const uint MoveFlag       = 0x0001;
    private const uint LeftDownFlag   = 0x0002;
    private const uint LeftUpFlag     = 0x0004;
    private const uint VirtualDeskFlag = 0x4000;
    private const uint AbsoluteFlag   = 0x8000;

    private const int SmXVirtualScreen  = 76;
    private const int SmYVirtualScreen  = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint DesktopSwitchDesktop = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT's payload is a union sized by its largest member, which is MOUSEINPUT. Declaring it as
    // one keeps Marshal.SizeOf right for both kinds - SendInput rejects a wrong cbSize outright.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputPayload
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputPayload Payload;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>
    /// False while the secure desktop is up - a lock screen, or a UAC prompt that came up mid-run.
    /// Input sent then goes to the desktop the caller cannot see, so a test would click into
    /// nowhere and fail for a reason that has nothing to do with the code. Same check
    /// <c>UiProbeDriver</c> makes before capturing, and for the same class of reason.
    /// </summary>
    public static bool DesktopIsInteractive()
    {
        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    /// <summary>
    /// The top-level window Windows itself puts at a screen point - the one a click there would go
    /// to. <c>WindowFromPoint</c> answers with the deepest child (a WebView2 host, a control's own
    /// HWND), so walk up to the root before comparing.
    /// </summary>
    public static IntPtr TopLevelWindowAt(Point screenPoint)
    {
        var window = WindowFromPoint(new NativePoint
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y),
        });
        return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, GaRoot);
    }

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);

    private const uint GwEnabledPopup = 6;

    /// <summary>
    /// Why a window is not receiving the input aimed at it. A window disabled by a modal dialog is
    /// still what <c>WindowFromPoint</c> names, and still hit-tests in WPF - it simply never gets the
    /// messages, so a click on it looks exactly like a click that did nothing.
    /// </summary>
    public static string WhyNotReceivingInput(IntPtr window)
    {
        if (IsWindowEnabled(window)) return "the window is enabled";

        var popup = GetWindow(window, GwEnabledPopup);
        if (popup == IntPtr.Zero || popup == window) return "the window is DISABLED";

        var title = new System.Text.StringBuilder(256);
        var length = GetWindowText(popup, title, title.Capacity);
        return length > 0
            ? $"the window is DISABLED by a modal dialog titled '{title}'"
            : "the window is DISABLED by a modal dialog with no title";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>
    /// The foreground window, so a test that takes it can hand it back.
    ///
    /// <para>Worth the trouble: clicking a window makes this process foreground, and closing that
    /// window afterwards does not necessarily give it to anyone in particular. Other tests in the
    /// run care - the account-dialog hint tests focus a control on a window shown with
    /// <c>ShowActivated = false</c> and expect the focus to take, which needs the process to hold the
    /// foreground. Leaving it wherever it fell made six of them fail, in a way that reads as
    /// flakiness somewhere else entirely.</para>
    /// </summary>
    public static IntPtr ForegroundWindow => GetForegroundWindow();

    public static void RestoreForeground(IntPtr window)
    {
        if (window != IntPtr.Zero) SetForegroundWindow(window);
    }

    /// <summary>Where the user left the pointer, so the test can put it back.</summary>
    public static Point CursorPosition =>
        GetCursorPos(out var point) ? new Point(point.X, point.Y) : new Point(0, 0);

    public static void RestoreCursor(Point position) =>
        SetCursorPos((int)Math.Round(position.X), (int)Math.Round(position.Y));

    /// <summary>
    /// Moves the pointer to a point in the CALLER'S screen coordinates - what
    /// <c>Visual.PointToScreen</c> hands back.
    ///
    /// <para>Those are not necessarily real screen pixels. A process that declares no DPI awareness
    /// is handed a virtualized desktop on a scaled display - measured on a 150% display here, an
    /// unaware process sees 1664x1109 where the hardware has 2496x1664. <c>SendInput</c> is not
    /// virtualized: its absolute coordinates are normalized across the REAL virtual desktop, so in
    /// that case the caller's numbers land the pointer two thirds of the way to where it was aimed.
    /// This maps between the two spaces before normalizing.</para>
    ///
    /// <para>The test host as it stands IS per-monitor aware, so both reads agree and the mapping is
    /// currently the identity - this is not load-bearing today. It is here because the failure it
    /// prevents is silent and misattributed: the click lands somewhere else entirely and the test
    /// reads as "the app ignored it".</para>
    /// </summary>
    public static void MoveTo(Point screenPoint)
    {
        var seen = VirtualScreen();

        // GetSystemMetrics answers for the calling THREAD's DPI awareness, so asking again under a
        // per-monitor-aware context is what reveals the real desktop. Per-thread and reversible -
        // nothing about the process, or any window, is changed. Restored in a finally: leaving the
        // calling thread permanently per-monitor-aware would silently change every later
        // PointToScreen and GetSystemMetrics on it.
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        (double Left, double Top, double Width, double Height) real;
        try { real = previous == IntPtr.Zero ? seen : VirtualScreen(); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }

        var physicalX = real.Left + ((screenPoint.X - seen.Left) * real.Width / seen.Width);
        var physicalY = real.Top + ((screenPoint.Y - seen.Top) * real.Height / seen.Height);

        // Absolute mouse input is normalized to 0..65535 across the virtual desktop, not given in
        // pixels. Ceiling, not Round: Windows maps a normalized value back with a floor, so rounding
        // down lands one pixel short of the target - at width 1920, pixel 3 needs 102.4 and rounding
        // gives 102, which floors back to pixel 2.
        var x = (int)Math.Ceiling((physicalX - real.Left) * 65535.0 / (real.Width - 1));
        var y = (int)Math.Ceiling((physicalY - real.Top) * 65535.0 / (real.Height - 1));

        Send(MoveFlag | AbsoluteFlag | VirtualDeskFlag,
             Math.Clamp(x, 0, 65535), Math.Clamp(y, 0, 65535));
    }

    private static (double Left, double Top, double Width, double Height) VirtualScreen() =>
    (
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
        Math.Max(1, GetSystemMetrics(SmCyVirtualScreen))
    );

    public static void PressLeft()   => Send(LeftDownFlag);
    public static void ReleaseLeft() => Send(LeftUpFlag);

    /// <summary>
    /// Holds Ctrl down at the OS level, so <c>Keyboard.Modifiers</c> reports it the way it does for
    /// a real Ctrl+click.
    ///
    /// <para>This goes to whichever window is in the FOREGROUND, not to the one being clicked, and
    /// the resulting key state is global - so the caller must have clicked the window first to bring
    /// it forward. If it did not, the modifier is pressed into whatever is really in front (the
    /// console running the tests, say); the test then fails rather than passing wrongly, but that is
    /// luck, not design. Always release in a <c>finally</c>: a key left down by a failed test stays
    /// down on the machine.</para>
    /// </summary>
    public static void HoldControl()    => SendKey(ControlKey, down: true);
    public static void ReleaseControl() => SendKey(ControlKey, down: false);

    private const ushort ControlKey = 0x11;   // VK_CONTROL
    private const uint KeyUpFlag    = 0x0002; // KEYEVENTF_KEYUP

    private static void SendKey(ushort virtualKey, bool down) => Send(new Input
    {
        Type = 1,   // INPUT_KEYBOARD
        Payload = new InputPayload
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = down ? 0 : KeyUpFlag },
        },
    });

    private static void Send(uint flags, int x = 0, int y = 0) => Send(new Input
    {
        Type = 0,   // INPUT_MOUSE
        Payload = new InputPayload { Mouse = new MouseInput { Dx = x, Dy = y, Flags = flags } },
    });

    private static void Send(Input input)
    {
        var sent = SendInput(1, [input], Marshal.SizeOf<Input>());
        if (sent != 1)
            throw new InvalidOperationException(
                $"SendInput sent {sent} of 1 events (Win32 error {Marshal.GetLastWin32Error()}). " +
                "UIPI blocks input into a higher-integrity window, so this usually means something " +
                "elevated is in the foreground.");
    }
}
