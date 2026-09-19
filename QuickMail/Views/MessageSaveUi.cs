using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// The window side of Save, Save As and Print (#728): the file dialogs, owned by the window the
/// user is in, and a hidden WebView2 that turns the saved web page into a PDF or sends it to a
/// printer. One per window that offers these commands.
/// </summary>
internal sealed class MessageSaveUi : IMessageSaveUi
{
    /// <summary>NavigateToString refuses documents over 2 MB; anything near that goes via a temp file.</summary>
    private const int MaxNavigateToStringChars = 1_000_000;

    private readonly Window _owner;
    private readonly Func<CoreWebView2Environment?> _environment;
    private readonly Func<Action?>? _beforeModal;
    private readonly string? _userDataFolder;

    /// <param name="beforeModal">
    /// Called before each modal dialog. When focus is inside the message body it moves focus to a
    /// WPF element and returns the action that puts it back once the dialog has closed. A modal loop
    /// opened over a focused WebView2 with a screen reader running is what froze GrabAddresses and
    /// crashed the palette (#676; CLAUDE.md, "Prefer modeless"), and a file dialog cannot be made
    /// modeless — so the body is simply not focused while one is up.
    /// </param>
    /// <param name="userDataFolder">
    /// Where to create a WebView2 environment when the window has none yet. Null means QuickMail's
    /// own, the one the reading pane uses; tests pass a throwaway folder.
    /// </param>
    public MessageSaveUi(Window owner, Func<CoreWebView2Environment?> environment,
        Func<Action?>? beforeModal = null, string? userDataFolder = null)
    {
        _owner          = owner;
        _environment    = environment;
        _beforeModal    = beforeModal;
        _userDataFolder = userDataFolder;
    }

    private T Modal<T>(Func<T> show)
    {
        var restore = _beforeModal?.Invoke();
        try { return show(); }
        finally { restore?.Invoke(); }
    }

    private static string Filter =>
        string.Join("|", MessageSaveFormats.All.Select(f =>
        {
            var ext = MessageSaveFormats.Extension(f);
            var pattern = f == MessageSaveFormat.Html ? "*.html;*.htm" : "*" + ext;
            return $"{MessageSaveFormats.DisplayName(f)} ({pattern})|{pattern}";
        }));

    private static int FilterIndexOf(MessageSaveFormat format) => Array.IndexOf(MessageSaveFormats.All, format) + 1;

    private static MessageSaveFormat FormatAt(int filterIndex) =>
        filterIndex >= 1 && filterIndex <= MessageSaveFormats.All.Length
            ? MessageSaveFormats.All[filterIndex - 1]
            : MessageSaveFormat.Eml;

