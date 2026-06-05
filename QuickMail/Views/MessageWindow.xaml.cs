using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Standalone window that shows a single message outside the main window.
/// Opened via Ctrl+Enter or MessageOpenMode = Window.
/// Each instance owns its own WebView2 process.
/// </summary>
public partial class MessageWindow : Window
{
    private static readonly TimeSpan HtmlRegexTimeout         = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WebViewNavigationTimeout = TimeSpan.FromSeconds(4);
    private const int MaxRichHtmlRenderChars  = 1_000_000;
    private const int MaxRichHtmlTableCount   = 500;
    private const int MaxReaderTextChars      = 140_000;

    private static readonly Regex AutoLinkUrl = new(
        @"\b((?:https?|mailto):[^\s<>""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        HtmlRegexTimeout);
    private static readonly char[] AutoLinkTrailingPunct =
        ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '\''];

    private readonly MessageWindowViewModel _vm;
    private bool _webViewReady;
    private int  _renderVersion;

    // Services needed for loading message bodies.
    private readonly Services.IMailService        _imap;
    private readonly Services.ILocalStoreService  _localStore;

    public MessageWindow(
        MessageWindowViewModel vm,
        Services.IMailService imap,
        Services.ILocalStoreService localStore)
    {
        _vm         = vm;
        _imap       = imap;
        _localStore = localStore;

        InitializeComponent();
        DataContext = vm;

        vm.RequestClose          += _ => Close();
        vm.RequestMoveToMainWindow += OnMoveToMainWindowRequested;
        vm.PropertyChanged        += async (_, e) =>
        {
            if (e.PropertyName == nameof(MessageWindowViewModel.SelectedMessage))
                await LoadSelectedMessageAsync();
        };

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickMail", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await MessageBody.EnsureCoreWebView2Async(env);
            _webViewReady = true;

            MessageBody.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MessageBody.CoreWebView2.Settings.AreDevToolsEnabled             = false;
            MessageBody.CoreWebView2.Settings.IsStatusBarEnabled             = false;

            await MessageBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){" +
                "if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}" +
                "else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}" +
                "});");

            MessageBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == "escape")
                    Dispatcher.InvokeAsync(Close, DispatcherPriority.Input);
                else if (msg == "shift-tab")
                    Dispatcher.InvokeAsync(FocusLastHeaderField, DispatcherPriority.Input);
            };

            MessageBody.CoreWebView2.NavigationStarting += (_, args) =>
            {
                var uri = args.Uri;
                if (string.IsNullOrEmpty(uri) ||
                    uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                    uri.StartsWith("data:",  StringComparison.OrdinalIgnoreCase))
                    return;
                args.Cancel = true;
                OpenExternal(uri);
            };

            if (_vm.MessageDetail != null)
                await ShowMessageBodyAsync(_vm.MessageDetail);
            else if (_vm.SelectedMessage != null)
                await LoadSelectedMessageAsync();
        }
        catch (Exception ex)
        {
            LogService.Log("MessageWindow WebView2 init failed", ex);
        }
    }

    private async Task LoadSelectedMessageAsync()
    {
        var summary = _vm.SelectedMessage;
        if (summary == null) return;

        _vm.IsLoading = true;
        try
        {
            var detail = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId);
            if (detail == null)
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId,
                    CancellationToken.None);
            }
            if (detail == null) return;

            _vm.MessageDetail = detail;
            await ShowMessageBodyAsync(detail);
        }
        catch (Exception ex)
        {
            LogService.Log("MessageWindow.LoadSelectedMessageAsync", ex);
        }
        finally
        {
            _vm.IsLoading = false;
        }
    }

    private async Task ShowMessageBodyAsync(MailMessageDetail detail)
    {
        if (!_webViewReady) return;

        var version = Interlocked.Increment(ref _renderVersion);
        var html = await Task.Run(() => BuildMessageHtml(detail));
        if (version != _renderVersion) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigated(object? s, CoreWebView2NavigationCompletedEventArgs ev)
        {
            MessageBody.CoreWebView2.NavigationCompleted -= OnNavigated;
            tcs.TrySetResult(ev.IsSuccess);
        }
        MessageBody.CoreWebView2.NavigationCompleted += OnNavigated;
        try { MessageBody.CoreWebView2.Stop(); } catch { /* best effort */ }
        MessageBody.CoreWebView2.NavigateToString(html);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(WebViewNavigationTimeout)) == tcs.Task;
        if (!completed)
            MessageBody.CoreWebView2.NavigationCompleted -= OnNavigated;

        if (version != _renderVersion) return;

        await FocusMessageBodyAsync(version, detail.Subject);
    }

    private async Task FocusMessageBodyAsync(int version, string? subject)
    {
        var focusLabel = string.IsNullOrWhiteSpace(subject)
            ? "Message body"
            : $"Message body. {subject.Trim()}";

        await Dispatcher.InvokeAsync(() =>
        {
            MessageBody.Focus();
            Keyboard.Focus(MessageBody);
        }, DispatcherPriority.Input);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (version != _renderVersion) return;

            await Dispatcher.InvokeAsync(FocusMessageBodyHost, DispatcherPriority.Input);

            try
            {
                if (await TryFocusDocumentAsync(focusLabel))
                    break;
            }
            catch (Exception ex)
            {
                if (attempt == 4)
                    LogService.Log("MessageWindow.FocusMessageBody", ex);
            }

            await Task.Delay(100);
        }

        if (version != _renderVersion) return;

        await Dispatcher.InvokeAsync(() =>
        {
            FocusMessageBodyHost();
            AccessibilityHelper.Announce(this, focusLabel, interrupt: true, category: AnnouncementCategory.Result);
        }, DispatcherPriority.Input);
    }

    private void FocusMessageBodyHost()
    {
        MessageBody.Focus();
        Keyboard.Focus(MessageBody);
        try
        {
            ((System.Windows.Interop.IKeyboardInputSink)MessageBody).TabInto(
                new TraversalRequest(FocusNavigationDirection.First));
        }
        catch { /* ignore */ }
    }

    private void MessageBody_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = TryFocusDocumentAsync(_vm.MessageDetail?.Subject is { } s && !string.IsNullOrWhiteSpace(s)
            ? $"Message body. {s.Trim()}"
            : "Message body");
    }

    private async Task<bool> TryFocusDocumentAsync(string focusLabel)
    {
        if (MessageBody.CoreWebView2 == null) return false;
        var label = JsonSerializer.Serialize(focusLabel);
        var result = await MessageBody.CoreWebView2.ExecuteScriptAsync(
            "(() => {" +
            "const body=document.body;" +
            "if(!body)return false;" +
            "window.focus();" +
            "body.setAttribute('tabindex','0');" +
            "body.setAttribute('role','document');" +
            $"body.setAttribute('aria-label',{label});" +
            "body.focus({preventScroll:true});" +
            "return document.hasFocus()&&document.activeElement===body;" +
            "})()");
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = Keyboard.Modifiers;

        if (key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (key == Key.Left && mod == ModifierKeys.Alt)
        {
            _vm.PreviousMessageCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.Right && mod == ModifierKeys.Alt)
        {
            _vm.NextMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e) { }

    private void FocusLastHeaderField() => SubjectField.Focus();

    private void OnMoveToMainWindowRequested(MessageWindowViewModel vm)
    {
        // Raise an event that App.xaml.cs / MainWindow can subscribe to.
        // The MainWindow subscription handles actually promoting the content.
        MoveToMainWindowRequested?.Invoke(this, vm);
        Close();
    }

    /// <summary>
    /// Raised when the user selects "Move to Main Window".
    /// The owning code (App.xaml.cs or MainWindow) should open the message as a tab
    /// and close this window.
    /// </summary>
    public event EventHandler<MessageWindowViewModel>? MoveToMainWindowRequested;

    private static void OpenExternal(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { LogService.Log($"MessageWindow.OpenExternal {uri}", ex); }
    }

    // ── HTML rendering (self-contained duplicate of MainWindow helpers) ────────────
    // Tech debt: these should be extracted to a shared MessageBodyHelper class in v2.

    private static string BuildMessageHtml(MailMessageDetail detail)
    {
        var htmlBody = detail.HtmlBody ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(htmlBody) && !ShouldUseReaderMode(htmlBody))
            return BuildSanitizedHtmlDocument(detail.Subject, htmlBody);

        var text = !string.IsNullOrWhiteSpace(detail.PlainTextBody)
            ? detail.PlainTextBody
            : HtmlToText(htmlBody);

        var note = !string.IsNullOrWhiteSpace(htmlBody)
            ? "This message uses complex HTML, so QuickMail is showing a simplified body."
            : null;
        return BuildPlainTextHtmlDocument(detail.Subject, text, note);
    }

    private static bool ShouldUseReaderMode(string html) =>
        html.Length > MaxRichHtmlRenderChars ||
        CountOccurrences(html, "<table") > MaxRichHtmlTableCount;

    private static string BuildSanitizedHtmlDocument(string? subject, string html)
    {
        var body     = StripHeavyHtml(html);
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        const string cspTag =
            "<meta http-equiv=\"Content-Security-Policy\" " +
            "content=\"default-src 'none'; script-src 'none'; object-src 'none'; " +
            "frame-src 'none'; img-src 'none'; media-src 'none'; connect-src 'none'; " +
            "form-action 'none'; base-uri 'none'; style-src 'unsafe-inline';\">";
        const string css =
            "<style>html,body{margin:0;padding:8px 12px;font-family:Segoe UI,Arial,sans-serif;" +
            "font-size:13px;line-height:1.45;word-break:break-word;background:Window;color:WindowText;}" +
            "table{max-width:100%;border-collapse:collapse;}td,th{vertical-align:top;}a{color:#0645ad;}</style>";

        var headIdx = body.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
        {
            body = RemoveTitle(body);
            body = body.Insert(headIdx + 6, "<meta charset=\"utf-8\">" + titleTag + cspTag + css);
            return EnsureBodyFocusable(body);
        }

        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
               titleTag + cspTag + css +
               "</head><body tabindex=\"0\">" + body + "</body></html>";
    }

    private static string BuildPlainTextHtmlDocument(string? subject, string text, string? note)
    {
        var clipped  = Truncate(text ?? string.Empty, MaxReaderTextChars);
        var encoded  = WebUtility.HtmlEncode(clipped);
        var linked   = AutoLinkPlainTextUrls(encoded);
        var titleTag = $"<title>{WebUtility.HtmlEncode(subject ?? string.Empty)}</title>";
        var noteHtml = string.IsNullOrWhiteSpace(note)
            ? string.Empty
            : "<p class=\"note\">" + WebUtility.HtmlEncode(note) + "</p>";

        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
               titleTag +
               "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline';\">" +
               "<style>html,body{margin:0;padding:8px 12px;font-family:Segoe UI,Arial,sans-serif;" +
               "font-size:13px;white-space:pre-wrap;word-break:break-word;background:Window;color:WindowText;}" +
               "a{color:#0645ad;}" +
               ".note{white-space:normal;border-left:3px solid #777;padding-left:8px;margin:0 0 12px 0;color:#555;}</style>" +
               "</head><body tabindex=\"0\">" + noteHtml + linked + "</body></html>";
    }

    private static string AutoLinkPlainTextUrls(string encoded)
    {
        try
        {
            return AutoLinkUrl.Replace(encoded, m =>
            {
                var url      = m.Value;
                var trailing = string.Empty;
                while (url.Length > 0 && Array.IndexOf(AutoLinkTrailingPunct, url[^1]) >= 0)
                {
                    trailing = url[^1] + trailing;
                    url = url[..^1];
                }
                if (url.Length == 0) return m.Value;
                return $"<a href=\"{url}\" rel=\"nofollow noreferrer\">{url}</a>{trailing}";
            });
        }
        catch (RegexMatchTimeoutException) { return encoded; }
    }

    private static string StripHeavyHtml(string html)
    {
        var body = html;
        body = SafeRegexReplace(body, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<script\\b.*?</script>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<style\\b.*?</style>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<svg\\b.*?</svg>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<(iframe|object|embed|video|audio|canvas|form)\\b.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "<(img|link|base|input|button|meta)\\b[^>]*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = SafeRegexReplace(body, "\\s(on\\w+|style|src|srcset|background)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return body;
    }

    private static string RemoveTitle(string html) =>
        SafeRegexReplace(html, "<title[^>]*>.*?</title>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static string EnsureBodyFocusable(string html)
    {
        if (!html.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("tabindex=", StringComparison.OrdinalIgnoreCase))
            return html;
        return SafeRegexReplace(html, "<body\\b", "<body tabindex=\"0\"", RegexOptions.IgnoreCase);
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = StripHeavyHtml(html);
        text = SafeRegexReplace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = SafeRegexReplace(text, "</(p|div|tr|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = SafeRegexReplace(text, "<[^>]+>", " ", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text);
        text = SafeRegexReplace(text, "[ \\t\\r\\f\\v]+", " ", RegexOptions.None);
        text = SafeRegexReplace(text, "\\n\\s+|\\s+\\n", "\n", RegexOptions.None);
        text = SafeRegexReplace(text, "\\n{3,}", "\n\n", RegexOptions.None);
        return text.Trim();
    }

    private static string SafeRegexReplace(string input, string pattern, string replacement, RegexOptions options)
    {
        try { return Regex.Replace(input, pattern, replacement, options, HtmlRegexTimeout); }
        catch (RegexMatchTimeoutException) { return input; }
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        return value[..maxChars] + "\n\n[Message truncated for display.]";
    }

    private static int CountOccurrences(
        string value, string needle, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, comparison)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
