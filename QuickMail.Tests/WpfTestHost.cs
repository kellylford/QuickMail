using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace QuickMail.Tests;

/// <summary>
/// Owns the single WPF <see cref="Application"/> the test run needs, on a thread that outlives the
/// run (issue #211).
///
/// <para>
/// Every WPF test class used to create it inline — nine copies of the same
/// <c>if (Application.Current == null) new Application(...)</c>. Whichever <c>[StaFact]</c> ran first
/// therefore owned it, and Xunit.StaFact gives each test a fresh STA thread that ends with the test.
/// Measured: <c>creatorThread=9 creatorAlive=False appDispatcherThread=9 appDispatcherAlive=False
/// shutdownStarted=False</c> — the Application spent the rest of the run with its Dispatcher pinned to
/// a dead thread, and that Dispatcher's message-only HWND is one of the orphans that crashes the host
/// (see <see cref="ShutDownStaDispatcherAttribute"/> for the full mechanism).
/// </para>
///
/// <para>
/// Here it gets a dedicated STA thread that runs a dispatcher loop for the life of the process, so it
/// is never on a thread that dies and <see cref="ShutDownStaDispatcherAttribute"/> never touches it.
/// The thread is a background thread, so it cannot keep the process alive at the end of the run.
/// Windows are still created on each test's own STA thread, exactly as before — that was already the
/// arrangement and is not what was crashing.
/// </para>
/// </summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static Thread? _host;
    private static Application? _app;

    private static Uri StyleUri(string name) =>
        new($"pack://application:,,,/QuickMail;component/Styles/{name}.xaml", UriKind.Absolute);

    /// <summary>
    /// Ensures an <see cref="Application"/> exists with the accessible styles merged in, and returns
    /// once it is usable. Idempotent and safe to call from every test.
    /// </summary>
    public static void EnsureApplication()
    {
        lock (Gate)
        {
            if (_host is not null) return;

            // An Application created by something else (a stray inline copy) would make the dedicated
            // thread pointless and reintroduce the orphan, so fail loudly rather than limp on.
            if (Application.Current is not null)
                throw new InvalidOperationException(
                    "An Application already exists, so it was created outside WpfTestHost — probably an " +
                    "inline `new Application(...)` in a test class. That puts its Dispatcher on a " +
                    "per-test STA thread that dies with the test, which is issue #211. Call " +
                    "WpfTestHost.EnsureApplication() instead.");

            using var ready = new ManualResetEventSlim();
            Exception? failure = null;

            _host = new Thread(() =>
            {
                try
                {
                    _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    _app.Resources.MergedDictionaries.Add(
                        new ResourceDictionary { Source = StyleUri("AccessibleStyles") });
                }
                catch (Exception ex) { failure = ex; }
                finally { ready.Set(); }

                // Keeps this thread — and so the Application's Dispatcher and its HWND — alive for the
                // whole run. Never shut down: the thread is a background thread, so the process still
                // exits normally.
                if (failure is null) Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WPF test Application host",
            };
            _host.SetApartmentState(ApartmentState.STA);
            _host.Start();
            ready.Wait();

            if (failure is not null)
            {
                _host = null;
                throw new InvalidOperationException("Could not start the WPF test Application host.", failure);
            }
        }
    }

    /// <summary>
    /// Merges additional style dictionaries — some suites need ThemedControls as well, or the XAML
    /// fails to parse with "Cannot find resource named 'System.Windows.Controls.ListViewItem'".
    ///
    /// The merge is marshalled onto the host thread. Reading resources across threads is what the
    /// suite has always done and works; MUTATING another thread's ResourceDictionary is not, and is
    /// the kind of thing that fails once in a hundred runs rather than reliably.
    /// </summary>
    public static void EnsureStyles(params string[] names)
    {
        EnsureApplication();
        var app = _app!;
        app.Dispatcher.Invoke(() =>
        {
            foreach (var name in names)
            {
                var uri = StyleUri(name);
                if (app.Resources.MergedDictionaries.All(d => d.Source != uri))
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
            }
        });
    }
}
