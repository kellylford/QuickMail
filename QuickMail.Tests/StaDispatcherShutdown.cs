using System;
using System.Collections.Generic;
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

        // Not yet if more rows of this theory are still to come — see MoreRowsToCome.
        if (MoreRowsToCome(test)) return;

        // InvokeShutdown, not BeginInvokeShutdown: this must complete before the thread ends, and we
        // are already on that thread, so it runs synchronously.
        dispatcher.InvokeShutdown();
    }

    /// <summary>
    /// True while a <c>[StaTheory]</c> still has data rows left to run on this thread.
    ///
    /// <para><b>Why this is needed.</b> Xunit.StaFact gives each test METHOD a fresh STA thread, but
    /// every data row of one theory shares that thread — measured: both rows of a two-row StaTheory
    /// reported the same managed thread id, while each StaFact got its own. Shutting the Dispatcher
    /// down after the first row therefore leaves the remaining rows running on a thread whose
    /// Dispatcher is dead, and the first WPF operation in them throws "Cannot perform requested
    /// operation because the Dispatcher shut down". That is how
    /// <c>AccountDialogHintTests.TheSmtpSslHintNamesBothPorts</c> failed on its second row while
    /// passing on its first, and it would silently claim any future StaTheory whose later rows touch
    /// WPF. Shutting down after the LAST row keeps the issue #211 protection intact — the thread
    /// still never outlives its Dispatcher — without breaking the rows in between.</para>
    ///
    /// <para>Rows are counted rather than detected: xUnit exposes no "is this the last case" flag,
    /// and <see cref="IXunitTestMethod"/> has no test-case collection to ask. When the expected count
    /// cannot be worked out (a data source other than <c>[InlineData]</c>, whose rows are only known
    /// after running it), this returns false and the old behaviour stands: better to shut down early
    /// than to leak a Dispatcher onto a thread that is about to die. The same applies to a filtered
    /// run where some rows are never executed — the count is simply never reached, and one Dispatcher
    /// survives on a dying thread, which is the pre-existing hazard rather than a new one.</para>
    /// </summary>
    private static bool MoreRowsToCome(IXunitTest test)
    {
        var expected = ExpectedRows(test.TestMethod);
        if (expected is not { } total || total <= 1) return false;

        var key = (Environment.CurrentManagedThreadId, test.TestMethod.Method.MetadataToken);
        lock (RowsRun)
        {
            var run = RowsRun.TryGetValue(key, out var already) ? already + 1 : 1;
            RowsRun[key] = run;
            return run < total;
        }
    }

    /// <summary>Rows completed so far, per (thread, test method).</summary>
    private static readonly Dictionary<(int Thread, int Method), int> RowsRun = [];

    private static int? ExpectedRows(IXunitTestMethod method)
    {
        if (method.DataAttributes.Count == 0) return 1;   // a plain [StaFact]

        var rows = 0;
        foreach (var data in method.DataAttributes)
        {
            // One row each, and countable without running the data source.
            if (data is not InlineDataAttribute) return null;
            rows++;
        }
        return rows;
    }
}