    public MessageSaveTarget? ChooseFile(string suggestedFileName, MessageSaveFormat format, string initialFolder)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Save As",
            FileName         = suggestedFileName,
            Filter           = Filter,
            FilterIndex      = FilterIndexOf(format),
            DefaultExt       = MessageSaveFormats.Extension(format),
            AddExtension     = true,
            OverwritePrompt  = true,
            InitialDirectory = initialFolder,
        };
        if (Modal(() => dlg.ShowDialog(_owner)) != true) return null;

        // A typed extension that names a format wins over the type list — "notes.txt" means text
        // whatever the list says. Otherwise the type list decides, and its extension is added.
        var chosen = MessageSaveFormats.FromExtension(Path.GetExtension(dlg.FileName)) ?? FormatAt(dlg.FilterIndex);
        var path = dlg.FileName;
        if (MessageSaveFormats.FromExtension(Path.GetExtension(path)) is null)
            path += MessageSaveFormats.Extension(chosen);
        return new MessageSaveTarget(path, chosen);
    }

    public MessageSaveTarget? ChooseFolder(int count, MessageSaveFormat format, string initialFolder)
    {
        // The standard Save dialog, so the folder AND the type are chosen in the one familiar place.
        // The file name box says what will happen instead of offering a name that would be ignored.
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = $"Save {count} Messages",
            FileName         = "Each message is saved under its own name",
            Filter           = Filter,
            FilterIndex      = FilterIndexOf(format),
            AddExtension     = false,
            OverwritePrompt  = false,
            CheckFileExists  = false,
            InitialDirectory = initialFolder,
        };
        if (Modal(() => dlg.ShowDialog(_owner)) != true) return null;

        var folder = Path.GetDirectoryName(dlg.FileName);
        return string.IsNullOrEmpty(folder) ? null : new MessageSaveTarget(folder, FormatAt(dlg.FilterIndex));
    }

    public bool ConfirmTryAnotherFormat(string explanation) =>
        Modal(() => MessageBox.Show(_owner, explanation, "Save", MessageBoxButton.YesNo, MessageBoxImage.Information))
        == MessageBoxResult.Yes;

    public async Task WritePdfAsync(string html, string path, CancellationToken ct)
    {
        await WithDocumentAsync(html, async (core, _) =>
        {
            // Through the DevTools protocol rather than PrintToPdfAsync, because only this asks for a
            // TAGGED PDF — headings, paragraphs, tables and reading order a screen reader can use.
            // PrintToPdfAsync writes an untagged one: measured, not assumed (no StructTreeRoot).
            // The call works with DevTools itself turned off; that setting only governs the window.
            var result = await core.CallDevToolsProtocolMethodAsync("Page.printToPDF",
                "{\"generateTaggedPDF\":true,\"generateDocumentOutline\":true," +
                "\"printBackground\":false,\"displayHeaderFooter\":false,\"preferCSSPageSize\":true}");
            using var json = System.Text.Json.JsonDocument.Parse(result);
            var data = json.RootElement.GetProperty("data").GetString()
                ?? throw new IOException("The PDF could not be written.");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(data), ct);
            return true;
        }, ct);
    }

    public async Task<bool> PrintAsync(string html, string documentTitle, CancellationToken ct)
    {
        // The Windows Print dialog, as every other program shows it. It is chosen here, before the
        // document is laid out, so a cancel costs nothing.
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage              = 1,
            MaxPage              = 9999,
        };
        if (Modal(() => dialog.ShowDialog()) != true) return false;

        return await WithDocumentAsync(html, async (core, env) =>
        {
            var settings = env.CreatePrintSettings();
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintBackgrounds     = false;
            settings.HeaderTitle                = documentTitle;
            if (dialog.PrintQueue?.FullName is { Length: > 0 } printer)
                settings.PrinterName = printer;

            var ticket = dialog.PrintTicket;
            if (ticket?.CopyCount is int copies and > 0) settings.Copies = copies;
            if (ticket?.PageOrientation is System.Printing.PageOrientation.Landscape
                                        or System.Printing.PageOrientation.ReverseLandscape)
                settings.Orientation = CoreWebView2PrintOrientation.Landscape;
            if (ticket?.PageMediaSize is { Width: > 0, Height: > 0 } media)
            {
                // PrintTicket measures in device-independent pixels (1/96 inch); WebView2 in inches.
                settings.PageWidth  = media.Width.Value  / 96.0;
                settings.PageHeight = media.Height.Value / 96.0;
            }
            if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
                settings.PageRanges = $"{dialog.PageRange.PageFrom}-{dialog.PageRange.PageTo}";

            var status = await core.PrintAsync(settings);
            return status switch
            {
                CoreWebView2PrintStatus.Succeeded          => true,
                CoreWebView2PrintStatus.PrinterUnavailable => throw new IOException("The printer is not available."),
                _                                          => throw new IOException("The printer reported an error."),
            };
        }, ct);
    }

    /// <summary>
    /// Loads <paramref name="html"/> into a WebView2 no one sees, runs <paramref name="action"/> on
    /// it, and tears it down. Script is off and every navigation after the first is refused: the
    /// page is the saved web page, whose CSP already denies script and remote loads, and this only
    /// has to lay it out.
    /// </summary>
    private async Task<T> WithDocumentAsync<T>(
        string html, Func<CoreWebView2, CoreWebView2Environment, Task<T>> action, CancellationToken ct)
    {
        var env = _environment() ?? await CoreWebView2Environment.CreateAsync(null,
            _userDataFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickMail", "WebView2"));

        var hwnd = new WindowInteropHelper(_owner).EnsureHandle();
        var controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
        string? tempFile = null;
        try
        {
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            // Safe here, unlike on the reading pane (see MessageBodyHtmlBuilder.TryStripHeavyHtml):
            // nothing on this surface needs a host-script callback.
            core.Settings.IsScriptEnabled                  = false;
            core.Settings.IsWebMessageEnabled              = false;
            core.Settings.AreDefaultContextMenusEnabled    = false;
            core.Settings.AreDevToolsEnabled               = false;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var navigations = 0;
            core.NavigationStarting += (_, e) =>
            {
                if (Interlocked.Increment(ref navigations) > 1) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) loaded.TrySetResult(true);
                else loaded.TrySetException(new IOException($"The page could not be laid out ({e.WebErrorStatus})."));
            };

            if (html.Length <= MaxNavigateToStringChars)
            {
                core.NavigateToString(html);
            }
            else
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"QuickMail-{Guid.NewGuid():N}.html");
                await File.WriteAllTextAsync(tempFile, html, ct);
                core.Navigate(new Uri(tempFile).AbsoluteUri);
            }

            using (ct.Register(() => loaded.TrySetCanceled(ct)))
            {
                var finished = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
                if (finished != loaded.Task) throw new TimeoutException("The page took too long to lay out.");
                await loaded.Task;
            }

            return await action(core, env);
        }
        finally
        {
            controller.Close();
            if (tempFile is not null)
            {
                try { File.Delete(tempFile); }
                catch (Exception ex) { LogService.Log("MessageSaveUi: could not delete the temporary page", ex); }
            }
        }
    }
}
