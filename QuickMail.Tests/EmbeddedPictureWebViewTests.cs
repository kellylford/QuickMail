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
