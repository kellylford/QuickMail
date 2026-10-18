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
    /// <para><b>Why this is needed.</b> Xunit.StaFact rents a fresh STA thread per TEST CASE, not per
    /// row. By default xUnit v3 pre-enumerates a theory whose data is serializable, so each
    /// <c>[InlineData]</c> row is its own <c>UITestCase</c> on its own thread, and nothing here
    /// applies. But a theory that is delay-enumerated — <c>DisableDiscoveryEnumeration = true</c>, or
    /// data xUnit cannot serialize — is ONE test case, StaFact's <c>UIDelayEnumeratedTestCase</c> (a
    /// <see cref="XunitDelayEnumeratedTheoryTestCase"/>), and all of its rows run one after another
    /// on that one rented thread. Shutting the Dispatcher down after the first row then leaves the
    /// remaining rows running on a thread whose Dispatcher is dead, and the first WPF operation in
    /// them throws "Cannot perform requested operation because the Dispatcher shut down". Shutting
    /// down after the LAST row keeps the issue #211 protection intact — the thread still never
    /// outlives its Dispatcher — without breaking the rows in between.
    /// <c>StaDispatcherShutdownTests</c> covers both shapes.</para>
    ///
    /// <para><b>Only for delay-enumerated cases.</b> A pre-enumerated row is its own test case on its
    /// own thread; deferring there would mean the count is never reached on any of them and every
    /// row leaks its Dispatcher — the #211 crash. So anything that is not a delay-enumerated theory
    /// case shuts down after the row, as before.</para>
    ///
    /// <para><b>Rows are counted rather than detected</b>: xUnit exposes no "is this the last row"
    /// flag. When the count cannot be trusted this returns false, and the Dispatcher is shut down
    /// after the first row — better to break later rows loudly than to leak a Dispatcher onto a thread
    /// that is about to die. That is the case for:</para>
    /// <list type="bullet">
    /// <item><c>[MemberData]</c>, <c>[ClassData]</c> or any other data source whose rows are only
    /// known after running it. Pre-enumerated, their rows are separate test cases and are fine; but
    /// a DELAY-enumerated StaTheory fed that way still gets per-row shutdown, so its rows after the
    /// first will fail if they touch WPF — use <c>[InlineData]</c>, serializable data, or separate
    /// StaFacts.</item>
    /// <item>An <c>[InlineData]</c> row with <c>Skip</c>, <c>SkipUnless</c>, <c>SkipWhen</c> or
    /// <c>Explicit</c> set. A row skipped that way never reaches <c>After</c>, so counting it would
    /// leave the total unreachable and leak the Dispatcher.</item>
    /// </list>
    /// </summary>
    private static bool MoreRowsToCome(IXunitTest test)
    {
        if (test.TestCase is not XunitDelayEnumeratedTheoryTestCase) return false;
        if (ExpectedRows(test.TestMethod.DataAttributes) is not { } total || total <= 1) return false;

        var key = test.TestCase.UniqueID;
        lock (RowsRun)
        {
            var run = RowsRun.TryGetValue(key, out var already) ? already + 1 : 1;
            if (run >= total)
            {
                RowsRun.Remove(key);
                return false;
            }
            RowsRun[key] = run;
            return true;
        }
    }

    /// <summary>Rows completed so far, per delay-enumerated test case (its <c>UniqueID</c>).</summary>
    private static readonly Dictionary<string, int> RowsRun = [];

    /// <summary>
    /// How many rows a theory with these data attributes will run through <c>After</c>, or null when
    /// that cannot be known without running the data source (see <see cref="MoreRowsToCome"/>).
    /// </summary>
    internal static int? ExpectedRows(IReadOnlyCollection<IDataAttribute> dataAttributes)
    {
        if (dataAttributes.Count == 0) return 1;   // not a theory

        var rows = 0;
        foreach (var data in dataAttributes)
        {
            // One row each, and countable without running the data source.
            if (data is not InlineDataAttribute) return null;

            // A row that may not run would leave the total unreachable.
            if (data.Skip is not null || data.SkipUnless is not null || data.SkipWhen is not null
                || data.Explicit == true)
                return null;

            rows++;
        }
        return rows;
    }
}
