using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Opening a message must put a screen reader straight into reading it, and Ctrl+A, Ctrl+C must copy the
/// whole message. A role or label on the body cut the copy to one paragraph (#786); a body focused with
/// neither left the reader on the page title until Tab. Focus now goes to the first text instead.
/// Neither failure happens without a screen reader running, so these tests guard the mechanism; the
/// result itself is checked by ear.
/// </summary>
[Collection("WpfTests")]
public class MessageBodyFocusTests
{
    [Fact]
    public void Label_NamesTheBodyAndTheSubject()
    {
        Assert.Equal("Message body", MessageBodyFocus.Label(null));
        Assert.Equal("Message body", MessageBodyFocus.Label("   "));
        Assert.Equal("Message body. Lunch", MessageBodyFocus.Label("  Lunch  "));
        var longSubject = new string('x', 200);
        Assert.Equal("Message body. " + new string('x', 120) + "...", MessageBodyFocus.Label(longSubject));
    }

    /// <summary>All three windows that show a message body focus it the same way.</summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    [InlineData("MarkdownPreviewWindow.xaml.cs")]
    public void EveryBodyWindow_UsesTheSharedScript(string file)
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));
        Assert.Contains("MessageBodyFocus.FocusScript", code);
    }

    /// <summary>
    /// The two windows that open messages focus them with the render's token (#959): the plain script,
    /// run while the previous message's page is still on show, reports focus in the wrong document.
    /// The Markdown preview shows one document and never replaces it, so it keeps the plain script.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void EveryMessageWindow_UsesTheRenderTokenScript(string file)
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));
        Assert.Contains("MessageBodyFocus.FocusScriptFor(", code);
        Assert.Contains("MessageBodyFocus.FocusUntilReadyAsync(", code);
        Assert.Contains("MessageBodyHtmlBuilder.WithRenderToken(", code);
        Assert.Contains("MessageBodyNavigation.Navigate(", code);
        Assert.DoesNotContain("MessageBodyFocus.FocusScript)", code);
        // The old wait took the first NavigationCompleted to come along, whichever navigation it was.
        Assert.DoesNotContain("NavigationCompleted +=", code);
    }

    /// <summary>The text of the method whose declaration contains <paramref name="signature"/>, up to its closing brace.</summary>
    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No method {signature}.");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"No end to {signature}.");
        return code[start..end];
    }

    private static string View(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", file));

    /// <summary>
    /// #959 review: the open loop's own first step (focus to the host) raises the host's
    /// GotKeyboardFocus, whose handler focuses the page without waiting for the load. It must stand
    /// aside while the loop for the current render runs, before it reaches the focus script.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs", "void MessageBody_GotKeyboardFocus(", "FocusScriptFor(", "_messageBodyRenderVersion")]
    [InlineData("MessageWindow.xaml.cs", "void MessageBody_GotKeyboardFocus(", "TryFocusDocumentAsync()", "_renderVersion")]
    public void GotKeyboardFocus_StandsAsideWhileTheOpenLoopRuns(string file, string handler, string focuses, string version)
    {
        var body = Method(View(file), handler);
        var guard = body.IndexOf($"_openFocus.IsRunning({version})) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The handler does not check the open loop.");
        Assert.True(guard < body.IndexOf(focuses, StringComparison.Ordinal), "The check comes after the focus script.");
    }

    /// <summary>
    /// One loop per render (F6 during an open must not start a second that announces again), always
    /// released in a finally, and the loop stops when the user moves focus away; after it, the host
    /// step and the announcement only when <c>ShouldFinish</c> says so.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml.cs")]
    [InlineData("MessageWindow.xaml.cs")]
    public void OpenLoop_IsGatedAndFinishesOnlyWhenItShould(string file)
    {
        var body = Method(View(file), "private async Task FocusMessageBodyAsync(");
        Assert.Contains("_openFocus.TryBegin(", body);
        Assert.Contains("finally", body);
        Assert.Contains("_openFocus.End(", body);
        Assert.Contains("MessageBodyHoldsFocus", body);
        var finish = body.IndexOf("MessageOpenFocusPolicy.ShouldFinish(result, MessageBodyHoldsFocus)", StringComparison.Ordinal);
        Assert.True(finish >= 0);
        Assert.True(finish < body.IndexOf("AccessibilityHelper.Announce(", StringComparison.Ordinal));
        Assert.True(finish < body.LastIndexOf("FocusMessageBodyHost();", StringComparison.Ordinal));
    }

    /// <summary>
    /// Closing a window, or the reading pane, mid-open ends the render: the loop stops (no log line,
    /// no announcement on a closed window) and picture requests still waiting are released.
    /// </summary>
    [Theory]
    [InlineData("MessageWindow.xaml.cs", "private void OnClosing(", "Interlocked.Increment(ref _renderVersion)")]
    [InlineData("MainWindow.xaml.cs", "protected override void OnClosed(", "Interlocked.Increment(ref _messageBodyRenderVersion)")]
    [InlineData("MainWindow.xaml.cs", "private void CloseReadingPane(", "Interlocked.Increment(ref _messageBodyRenderVersion)")]
    public void Closing_EndsTheRender(string file, string method, string advance)
    {
        var body = Method(View(file), method);
        Assert.Contains(advance, body);
        Assert.Contains("_pictureHost?.End();", body);
    }

    private static readonly string[] TrueOrFalse = ["true", "false"];

    [Fact]
    public void FocusScriptFor_EmbedsTheTokenAndTheSharedScript()
    {
        var script = MessageBodyFocus.FocusScriptFor("42");

        Assert.Contains("const want=\"42\";", script);
        Assert.Contains("'" + MessageBodyHtmlBuilder.RenderTokenAttribute + "'", script);
        Assert.Contains("return 'stale'", script);
        Assert.Contains(MessageBodyFocus.FocusScript, script);
    }

    /// <summary>
    /// A page whose load event has not fired is not ready (#959: that is the state that left the reader
    /// on the title), until the loop stops waiting for the load; then only an unparsed page is.
    /// </summary>
    [Fact]
    public void Scripts_WaitForTheLoad_UnlessTold()
    {
        foreach (var script in new[] { MessageBodyFocus.FocusScriptFor("1"), MessageBodyFocus.ReadinessScriptFor("1") })
        {
            Assert.Contains("document.readyState!=='complete')return 'loading'", script);
            Assert.Contains("return 'stale'", script);
        }
        foreach (var script in new[]
                 {
                     MessageBodyFocus.FocusScriptFor("1", waitForLoad: false),
                     MessageBodyFocus.ReadinessScriptFor("1", waitForLoad: false),
                 })
        {
            Assert.DoesNotContain("'complete'", script);
            Assert.Contains("document.readyState==='loading')return 'loading'", script);
        }
        Assert.DoesNotContain(MessageBodyFocus.FocusScript, MessageBodyFocus.ReadinessScriptFor("1"));
        Assert.Contains("return 'ready'", MessageBodyFocus.ReadinessScriptFor("1"));
    }

    [Fact]
    public void FocusScriptFor_EncodesTheToken() =>
        Assert.Contains("const want=\"a\\u0027b\\u0022\";", MessageBodyFocus.FocusScriptFor("a'b\""));

    /// <summary>
    /// Against a real WebView2 (#959): the script for this render focuses its page; the script for any
    /// other render answers "stale" and leaves the page untouched.
    /// </summary>
    [StaFact]
    public void ARealWebView2_TokenScript_FocusesThisRender_AndIsStaleForAnother()
    {
        var html = MessageBodyHtmlBuilder.WithRenderToken(
            MessageBodyHtmlBuilder.BuildPlainTextHtmlDocument("Lunch", "Hello Kelly,\nSecond line.", note: null), "5");
        string? previous = null, previousActive = null, wrapped = null, current = null, active = null;
        string? readyPrevious = null, readyCurrent = null;

        WithWebView2(html, async core =>
        {
            readyPrevious = await core.ExecuteScriptAsync(MessageBodyFocus.ReadinessScriptFor("4"));
            readyCurrent = await core.ExecuteScriptAsync(MessageBodyFocus.ReadinessScriptFor("5"));
            previous = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScriptFor("4"));
            previousActive = await core.ExecuteScriptAsync("document.activeElement===document.body||document.activeElement===null");
            wrapped = await core.ExecuteScriptAsync("document.body.querySelectorAll('span').length");
            current = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScriptFor("5"));
            active = await core.ExecuteScriptAsync("document.activeElement.textContent");
        });

        Assert.Equal("\"stale\"", readyPrevious);
        Assert.Equal("\"ready\"", readyCurrent);
        Assert.Equal("\"stale\"", previous);
        Assert.Equal("true", previousActive);
        Assert.Equal("0", wrapped);
        // true, or false where the test window is never the foreground (document.hasFocus); never stale.
        Assert.Contains(current, TrueOrFalse);
        Assert.Equal("\"Hello Kelly,\"", active);
    }

    /// <summary>
    /// No view gives the body a role or a name: either one brought back the one-paragraph copy (#786),
    /// and it looks like the obvious fix for reading not starting, which is how it came back once.
    /// </summary>
    [Fact]
    public void NoView_GivesTheBodyARoleOrALabel()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "QuickMail", "Views"), "*.cs"))
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain("'role','document'", code);
            Assert.DoesNotContain("body.setAttribute('aria-label'", code);
        }
    }

    [StaFact]
    public void ARealWebView2_FocusesTheFirstText_AndLeavesTheBodyBare()
    {
        string? active = null, role = null, name = null;

        WithWebView2("<div>  <p>  </p><div><p>First paragraph.</p></div><p>Second.</p></div>", async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            active = await core.ExecuteScriptAsync("document.activeElement.textContent");
            role = await core.ExecuteScriptAsync("document.body.getAttribute('role')");
            name = await core.ExecuteScriptAsync("document.body.getAttribute('aria-label')");
        });

        Assert.Equal("\"First paragraph.\"", active);
        Assert.Equal("null", role);
        Assert.Equal("null", name);
    }

    /// <summary>A style block in the body and a hidden pre-header are text no one sees; they are passed over.</summary>
    [StaFact]
    public void ARealWebView2_SkipsTextThatIsNotRendered()
    {
        string? active = null;

        WithWebView2(
            "<style>p{color:black}</style><span style='display:none'>Preheader</span><p>Hello there.</p>",
            async core =>
            {
                await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
                active = await core.ExecuteScriptAsync("document.activeElement.textContent");
            });

        Assert.Equal("\"Hello there.\"", active);
    }

    /// <summary>A message with no text at all (pictures only) still takes focus, on the body.</summary>
    [StaFact]
    public void ARealWebView2_WithNoText_FocusesTheBody()
    {
        string? isBody = null;

        WithWebView2("<img alt='' width='10' height='10'>", async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            isBody = await core.ExecuteScriptAsync("document.activeElement===document.body");
        });

        Assert.Equal("true", isBody);
    }

    /// <summary>When the first text is a link, the link itself takes focus, with its tabindex and focus outline untouched.</summary>
    [StaFact]
    public void ARealWebView2_FirstTextInALink_FocusesTheLink()
    {
        string? tag = null, tabindex = null, outline = null;

        WithWebView2("<p><a href='https://example.com/'>A link</a> then text.</p>", async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            tag = await core.ExecuteScriptAsync("document.activeElement.tagName");
            tabindex = await core.ExecuteScriptAsync("document.activeElement.getAttribute('tabindex')");
            outline = await core.ExecuteScriptAsync("document.activeElement.style.outline");
        });

        Assert.Equal("\"A\"", tag);
        Assert.Equal("null", tabindex);
        Assert.Equal("\"\"", outline);
    }

    /// <summary>
    /// Coming back into a message (F6 out and back) leaves focus where the user had it, rather than
    /// returning to the first paragraph.
    /// </summary>
    [StaFact]
    public void ARealWebView2_FocusAlreadyInside_StaysPut()
    {
        string? active = null;

        WithWebView2("<p>First.</p><p><a id='l' href='https://example.com/'>Later link</a></p>", async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            await core.ExecuteScriptAsync("document.getElementById('l').focus()");
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            active = await core.ExecuteScriptAsync("document.activeElement.id");
        });

        Assert.Equal("\"l\"", active);
    }

    /// <summary>
    /// A plain-text message has its text straight in the body. Focusing the body is what left the reader
    /// on the page title, so the first line is wrapped and focused instead: that line only, and the
    /// message's text is otherwise unchanged.
    /// </summary>
    [StaFact]
    public void ARealWebView2_PlainTextMessage_FocusesItsFirstLine()
    {
        var html = MessageBodyHtmlBuilder.BuildPlainTextHtmlDocument(
            "Lunch", "\n  Hello Kelly,\n\nSecond line.\nThird line.", note: null);
        string? isBody = null, active = null, text = null;

        WithWebView2(html, async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            isBody = await core.ExecuteScriptAsync("document.activeElement===document.body");
            active = await core.ExecuteScriptAsync("document.activeElement.textContent");
            text = await core.ExecuteScriptAsync("document.body.textContent");
        });

        Assert.Equal("false", isBody);
        Assert.Equal("\"Hello Kelly,\"", active);
        Assert.Equal("\"\\n  Hello Kelly,\\n\\nSecond line.\\nThird line.\"", text);
    }

    /// <summary>HTML whose first text sits straight in the body is handled the same way.</summary>
    [StaFact]
    public void ARealWebView2_TextDirectlyInTheBody_FocusesThatText()
    {
        string? tag = null, active = null;

        WithWebView2("Hi there,<br><p>More.</p>", async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            tag = await core.ExecuteScriptAsync("document.activeElement.tagName");
            active = await core.ExecuteScriptAsync("document.activeElement.textContent");
        });

        Assert.Equal("\"SPAN\"", tag);
        Assert.Equal("\"Hi there,\"", active);
    }

    /// <summary>
    /// Text that is invisible, hidden from screen readers, inert, or only zero-width filler is passed
    /// over, and an element it gave up on is left as it was.
    /// </summary>
    [StaFact]
    public void ARealWebView2_SkipsHiddenAndBlankText()
    {
        string? active = null, leftovers = null;

        WithWebView2(
            "<p style='visibility:hidden'>Invisible</p>" +
            "<p aria-hidden='true'>Hidden from readers</p>" +
            "<div inert><p>Inert</p></div>" +
            "<p>\u200B\u00AD\uFEFF</p>" +
            "<p>Real text.</p>",
            async core =>
            {
                await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
                active = await core.ExecuteScriptAsync("document.activeElement.textContent");
                leftovers = await core.ExecuteScriptAsync("document.querySelectorAll('[tabindex]').length");
            });

        Assert.Equal("\"Real text.\"", active);
        Assert.Equal("1", leftovers);   // only the focused paragraph
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static void WithWebView2(string html, Func<CoreWebView2, Task> act)
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
                                var done = new TaskCompletionSource<bool>();
                core.NavigationCompleted += (_, _) => done.TrySetResult(true);
                core.NavigateToString(html);
                await done.Task;
                controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                await act(core);
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
