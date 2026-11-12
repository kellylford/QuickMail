using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// End to end against a real WebView2 (#729): a message rendered by the reading pane's own
/// sanitizer, loaded with NavigateToString, shows its embedded picture through
/// <see cref="EmbeddedPictureHost"/> — and a picture the message links to on the web is never
/// even requested. This is the premise the whole feature rests on: that a NavigateToString
/// document's image request reaches WebResourceRequested and the CSP lets exactly that through.
/// </summary>
[Collection("WpfTests")]
public class EmbeddedPictureWebViewTests
{
    private static byte[] Png(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [StaFact]
    public void EmbeddedPictureLoads_AndAWebPictureIsNeverRequested()
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPics-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 400, Height = 300, Left = -10000, Top = -10000,
        };
        window.Show();
        CoreWebView2Controller? controller = null;
        try
        {
            string? widths = null;
            var requested = new List<string>();
            Run(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 400, 300);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) => { lock (requested) requested.Add(e.Request.Uri); };
                var host = EmbeddedPictureHost.Attach(core, env);

                var detail = new MailMessageDetail
                {
                    Subject = "Pictures",
                    HtmlBody = "<p><img src=\"cid:logo@x\" alt=\"Logo\"> and " +
                               "<img src=\"https://tracker.invalid/p.gif\" alt=\"Web\"></p>",
                };
                IReadOnlyDictionary<string, AttachmentModel> pictures =
                    new Dictionary<string, AttachmentModel>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["logo@x"] = new() { ContentId = "logo@x", ContentType = "image/png", Content = Png(37, 21) },
                    };
                var sources = host.BeginMessage(embedded: true, web: false, plainText: false, detail, () => Task.FromResult(pictures));
                Assert.NotNull(sources.EmbeddedBase);
                Assert.Null(sources.WebBase);

                var loaded = new TaskCompletionSource<bool>();
                void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(true);
                core.NavigationCompleted += OnDone;
                var document = MessageBodyHtmlBuilder.BuildMessageDocument(detail, null, false, null, sources);
                Assert.Equal(1, document.BlockedWebPictures);
                core.NavigateToString(document.Html);
                await loaded.Task;
                core.NavigationCompleted -= OnDone;

                var until = DateTime.UtcNow.AddSeconds(10);
                do
                {
                    widths = await core.ExecuteScriptAsync(
                        "Array.from(document.images).map(i => i.complete + ':' + i.naturalWidth + ':' + i.alt).join('|')");
                    if (widths.Contains("true:37:Logo", StringComparison.Ordinal)) break;
                    await Task.Delay(100);
                } while (DateTime.UtcNow < until);
            }, TimeSpan.FromSeconds(60));

            Assert.Contains("true:37:Logo", widths);
            lock (requested)
                Assert.DoesNotContain(requested, u => u.Contains("tracker.invalid", StringComparison.Ordinal));
        }
        finally
        {
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold its data folder */ }
        }
    }

    /// <summary>
    /// Load Pictures (#508), end to end: the picture shows, fetched by QuickMail — and the WebView2
    /// itself never asks the picture's own server for anything. A tracking pixel is not fetched.
    /// </summary>
    [StaFact]
    public void WebPictureLoads_ThroughQuickMail_NeverFromTheWebView()
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPics-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 400, Height = 300, Left = -10000, Top = -10000,
        };
        window.Show();
        CoreWebView2Controller? controller = null;
        var fetched = new List<string>();
        var png = Png(41, 9);
        WebPictureFetcherTests.UseNetwork(request =>
        {
            lock (fetched) fetched.Add(request.RequestUri!.AbsoluteUri);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(png),
            };
        });
        try
        {
            string? widths = null;
            var requested = new List<string>();
            Run(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 400, 300);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) => { lock (requested) requested.Add(e.Request.Uri); };
                var host = EmbeddedPictureHost.Attach(core, env);

                var detail = new MailMessageDetail
                {
                    Subject = "Newsletter",
                    HtmlBody = "<p><img src=\"https://pictures.example/banner.png\" alt=\"Banner\"> " +
                               "<img src=\"https://tracker.example/open.gif\" width=\"1\" height=\"1\"></p>",
                };
                var sources = host.BeginMessage(embedded: true, web: true, plainText: false, detail,
                    () => Task.FromResult<IReadOnlyDictionary<string, AttachmentModel>>(
                        new Dictionary<string, AttachmentModel>()));
                Assert.NotNull(sources.WebBase);
                var document = MessageBodyHtmlBuilder.BuildMessageDocument(detail, null, false, null, sources);
                Assert.Equal(["https://pictures.example/banner.png"], document.WebPictures);
                Assert.DoesNotContain("pictures.example", document.Html, StringComparison.Ordinal);
                host.ServeWebPictures(sources, document.WebPictures);

                var loaded = new TaskCompletionSource<bool>();
                void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(true);
                core.NavigationCompleted += OnDone;
                core.NavigateToString(document.Html);
                await loaded.Task;
                core.NavigationCompleted -= OnDone;

                var until = DateTime.UtcNow.AddSeconds(10);
                do
                {
                    widths = await core.ExecuteScriptAsync(
                        "Array.from(document.images).map(i => i.complete + ':' + i.naturalWidth + ':' + i.alt).join('|')");
                    if (widths.Contains("true:41:Banner", StringComparison.Ordinal)) break;
                    await Task.Delay(100);
                } while (DateTime.UtcNow < until);
            }, TimeSpan.FromSeconds(60));

            Assert.Contains("true:41:Banner", widths);
            lock (fetched) Assert.Equal(["https://pictures.example/banner.png"], fetched);
            lock (requested)
            {
                Assert.DoesNotContain(requested, u => u.Contains("pictures.example", StringComparison.Ordinal));
                Assert.DoesNotContain(requested, u => u.Contains("tracker.example", StringComparison.Ordinal));
            }
        }
        finally
        {
            WebPictureFetcherTests.UseRealNetwork();
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold its data folder */ }
        }
    }

    /// <summary>
    /// #959: a picture whose download never finishes must not hold the page. The host has the picture
    /// request (it is in view, and asked for), and still: this render's NavigationCompleted arrives
    /// promptly, the page is complete, the focus script for this render focuses it, and the script for
    /// the previous render answers "stale". Then a second message replaces the first, while its own
    /// picture also hangs, and the wait on that navigation ends only on its own completion.
    /// </summary>
    [StaFact]
    public void APictureThatNeverArrives_DoesNotHoldThePage()
    {
        string? state = null, focused = null, active = null, stale = null, second = null, secondStale = null, visibility = null;
        long firstMs = -1, secondMs = -1;
        var pictureRequested = false;
        WithPictureHost(async (core, env, host) =>
        {
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource<IReadOnlyDictionary<string, AttachmentModel>>();
            Func<Task<IReadOnlyDictionary<string, AttachmentModel>>> load = () =>
            {
                asked.TrySetResult();
                return never.Task;
            };

            var html = Document(host, "first@x", "First message.", "1", load);
            using (var navigation = MessageBodyNavigation.Navigate(core, html, "test"))
            {
                Assert.True(await navigation.WaitAsync(TimeSpan.FromSeconds(10)), "NavigationCompleted never arrived.");
                firstMs = navigation.Clock.ElapsedMilliseconds;
            }
            visibility = await core.ExecuteScriptAsync("document.visibilityState");
            // The picture is requested (lazy pictures in view still load), and is still pending.
            pictureRequested = await Task.WhenAny(asked.Task, Task.Delay(5000)) == asked.Task;
            state = await core.ExecuteScriptAsync("document.readyState");
            controller!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            stale = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScriptFor("0"));
            focused = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScriptFor("1"));
            active = await core.ExecuteScriptAsync("document.activeElement.textContent");

            var html2 = Document(host, "second@x", "Second message.", "2", load);
            using (var navigation = MessageBodyNavigation.Navigate(core, html2, "test"))
            {
                Assert.True(await navigation.WaitAsync(TimeSpan.FromSeconds(10)), "The second NavigationCompleted never arrived.");
                secondMs = navigation.Clock.ElapsedMilliseconds;
                // Its own completion, never the first page's: this page is the one on show.
                second = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScriptFor("2"));
                secondStale = await core.ExecuteScriptAsync(MessageBodyFocus.FocusScriptFor("1"));
            }
        });

        Assert.True(firstMs is >= 0 and < 3000, $"The page took {firstMs} ms to complete (visibility {visibility}).");
        Assert.True(pictureRequested, $"The picture was never requested (visibility {visibility}).");
        Assert.Equal("\"complete\"", state);
        Assert.Equal("\"stale\"", stale);
        // true, or false where the test window is never the foreground (document.hasFocus); never stale.
        Assert.Contains(focused, TrueOrFalse);
        Assert.Equal("\"First message.\"", active);
        Assert.True(secondMs is >= 0 and < 3000, $"The second page took {secondMs} ms to complete.");
        Assert.Contains(second, TrueOrFalse);
        Assert.Equal("\"stale\"", secondStale);
    }

    /// <summary>
    /// #959: the wait for a picture is bounded. When it runs out, the request is answered
    /// "not found" and the picture shows as missing, rather than holding its request open.
    /// </summary>
    [StaFact]
    public void APictureThatNeverArrives_IsGivenUpOnAtItsBound()
    {
        string? pictures = null;
        WithPictureHost(async (core, env, host) =>
        {
            var never = new TaskCompletionSource<IReadOnlyDictionary<string, AttachmentModel>>();
            var html = Document(host, "stuck@x", "Stuck picture.", "1", () => never.Task, TimeSpan.FromSeconds(1));
            using var navigation = MessageBodyNavigation.Navigate(core, html, "test");
            await navigation.WaitAsync(TimeSpan.FromSeconds(10));

            var until = DateTime.UtcNow.AddSeconds(10);
            do
            {
                pictures = await core.ExecuteScriptAsync(
                    "Array.from(document.images).map(i => i.complete + ':' + i.naturalWidth).join('|')");
                if (pictures == "\"true:0\"") break;
                await Task.Delay(100);
            } while (DateTime.UtcNow < until);
        });

        Assert.Equal("\"true:0\"", pictures);
    }

    /// <summary>
    /// #959 review: the bound on a picture's wait runs from its own request, not from when the message
    /// opened. A lazy picture far down a long message is asked for only when the reader scrolls to it,
    /// well after the limit; it must still load.
    /// </summary>
    [StaFact]
    public void APictureFarDown_RequestedAfterTheLimit_StillLoads()
    {
        string? pictures = null, visibility = null;
        var askedBeforeScroll = true;
        WithPictureHost(async (core, env, host) =>
        {
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyDictionary<string, AttachmentModel> found =
                new Dictionary<string, AttachmentModel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["far@x"] = new() { ContentId = "far@x", ContentType = "image/png", Content = Png(37, 21) },
                };
            var detail = new MailMessageDetail
            {
                Subject = "Newsletter",
                HtmlBody = string.Concat(System.Linq.Enumerable.Repeat("<p>A paragraph of the newsletter.</p>", 400)) +
                           "<p><img src=\"cid:far@x\" alt=\"Far down\" width=\"37\" height=\"21\"></p>",
            };
            var sources = host.BeginMessage(embedded: true, web: false, plainText: false, detail, async () =>
            {
                asked.TrySetResult();
                await Task.Delay(300);   // quicker than the limit, once asked
                return found;
            }, TimeSpan.FromSeconds(1));
            var html = MessageBodyHtmlBuilder.WithRenderToken(
                MessageBodyHtmlBuilder.BuildMessageDocument(detail, null, false, null, sources).Html, "1");

            using (var navigation = MessageBodyNavigation.Navigate(core, html, "test"))
                await navigation.WaitAsync(TimeSpan.FromSeconds(10));
            visibility = await core.ExecuteScriptAsync("document.visibilityState");

            await Task.Delay(2000);   // twice the limit, with the picture out of view
            askedBeforeScroll = asked.Task.IsCompleted;
            await core.ExecuteScriptAsync("document.images[0].scrollIntoView()");

            var until = DateTime.UtcNow.AddSeconds(10);
            do
            {
                pictures = await core.ExecuteScriptAsync(
                    "Array.from(document.images).map(i => i.complete + ':' + i.naturalWidth).join('|')");
                if (pictures == "\"true:37\"") break;
                await Task.Delay(100);
            } while (DateTime.UtcNow < until);
        });

        Assert.False(askedBeforeScroll, "The picture far down was asked for before it was scrolled to, so this proves nothing.");
        Assert.True(pictures == "\"true:37\"", $"Pictures: {pictures} (visibility {visibility}).");
    }

    /// <summary>Closing the message (<see cref="EmbeddedPictureHost.End"/>) answers a picture still waiting, at once.</summary>
    [StaFact]
    public void End_ReleasesAWaitingPicture()
    {
        string? pictures = null;
        long releasedMs = -1;
        WithPictureHost(async (core, env, host) =>
        {
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource<IReadOnlyDictionary<string, AttachmentModel>>();
            var html = Document(host, "stuck@x", "Closing.", "1", () => { asked.TrySetResult(); return never.Task; });
            using (var navigation = MessageBodyNavigation.Navigate(core, html, "test"))
                await navigation.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAny(asked.Task, Task.Delay(5000));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            host.End();
            var until = DateTime.UtcNow.AddSeconds(10);
            do
            {
                pictures = await core.ExecuteScriptAsync(
                    "Array.from(document.images).map(i => i.complete + ':' + i.naturalWidth).join('|')");
                if (pictures == "\"true:0\"") break;
                await Task.Delay(50);
            } while (DateTime.UtcNow < until);
            releasedMs = clock.ElapsedMilliseconds;
        });

        Assert.Equal("\"true:0\"", pictures);
        Assert.True(releasedMs < 5000, $"Released after {releasedMs} ms; the picture's own wait was 60 s.");
    }

    private static readonly string[] TrueOrFalse = ["true", "false"];

    private CoreWebView2Controller? controller;

    /// <summary>A message showing one embedded picture, with render token <paramref name="token"/>.</summary>
    private static string Document(EmbeddedPictureHost host, string contentId, string text, string token,
        Func<Task<IReadOnlyDictionary<string, AttachmentModel>>> load, TimeSpan? pictureWait = null)
    {
        var detail = new MailMessageDetail
        {
            Subject = text,
            HtmlBody = $"<p>{text}</p><p><img src=\"cid:{contentId}\" alt=\"Logo\" width=\"40\" height=\"20\"></p>",
        };
        var sources = host.BeginMessage(embedded: true, web: false, plainText: false, detail, load,
            pictureWait ?? TimeSpan.FromSeconds(60));
        var document = MessageBodyHtmlBuilder.BuildMessageDocument(detail, null, false, null, sources);
        return MessageBodyHtmlBuilder.WithRenderToken(document.Html, token);
    }

    private void WithPictureHost(Func<CoreWebView2, CoreWebView2Environment, EmbeddedPictureHost, Task> act)
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPics-{Guid.NewGuid():N}");
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 400, Height = 300, Left = -10000, Top = -10000,
        };
        window.Show();
        try
        {
            Run(async () =>
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 400, 300);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                var host = EmbeddedPictureHost.Attach(core, env);
                await act(core, env, host);
            }, TimeSpan.FromSeconds(90));
        }
        finally
        {
            controller?.Close();
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold its data folder */ }
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
