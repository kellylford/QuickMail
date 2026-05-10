using System;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Converts IsRead (bool) to FontWeight: false (unread) = Bold, true (read) = Normal.
/// </summary>
public class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? FontWeights.Normal : FontWeights.Bold;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Converts bool to Visibility: true → Collapsed, false → Visible.
/// Used to hide the standard message ListView when conversation view is on.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ISmtpService _smtp;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly IImapService _imap;
    private bool _webViewReady;

    public MainWindow(
        MainViewModel vm,
        ISmtpService smtp,
        IAccountService accountService,
        ICredentialService credentials,
        IImapService imap)
    {
        _vm = vm;
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;

        InitializeComponent();
        DataContext = vm;

        vm.ComposeRequested += OpenComposeWindow;
        vm.ManageAccountsRequested += OpenAccountManager;
        vm.MessageListFocusRequested += ReturnFocusToMessageList;

        // Re-focus the active message panel whenever the Messages collection is replaced
        // (happens after Refresh, Load More, and folder changes).
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Messages) && IsActive)
                Dispatcher.InvokeAsync(FocusActiveMessagePanel, DispatcherPriority.Input);
        };

        PreviewKeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
    }

    // On startup: initialise WebView2, connect to first account, open INBOX, focus message list
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialise the embedded browser.  Wire Escape before doing anything else.
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickMail", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await MessageBody.EnsureCoreWebView2Async(env);
            _webViewReady = true;

            // Disable unnecessary browser chrome / context menus
            MessageBody.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MessageBody.CoreWebView2.Settings.AreDevToolsEnabled = false;
            MessageBody.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Inject Escape and F6 relay into every page at the host level — runs before any CSP.
            await MessageBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){"
                +"if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}"
                +"else if(e.key==='F6'){window.chrome.webview.postMessage(e.shiftKey?'shift-f6':'f6');e.preventDefault();}"
                +"});");

            // JavaScript in every page posts a message when Escape or F6 is pressed,
            // which we relay back to WPF to move focus appropriately.
            MessageBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == "escape")
                    Dispatcher.InvokeAsync(ReturnFocusToMessageList, DispatcherPriority.Input);
                else if (msg == "f6")
                    Dispatcher.InvokeAsync(() => _ = CycleFocusAsync(true), DispatcherPriority.Input);
                else if (msg == "shift-f6")
                    Dispatcher.InvokeAsync(() => _ = CycleFocusAsync(false), DispatcherPriority.Input);
            };
        }
        catch (Exception ex)
        {
            LogService.Log("WebView2 init failed", ex);
            // Continue — message body just won't show
        }

        var firstAccount = _vm.Accounts.FirstOrDefault();
        if (firstAccount == null)
        {
            OpenAccountManager();
            return;
        }

        // Show local cache immediately so the UI is never blank on startup.
        await _vm.InitialLoadAsync();
        FocusMessageListFirstItem();

        // Connect accounts and sync new mail in the background; messages trickle in via FolderSynced.
        _ = _vm.StartBackgroundSyncAsync();
    }

    // Global key handler (PreviewKeyDown so it fires before any child can swallow the event).
    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.KeyboardDevice.Modifiers)
        {
            case ModifierKeys.Control:
                switch (e.Key)
                {
                    case Key.D0: ToolbarFirstButton.Focus(); e.Handled = true; break;
                    case Key.D1: AccountList.Focus();        e.Handled = true; break;
                    case Key.D2: FolderList.Focus();         e.Handled = true; break;
                    case Key.D3:
                        // Focus whichever message panel is currently visible
                        if (_vm.IsConversationView)
                            ConversationTree.Focus();
                        else
                            MessageList.Focus();
                        e.Handled = true;
                        break;
                    case Key.Y:  OpenFolderPicker();         e.Handled = true; break;
                    case Key.R:  e.Handled = true; await _vm.ReplyCommand.ExecuteAsync(null);   break;
                    case Key.F:  e.Handled = true; await _vm.ForwardCommand.ExecuteAsync(null); break;
                }
                break;

            case ModifierKeys.Control | ModifierKeys.Shift:
                switch (e.Key)
                {
                    case Key.R:
                        e.Handled = true;
                        await _vm.ReplyAllCommand.ExecuteAsync(null);
                        break;
                    case Key.C:
                        _vm.IsConversationView = !_vm.IsConversationView;
                        e.Handled = true;
                        break;
                }
                break;

            case ModifierKeys.None:
                if (e.Key == Key.F6) { e.Handled = true; await CycleFocusAsync(true); }
                break;

            case ModifierKeys.Shift:
                if (e.Key == Key.F6) { e.Handled = true; await CycleFocusAsync(false); }
                break;
        }
    }

    private async void OpenFolderPicker()
    {
        if (_vm.CachedFolders.Count == 0) return;
        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, MainViewModel.AllMailFolder) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedFolder is MailFolderModel folder)
        {
            // Switch accounts if needed (AllMail has no specific account)
            if (picker.SelectedAccount != null && picker.SelectedAccount.Id != _vm.SelectedAccount?.Id)
                await _vm.SelectAccountCommand.ExecuteAsync(picker.SelectedAccount);

            // Resolve to the live instance from the folder list
            var target = _vm.Folders.FirstOrDefault(f =>
                             !f.IsHeader &&
                             f.FullName.Equals(folder.FullName, StringComparison.OrdinalIgnoreCase) &&
                             (folder.AccountId == Guid.Empty || f.AccountId == folder.AccountId))
                         ?? folder;
            await _vm.SelectFolderCommand.ExecuteAsync(target);
            FocusActiveMessagePanel();
        }
    }

    // When keyboard focus enters a list, ensure an item is selected and — for the
    // Tab while inside the toolbar exits to the adjacent tab stop instead of cycling
    // through every button.  WPF's ToolBar template hard-codes TabNavigation=Cycle on its
    // inner ToolBarPanel, which our XAML attribute cannot override, so we handle it here.
    private void Toolbar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;
        e.Handled = true;
        var dir = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0
            ? FocusNavigationDirection.Previous
            : FocusNavigationDirection.Next;
        MainToolbar.MoveFocus(new TraversalRequest(dir));
    }

    // When keyboard focus enters a list, ensure an item is selected and — for the
    // message list — ensure focus lands on the actual row (not the ListView shell).
    // This covers both Tab navigation (Once mode) and Ctrl+1/2/3 direct jumps.
    private void List_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ListBox lb) return;

        if (lb.SelectedIndex < 0 && lb.Items.Count > 0)
            lb.SelectedIndex = 0;

        // If focus landed on the ListView container itself rather than on a row
        // (e.g. via Ctrl+3), redirect into the selected row so arrow keys work.
        if (ReferenceEquals(e.NewFocus, lb))
        {
            var idx = lb.SelectedIndex >= 0 ? lb.SelectedIndex : 0;
            if (lb == MessageList)
                Dispatcher.InvokeAsync(() => FocusItemAt(idx), DispatcherPriority.Input);
        }
    }

    // Enter on an account: connect and load folders; focus stays here
    private async void AccountList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AccountList.SelectedItem is AccountModel account)
        {
            e.Handled = true;
            await _vm.SelectAccountCommand.ExecuteAsync(account);
        }
    }

    // Enter on a folder node: load messages then move focus to the active message panel.
    // Account-group nodes (IsHeader=true) and intermediate path nodes (Folder=null) are skipped.
    private async void FolderList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            FolderList.SelectedItem is FolderTreeNode node &&
            node.Folder != null)
        {
            e.Handled = true;
            await _vm.SelectFolderCommand.ExecuteAsync(node.Folder);
            FocusActiveMessagePanel();
        }
    }

    // Focuses the first (or currently selected) ListViewItem so Up/Down arrow work
    // immediately after loading a folder.
    //
    // Why the two-step approach:
    //   After an async data load, WPF has queued DataBind (8), Render (7), and
    //   Loaded (6) dispatcher items to update the ListView.  Calling this method
    //   synchronously would run before those items, so ContainerFromIndex returns
    //   null and the fallback MessageList.Focus() gives the *control* focus — not
    //   any row — causing Down arrow to exit to the next tab stop (the toolbar).
    //
    //   Queuing at DispatcherPriority.Input (5) defers execution until all of those
    //   higher-priority items have been processed.  If the VirtualizingStackPanel
    //   still hasn't generated the container (rare first-load scenario), the
    //   ItemContainerGenerator.StatusChanged event covers the remaining gap.
    private void FocusMessageListFirstItem()
    {
        if (MessageList.Items.Count == 0) { MessageList.Focus(); return; }
        if (MessageList.SelectedIndex < 0) MessageList.SelectedIndex = 0;

        var idx = MessageList.SelectedIndex;
        MessageList.ScrollIntoView(MessageList.Items[idx]);

        Dispatcher.InvokeAsync(() => FocusItemAt(idx), DispatcherPriority.Input);
    }

    private void FocusItemAt(int idx)
    {
        if (idx >= MessageList.Items.Count) { MessageList.Focus(); return; }

        if (MessageList.ItemContainerGenerator.ContainerFromIndex(idx) is ListViewItem row)
        {
            row.Focus();
            return;
        }

        // VirtualizingStackPanel hasn't realized the container yet.
        // Wait for generation to complete, then focus.
        void OnStatusChanged(object? s, EventArgs e)
        {
            if (MessageList.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
                return;
            MessageList.ItemContainerGenerator.StatusChanged -= OnStatusChanged;
            Dispatcher.InvokeAsync(() =>
            {
                if (MessageList.ItemContainerGenerator.ContainerFromIndex(idx) is ListViewItem r)
                    r.Focus();
                else
                    MessageList.Focus();
            }, DispatcherPriority.Input);
        }
        MessageList.ItemContainerGenerator.StatusChanged += OnStatusChanged;
    }

    // Single click: load message into the reading pane (standard reading-pane UX).
    private async void MessageList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (MessageList.SelectedItem is MailMessageSummary summary)
        {
            await _vm.SelectMessageCommand.ExecuteAsync(summary);
            if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                await ShowMessageBodyAsync(_vm.MessageDetail);
        }
    }

    // Enter on a message: load body; Delete: delete all selected messages
    private async void MessageList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && MessageList.SelectedItem is MailMessageSummary summary)
        {
            e.Handled = true;
            await _vm.SelectMessageCommand.ExecuteAsync(summary);
            if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                await ShowMessageBodyAsync(_vm.MessageDetail);
        }
        else if (e.Key == Key.Delete && MessageList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            var toDelete = MessageList.SelectedItems
                .OfType<MailMessageSummary>()
                .ToList();
            await _vm.DeleteMessagesAsync(toDelete);
            FocusMessageListFirstItem();
        }
    }

    // Render the message body in the browser and move focus into it
    private async Task ShowMessageBodyAsync(MailMessageDetail detail)
    {
        if (!_webViewReady) return;

        string html;
        if (!string.IsNullOrWhiteSpace(detail.HtmlBody))
        {
            // Render the sender's HTML directly.
            // Inject a tight CSP that blocks email-embedded scripts; our Escape relay
            // was added via AddScriptToExecuteOnDocumentCreatedAsync and is unaffected by CSP.
            const string cspTag =
                "<meta http-equiv=\"Content-Security-Policy\" " +
                "content=\"script-src 'none'; object-src 'none'; frame-src 'none';\">";
            var body = detail.HtmlBody;
            var headIdx = body.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            html = headIdx >= 0
                ? body.Insert(headIdx + 6, cspTag)
                : cspTag + body;
        }
        else
        {
            // Plain-text fallback: HTML-encode so tags/special chars are safe
            var encoded = WebUtility.HtmlEncode(detail.PlainTextBody ?? string.Empty);
            html =
                "<!DOCTYPE html>\n" +
                "<html lang=\"en\">\n" +
                "<head><meta charset=\"utf-8\"><style>\n" +
                "html,body{margin:0;padding:8px 12px;font-family:Segoe UI,Arial,sans-serif;" +
                "font-size:13px;white-space:pre-wrap;word-break:break-word;" +
                "background:Window;color:WindowText;outline:none;}\n" +
                "</style></head>\n" +
                "<body tabindex=\"0\">" + encoded + "</body>\n" +
                "</html>";
        }

        // Wait for navigation to finish before focusing so the screen reader
        // gets the fully rendered document, not a blank page.
        var tcs = new TaskCompletionSource<bool>();
        void OnNavigated(object? s, CoreWebView2NavigationCompletedEventArgs ev)
        {
            MessageBody.CoreWebView2.NavigationCompleted -= OnNavigated;
            tcs.TrySetResult(ev.IsSuccess);
        }
        MessageBody.CoreWebView2.NavigationCompleted += OnNavigated;
        MessageBody.CoreWebView2.NavigateToString(html);

        await tcs.Task;

        // Give focus to the browser control and push keyboard focus into <body>
        MessageBody.Focus();
        await MessageBody.CoreWebView2.ExecuteScriptAsync("document.body.focus()");
    }

    // Return keyboard focus to the active message panel after reading a message.
    private void ReturnFocusToMessageList()
    {
        if (_vm.IsConversationView)
        {
            FocusConversationTreeFirstItem();
            return;
        }
        if (MessageList.Items.Count == 0) { MessageList.Focus(); return; }
        var idx = MessageList.SelectedIndex >= 0 ? MessageList.SelectedIndex : 0;
        MessageList.ScrollIntoView(MessageList.Items[idx]);
        Dispatcher.InvokeAsync(() => FocusItemAt(idx), DispatcherPriority.Input);
    }

    // Routes focus to whichever message panel is currently visible.
    private void FocusActiveMessagePanel()
    {
        if (_vm.IsConversationView)
            FocusConversationTreeFirstItem();
        else
            FocusMessageListFirstItem();
    }

    // ── F6 pane-cycling helpers ──────────────────────────────────────────────

    // Returns the index of the pane that currently holds keyboard focus:
    //   0 = Toolbar, 1 = Account list, 2 = Folder list,
    //   3 = Message list / Conversation tree, 4 = Reading pane (WebView2).
    // Falls back to 0 if no match (e.g. focus is on a dialog or status bar).
    private int GetFocusedPaneIndex()
    {
        if (MainToolbar.IsKeyboardFocusWithin)  return 0;
        if (AccountList.IsKeyboardFocusWithin)  return 1;
        if (FolderList.IsKeyboardFocusWithin)   return 2;
        if (MessageList.IsKeyboardFocusWithin || ConversationTree.IsKeyboardFocusWithin) return 3;
        if (MessageBody.IsKeyboardFocusWithin)  return 4;
        return 0;
    }

    // Moves keyboard focus to the pane at the given index.
    private async Task FocusPaneAtAsync(int index)
    {
        switch (index)
        {
            case 0: ToolbarFirstButton.Focus(); break;
            case 1: AccountList.Focus(); break;
            case 2: FolderList.Focus(); break;
            case 3: FocusActiveMessagePanel(); break;
            case 4:
                if (_vm.IsMessageOpen && _webViewReady)
                {
                    MessageBody.Focus();
                    await MessageBody.CoreWebView2.ExecuteScriptAsync("document.body.focus()");
                }
                else
                {
                    FocusActiveMessagePanel();
                }
                break;
        }
    }

    // Cycles keyboard focus forward (F6) or backward (Shift+F6) through the pane ring.
    // The reading pane (index 4) is included only when a message is open.
    private async Task CycleFocusAsync(bool forward)
    {
        int count   = (_vm.IsMessageOpen && _webViewReady) ? 5 : 4;
        int current = Math.Min(GetFocusedPaneIndex(), count - 1);
        int next    = forward
            ? (current + 1) % count
            : (current - 1 + count) % count;
        await FocusPaneAtAsync(next);
    }

    // Focuses the first (or currently selected) TreeViewItem in the conversation tree.
    // WPF TreeView manages its own focus state natively; we just call Focus() on the
    // control and let it route to the last-focused item (or the first if none).
    private void FocusConversationTreeFirstItem()
    {
        if (ConversationTree.Items.Count == 0) { ConversationTree.Focus(); return; }
        Dispatcher.InvokeAsync(ConversationTree.Focus, DispatcherPriority.Input);
    }

    // ── Conversation tree event handlers ────────────────────────────────────────

    // When the ConversationTree gets keyboard focus, ensure an item is highlighted.
    private void ConversationTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // If no item is selected, select and focus the first root item.
        if (ConversationTree.SelectedItem == null && ConversationTree.Items.Count > 0)
        {
            if (ConversationTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                first.IsSelected = true;
                first.Focus();
            }
        }
    }

    // Sync the ViewModel's SelectedMessage when the user navigates to a message leaf node,
    // so Reply/Forward/Delete commands always operate on the right message.
    private void ConversationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MailMessageSummary msg)
            _vm.SelectedMessage = msg;
    }

    // Keyboard actions in the conversation tree.
    private async void ConversationTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ConversationTree.SelectedItem is MailMessageSummary msg)
            {
                // Open the selected message in the reading pane.
                e.Handled = true;
                await _vm.SelectMessageCommand.ExecuteAsync(msg);
                if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                    await ShowMessageBodyAsync(_vm.MessageDetail);
            }
            else if (ConversationTree.SelectedItem is ConversationGroup group)
            {
                e.Handled = true;
                if (group.Messages.Count == 1)
                {
                    // Single-message conversation: open the message directly.
                    var singleMsg = group.Messages[0];
                    _vm.SelectedMessage = singleMsg;
                    await _vm.SelectMessageCommand.ExecuteAsync(singleMsg);
                    if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                        await ShowMessageBodyAsync(_vm.MessageDetail);
                }
                else
                {
                    // Toggle expand/collapse on the selected conversation node.
                    if (ConversationTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi)
                        tvi.IsExpanded = !tvi.IsExpanded;
                }
            }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (ConversationTree.SelectedItem is MailMessageSummary toDelete)
            {
                _vm.SelectedMessage = toDelete;
                await _vm.DeleteMessageCommand.ExecuteAsync(null);
            }
            else if (ConversationTree.SelectedItem is ConversationGroup group)
            {
                var targetIdx = _vm.Conversations.IndexOf(group);
                await _vm.DeleteMessagesAsync(group.Messages);
                LandOnConversationAfterRebuild(targetIdx);
            }
        }
    }

    // After an async conversation rebuild, selects and focuses the conversation
    // at the given index (clamped to the new list size).
    private void LandOnConversationAfterRebuild(int targetIdx)
    {
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.Conversations)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            Dispatcher.InvokeAsync(() =>
            {
                if (_vm.Conversations.Count == 0) return;
                var idx = Math.Max(0, Math.Min(targetIdx, _vm.Conversations.Count - 1));
                var conv = _vm.Conversations[idx];
                if (ConversationTree.ItemContainerGenerator.ContainerFromItem(conv) is TreeViewItem tvi)
                {
                    tvi.IsSelected = true;
                    tvi.Focus();
                }
                else
                {
                    // Container not yet realized — retry at a lower priority.
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ConversationTree.ItemContainerGenerator.ContainerFromItem(conv) is TreeViewItem tvi2)
                        {
                            tvi2.IsSelected = true;
                            tvi2.Focus();
                        }
                    }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    private void OpenComposeWindow(ComposeModel composeModel)
    {
        var composeVm = new ComposeViewModel(_smtp, _accountService, _credentials);
        composeVm.Seed(composeModel);
        var window = new ComposeWindow(composeVm) { Owner = this };
        composeVm.CloseRequested += window.Close;
        window.Show();
    }

    private void OpenAccountManager()
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap);
        var dialog = new AccountManagerDialog(accountVm) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.RefreshAccountList();
    }

}
