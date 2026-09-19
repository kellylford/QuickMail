using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Save as PDF (#728) against a real WebView2 — the one part of saving the fakes cannot stand in
/// for: a hidden controller must lay out the saved web page and write a real, tagged PDF, without a
/// visible window.
/// </summary>
[Collection("WpfTests")]
public class MessageSavePdfTests
{
    [StaFact]
    public void WritePdf_WritesARealPdf_FromTheSavedWebPage()
    {
        WpfTestHost.EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 50, Height = 50, Left = -10000, Top = -10000,
        };
        window.Show();
        try
        {
            var detail = new MailMessageDetail
            {
                Subject = "PDF check", From = "Jane <jane@example.com>", To = "kelly@example.com",
                Date = DateTimeOffset.Now,
                HtmlBody = "<h2>Heading</h2><p>Body text</p><script>document.title='ran'</script>",
            };
            var html = MessageExport.BuildHtmlDocument(detail,
                new MessageSaveContext("Kelly", "Inbox", null, DateTimeOffset.Now));
            var ui = new MessageSaveUi(window, () => null,
                beforeModal: null, userDataFolder: Path.Combine(dir, "webview2"));

            byte[] bytes = [];
            Run(async () => bytes = await ui.RenderPdfAsync(html, CancellationToken.None), TimeSpan.FromSeconds(60));

            Assert.True(bytes.Length > 500, $"PDF is only {bytes.Length} bytes.");
            Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

            // Tagged, so a screen reader gets structure rather than a flat run of text: the subject
            // heading, and the details as a table with row headers. PrintToPdfAsync wrote none of this.
            var text = Encoding.Latin1.GetString(bytes);
            Assert.Contains("/StructTreeRoot", text);
            Assert.Contains("/S /H1", text);
            Assert.Contains("/S /Table", text);
            Assert.Contains("/S /TH", text);
            Assert.Contains("/Lang (en)", text);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* WebView2 may still hold its data folder */ }
        }
    }

    /// <summary>Runs an async UI operation to completion on this STA thread, pumping its dispatcher.</summary>
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
            Assert.True(task.IsCompleted, "Timed out writing the PDF.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
