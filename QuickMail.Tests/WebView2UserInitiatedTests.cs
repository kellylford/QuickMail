using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// A navigation leaves a message body only when WebView2 reports it user-initiated AND it matches a link
/// the user activated (#728 security review; see LinkActivationGate). A document-started navigation,
/// such as a meta refresh, must go nowhere. This checks
/// that premise against a real WebView2 both ways round. A meta refresh must NOT count as the user's,
/// and a link activated by keyboard or mouse MUST, or every link in every message stops opening.
/// </summary>
[Collection("WpfTests")]
public class WebView2UserInitiatedTests
{
    [StaFact]
    public void MetaRefreshIsNotUserInitiated_ButAnActivatedLinkIs()
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailNav-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 400, Height = 300, Left = -10000, Top = -10000,
        };
        window.Show();
        CoreWebView2Controller? controller = null;
        try
        {
            var seen = new List<(string Uri, bool User)>();
            Run(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 400, 300);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                core.NavigationStarting += (_, e) =>
                {
                    if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri.StartsWith("about:", StringComparison.Ordinal)) return;
                    lock (seen) seen.Add((e.Uri, e.IsUserInitiated));
                    e.Cancel = true;
                };

                // 1. A document that navigates by itself.
                core.NavigateToString("<meta http-equiv=\"refresh\" content=\"0;url=https://example.invalid/refresh\"><p>x</p>");
                await WaitFor(() => Has(seen, "refresh"), TimeSpan.FromSeconds(10));

                // 2. A link, activated from the keyboard: Tab to it, then Enter.
                var loaded = new TaskCompletionSource<bool>();
                void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(true);
                core.NavigationCompleted += OnDone;
                core.NavigateToString(
                    "<a href=\"https://example.invalid/keyboard\">keyboard link</a> " +
                    "<a href=\"https://example.invalid/mouse\" style=\"position:absolute;left:0;top:100px;width:300px;height:80px;display:block\">mouse link</a>");
                await loaded.Task;
                core.NavigationCompleted -= OnDone;

                // Focus the link the way a Tab would land on it; the window here is never activated, so a
                // synthesized Tab has nowhere to start from. Focusing by script is not a user gesture: the
                // Enter that follows is the only one, and it is what must count.
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                await core.ExecuteScriptAsync("document.querySelector('a').focus()");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"text\":\"\\r\"}");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
                await WaitFor(() => Has(seen, "keyboard"), TimeSpan.FromSeconds(10));

                // 3. The same with the mouse.
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                    "{\"type\":\"mousePressed\",\"x\":50,\"y\":130,\"button\":\"left\",\"clickCount\":1}");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                    "{\"type\":\"mouseReleased\",\"x\":50,\"y\":130,\"button\":\"left\",\"clickCount\":1}");
                await WaitFor(() => Has(seen, "mouse"), TimeSpan.FromSeconds(10));
            }, TimeSpan.FromSeconds(60));

            lock (seen)
            {
                var report = "Navigations seen: " + string.Join("; ", seen.ConvertAll(n => $"{n.Uri} user={n.User}"));
                Assert.True(seen.Exists(n => n.Uri.EndsWith("/refresh", StringComparison.Ordinal) && !n.User), report);
                Assert.True(seen.Exists(n => n.Uri.EndsWith("/keyboard", StringComparison.Ordinal) && n.User), report);
                Assert.True(seen.Exists(n => n.Uri.EndsWith("/mouse", StringComparison.Ordinal) && n.User), report);
            }
        }
        finally
        {
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold its data folder */ }
        }
    }

    /// <summary>
    /// End to end, wired as the message windows wire it: the host activation script, the gate, and
    /// NavigationStarting. A link activated with Enter opens. A delayed refresh that fires after the
    /// reader pressed an arrow key (which WebView2 reports as user-initiated) opens nothing.
    /// </summary>
    [StaFact]
    public void TheGate_OpensAnActivatedLink_ButNotARefreshAfterAKeypress()
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailGate-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 400, Height = 300, Left = -10000, Top = -10000,
        };
        window.Show();
        CoreWebView2Controller? controller = null;
        try
        {
            var opened = new List<string>();
            var reportedUser = new List<string>();
            Run(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 400, 300);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                var gate = new QuickMail.Helpers.LinkActivationGate();
                await core.AddScriptToExecuteOnDocumentCreatedAsync(QuickMail.Helpers.LinkActivationGate.ReportActivationsScript);
                core.WebMessageReceived += (_, e) =>
                {
                    var msg = e.TryGetWebMessageAsString();
                    if (msg.StartsWith(QuickMail.Helpers.LinkActivationGate.ActivationMessagePrefix, StringComparison.Ordinal))
                        gate.NoteActivated(msg[QuickMail.Helpers.LinkActivationGate.ActivationMessagePrefix.Length..]);
                };
                core.NavigationStarting += (_, e) =>
                {
                    if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri.StartsWith("about:", StringComparison.Ordinal)) return;
                    e.Cancel = true;
                    if (!e.IsUserInitiated) return;
                    reportedUser.Add(e.Uri);
                    var target = e.Uri;
                    gate.Request(target, () => opened.Add(target));
                };

                async Task Load(string html)
                {
                    var done = new TaskCompletionSource<bool>();
                    void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(true);
                    core.NavigationCompleted += OnDone;
                    core.NavigateToString(html);
                    await done.Task;
                    core.NavigationCompleted -= OnDone;
                }
                Task Key(string type, string key, string code, int vk) =>
                    core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                        $"{{\"type\":\"{type}\",\"key\":\"{key}\",\"code\":\"{code}\",\"windowsVirtualKeyCode\":{vk}}}");

                // The attack: a refresh 1s out, and the reader arrows through the message meanwhile.
                await Load("<meta http-equiv=\"refresh\" content=\"1;url=https://example.invalid/refresh\"><p>Line one</p><p>Line two</p>");
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                await Key("keyDown", "ArrowDown", "ArrowDown", 40);
                await Key("keyUp", "ArrowDown", "ArrowDown", 40);
                await WaitFor(() => reportedUser.Exists(u => u.EndsWith("/refresh", StringComparison.Ordinal)), TimeSpan.FromSeconds(6));
                await Task.Delay(300);

                // A real link, activated with Enter.
                await Load("<a href=\"https://example.invalid/real\">real link</a>");
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                await core.ExecuteScriptAsync("document.querySelector('a').focus()");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"text\":\"\r\"}");
                await Key("keyUp", "Enter", "Enter", 13);
                await WaitFor(() => opened.Exists(u => u.EndsWith("/real", StringComparison.Ordinal)), TimeSpan.FromSeconds(6));
            }, TimeSpan.FromSeconds(60));

            var report = $"reported as user's: [{string.Join(", ", reportedUser)}]; opened: [{string.Join(", ", opened)}]";
            Assert.True(opened.Exists(u => u.EndsWith("/real", StringComparison.Ordinal)), report);
            // The premise: WebView2 DID call the refresh the user's, after the arrow key. Only the gate stops it.
            Assert.True(reportedUser.Exists(u => u.EndsWith("/refresh", StringComparison.Ordinal)), report);
            Assert.False(opened.Exists(u => u.EndsWith("/refresh", StringComparison.Ordinal)), report);
        }
        finally
        {
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold its data folder */ }
        }
    }

    private static bool Has(List<(string Uri, bool User)> seen, string suffix)
    {
        lock (seen) return seen.Exists(n => n.Uri.EndsWith("/" + suffix, StringComparison.Ordinal));
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until) return;   // the assertions report what was missing
            await Task.Delay(50);
        }
    }

    private static void Run(Func<Task> operation, TimeSpan timeout)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = operation();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            var timer = new DispatcherTimer(timeout, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Assert.True(task.IsCompleted, "Timed out driving the WebView2.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
