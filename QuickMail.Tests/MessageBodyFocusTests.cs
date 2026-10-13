using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Opening a message must put a screen reader straight into reading it, and Ctrl+A, Ctrl+C must copy the
/// whole message. 0.8.55 dropped the body's document role to fix the copy (#786), and that left the
/// reader on the page's title until Tab was pressed. The role is back, and Ctrl+A is done by script.
/// </summary>
[Collection("WpfTests")]
public class MessageBodyFocusTests
{
    private const string Body =
        "<p>First paragraph.</p><p></p><p>Second paragraph.</p>" +
        "<table><tr><td>A cell</td></tr></table><p>Last paragraph.</p>";

    [Fact]
    public void Label_NamesTheBodyAndTheSubject()
    {
        Assert.Equal("Message body", MessageBodyFocus.Label(null));
        Assert.Equal("Message body", MessageBodyFocus.Label("   "));
        Assert.Equal("Message body. Lunch", MessageBodyFocus.Label("  Lunch  "));
        var longSubject = new string('x', 200);
        Assert.Equal("Message body. " + new string('x', 120) + "...", MessageBodyFocus.Label(longSubject));
    }

    [Fact]
    public void FocusBodyScript_WithoutALabel_SetsNoName()
    {
        var script = MessageBodyFocus.FocusBodyScript(null);
        Assert.Contains("'role','document'", script);
        Assert.DoesNotContain("aria-label", script);
    }

