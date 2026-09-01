using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
[SuppressMessage("Design", "CA1001", Justification = "_loadCts is cancelled and replaced in OnClosing; WPF never calls Dispose on a Window, so implementing IDisposable would be dead code.")]
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
    private readonly IThemeService?      _themeService;
    private readonly IConfigService?     _configService;

    // Local command registry for the command palette (issue 53).
    private readonly CommandRegistry _localRegistry = new();

    // F6 focus cycle: 0=Toolbar, 1=Headers, 2=Body
    private int _f6FocusStop;

    public MessageWindow(
        MessageWindowViewModel vm,
        IMailService imap,
        ILocalStoreService localStore,
        CoreWebView2Environment? sharedEnv = null,
        IThemeService? themeService = null,
        IConfigService? configService = null)
    {
        _vm            = vm;
        _imap          = imap;
        _localStore    = localStore;
        _sharedEnv     = sharedEnv;
        _themeService  = themeService;
        _configService = configService;

        InitializeComponent();
        DataContext = vm;

        if (_themeService != null)
            _themeService.ThemeChanged += OnThemeChanged;

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
            id: "window.togglePlainText", category: "View", title: "Toggle Plain Text View",
            execute: TogglePlainTextView,
            defaultKey: Key.H, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _localRegistry.Register(new CommandDefinition(
            id: "window.previousMessage", category: "Mail", title: "Previous Message",
            execute: () => _vm.PreviousMessageCommand.Execute(null),
            isAvailable: () => _vm.CanNavigatePrevious));

        _localRegistry.Register(new CommandDefinition(
            id: "window.nextMessage", category: "Mail", title: "Next Message",
            execute: () => _vm.NextMessageCommand.Execute(null),
            isAvailable: () => _vm.CanNavigateNext));

        _localRegistry.Register(new CommandDefinition(
            id: "window.focusAttachments", category: "View", title: "Focus Attachment List",
            execute: FocusAttachmentList,
            defaultKey: Key.A, defaultModifiers: ModifierKeys.Alt));

        _localRegistry.Register(new CommandDefinition(
            id: "window.moveToMainWindow", category: "View", title: "Move to Main Window",
            execute: () => _vm.MoveToMainWindowCommand.Execute(null)));

        // Ctrl+Shift+W here too, so watching a thread works at the moment you are actually reading
        // it. This window owns a separate registry, which is why the main window's registration
        // does not reach it. The work itself is routed back to MainViewModel (see
        // WatchToggleRequested) rather than done here: the watch list is one thing, and the main
        // window has rows and a possibly-open watched folder to keep in step with it.
        _localRegistry.Register(new CommandDefinition(
            id: "window.toggleWatch", category: "Mail", title: "Watch Conversation",
            description: "Watch or unwatch this message's conversation",
            execute: RequestWatchToggle,
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => !string.IsNullOrWhiteSpace(_vm.SelectedMessage?.Subject)));

        // Answering an invitation is otherwise reachable only by tabbing to the links inside the
        // card's document. No default keys — the palette is the discoverable route, and the New
        // Window Checklist wants window-scoped actions there whether or not they have a gesture.
        // Category "Mail" matches mail.acceptInvite and friends in MainViewModel: the same three
        // actions must not group differently depending on which palette the user opened.
        _localRegistry.Register(new CommandDefinition(
            id: "window.acceptInvite", category: "Mail", title: "Accept Invitation",
            execute: () => RespondToInvite(InviteResponse.Accept),
            isAvailable: CanRespondToInvite));

        _localRegistry.Register(new CommandDefinition(
            id: "window.tentativeInvite", category: "Mail", title: "Tentatively Accept Invitation",
            execute: () => RespondToInvite(InviteResponse.Tentative),
            isAvailable: CanRespondToInvite));

        _localRegistry.Register(new CommandDefinition(
            id: "window.declineInvite", category: "Mail", title: "Decline Invitation",
            execute: () => RespondToInvite(InviteResponse.Decline),
            isAvailable: CanRespondToInvite));

        _localRegistry.Register(new CommandDefinition(
            id: "window.close", category: "View", title: "Close Window",
            execute: Close,
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control));
    }

    /// <summary>
    /// Raised when the user asks to watch or unwatch the open message's conversation. The main
    /// window handles it, so the watch list has exactly one writer and the main window's rows and
    /// watched folder stay in step. Carries the subject because that is the whole input.
    /// </summary>
    public event Action<string>? WatchToggleRequested;

    private void RequestWatchToggle()
    {
        var subject = _vm.SelectedMessage?.Subject;
        if (!string.IsNullOrWhiteSpace(subject))
            WatchToggleRequested?.Invoke(subject);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AccessibilityHelper.RegisterDebugInputTrace(this);

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

            ApplyWebViewColorScheme();

            // Relay Escape, Shift+Tab, F6 / Shift+F6, Ctrl+W and Ctrl+Shift+W from inside the WebView.
            //
            // Focus lands INSIDE this document as soon as the window opens, so a gesture that is
            // only handled by the WPF key ladder is unreachable in practice — which is why watching
            // a thread had to be relayed here as well as registered there. Note the Ctrl+Shift+W
            // test must accept 'W': the browser reports the upper-case key when Shift is held, so
            // the lower-case-only Ctrl+W branch below cannot match it (and must not).
            await MessageBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){" +
                "if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}" +
                "else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}" +
                "else if(e.key==='F6'&&!e.shiftKey){window.chrome.webview.postMessage('f6');e.preventDefault();}" +
                "else if(e.key==='F6'&&e.shiftKey){window.chrome.webview.postMessage('shift-f6');e.preventDefault();}" +
                "else if(e.altKey&&(e.key==='a'||e.key==='A')){window.chrome.webview.postMessage('focus-attachments');e.preventDefault();}" +
                "else if(e.ctrlKey&&e.shiftKey&&(e.key==='w'||e.key==='W')){window.chrome.webview.postMessage('ctrl-shift-w');e.preventDefault();}" +
                "else if(e.ctrlKey&&e.key==='w'){window.chrome.webview.postMessage('ctrl-w');e.preventDefault();}" +
                "else if(e.ctrlKey&&e.shiftKey&&(e.key==='p'||e.key==='P')){window.chrome.webview.postMessage('ctrl-shift-p');e.preventDefault();}" +
                "});");

            MessageBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                switch (msg)
                {
                    case "escape":     Dispatcher.InvokeAsync(Close,                DispatcherPriority.Input); break;
                    case "ctrl-w":     Dispatcher.InvokeAsync(Close,                DispatcherPriority.Input); break;
                    case "ctrl-shift-w": Dispatcher.InvokeAsync(RequestWatchToggle, DispatcherPriority.Input); break;
                    case "ctrl-shift-p": Dispatcher.InvokeAsync(OpenCommandPalette, DispatcherPriority.Input); break;
                    case "shift-tab":  Dispatcher.InvokeAsync(FocusLastHeaderField,  DispatcherPriority.Input); break;
                    case "focus-attachments": Dispatcher.InvokeAsync(FocusAttachmentList, DispatcherPriority.Input); break;
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
                // The event card's RSVP buttons are quickmail: links. Cancelling the navigation is
                // what keeps this document — and its aria-live status region — alive across the reply.
                if (uri.StartsWith("quickmail:", StringComparison.OrdinalIgnoreCase))
                {
                    HandleQuickMailUri(uri);
                    return;
                }
                OpenExternal(uri);
            };

            // A link with target="_blank" — or one activated with Ctrl/Shift/middle-click —
            // raises NewWindowRequested INSTEAD of NavigationStarting, so the handler above
            // never sees it. Left unhandled, WebView2's default is to open the URL in its own
            // popup window using QuickMail's user-data folder: no default-browser cookies,
            // passkeys, or extensions. Issue #483.
            MessageBody.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
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
            Exception? storeFailure = null;
            try
            {
                detail = await _localStore.LoadDetailAsync(
                    summary.AccountId, summary.FolderName, summary.MessageId);
            }
            catch (Exception storeEx)
            {
                // Kept, not swallowed. A locked database lands here too, and reporting that as a
                // message which "cannot be recovered" -- with an instruction to delete it -- is
                // how a transient failure becomes real data loss. POP3 makes it worse: every POP3
                // message has a local- id and lives only in this store, so the sentence below
                // would be describing ordinary received mail.
                storeFailure = storeEx;
                LogService.Log("MessageWindow: the local store could not be read", storeEx);
            }

            // A locally-stored draft exists here and nowhere else, so it is never fetched: the
            // backend would be handed an id it never issued, in a folder it may not have (#637).
            //
            // Tests the id alone rather than the account's backend, unlike the guards in
            // MainViewModel. This window has no account service to ask, and it costs nothing:
            // POP3 is the one backend that mints these ids, and its GetMessageDetailAsync reads
            // the same store row that has already come back null here — so the fetch this skips
            // could not have succeeded either.
            if (detail == null && !LocalMessageId.IsLocal(summary.MessageId))
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.MessageId, ct);
            }
            else if (detail != null)
            {
                // A detail cached before the from_addr column existed carries the summary's display
                // name in place of the sender's address, so this window's From line — and a reply
                // started from it — would show a bare name (issue #636). Re-fetching is the only
                // repair: the address is stored nowhere else in the database.
                //
                // Guarded on there BEING a detail: a message whose stored copy will not load now
                // reaches this branch with null, because #637 stops it falling through to the
                // server fetch above. The two changes merged cleanly and compiled anyway.
                detail = await DetailFromAddressRepair.RepairAsync(
                    detail, _localStore, _imap, background: false, ct);
            }

            ct.ThrowIfCancellationRequested();
            if (detail == null)
            {
                // Rendered into the body, not merely announced. AnnouncementCategory.Result is
                // gated by AnnounceResults, so for a user running with custom announcements off an
                // announce-only path IS the blank window with nothing said that this guard exists
                // to prevent -- it just looks fine to anyone who has them on. The sentence goes
                // where focus is about to land, and stays there to be re-read (#637).
                // The SAME sentences the reading pane uses, not a second wording of the same
                // thing: the user guide can only quote one, and a user in Window mode was meeting
                // the other and could not find it (#637).
                // The MESSAGE-worded pair. This branch is not restricted to drafts -- POP3 gives
                // every message a local id, so the fetch above is skipped for all of them -- and
                // the draft-worded constants told those users their draft could not be recovered.
                var missing = storeFailure != null
                    ? ViewModels.MainViewModel.StoreUnreadableMessage
                    : ViewModels.MainViewModel.MissingSavedCopyMessage;
                var placeholder = new MailMessageDetail
                {
                    MessageId  = summary.MessageId,
                    AccountId  = summary.AccountId,
                    FolderName = summary.FolderName,
                    Subject    = summary.Subject,
                    From       = summary.From,
                    PlainTextBody = missing,
                };
                _vm.MessageDetail = placeholder;
                await ShowMessageBodyAsync(placeholder);
                AccessibilityHelper.Announce(this, missing,
                    interrupt: true, category: AnnouncementCategory.Result);
                return;
            }

            _vm.MessageDetail = detail;
            await ShowMessageBodyAsync(detail);

            // Opening a message in a standalone window must mark it read on the server, same as
            // the reading pane and tabs do. Loading here is cache-first, so relying on the body
            // fetch's \Seen side effect would leave prefetched (cache-hit) messages unread in
            // other clients (issue #225). MarkReadCommand invokes MarkReadAction, which MainWindow
            // wires to MainViewModel.MarkMessagesReadAsync — that updates the summary, the local
            // store, and the server, and is a no-op when already read. Runs after the body is
            // shown so a load failure never marks an unread message.
            if (summary.IsRead == false)
                _vm.MarkReadCommand.Execute(null);
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

    /// <summary>The theme's --qm-* CSS block for this window's documents, or null.</summary>
    private string? BuildThemeCss() =>
        _themeService?.BuildMessageCss(_configService?.Load().AppearanceForceMessageTheme ?? false);

    /// <summary>The sticky "read as plain text" preference (issue #34), read live at render time.</summary>
    private bool ReadAsPlainText() => _configService?.Load().ReadAsPlainText ?? false;

    /// <summary>
    /// UID of the invite the in-flight RSVP belongs to. A send takes seconds, and this window has
    /// Previous/Next navigation, so the user can be on another message by the time the reply lands.
    /// Without this, a confirmation would be written into whatever card is showing — announcing
    /// "you accepted this meeting" inside a different invitation.
    /// </summary>
    private string? _pendingRsvpUid;

    /// <summary>
    /// Writes RSVP feedback into this window's own card, inside the document the screen reader is
    /// reading. A host-window announcement is dropped while focus is in the WebView2 (issue #329),
    /// and the main window's reading pane is not showing this message in Window mode.
    /// </summary>
    internal async void SetInviteCardStatus(string text)
    {
        if (!_webViewReady || MessageBody.CoreWebView2 is null) return;
        // Only while this window is still showing the invite the reply was sent for.
        if (!string.Equals(_vm.MessageDetail?.CalendarInvite?.Uid, _pendingRsvpUid, StringComparison.Ordinal))
            return;
        try { await MessageBody.CoreWebView2.ExecuteScriptAsync(EventCardHtmlBuilder.StatusScript(text)); }
        catch (Exception ex) { LogService.Log("MessageWindow.SetInviteCardStatus", ex); }
    }

    /// <summary>
    /// Raised when the user answers the open invitation from this window, with the message the window
    /// has open. The main window handles it, because sending the reply, updating the calendar row,
    /// and announcing the outcome all belong to MainViewModel — the same routing Ctrl+Shift+W uses
    /// (see WatchToggleRequested). The response travels as an <see cref="InviteResponse"/> so the ICS
    /// PARTSTAT and the wording of the confirmation stay in the ViewModel.
    /// </summary>
    public event Action<MailMessageDetail, InviteResponse>? InviteResponseRequested;

    /// <summary>Handles quickmail: pseudo-URIs from this window's event card buttons.</summary>
    private void HandleQuickMailUri(string uri)
    {
        if (uri.StartsWith("quickmail:ics-accept", StringComparison.OrdinalIgnoreCase))
            RespondToInvite(InviteResponse.Accept);
        else if (uri.StartsWith("quickmail:ics-tentative", StringComparison.OrdinalIgnoreCase))
            RespondToInvite(InviteResponse.Tentative);
        else if (uri.StartsWith("quickmail:ics-decline", StringComparison.OrdinalIgnoreCase))
            RespondToInvite(InviteResponse.Decline);
    }

    private void RespondToInvite(InviteResponse response)
    {
        if (_vm.MessageDetail is not { } detail) return;
        _pendingRsvpUid = detail.CalendarInvite?.Uid;
        InviteResponseRequested?.Invoke(detail, response);
    }

    /// <summary>True when this window has an invitation open that can still be answered.</summary>
    private bool CanRespondToInvite() =>
        _vm.MessageDetail?.CalendarInvite is { } invite &&
        !string.Equals(invite.Method, "CANCEL", StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-renders the open message with fresh theme CSS. Never moves focus.</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyWebViewColorScheme();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!_webViewReady || _vm.MessageDetail is not { } detail) return;
            var version = Interlocked.Increment(ref _renderVersion);
            var plainText = ReadAsPlainText();
            var html = await Task.Run(() => MessageBodyHtmlBuilder.BuildMessageHtml(detail, BuildThemeCss(), plainText, _themeService));
            if (version != _renderVersion) return;
            try { MessageBody.CoreWebView2.Stop(); } catch { /* best effort */ }
            MessageBody.CoreWebView2.NavigateToString(html);
        });
    }

    /// <summary>
    /// Flips the sticky plain-text preference (issue #34) and re-renders this window's message
    /// in place without moving focus. Shares <see cref="ConfigModel.ReadAsPlainText"/> with the
    /// main window, so the choice sticks everywhere. Announces the new state.
    /// </summary>
    private void TogglePlainTextView()
    {
        if (_configService is null) return;
        var cfg = _configService.Load();
        cfg.ReadAsPlainText = !cfg.ReadAsPlainText;
        _configService.Save(cfg);

        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_webViewReady && _vm.MessageDetail is { } detail)
            {
                var version = Interlocked.Increment(ref _renderVersion);
                var plainText = cfg.ReadAsPlainText;
                var html = await Task.Run(() => MessageBodyHtmlBuilder.BuildMessageHtml(detail, BuildThemeCss(), plainText, _themeService));
                if (version != _renderVersion) return;
                try { MessageBody.CoreWebView2.Stop(); } catch { /* best effort */ }
                MessageBody.CoreWebView2.NavigateToString(html);
            }
        });

        var msg = cfg.ReadAsPlainText ? "Plain text view on." : "Plain text view off.";
        AccessibilityHelper.Announce(this, msg, interrupt: true, category: AnnouncementCategory.Result);
    }

    private void ApplyWebViewColorScheme()
    {
        if (!_webViewReady || _themeService is null) return;
        try
        {
            MessageBody.CoreWebView2.Profile.PreferredColorScheme = _themeService.IsHighContrastActive
                ? CoreWebView2PreferredColorScheme.Auto
                : _themeService.ResolvedTheme.Base == "dark"
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception ex)
        {
            LogService.Log("MessageWindow.ApplyWebViewColorScheme", ex);
        }
    }

    private async Task ShowMessageBodyAsync(MailMessageDetail detail)
    {
        if (!_webViewReady) return;

        var version = Interlocked.Increment(ref _renderVersion);
        var plainText = ReadAsPlainText();
        // The builder prepends the calendar invite event card when this message is an invitation.
        var html = await Task.Run(() => MessageBodyHtmlBuilder.BuildMessageHtml(detail, BuildThemeCss(), plainText, _themeService));
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

        // Debug screenshot capture (#175): deferred to ApplicationIdle so WebView2
        // has presented the frame; skipped on timeout (page may never have rendered).
        if (completed)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                // A newer render may have started before idle — skip rather than
                // save the next message's pixels under this message's label.
                if (version != _renderVersion) return;
                (Application.Current as App)?.ScreenshotCapture?.Capture(this, $"MessageWindow-{detail.Subject}");
            });

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
        else if (key == Key.H && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            TogglePlainTextView();
            e.Handled = true;
        }
        else if (key == Key.A && mod == ModifierKeys.Alt)
        {
            FocusAttachmentList();
            e.Handled = true;
        }
        // Before the Ctrl+W branch would ever be reached: this window dispatches gestures with a
        // hand-written ladder rather than through the registry, and the branches test `mod` for
        // equality, so Ctrl+Shift+W cannot fall through to Ctrl+W. Kept adjacent so that stays true.
        else if (key == Key.W && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            RequestWatchToggle();
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
        var prev = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
        palette.ShowDialog();
        prev?.Focus();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Unsubscribe before disposing _loadCts — a queued PropertyChanged event
        // would otherwise call LoadSelectedMessageAsync on the disposed CTS.
        if (_vmPropertyChangedHandler != null)
            _vm.PropertyChanged -= _vmPropertyChangedHandler;
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeChanged;
        _loadCts.Cancel();
        _loadCts.Dispose();
    }

    private void AttachmentList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var isShell = ReferenceEquals(e.OriginalSource, AttachmentList);
        LogService.Debug($"[ATTLOG] MsgWin_AttachList_GotFocus: OrigSrc={e.OriginalSource?.GetType().Name ?? "null"}, " +
                       $"IsShell={isShell}, Items={AttachmentList.Items.Count}, SelIdx={AttachmentList.SelectedIndex}");

        // GotKeyboardFocus bubbles; only act when focus landed on the ListBox shell
        // itself, not on a child ListBoxItem that already has focus.
        if (!isShell) return;
        if (AttachmentList.Items.Count == 0) return;

        if (AttachmentList.SelectedIndex < 0)
            AttachmentList.SelectedIndex = 0;

        var idx = AttachmentList.SelectedIndex;
        var container = AttachmentList.ItemContainerGenerator.ContainerFromIndex(idx);
        LogService.Debug($"[ATTLOG] MsgWin_AttachList_GotFocus: ContainerFromIndex({idx})={container?.GetType().Name ?? "null"}");
        if (container is ListBoxItem focusItem)
            focusItem.Focus();
    }

    // Shift+F10: open the attachment ContextMenu directly. The Applications key is deliberately
    // NOT handled here — Windows raises WM_CONTEXTMENU for it on key up, so opening the menu on key
    // down only gets it torn down again (issue #631). See ContextMenuKeys for the full account.
    // Enter opens the selected attachment; Alt+Enter shows its properties.
    private void AttachmentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        LogService.Debug($"[ATTLOG] MsgWin_AttachList_PreviewKeyDown: eKey={e.Key}, sysKey={e.SystemKey}, computed={key}, mod={e.KeyboardDevice.Modifiers}");

        if (ContextMenuKeys.OpensMenuOnKeyDown(key, e.KeyboardDevice.Modifiers))
        {
            LogService.Debug($"[ATTLOG] MsgWin_AttachList_PreviewKeyDown: opening ContextMenu directly, IsNull={AttachmentList.ContextMenu == null}");
            if (AttachmentList.ContextMenu != null)
            {
                AttachmentList.ContextMenu.PlacementTarget = AttachmentList;
                AttachmentList.ContextMenu.IsOpen = true;
            }
            e.Handled = true;
            return;
        }

        if (key == Key.Return && e.KeyboardDevice.Modifiers == ModifierKeys.None
            && AttachmentList.SelectedItem is AttachmentModel openAtt)
        {
            _ = _vm.OpenAttachmentCommand.ExecuteAsync(openAtt);
            e.Handled = true;
            return;
        }
        if (key == Key.Return && e.KeyboardDevice.Modifiers == ModifierKeys.Alt
            && AttachmentList.SelectedItem is AttachmentModel attachment)
        {
            var (title, sections) = AttachmentPropertiesBuilder.Build(attachment);
            var win = new PropertiesWindow(new PropertiesViewModel(title, sections)) { Owner = this };
            win.ShowDialog();
            e.Handled = true;
        }
    }

    // Attachments open on double-click, the mouse counterpart of Enter above. Single click stays
    // selection only, so clicking down a list of attachments does not launch each one in turn.
    // Left button only: MouseDoubleClick is raised for any button, and a double right-click must
    // not shell-open a file.
    private void AttachmentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (MouseActivation.ItemFromClick<AttachmentModel>(e.OriginalSource) is { } attachment)
            _ = _vm.OpenAttachmentCommand.ExecuteAsync(attachment);
    }

    // Alt+A (window.focusAttachments, issue #350): move focus to this message's attachment list.
    // GotKeyboardFocus selects the first item so the screen reader lands on an attachment rather
    // than the empty list shell. When the message has none, announce it instead of moving focus
    // to a collapsed control.
    private void FocusAttachmentList()
    {
        if (AttachmentList.Visibility == Visibility.Visible && AttachmentList.Items.Count > 0)
        {
            AttachmentList.Focus();
        }
        else
        {
            AccessibilityHelper.Announce(this, "No attachments.",
                interrupt: true, category: AnnouncementCategory.Result);
        }
    }

    // Shift+Tab from the WebView2 body lands on the last visible header stop: the attachment
    // list when the message has attachments (issue #350), otherwise the Date field. Previously
    // this always jumped to Subject (the first field), leaving attachments unreachable by
    // Shift+Tab in window mode.
    private void FocusLastHeaderField()
    {
        if (AttachmentList.Visibility == Visibility.Visible && AttachmentList.Items.Count > 0)
            AttachmentList.Focus();
        else
            DateField.Focus();
    }

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

    // Message content is untrusted; only allow-listed schemes (http/https/mailto)
    // may leave the app via ShellExecute. See ExternalUriPolicy.
    private static void OpenExternal(string uri) =>
        Helpers.ExternalUriPolicy.TryOpenExternal(uri);
}
