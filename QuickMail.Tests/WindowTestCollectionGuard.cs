using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using Xunit.v3;

// Applied to the whole assembly, so every test is checked, including ones written after this.
[assembly: QuickMail.Tests.WindowTestCollectionGuard]

namespace QuickMail.Tests;

/// <summary>
/// Fails any test that activates a window from a collection that runs alongside the "WpfTests"
/// collection.
///
/// <para><b>Why.</b> xUnit runs test collections in parallel, and a class without a
/// <c>[Collection]</c> is a collection of its own. The focus and tab-order tests in "WpfTests" read
/// <see cref="System.Windows.Input.Keyboard.FocusedElement"/>, walk <c>MoveFocus</c>, or open
/// context menus, and every one of those needs the test's window to keep Windows' keyboard focus.
/// All test threads share one process, so a window activated on ANY other test thread at the same
/// moment takes the foreground; the WpfTests window is then deactivated, WPF drops its keyboard
/// focus to null, and a context menu closes. The test that happened to be running fails with
/// "focus is null", a tab walk that stops part-way, or a menu that never opened. It was a different
/// test each time, about one CI run in five, and a rerun passed. Classes that showed activated
/// windows outside the collection were the cause (SoundTutorialTests, AddressSuggestionTests,
/// TreeViewItemRealizerTests); the rate rose when SoundTutorialTests landed.</para>
///
/// <para>Keeping every window-showing test in "WpfTests" (which runs its classes one at a time),
/// or in a collection with <c>DisableParallelization = true</c> (which runs on its own), means only
/// one test at a time can own the foreground. This attribute fails the test that activated a window
/// in the wrong place, every run, naming the fix, rather than waiting for the race to happen.</para>
///
/// <para><b>How it detects one.</b> A per-thread <c>WH_CBT</c> hook, installed on the test's own STA
/// thread for the length of the test, sees <c>HCBT_ACTIVATE</c>, which Windows raises inside
/// <c>Show()</c> itself as the window is activated. That is the harm itself, and it does not depend on
/// the dispatcher being pumped: a test that shows, asserts and closes a window without ever pumping
/// still trips it (<see cref="WindowTestCollectionGuardTests"/> proves both). A window constructed
/// but never shown, or shown with <c>ShowActivated = false</c>, takes no focus and is not counted.</para>
///
/// <para><b>What it cannot see.</b> Only windows activated on the test's own thread: one shown on a
/// thread the test starts, through another thread's dispatcher, or in a child process is invisible
/// to it. A test that shows a window in any of those ways must still go in "WpfTests" or a
/// collection with <c>DisableParallelization = true</c>.</para>
/// </summary>
public sealed class WindowTestCollectionGuardAttribute : BeforeAfterTestAttribute
{
    [ThreadStatic] private static ActivationWatch? _watch;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        _watch?.Dispose();
        // WPF windows can only be shown from an STA thread; a plain [Fact] on a pool thread cannot
        // show one, so it needs no hook.
        _watch = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? ActivationWatch.Start() : null;
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var watch = _watch;
        _watch = null;
        if (watch is null) return;
        var activated = watch.Activated;
        watch.Dispose();
        if (!activated) return;

        if (RunsAloneOrInWpfTests(test.TestCase.TestCollection.CollectionDefinition)) return;

        throw new InvalidOperationException(
            $"{test.TestCase.TestClassName}.{methodUnderTest.Name} showed a window, but its class is not in " +
            "the \"WpfTests\" collection. Add [Collection(\"WpfTests\")] to the class. A window shown from a " +
            "collection that runs in parallel with WpfTests takes keyboard focus from whichever focus test is " +
            "running there, and that test fails at random (see WindowTestCollectionGuard.cs).");
    }

    /// <summary>
    /// True for the WpfTests collection, and for a collection that runs on its own
    /// (<c>DisableParallelization = true</c>), since nothing else is running to lose focus to it.
    /// </summary>
    internal static bool RunsAloneOrInWpfTests(Type? collectionDefinition)
    {
        if (collectionDefinition is null) return false;
        if (collectionDefinition == typeof(WpfTestsCollection)) return true;
        return collectionDefinition.GetCustomAttribute<CollectionDefinitionAttribute>()?.DisableParallelization == true;
    }

    /// <summary>
    /// Records whether any window is activated on the calling thread between <see cref="Start"/> and
    /// <see cref="Dispose"/>. Must be disposed on the thread that started it.
    /// </summary>
    internal sealed class ActivationWatch : IDisposable
    {
        private const int WH_CBT = 5;
        private const int HCBT_ACTIVATE = 5;

        private readonly HookProc _proc;   // held so the delegate outlives the native hook
        private IntPtr _hook;

        public bool Activated { get; private set; }

        private ActivationWatch()
        {
            _proc = OnCbt;
            _hook = SetWindowsHookEx(WH_CBT, _proc, IntPtr.Zero, GetCurrentThreadId());
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        }

        public static ActivationWatch Start() => new();

        private IntPtr OnCbt(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == HCBT_ACTIVATE) Activated = true;
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