    /// <summary>All three windows that show a message body focus it the same way and take Ctrl+A.</summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    [InlineData("MarkdownPreviewWindow.xaml.cs")]
    public void EveryBodyWindow_UsesTheSharedScripts(string file)
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));
        Assert.Contains("MessageBodyFocus.FocusBodyScript(", code);
        Assert.Contains("MessageBodyFocus.SelectAllScript", code);
    }

    /// <summary>
    /// In a real WebView2: the body is focused as a document carrying its label (quotes and markup in a
    /// subject survive the trip into script), and Ctrl+A then selects the body from its first child to its
    /// last. Without a screen reader Chromium's own select-all would take everything too, so the text
    /// alone proves nothing about the script; <see cref="ARealWebView2_CtrlAInTheBody_IsClaimed"/> does.
    /// The first-paragraph copy itself only happens with a screen reader running and is checked by ear.
    /// </summary>
    [StaFact]
    public void ARealWebView2_FocusesTheBodyAsADocument_AndCtrlASelectsAll()
    {
        const string label = "Message body. \"Quotes\" & </script><b>tags</b>";
        string? focused = null, role = null, name = null, selection = null, ends = null;

        WithWebView2(Body, async (controller, core) =>
        {
            focused = await core.ExecuteScriptAsync(MessageBodyFocus.FocusBodyScript(label));
            role = await core.ExecuteScriptAsync("document.body.getAttribute('role')");
            name = await core.ExecuteScriptAsync("document.body.getAttribute('aria-label')");
            await PressCtrlA(core);
            selection = await core.ExecuteScriptAsync("window.getSelection().toString()");
            // selectAllChildren anchors the selection on the body itself, at offsets 0 and childNodes.length.
            ends = await core.ExecuteScriptAsync(
                "(()=>{const s=window.getSelection(),b=document.body;" +
                "return s.anchorNode===b&&s.anchorOffset===0&&s.focusNode===b&&s.focusOffset===b.childNodes.length;})()");
        });

        Assert.Equal("true", focused);
        Assert.Equal("true", ends);
        Assert.Equal("\"document\"", role);
        Assert.Equal(label, JsonSerializer.Deserialize<string>(name!));
        var text = JsonSerializer.Deserialize<string>(selection!)!;
        Assert.Contains("First paragraph.", text);
        Assert.Contains("Second paragraph.", text);
        Assert.Contains("A cell", text);
        Assert.Contains("Last paragraph.", text);
    }

    /// <summary>A field that takes text keeps its own Ctrl+A: the body is not selected out from under it.</summary>
    [StaFact]
    public void ARealWebView2_CtrlAInATextField_IsLeftToTheField()
    {
        string? selection = null, prevented = null;

        WithWebView2("<p>Outside text.</p><input id='f' value='abc'>", async (controller, core) =>
        {
            await WatchDefaultPrevented(core);
            await core.ExecuteScriptAsync("document.getElementById('f').focus()");
            await PressCtrlA(core);
            selection = await core.ExecuteScriptAsync("window.getSelection().toString()");
            prevented = await core.ExecuteScriptAsync("window.__qmPrevented");
        });

        Assert.DoesNotContain("Outside text.", JsonSerializer.Deserialize<string>(selection!)!);
        Assert.Equal("false", prevented);   // the field's own select-all still runs
    }

    /// <summary>Ctrl+A in the body is claimed, so Chromium's truncating select-all never runs after it.</summary>
    [StaFact]
    public void ARealWebView2_CtrlAInTheBody_IsClaimed()
    {
        string? prevented = null;

        WithWebView2(Body, async (controller, core) =>
        {
            await WatchDefaultPrevented(core);
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusBodyScript("Message body"));
            await PressCtrlA(core);
            prevented = await core.ExecuteScriptAsync("window.__qmPrevented");
        });

        Assert.Equal("true", prevented);
    }

    // A bubbling listener runs after the select-all script's capturing one, so it sees its verdict.
    private static Task WatchDefaultPrevented(CoreWebView2 core) => core.ExecuteScriptAsync(
        "window.__qmPrevented=null;" +
        "window.addEventListener('keydown',function(e){if(e.code==='KeyA')window.__qmPrevented=e.defaultPrevented;});");

    /// <summary>Ctrl+Shift+A and Alt+A are other commands' keys; neither selects the body.</summary>
    [StaFact]
    public void ARealWebView2_OnlyPlainCtrlA_SelectsAll()
    {
        string? selection = null;

        WithWebView2(Body, async (controller, core) =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusBodyScript(null));
            await Press(core, 2 | 8);   // Ctrl+Shift+A
            await Press(core, 1);       // Alt+A
            selection = await core.ExecuteScriptAsync("window.getSelection().toString()");
        });

        Assert.Equal("\"\"", selection);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static Task PressCtrlA(CoreWebView2 core) => Press(core, 2);

    // CDP modifiers: 1 Alt, 2 Ctrl, 8 Shift.
    private static async Task Press(CoreWebView2 core, int modifiers)
    {
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            $"{{\"type\":\"rawKeyDown\",\"modifiers\":{modifiers},\"key\":\"a\",\"code\":\"KeyA\",\"windowsVirtualKeyCode\":65}}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            $"{{\"type\":\"keyUp\",\"modifiers\":{modifiers},\"key\":\"a\",\"code\":\"KeyA\",\"windowsVirtualKeyCode\":65}}");
    }

    private static void WithWebView2(string html, Func<CoreWebView2Controller, CoreWebView2, Task> act)
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailFocus-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 300, Height = 200, Left = -10000, Top = -10000,
        };
        window.Show();
        CoreWebView2Controller? controller = null;
        try
        {
            RunAsync(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 300, 200);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(MessageBodyFocus.SelectAllScript);
                var done = new TaskCompletionSource<bool>();
                core.NavigationCompleted += (_, _) => done.TrySetResult(true);
                core.NavigateToString(html);
                await done.Task;
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                await act(controller, core);
            }, TimeSpan.FromSeconds(60));
        }
        finally
        {
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold it */ }
        }
    }

    private static void RunAsync(Func<Task> operation, TimeSpan timeout)
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
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "QuickMail", "Views"))) return d.FullName;
        throw new InvalidOperationException("Repo source tree not found.");
    }
}
