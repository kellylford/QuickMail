using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Read-only preview of a compose message rendered as HTML.
/// Opened by pressing F8 in Markdown or HTML compose mode; Escape or Ctrl+W closes it.
/// The WebView2 is focusable so screen readers can browse the rendered content.
/// </summary>
public partial class MarkdownPreviewWindow : Window
{
    private readonly string _html;
    private readonly CommandRegistry _registry = new();
    private readonly CancellationTokenSource _cts = new();

    public MarkdownPreviewWindow(string? subject, string htmlFragment)
    {
        Title = string.IsNullOrWhiteSpace(subject)
            ? "Compose — Preview — QuickMail"
            : $"{subject.Trim()} — Preview — QuickMail";

        _html = BuildHtml(subject, htmlFragment);
        InitializeComponent();
        RegisterCommands();
        Loaded += OnLoaded;
    }

    private void RegisterCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "preview.close", category: "View", title: "Close Preview",
            execute: Close,
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "preview.commandPalette", category: "View", title: "Command Palette",
            execute: OpenCommandPalette));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickMail", "WebView2Preview");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            if (_cts.IsCancellationRequested) return;
            await PreviewBody.EnsureCoreWebView2Async(env);
            if (_cts.IsCancellationRequested) return;

            PreviewBody.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewBody.CoreWebView2.Settings.AreDevToolsEnabled             = false;
            PreviewBody.CoreWebView2.Settings.IsStatusBarEnabled             = false;

            // Relay F6, Shift+F6, Escape, Ctrl+W back to WPF from inside the WebView.
            await PreviewBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){" +
                "if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}" +
                "else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}" +
                "else if(e.key==='F6'&&!e.shiftKey){window.chrome.webview.postMessage('f6');e.preventDefault();}" +
                "else if(e.key==='F6'&&e.shiftKey){window.chrome.webview.postMessage('shift-f6');e.preventDefault();}" +
                "else if(e.ctrlKey&&e.key==='w'){window.chrome.webview.postMessage('ctrl-w');e.preventDefault();}" +
                "else if(e.ctrlKey&&e.shiftKey&&e.key==='P'){window.chrome.webview.postMessage('palette');e.preventDefault();}" +
                "});");

            PreviewBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                switch (args.TryGetWebMessageAsString())
                {
                    case "escape":
                    case "ctrl-w":   Dispatcher.InvokeAsync(Close,               DispatcherPriority.Input); break;
                    case "shift-tab": Dispatcher.InvokeAsync(Close,              DispatcherPriority.Input); break;
                    case "f6":
                    case "shift-f6": Dispatcher.InvokeAsync(FocusBody,           DispatcherPriority.Input); break;
                    case "palette":  Dispatcher.InvokeAsync(OpenCommandPalette,  DispatcherPriority.Input); break;
                }
            };

            // Cancel external navigation — http/https/mailto open in the default browser;
            // data: navigations are always blocked (they load without CSP).
            PreviewBody.CoreWebView2.NavigationStarting += (_, args) =>
            {
                var uri = args.Uri;
                if (string.IsNullOrEmpty(uri) ||
                    uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    return;
                args.Cancel = true;
                if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                    }
                    catch { /* best effort */ }
                }
            };

            PreviewBody.CoreWebView2.NavigateToString(_html);

            await Task.Delay(200, _cts.Token); // let navigation settle before focusing
            await FocusBodyAsync();
        }
        catch (OperationCanceledException) { /* window closed during init — normal */ }
        catch (Exception ex)
        {
            LogService.Log("MarkdownPreviewWindow WebView2 init failed", ex);
        }
    }

    private void PreviewBody_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = FocusBodyAsync();
    }

    private async Task FocusBodyAsync()
    {
        if (PreviewBody.CoreWebView2 == null) return;
        try
        {
            await PreviewBody.CoreWebView2.ExecuteScriptAsync(
                "(() => {" +
                "const b = document.body;" +
                "if (!b) return;" +
                "b.setAttribute('tabindex','0');" +
                "b.setAttribute('role','document');" +
                "b.focus({preventScroll:true});" +
                "})()");
        }
        catch { /* navigation may not be complete yet */ }
    }

    private void FocusBody()
    {
        PreviewBody.Focus();
        Keyboard.Focus(PreviewBody);
        try
        {
            ((System.Windows.Interop.IKeyboardInputSink)PreviewBody).TabInto(
                new TraversalRequest(FocusNavigationDirection.First));
        }
        catch { /* ignore */ }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = Keyboard.Modifiers;

        if (key == Key.Escape || (key == Key.W && mod == ModifierKeys.Control))
        {
            Close();
            e.Handled = true;
            return;
        }

        if (key == Key.F6)
        {
            FocusBody();
            e.Handled = true;
            return;
        }

        if (key == Key.P && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        var cmd = _registry.FindByGesture(key, mod);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            cmd.Execute();
            e.Handled = true;
        }
    }

    private void OpenCommandPalette()
    {
        var palette = new CommandPaletteWindow(_registry) { Owner = this };
        palette.ShowDialog();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e) => _cts.Cancel();

    // ── HTML builder ─────────────────────────────────────────────────────────

    private static string BuildHtml(string? subject, string fragment)
    {
        var isEmpty = string.IsNullOrWhiteSpace(fragment);
        var body = isEmpty
            ? "<p style=\"color:#888;font-style:italic;\">No content to preview.</p>"
            : fragment;

        var titleText = string.IsNullOrWhiteSpace(subject)
            ? "Preview"
            : $"Preview — {subject.Trim()}";
        var encodedTitle = System.Net.WebUtility.HtmlEncode(titleText);

        return
            "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n" +
            "<meta charset=\"utf-8\">\n" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n" +
            "<meta http-equiv=\"Content-Security-Policy\" " +
            "content=\"default-src 'none'; style-src 'unsafe-inline'; font-src 'unsafe-inline'; img-src data:;\">\n" +
            $"<title>{encodedTitle}</title>\n" +
            "<style>\n" + PreviewCss + "\n</style>\n" +
            "</head>\n<body>\n" +
            body +
            "\n</body>\n</html>";
    }

    private const string PreviewCss =
        "*, *::before, *::after { box-sizing: border-box; }\n" +
        ":root {\n" +
        "  --font: 'Segoe UI', system-ui, -apple-system, sans-serif;\n" +
        "  --font-mono: 'Cascadia Code', 'Consolas', 'Courier New', monospace;\n" +
        "  --text: #1a1a1a; --text-muted: #6b6b6b; --bg: #ffffff;\n" +
        "  --surface: #f4f4f5; --border: #d8d8d8;\n" +
        "  --accent: #0563bb; --quote-bg: #f8f8f8;\n" +
        "}\n" +
        "@media (prefers-color-scheme: dark) {\n" +
        "  :root {\n" +
        "    --text: #e4e4e4; --text-muted: #999; --bg: #1e1e1e;\n" +
        "    --surface: #2a2a2a; --border: #404040;\n" +
        "    --accent: #4da6ff; --quote-bg: #252525;\n" +
        "  }\n" +
        "}\n" +
        "html { scroll-behavior: smooth; }\n" +
        "body {\n" +
        "  font-family: var(--font); font-size: 15px; line-height: 1.7;\n" +
        "  color: var(--text); background: var(--bg);\n" +
        "  max-width: 800px; margin: 0 auto; padding: 36px 48px;\n" +
        "}\n" +
        "h1,h2,h3,h4,h5,h6 {\n" +
        "  font-weight: 600; line-height: 1.3;\n" +
        "  margin: 1.6em 0 0.5em; color: var(--text);\n" +
        "}\n" +
        "h1 { font-size: 2em; border-bottom: 2px solid var(--border); padding-bottom: 0.3em; margin-top: 0; }\n" +
        "h2 { font-size: 1.5em; border-bottom: 1px solid var(--border); padding-bottom: 0.2em; }\n" +
        "h3 { font-size: 1.25em; }\n" +
        "h4,h5,h6 { font-size: 1em; }\n" +
        "p { margin: 0 0 1em; }\n" +
        "a { color: var(--accent); text-decoration: underline; }\n" +
        "strong { font-weight: 600; }\n" +
        "em { font-style: italic; }\n" +
        "del { text-decoration: line-through; color: var(--text-muted); }\n" +
        "ul,ol { margin: 0 0 1em 1.5em; padding: 0; }\n" +
        "li { margin-bottom: 0.3em; }\n" +
        "li:last-child { margin-bottom: 0; }\n" +
        "blockquote {\n" +
        "  border-left: 4px solid var(--border); margin: 1em 0;\n" +
        "  padding: 0.6em 1em; background: var(--quote-bg);\n" +
        "  color: var(--text-muted); border-radius: 0 4px 4px 0;\n" +
        "}\n" +
        "blockquote p:last-child { margin: 0; }\n" +
        "code {\n" +
        "  font-family: var(--font-mono); font-size: 0.875em;\n" +
        "  background: var(--surface); padding: 0.15em 0.45em; border-radius: 4px;\n" +
        "}\n" +
        "pre {\n" +
        "  background: var(--surface); border: 1px solid var(--border);\n" +
        "  border-radius: 6px; padding: 16px; overflow-x: auto; margin: 1em 0;\n" +
        "}\n" +
        "pre code { background: none; padding: 0; font-size: 0.875em; border-radius: 0; }\n" +
        "table { border-collapse: collapse; width: 100%; margin: 1em 0; font-size: 0.95em; }\n" +
        "th,td { border: 1px solid var(--border); padding: 0.6em 0.9em; text-align: left; }\n" +
        "th { background: var(--surface); font-weight: 600; }\n" +
        "tr:nth-child(even) td { background: var(--surface); }\n" +
        "hr { border: none; border-top: 2px solid var(--border); margin: 1.5em 0; }\n" +
        "img { max-width: 100%; height: auto; }\n" +
        "body:focus { outline: none; }\n";
}
