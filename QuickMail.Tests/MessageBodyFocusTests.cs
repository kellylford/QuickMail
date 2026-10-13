using System;
using System.IO;
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
        string? focused = null, active = null, role = null, name = null;

        WithWebView2("<div>  <p>  </p><div><p>First paragraph.</p></div><p>Second.</p></div>", async core =>
        {
            focused = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            active = await core.ExecuteScriptAsync("document.activeElement.textContent");
            role = await core.ExecuteScriptAsync("document.body.getAttribute('role')");
            name = await core.ExecuteScriptAsync("document.body.getAttribute('aria-label')");
        });

        Assert.Equal("true", focused);
        Assert.Equal("\"First paragraph.\"", active);
        Assert.Equal("null", role);
        Assert.Equal("null", name);
    }

    /// <summary>A style block in the body and a hidden pre-header are text no one sees; they are passed over.</summary>
    [StaFact]
    public void ARealWebView2_SkipsTextThatIsNotRendered()
    {
        string? focused = null, active = null;

        WithWebView2(
            "<style>p{color:black}</style><span style='display:none'>Preheader</span><p>Hello there.</p>",
            async core =>
            {
                focused = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
                active = await core.ExecuteScriptAsync("document.activeElement.textContent");
            });

        Assert.Equal("true", focused);
        Assert.Equal("\"Hello there.\"", active);
    }

    /// <summary>A message with no text at all (pictures only) still takes focus, on the body.</summary>
    [StaFact]
    public void ARealWebView2_WithNoText_FocusesTheBody()
    {
        string? focused = null, isBody = null;

        WithWebView2("<img alt='' width='10' height='10'>", async core =>
        {
            focused = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            isBody = await core.ExecuteScriptAsync("document.activeElement===document.body");
        });

        Assert.Equal("true", focused);
        Assert.Equal("true", isBody);
    }

    /// <summary>When the first text is a link, the link itself takes focus, unaltered.</summary>
    [StaFact]
    public void ARealWebView2_FirstTextInALink_FocusesTheLink()
    {
        string? tag = null, tabindex = null;

        WithWebView2("<p><a href='https://example.com/'>A link</a> then text.</p>", async core =>
        {
            await core.ExecuteScriptAsync(MessageBodyFocus.FocusScript);
            tag = await core.ExecuteScriptAsync("document.activeElement.tagName");
            tabindex = await core.ExecuteScriptAsync("document.activeElement.getAttribute('tabindex')");
        });

        Assert.Equal("\"A\"", tag);
        Assert.Equal("null", tabindex);
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
