using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
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
    private static readonly TimeSpan WebViewNavigationTimeout = TimeSpan.FromSeconds(4);

    private readonly MessageWindowViewModel _vm;
    private bool _webViewReady;
    private int  _renderVersion;

    private CancellationTokenSource _loadCts = new();
    private PropertyChangedEventHandler? _vmPropertyChangedHandler;

    // Services needed for loading message bodies.
    private readonly IMailService        _imap;
    private readonly ILocalStoreService  _localStore;
    private readonly CoreWebView2Environment? _sharedEnv;

    // Local command registry for the command palette (issue 53).
    private readonly CommandRegistry _localRegistry = new();

    // F6 focus cycle: 0=Toolbar, 1=Headers, 2=Body
    private int _f6FocusStop;

    public MessageWindow(
        MessageWindowViewModel vm,
        IMailService imap,
        ILocalStoreService localStore,
        CoreWebView2Environment? sharedEnv = null)
    {
        _vm         = vm;
        _imap       = imap;
        _localStore = localStore;
        _sharedEnv  = sharedEnv;

        InitializeComponent();
        DataContext = vm;

        vm.RequestClose            += _ => Close();
        vm.RequestMoveToMainWindow += OnMoveToMainWindowRequested;
        _vmPropertyChangedHandler   = async (_, e) =>
        {
            if (e.PropertyName == nameof(MessageWindowViewModel.SelectedMessage))
                await LoadSelectedMessageAsync();
        };
        vm.PropertyChanged += _vmPropertyChangedHandler;

        RegisterLocalCommands();

        Loaded += OnLoaded;
    }

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(
            id: "message.reply", category: "Mail", title: "Reply",
            execute: () => _vm.ReplyCommand.Execute(null),
            defaultKey: Key.R, defaultModifiers: ModifierKeys.Control));

        _localRegistry.Register(new CommandDefinition(
            id: "message.replyAll", category: "Mail", title: "Reply All",
            execute: () => _vm.ReplyAllCommand.Execute(null),
            defaultKey: Key.R, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _localRegistry.Register(new CommandDefinition(
            id: "message.forward", category: "Mail", title: "Forward",
            execute: () => _vm.ForwardCommand.Execute(null),
            defaultKey: Key.F, defaultModifiers: ModifierKeys.Control));

        _localRegistry.Register(new CommandDefinition(
            id: "message.delete", category: "Mail", title: "Delete",
            execute: () => _vm.DeleteMessageCommand.Execute(null),
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None));

        _localRegistry.Register(new CommandDefinition(
            id: "message.markRead", category: "Mail", title: "Mark as Read",
            execute: () => _vm.MarkReadCommand.Execute(null),
            defaultKey: Key.Q, defaultModifiers: ModifierKeys.Control));

        _localRegistry.Register(new CommandDefinition(
            id: "message.grabAddresses", category: "Contacts", title: "Grab Addresses from Message",
            execute: () => _vm.GrabAddressesCommand.Execute(null),
            defaultKey: Key.G, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _localRegistry.Register(new CommandDefinition(
            id: "window.previousMessage", category: "Mail", title: "Previous Message",
            execute: () => _vm.PreviousMessageCommand.Execute(null),
            isAvailable: () => _vm.CanNavigatePrevious));

        _localRegistry.Register(new CommandDefinition(
            id: "window.nextMessage", category: "Mail", title: "Next Message",
            execute: () => _vm.NextMessageCommand.Execute(null),
            isAvailable: () => _vm.CanNavigateNext));

        _localRegistry.Register(new CommandDefinition(
            id: "window.moveToMainWindow", category: "View", title: "Move to Main Window",
            execute: () => _vm.MoveToMainWindowCommand.Execute(null)));

        _localRegistry.Register(new CommandDefinition(
            id: "window.close", category: "View", title: "Close Window",
            execute: Close,
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = _sharedEnv ?? await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "QuickMail", "WebView2"));
            await MessageBody.EnsureCoreWebView2Async(env);
            _webViewReady = true;

            MessageBody.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MessageBody.CoreWebView2.Settings.AreDevToolsEnabled             = false;
            MessageBody.CoreWebView2.Settings.IsStatusBarEnabled             = false;

            // Relay Escape, Shift+Tab, F6 / Shift+F6, and Ctrl+W from inside the WebView.
            await MessageBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){" +
                "if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}" +
                "else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}" +
                "else if(e.key==='F6'&&!e.shiftKey){window.chrome.webview.postMessage('f6');e.preventDefault();}" +
                "else if(e.key==='F6'&&e.shiftKey){window.chrome.webview.postMessage('shift-f6');e.preventDefault();}" +
                "else if(e.ctrlKey&&e.key==='w'){window.chrome.webview.postMessage('ctrl-w');e.preventDefault();}" +
                "});");

            MessageBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                switch (msg)
                {
                    case "escape":     Dispatcher.InvokeAsync(Close,                DispatcherPriority.Input); break;
                    case "ctrl-w":     Dispatcher.InvokeAsync(Close,                DispatcherPriority.Input); break;
                    case "shift-tab":  Dispatcher.InvokeAsync(FocusLastHeaderField,  DispatcherPriority.Input); break;
                    case "f6":         Dispatcher.InvokeAsync(() => CycleFocus(true),  DispatcherPriority.Input); break;
                    case "shift-f6":   Dispatcher.InvokeAsync(() => CycleFocus(false), DispatcherPriority.Input); break;
                }
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

        // Cancel any in-flight load from a previous navigation (issue 42).
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _vm.IsLoading = true;
        try
        {
            MailMessageDetail? detail = null;
            try
            {
                detail = await _localStore.LoadDetailAsync(
                    summary.AccountId, summary.FolderName, summary.MessageId);
            }
            catch { /* local store unavailable — fetch from IMAP below */ }

            if (detail == null)
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.MessageId, ct);
            }

            ct.ThrowIfCancellationRequested();
            if (detail == null) return;

            _vm.MessageDetail = detail;
            await ShowMessageBodyAsync(detail);
        }
        catch (OperationCanceledException) { /* window closed mid-load — normal */ }
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
        var html = await Task.Run(() => MessageBodyHtmlBuilder.BuildMessageHtml(detail));
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
            _f6FocusStop = 2; // body is now focused
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
        _f6FocusStop = 2;
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

    // ── F6 focus cycle (issue 53 Bug 1) ──────────────────────────────────────────
    // Three stops: 0 = toolbar, 1 = header fields, 2 = message body.

    private void CycleFocus(bool forward)
    {
        _f6FocusStop = forward
            ? (_f6FocusStop + 1) % 3
            : (_f6FocusStop - 1 + 3) % 3;

        switch (_f6FocusStop)
        {
            case 0: ToolbarFirstFocus(); break;
            case 1: SubjectField.Focus(); break;
            case 2: FocusMessageBodyHost(); break;
        }
    }

    private void ToolbarFirstFocus()
    {
        // Focus the first focusable button in the toolbar.
        PrevButton.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = Keyboard.Modifiers;

        if (key == Key.Escape || (key == Key.W && mod == ModifierKeys.Control))
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
        else if (key == Key.R && mod == ModifierKeys.Control)
        {
            _vm.ReplyCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.R && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _vm.ReplyAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.F && mod == ModifierKeys.Control)
        {
            _vm.ForwardCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.Delete && mod == ModifierKeys.None)
        {
            _vm.DeleteMessageCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.Q && mod == ModifierKeys.Control)
        {
            _vm.MarkReadCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.G && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _vm.GrabAddressesCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.F6 && mod == ModifierKeys.None)
        {
            CycleFocus(true);
            e.Handled = true;
        }
        else if (key == Key.F6 && mod == ModifierKeys.Shift)
        {
            CycleFocus(false);
            e.Handled = true;
        }
        else if (key == Key.P && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
        }
    }

    private void OpenCommandPalette()
    {
        var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
        palette.ShowDialog();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Unsubscribe before disposing _loadCts — a queued PropertyChanged event
        // would otherwise call LoadSelectedMessageAsync on the disposed CTS.
        if (_vmPropertyChangedHandler != null)
            _vm.PropertyChanged -= _vmPropertyChangedHandler;
        _loadCts.Cancel();
        _loadCts.Dispose();
    }

    private void FocusLastHeaderField() => SubjectField.Focus();

    private void OnMoveToMainWindowRequested(MessageWindowViewModel vm)
    {
        MoveToMainWindowRequested?.Invoke(this, vm);
        Close();
    }

    /// <summary>
    /// Raised when the user selects "Move to Main Window".
    /// The owning code (App.xaml.cs or MainWindow) should open the message as a tab.
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
}
