using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Xunit;
using Xunit.v3;

// Applied to the whole assembly: After() runs on the test's own thread once the test body has
// finished, which is the only place a per-test STA thread's Dispatcher can still be shut down
// while its owner is alive.
[assembly: QuickMail.Tests.ShutDownStaDispatcher]

namespace QuickMail.Tests;

/// <summary>
/// Fixes the test-host crash tracked as issue #211 — the one that silently cost between 4% and 46%
/// of the suite on every CI run.
///
/// <para>
/// Xunit.StaFact runs each <c>[StaFact]</c> on a FRESH STA thread and does not shut that thread's
/// <see cref="Dispatcher"/> down when the test ends. Measured directly: a later test reported
/// <c>priorTestThread=9 priorAlive=False priorDispatcherShutdownStarted=False</c>. A Dispatcher owns
/// a message-only HWND, so every StaFact test left one behind on a dead thread — a hundred-odd of
/// them per run. When a stray window message reaches any of them,
/// <c>MS.Win32.HwndSubclass.SubclassWndProc</c> calls <c>Thread.get_CurrentThread()</c>, which is
/// null because the owning managed thread is gone, and the NullReferenceException takes down the
/// whole xUnit host. Tests that had not run yet simply never appear in the trx: `failed` stays 0 and
/// the job goes green having skipped hundreds of cases. That is why the loss varied so wildly between
/// runs — it depends on which orphan happens to get a message, and when.
/// </para>
///
/// <para>
/// Shutting the Dispatcher down destroys that HWND while the thread is still alive, so there is
/// nothing left to receive a message. The Application's own Dispatcher must NOT be caught by this,
/// which is why <see cref="WpfTestHost"/> puts it on a dedicated thread that outlives every test.
/// </para>
/// </summary>
public sealed class ShutDownStaDispatcherAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        // Only STA test threads have one, and only ones that actually used WPF.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return;

        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        // Never the Application's — it lives on WpfTestHost's dedicated thread and must survive the
        // whole run. Guarded anyway: if some future test creates an Application inline, shutting its
        // dispatcher down here would break every later test in a way that looks nothing like the cause.
        if (System.Windows.Application.Current?.Dispatcher == dispatcher) return;

        // InvokeShutdown, not BeginInvokeShutdown: this must complete before the thread ends, and we
        // are already on that thread, so it runs synchronously.
        dispatcher.InvokeShutdown();
    }
}
