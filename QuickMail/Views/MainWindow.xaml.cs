using System;
using System.Collections.Generic;
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
using MimeKit;
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

/// <summary>
/// Converts a bool (ShowMessageStatus) to a GridViewColumn width.
/// true → fixed column width; false → 0 (hidden).
/// </summary>
public class BoolToColumnWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 65.0 : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private static readonly TimeSpan TypeAheadResetDelay = TimeSpan.FromSeconds(1);

    private readonly MainViewModel _vm;
    private readonly ISmtpService _smtp;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly IImapService _imap;
    private readonly IOAuthService _oauth;
    private readonly ICommandRegistry _registry;
    private bool _webViewReady;
    private string _typeAheadBuffer = string.Empty;
    private DateTime _typeAheadLastInputUtc = DateTime.MinValue;
    private object? _typeAheadScope;

    private readonly IContactService _contactService;

    public MainWindow(
        MainViewModel vm,
        ISmtpService smtp,
        IAccountService accountService,
        ICredentialService credentials,
        IImapService imap,
        IOAuthService oauth,
        ICommandRegistry registry,
        IContactService contactService)
    {
        _vm = vm;
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        _oauth = oauth;
        _registry = registry;
        _contactService = contactService;

        InitializeComponent();
        DataContext = vm;

        vm.ComposeRequested += OpenComposeWindow;
        vm.ManageAccountsRequested += OpenAccountManager;
        vm.OpenAccountSettingsRequested += OpenAccountManagerForAccount;
        vm.MessageListFocusRequested += ReturnFocusToMessageList;
        vm.AnnouncementRequested += (_, text) => AccessibilityHelper.Announce(this, text, interrupt: true);

        // Re-focus the active message panel whenever the message collections are replaced
        // (happens after Refresh, Load More, folder changes, and view-mode switches).
        //
        // Conversations/SenderGroups use FocusTreeSelectedOrFirst rather than the old
        // FocusActiveMessagePanel so they enqueue exactly one dispatcher item (Y1').
        // LandOnX registers its own item (Y2) after this handler fires, so Y2 always
        // runs last and wins — the correct post-delete focus position is preserved.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Messages) && IsActive)
            {
                LogService.Debug($"[FOCUS] PropChanged:Messages viewMode={_vm.ViewMode} {FocusInfo()}");
                if (vm.IsConversationsView)
                {
                    LogService.Debug("[FOCUS]   → LandOnConversationAfterRebuild(0)");
                    LandOnConversationAfterRebuild(0);
                }
                else if (vm.IsFromView)
                {
                    LogService.Debug("[FOCUS]   → LandOnSenderGroupAfterRebuild(0)");
                    LandOnSenderGroupAfterRebuild(0);
                }
                else if (vm.IsToView)
                {
                    LogService.Debug("[FOCUS]   → LandOnToGroupAfterRebuild(0)");
                    LandOnToGroupAfterRebuild(0);
                }
                else
                {
                    LogService.Debug("[FOCUS]   → FocusActiveMessagePanel (dispatched)");
                    Dispatcher.InvokeAsync(FocusActiveMessagePanel, DispatcherPriority.Input);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.Conversations) && IsActive && vm.IsConversationsView)
            {
                // Capture selected index now (before DataBind replaces items) so we can
                // restore position after a background sync that rebuilds Conversations.
                var oldIdx = ConversationTree.Items.IndexOf(ConversationTree.SelectedItem);
                LogService.Debug($"[FOCUS] PropChanged:Conversations convCount={vm.Conversations.Count} oldIdx={oldIdx} {FocusInfo()}");
                FocusTreeSelectedOrFirst(ConversationTree, oldIdx);
            }
            else if (e.PropertyName == nameof(MainViewModel.SenderGroups) && IsActive && vm.IsFromView)
            {
                // Capture selected index now (before DataBind replaces items) so we can
                // restore position after a background sync that rebuilds SenderGroups.
                var oldIdx = SenderGroupTree.Items.IndexOf(SenderGroupTree.SelectedItem);
                LogService.Debug($"[FOCUS] PropChanged:SenderGroups grpCount={vm.SenderGroups.Count} oldIdx={oldIdx} {FocusInfo()}");
                FocusTreeSelectedOrFirst(SenderGroupTree, oldIdx);
            }
            else if (e.PropertyName == nameof(MainViewModel.ToGroups) && IsActive && vm.IsToView)
            {
                // Capture selected index now (before DataBind replaces items) so we can
                // restore position after a background sync that rebuilds ToGroups.
                var oldIdx = ToGroupTree.Items.IndexOf(ToGroupTree.SelectedItem);
                LogService.Debug($"[FOCUS] PropChanged:ToGroups grpCount={vm.ToGroups.Count} oldIdx={oldIdx} {FocusInfo()}");
                FocusTreeSelectedOrFirst(ToGroupTree, oldIdx);
            }
            else if (e.PropertyName == nameof(MainViewModel.ViewMode))
            {
                LogService.Debug($"[FOCUS] ViewMode → {vm.ViewMode} {FocusInfo()}");
                if (ShouldRestoreMessagePanelFocusAfterViewModeChange())
                {
                    LogService.Debug("[FOCUS]   → FocusActiveMessagePanel (view mode change)");
                    Dispatcher.InvokeAsync(FocusActiveMessagePanel, DispatcherPriority.Input);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.SelectedFolder) ||
                     e.PropertyName == nameof(MainViewModel.FolderTree))
            {
                Dispatcher.InvokeAsync(() => SyncFolderTreeSelection(false), DispatcherPriority.Input);
            }

            if (e.PropertyName == nameof(MainViewModel.StatusText) && !string.IsNullOrEmpty(vm.StatusText))
                AccessibilityHelper.Announce(this, vm.StatusText);
        };

        PreviewKeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;

        // Debug: trace every SelectionChanged on the message list so we can see
        // when and why the selection is reset (only fires when /debug is active).
        MessageList.SelectionChanged += (_, args) =>
        {
            LogService.Debug(
                $"MessageList.SelectionChanged — added:{args.AddedItems.Count} removed:{args.RemovedItems.Count} " +
                $"total selected:{MessageList.SelectedItems.Count} " +
                $"focusedEl:{Keyboard.FocusedElement?.GetType().Name ?? "null"}");
        };

        // Announce the newly selected message to the screen reader whenever the selection
        // changes via keyboard (arrow keys, etc.).  WPF ListView does not reliably fire
        // UIA focus events on arrow-key navigation, so RaiseNotificationEvent is required.
        MessageList.SelectionChanged += (_, _) =>
        {
            if (MessageList.IsKeyboardFocusWithin && MessageList.SelectedItem is MailMessageSummary msg)
                AccessibilityHelper.Announce(this, MessageSummaryAnnouncement(msg), interrupt: true);
        };

        // ── Focus-enter / focus-leave traces for every message panel ────────────
        MessageList.GotKeyboardFocus       += (_, e) => LogService.Debug($"[FOCUS] GotFocus  MsgList   from={e.OldFocus?.GetType().Name ?? "null"}");
        MessageList.LostKeyboardFocus      += (_, e) => LogService.Debug($"[FOCUS] LostFocus MsgList   to={e.NewFocus?.GetType().Name ?? "null"}");
        ConversationTree.GotKeyboardFocus  += (_, e) => LogService.Debug($"[FOCUS] GotFocus  ConvTree  from={e.OldFocus?.GetType().Name ?? "null"} selectedItem={ConversationTree.SelectedItem?.GetType().Name ?? "null"}");
        ConversationTree.LostKeyboardFocus += (_, e) => LogService.Debug($"[FOCUS] LostFocus ConvTree  to={e.NewFocus?.GetType().Name ?? "null"}");
        SenderGroupTree.GotKeyboardFocus   += (_, e) => LogService.Debug($"[FOCUS] GotFocus  SenderTree from={e.OldFocus?.GetType().Name ?? "null"} selectedItem={SenderGroupTree.SelectedItem?.GetType().Name ?? "null"}");
        SenderGroupTree.LostKeyboardFocus  += (_, e) => LogService.Debug($"[FOCUS] LostFocus SenderTree to={e.NewFocus?.GetType().Name ?? "null"}");
    }

    // On startup: initialise WebView2, connect to first account, open INBOX, focus message list
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Register commands that require UI access (must run after InitializeComponent).
        _registry.Register(new CommandDefinition(
            id: "view.folderPicker", category: "View", title: "Go to Folder…",
            execute: OpenFolderPicker,
            defaultKey: Key.Y, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "view.openViewMenu", category: "View", title: "Open View Menu",
            execute: OpenViewMenu,
            defaultKey: Key.V, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "view.focusStatusBar", category: "View", title: "Focus Status Bar",
            execute: FocusStatusBar,
            defaultKey: Key.D9, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "contacts.grabAddresses", category: "Contacts", title: "Grab Addresses from Message",
            execute: GrabAddressesFromMessage,
            defaultKey: Key.G, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => _vm.IsMessageOpen));

        _registry.Register(new CommandDefinition(
            id: "contacts.openAddressBook", category: "Contacts", title: "Address Book",
            execute: OpenAddressBook,
            defaultKey: Key.B, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

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

            // Inject Escape, F6, and Shift+Tab relay into every page at the host level — runs before any CSP.
            await MessageBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){"
                +"if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}"
                +"else if(e.key==='F6'){window.chrome.webview.postMessage(e.shiftKey?'shift-f6':'f6');e.preventDefault();}"
                +"else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}"
                +"});");

            // JavaScript in every page posts a message when Escape, F6, or Shift+Tab is pressed,
            // which we relay back to WPF to move focus appropriately.
            MessageBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == "escape")
                    Dispatcher.InvokeAsync(() =>
                    {
                        _vm.IsMessageOpen = false;
                        _vm.MessageDetail = null;
                        ReturnFocusToMessageList();
                    }, DispatcherPriority.Input);
                else if (msg == "f6")
                    Dispatcher.InvokeAsync(() => _ = CycleFocusAsync(true), DispatcherPriority.Input);
                else if (msg == "shift-f6")
                    Dispatcher.InvokeAsync(() => _ = CycleFocusAsync(false), DispatcherPriority.Input);
                else if (msg == "shift-tab")
                    Dispatcher.InvokeAsync(FocusLastHeaderField, DispatcherPriority.Input);
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
        FocusActiveMessagePanel();

        // Connect accounts and sync new mail in the background; messages trickle in via FolderSynced.
        _ = _vm.StartBackgroundSyncAsync();
    }

    // Global key handler (PreviewKeyDown so it fires before any child can swallow the event).
    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = e.KeyboardDevice.Modifiers;
        var key       = e.Key;

        // ── Navigation shortcuts (hardcoded, not in the command registry) ──────────
        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.D0: ToolbarFirstButton.Focus(); e.Handled = true; return;
                case Key.D1: AccountList.Focus();        e.Handled = true; return;
                case Key.D2: FolderList.Focus();         e.Handled = true; return;
                case Key.D3:
                    if (_vm.IsConversationsView)
                        ConversationTree.Focus();
                    else if (_vm.IsFromView)
                        SenderGroupTree.Focus();
                    else if (_vm.IsToView)
                        ToGroupTree.Focus();
                    else
                        MessageList.Focus();
                    e.Handled = true;
                    return;
            }
        }
        else if (modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.F6:
                    e.Handled = true;
                    await CycleFocusAsync(true);
                    return;
                case Key.Escape when _vm.IsMessageOpen:
                    _vm.IsMessageOpen = false;
                    _vm.MessageDetail = null;
                    ReturnFocusToMessageList();
                    e.Handled = true;
                    return;
            }
        }
        else if (modifiers == ModifierKeys.Shift && key == Key.F6)
        {
            e.Handled = true;
            await CycleFocusAsync(false);
            return;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.P)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        // ── Registry-based action commands ───────────────────────────────────────
        var cmd = _registry.FindByGesture(key, modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            cmd.Execute();
            e.Handled = true;
        }
    }

    private void OpenCommandPalette()
    {
        // Remember what had focus so we can restore it if the user dismisses without running a command.
        var previousFocus = Keyboard.FocusedElement as IInputElement;

        var palette = new CommandPaletteWindow(_registry) { Owner = this };
        palette.ShowDialog();

        // Restore focus. Fall back to the message list if nothing was previously focused.
        (previousFocus ?? MessageList).Focus();
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e) => OpenViewMenu();

    private void SyncRangeButton_Click(object sender, RoutedEventArgs e)
    {
        var cm = SyncRangeButton.ContextMenu;
        if (cm == null) return;
        cm.PlacementTarget = SyncRangeButton;
        cm.Placement = PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    private void OpenViewMenu()
    {
        var cm = ViewModeButton.ContextMenu;
        if (cm == null) return;
        cm.PlacementTarget = ViewModeButton;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    private async void OpenFolderPicker()
    {
        if (_vm.CachedFolders.Count == 0) return;
        var acctMailFolders = _vm.Accounts
            .Where(a => _vm.CachedFolders.ContainsKey(a.Id))
            .ToDictionary(a => a.Id, a => MainViewModel.CreateAccountMailVirtualFolder(a));
        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders,
            [MainViewModel.AllMailFolder,    MainViewModel.AllInboxesFolder,
             MainViewModel.AllDraftsFolder,  MainViewModel.AllSentFolder,
             MainViewModel.AllTrashFolder],
            initialFolder: _vm.SelectedFolder,
            accountMailFolders: acctMailFolders) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedFolder is MailFolderModel folder)
        {
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
        LogService.Debug($"List_GotKeyboardFocus sender={lb.Name} newFocus={e.NewFocus?.GetType().Name} oldFocus={e.OldFocus?.GetType().Name}");

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
        if (TryGetTypeAheadKeyText(e, out var searchText) &&
            TryHandleFolderTreeTypeAhead(FolderList, FolderList.Items.OfType<FolderTreeNode>(), searchText))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter &&
            FolderList.SelectedItem is FolderTreeNode node &&
            node.Folder != null)
        {
            e.Handled = true;
            await _vm.SelectFolderCommand.ExecuteAsync(node.Folder);
            FocusActiveMessagePanel();
        }
    }

    private void FolderList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OldFocus is DependencyObject oldFocus && IsDescendantOf(FolderList, oldFocus))
            return;

        if (SyncFolderTreeSelection(true))
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (FolderList.Items.Count == 0)
            {
                FolderList.Focus();
                return;
            }

            FolderList.UpdateLayout();
            if (FolderList.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                first.Focus();
            else
                FolderList.Focus();
        }, DispatcherPriority.Input);
    }

    // First-letter navigation for the folder TreeView (TreeView has no built-in TextSearch).
    private void FolderList_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (TryHandleFolderTreeTypeAhead(FolderList, FolderList.Items.OfType<FolderTreeNode>(), e.Text))
            e.Handled = true;
    }

    private void MessageList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (TryHandleMessageListTypeAhead(e.Text))
            e.Handled = true;
    }

    // Recursively yields visible (expanded) FolderTreeNode items in depth-first order.
    private static System.Collections.Generic.IEnumerable<FolderTreeNode> GetVisibleTreeNodes(
        System.Collections.Generic.IEnumerable<FolderTreeNode> nodes)
        => TreeViewFocusHelper.GetVisibleTreeNodes(nodes);

    // Walks the TreeView container hierarchy to find and select the target node.
    private static bool SelectTreeViewNode(System.Windows.Controls.ItemsControl parent, FolderTreeNode target, bool focusNode = true)
        => TreeViewFocusHelper.SelectTreeViewNode(parent, target, focusNode);

    private bool SyncFolderTreeSelection(bool focusNode)
    {
        if (_vm.SelectedFolder == null)
            return false;

        var roots = FolderList.Items.OfType<FolderTreeNode>().ToList();
        if (roots.Count == 0)
            return false;

        if (!FindAndExpandFolderPath(roots, _vm.SelectedFolder))
            return false;

        FolderList.UpdateLayout();
        var target = FindFolderTreeNode(roots, _vm.SelectedFolder);
        return target != null && SelectTreeViewNode(FolderList, target, focusNode);
    }

    private static FolderTreeNode? FindFolderTreeNode(System.Collections.Generic.IEnumerable<FolderTreeNode> nodes, MailFolderModel target)
        => TreeViewFocusHelper.FindFolderTreeNode(nodes, target);

    private static bool FindAndExpandFolderPath(System.Collections.Generic.IEnumerable<FolderTreeNode> nodes, MailFolderModel target)
        => TreeViewFocusHelper.FindAndExpandFolderPath(nodes, target);

    private static bool FolderMatches(MailFolderModel left, MailFolderModel right) =>
        left.AccountId == right.AccountId &&
        string.Equals(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendantOf(DependencyObject ancestor, DependencyObject descendant)
    {
        DependencyObject? current = descendant;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;

            current = current switch
            {
                System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D => System.Windows.Media.VisualTreeHelper.GetParent(current),
                FrameworkContentElement fce => fce.Parent,
                _ => null,
            };
        }

        return false;
    }


    private bool TryHandleFolderTreeTypeAhead(TreeView tree, System.Collections.Generic.IEnumerable<FolderTreeNode> roots, string? text)
    {
        if (string.IsNullOrEmpty(text) || char.IsControl(text[0]))
            return false;

        var allNodes = GetVisibleTreeNodes(roots).ToList();
        if (allNodes.Count == 0)
            return false;

        var current = tree.SelectedItem as FolderTreeNode;
        var startIdx = current != null ? allNodes.IndexOf(current) : -1;

        for (int i = 1; i <= allNodes.Count; i++)
        {
            var candidate = allNodes[(startIdx + i) % allNodes.Count];
            if (!candidate.Label.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                continue;

            return SelectTreeViewNode(tree, candidate);
        }

        return false;
    }

    private bool TryHandleMessageListTypeAhead(string? text)
    {
        if (!TryBuildTypeAheadPrefix(text, MessageList, out var prefix))
            return false;

        var items = MessageList.Items.OfType<MailMessageSummary>().ToList();
        if (items.Count == 0)
            return false;

        var current = MessageList.SelectedItem as MailMessageSummary;
        var startIdx = current != null ? items.IndexOf(current) : -1;

        for (int i = 1; i <= items.Count; i++)
        {
            var candidate = items[(startIdx + i) % items.Count];
            if (!GetTypeAheadText(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            MessageList.SelectedItem = candidate;
            MessageList.ScrollIntoView(candidate);
            var targetIndex = MessageList.Items.IndexOf(candidate);
            Dispatcher.InvokeAsync(() => FocusItemAt(targetIndex), DispatcherPriority.Input);
            return true;
        }

        return false;
    }

    private bool TryBuildTypeAheadPrefix(string? text, object scope, out string prefix)
    {
        prefix = string.Empty;

        if (string.IsNullOrWhiteSpace(text) || Keyboard.Modifiers != ModifierKeys.None)
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
            return false;

        var now = DateTime.UtcNow;
        if (!ReferenceEquals(_typeAheadScope, scope) || now - _typeAheadLastInputUtc > TypeAheadResetDelay)
            _typeAheadBuffer = trimmed;
        else
            _typeAheadBuffer += trimmed;

        _typeAheadScope = scope;
        _typeAheadLastInputUtc = now;
        prefix = _typeAheadBuffer;
        return true;
    }

    private static bool TryGetTypeAheadKeyText(KeyEventArgs e, out string text)
        => TreeViewFocusHelper.TryGetTypeAheadKeyText(e, out text);

    private static string GetTypeAheadText(object? item) => item switch
    {
        MailMessageSummary msg => string.IsNullOrWhiteSpace(msg.From)
            ? msg.Subject ?? string.Empty
            : msg.From,
        ConversationGroup group => group.Subject ?? string.Empty,
        SenderGroup group => group.SenderName ?? string.Empty,
        FolderTreeNode node => node.Label ?? string.Empty,
        _ => string.Empty,
    };

    private static System.Collections.Generic.IEnumerable<object> GetVisibleConversationItems(System.Collections.Generic.IEnumerable<ConversationGroup> groups)
    {
        foreach (var group in groups)
        {
            yield return group;
            if (!group.IsExpanded) continue;
            foreach (var msg in group.Messages)
                yield return msg;
        }
    }

    private static System.Collections.Generic.IEnumerable<object> GetVisibleSenderItems(System.Collections.Generic.IEnumerable<SenderGroup> groups)
    {
        foreach (var group in groups)
        {
            yield return group;
            if (!group.IsExpanded) continue;
            foreach (var msg in group.Messages)
                yield return msg;
        }
    }

    private void FocusTreeItem(TreeView tree, object item)
    {
        tree.Dispatcher.InvokeAsync(() =>
        {
            tree.UpdateLayout();

            if (FindTreeViewItem(tree, item) is TreeViewItem tvi)
            {
                tvi.IsSelected = true;
                tvi.BringIntoView();
                tvi.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, object target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
                continue;

            if (ReferenceEquals(item, target))
                return container;

            var child = FindTreeViewItem(container, target);
            if (child != null)
                return child;
        }

        return null;
    }

    private void HandleMessageTreeTypeAhead(
        TreeView tree,
        object? currentSelection,
        System.Collections.Generic.List<object> visibleItems,
        string prefix,
        TextCompositionEventArgs e)
    {
        if (visibleItems.Count == 0) return;

        var startIdx = currentSelection != null ? visibleItems.IndexOf(currentSelection) : -1;
        for (int i = 1; i <= visibleItems.Count; i++)
        {
            var candidate = visibleItems[(startIdx + i) % visibleItems.Count];
            if (!GetTypeAheadText(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            FocusTreeItem(tree, candidate);
            e.Handled = true;
            return;
        }
    }

    private bool TryHandleMessageTreeTypeAhead(
        TreeView tree,
        object? currentSelection,
        System.Collections.Generic.List<object> visibleItems,
        string? text)
    {
        if (!TryBuildTypeAheadPrefix(text, tree, out var prefix) || visibleItems.Count == 0)
            return false;

        var startIdx = currentSelection != null ? visibleItems.IndexOf(currentSelection) : -1;
        for (int i = 1; i <= visibleItems.Count; i++)
        {
            var candidate = visibleItems[(startIdx + i) % visibleItems.Count];
            if (!GetTypeAheadText(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            FocusTreeItem(tree, candidate);
            return true;
        }

        return false;
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
            if (_vm.IsSelectedFolderDrafts)
            {
                await _vm.OpenDraftCommand.ExecuteAsync(null);
            }
            else
            {
                await _vm.SelectMessageCommand.ExecuteAsync(summary);
                if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                    await ShowMessageBodyAsync(_vm.MessageDetail);
            }
        }
    }

    // Enter on a message: load body; Delete: delete all selected messages;
    // Shift+Up/Down: extend consecutive selection without opening the reading pane.
    private async void MessageList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogService.Debug($"MessageList_PreviewKeyDown key={e.Key} mod={Keyboard.Modifiers} focused={Keyboard.FocusedElement?.GetType().Name}");
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            SyncFolderTreeSelection(true);
            return;
        }

        if (TryGetTypeAheadKeyText(e, out var searchText) && TryHandleMessageListTypeAhead(searchText))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && MessageList.SelectedItem is MailMessageSummary summary)
        {
            e.Handled = true;
            if (_vm.IsSelectedFolderDrafts)
            {
                await _vm.OpenDraftCommand.ExecuteAsync(null);
            }
            else
            {
                await _vm.SelectMessageCommand.ExecuteAsync(summary);
                if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                    await ShowMessageBodyAsync(_vm.MessageDetail);
            }
        }
        else if (e.Key == Key.Delete && MessageList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            var toDelete = MessageList.SelectedItems
                .OfType<MailMessageSummary>()
                .ToList();
            LogService.Debug($"Delete key: SelectedItems.Count={MessageList.SelectedItems.Count} toDelete={toDelete.Count}");
            await _vm.DeleteMessagesAsync(toDelete);
            FocusMessageListFirstItem();
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            ExtendMessageSelection(e.Key == Key.Down ? 1 : -1);
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
                 && Keyboard.Modifiers == ModifierKeys.None
                 && MessageList.Items.Count == 0)
        {
            // Prevent arrow keys from escaping an empty ListView to the toolbar.
            e.Handled = true;
        }
    }

    private void ConversationTree_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var visibleItems = GetVisibleConversationItems(_vm.Conversations).ToList();
        if (TryHandleMessageTreeTypeAhead(ConversationTree, ConversationTree.SelectedItem, visibleItems, e.Text))
            e.Handled = true;
    }

    // Extends (or shrinks) the MessageList selection by one step in the given direction.
    // Shift+Down adds the next item; Shift+Up adds the previous item (or de-selects
    // the focused item if moving back toward the anchor).
    private void ExtendMessageSelection(int direction)
    {
        // Prefer the focused container's index so repeated Shift+Arrow calls chain correctly.
        int focusedIndex = -1;
        if (Keyboard.FocusedElement is ListViewItem focused)
            focusedIndex = MessageList.ItemContainerGenerator.IndexFromContainer(focused);
        if (focusedIndex < 0)
            focusedIndex = MessageList.SelectedIndex;
        if (focusedIndex < 0) return;

        int targetIndex = Math.Max(0, Math.Min(MessageList.Items.Count - 1, focusedIndex + direction));
        if (targetIndex == focusedIndex) return;

        var targetItem = MessageList.Items[targetIndex] as MailMessageSummary;
        if (targetItem == null) return;

        LogService.Debug(
            $"ExtendMessageSelection dir={direction} focusedIdx={focusedIndex} targetIdx={targetIndex} " +
            $"selectedBefore={MessageList.SelectedItems.Count} targetAlreadySelected={MessageList.SelectedItems.Contains(targetItem)}");

        if (MessageList.SelectedItems.Contains(targetItem))
        {
            // Moving back into an already-selected item: de-select the item we're leaving.
            var leavingItem = MessageList.Items[focusedIndex] as MailMessageSummary;
            if (leavingItem != null && MessageList.SelectedItems.Count > 1)
                MessageList.SelectedItems.Remove(leavingItem);
        }
        else
        {
            MessageList.SelectedItems.Add(targetItem);
        }

        LogService.Debug($"ExtendMessageSelection after add/remove: selectedNow={MessageList.SelectedItems.Count}");

        MessageList.ScrollIntoView(targetItem);
        Dispatcher.InvokeAsync(() =>
        {
            LogService.Debug($"ExtendMessageSelection focus dispatch: selectedBeforeFocus={MessageList.SelectedItems.Count}");
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ListViewItem container)
                container.Focus();
            LogService.Debug($"ExtendMessageSelection after Focus(): selectedAfterFocus={MessageList.SelectedItems.Count}");
        }, DispatcherPriority.Input);
    }

    // Render the message body in the browser and move focus into it
    private async Task ShowMessageBodyAsync(MailMessageDetail detail)
    {
        if (!_webViewReady) return;

        var encodedSubject = WebUtility.HtmlEncode(detail.Subject ?? string.Empty);
        var titleTag = $"<title>{encodedSubject}</title>";

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

            // Replace any existing <title> so screen readers announce our subject, not the sender's.
            body = System.Text.RegularExpressions.Regex.Replace(
                body, @"<title[^>]*>.*?</title>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var headIdx = body.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            html = headIdx >= 0
                ? body.Insert(headIdx + 6, titleTag + cspTag)
                : titleTag + cspTag + body;
        }
        else
        {
            // Plain-text fallback: HTML-encode so tags/special chars are safe
            var encoded = WebUtility.HtmlEncode(detail.PlainTextBody ?? string.Empty);
            html =
                "<!DOCTYPE html>\n" +
                "<html lang=\"en\">\n" +
                $"<head><meta charset=\"utf-8\">{titleTag}<style>\n" +
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

    // When the WebView2 host receives WPF keyboard focus (e.g. Tab from a header field),
    // push focus into the HTML document body so the user can read/navigate content.
    private async void MessageBody_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_webViewReady)
            await MessageBody.CoreWebView2.ExecuteScriptAsync("document.body.focus()");
    }

    // Focuses the last visible field in the reading-pane header so Shift+Tab from the
    // WebView2 body lands in the right place (attachments list if present, else Date).
    private void FocusLastHeaderField()
    {
        if (ReadingPaneAttachmentList.Visibility == Visibility.Visible)
            ReadingPaneAttachmentList.Focus();
        else
            DateField.Focus();
    }

    // Return keyboard focus to the active message panel after reading a message.
    private void ReturnFocusToMessageList()
    {
        LogService.Debug($"[FOCUS] ReturnFocusToMessageList viewMode={_vm.ViewMode} {FocusInfo()}");
        if (_vm.IsConversationsView)
        {
            FocusTreeSelectedOrFirst(ConversationTree);
            return;
        }
        if (_vm.IsFromView)
        {
            FocusTreeSelectedOrFirst(SenderGroupTree);
            return;
        }
        if (MessageList.Items.Count == 0)
        {
            LogService.Debug("[FOCUS]   → MessageList.Focus() (empty list)");
            // Dispatch at Input priority so we queue AFTER any WPF-internal focus
            // restoration that was enqueued when items were removed from the list.
            Dispatcher.InvokeAsync(() => MessageList.Focus(), DispatcherPriority.Input);
            return;
        }
        var idx = MessageList.SelectedIndex >= 0 ? MessageList.SelectedIndex : 0;
        LogService.Debug($"[FOCUS]   → FocusItemAt({idx}) count={MessageList.Items.Count}");
        MessageList.ScrollIntoView(MessageList.Items[idx]);
        Dispatcher.InvokeAsync(() => FocusItemAt(idx), DispatcherPriority.Input);
    }

    // Routes focus to whichever message panel is currently visible.
    private void FocusActiveMessagePanel()
    {
        LogService.Debug($"[FOCUS] FocusActiveMessagePanel viewMode={_vm.ViewMode} {FocusInfo()}");
        if (_vm.IsConversationsView)
            FocusConversationTreeFirstItem();
        else if (_vm.IsFromView)
            FocusSenderGroupTreeFirstItem();
        else if (_vm.IsToView)
            FocusToGroupTreeFirstItem();
        else
            FocusMessageListFirstItem();
    }

    private bool ShouldRestoreMessagePanelFocusAfterViewModeChange() =>
        MessageList.IsKeyboardFocusWithin ||
        ConversationTree.IsKeyboardFocusWithin ||
        SenderGroupTree.IsKeyboardFocusWithin ||
        ToGroupTree.IsKeyboardFocusWithin ||
        MessageBody.IsKeyboardFocusWithin ||
        ViewModeButton.IsKeyboardFocusWithin;

    // Moves keyboard focus to the status bar's read-only TextBox.
    // StatusTextBox is ControlType.Edit + ValuePattern, so screen readers
    // announce its value natively when it receives focus — no Announce hack needed.
    // The Announce call is kept as a belt-and-suspenders fallback for screen readers
    // that have slow UIA update cycles.
    private void FocusStatusBar()
    {
        StatusTextBox.Focus();
        AccessibilityHelper.Announce(this, $"Status bar: {_vm.StatusText}");
    }

    // ── F6 pane-cycling helpers ──────────────────────────────────────────────

    // Returns the index of the pane that currently holds keyboard focus:
    //   0 = Toolbar, 1 = Account list, 2 = Folder list,
    //   3 = Message list / Conversation tree, 4 = Reading pane (WebView2),
    //   5 = Status bar.
    // Falls back to 0 if no match.
    private int GetFocusedPaneIndex()
    {
        if (MainToolbar.IsKeyboardFocusWithin)  return 0;
        if (AccountList.IsKeyboardFocusWithin)  return 1;
        if (FolderList.IsKeyboardFocusWithin)   return 2;
        if (MessageList.IsKeyboardFocusWithin || ConversationTree.IsKeyboardFocusWithin || SenderGroupTree.IsKeyboardFocusWithin || ToGroupTree.IsKeyboardFocusWithin) return 3;
        if (MessageBody.IsKeyboardFocusWithin)  return 4;
        if (MainStatusBar.IsKeyboardFocusWithin) return 5;
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
                    FocusStatusBar();
                }
                break;
            case 5: FocusStatusBar(); break;
        }
    }

    // Cycles keyboard focus forward (F6) or backward (Shift+F6) through the pane ring.
    // The reading pane (index 4) is included only when a message is open.
    // StatusBar (index 5) is always the last stop before wrapping back to Toolbar.
    private async Task CycleFocusAsync(bool forward)
    {
        // Build the ordered list of active pane indices.
        var panes = new System.Collections.Generic.List<int> { 0, 1, 2, 3 };
        if (_vm.IsMessageOpen && _webViewReady) panes.Add(4);
        panes.Add(5); // StatusBar always included

        int current    = GetFocusedPaneIndex();
        int currentPos = panes.IndexOf(current);
        if (currentPos < 0) currentPos = 0;
        int nextPos = forward
            ? (currentPos + 1) % panes.Count
            : (currentPos - 1 + panes.Count) % panes.Count;
        await FocusPaneAtAsync(panes[nextPos]);
    }

    // Focuses the first (or currently selected) TreeViewItem in the conversation tree.
    // WPF TreeView manages its own focus state natively; we just call Focus() on the
    // control and let it route to the last-focused item (or the first if none).
    private void FocusConversationTreeFirstItem()
    {
        if (ConversationTree.Items.Count == 0) { ConversationTree.Focus(); return; }
        Dispatcher.InvokeAsync(ConversationTree.Focus, DispatcherPriority.Input);
    }

    // Focuses the selected TreeViewItem; falls back to the first item if nothing is selected.
    // fallbackIdx: index to use when tree.SelectedItem is null at dispatch time
    // (e.g. after DataBind cleared the selection during a background rebuild).
    // Pass -1 (default for direct calls) to use item 0; pass the old selected index
    // from PropChanged handlers to preserve position across background sync rebuilds.
    private void FocusTreeSelectedOrFirst(TreeView tree, int fallbackIdx = -1)
    {
        var name = tree == ConversationTree ? "ConvTree" : "SenderTree";
        // Defer ALL logic to Input priority so WPF's DataBind pass (which runs at
        // higher priority) has already updated ItemsSource and recycled old containers
        // before we try to locate a TreeViewItem.  Capturing tvi synchronously here
        // and focusing it in the dispatch results in a stale/detached container when
        // the items source just changed, causing Focus() to silently fail.
        Dispatcher.InvokeAsync(() =>
        {
            if (tree.Items.Count == 0)
            {
                LogService.Debug($"[FOCUS] FocusTreeSelectedOrFirst({name}) empty — tree.Focus()");
                tree.Focus();
                return;
            }
            // Prefer the live selected item; fall back to the captured index position
            // (best match after a rebuild), then absolute first.
            object? item;
            string  source;
            if (tree.SelectedItem != null)
            {
                item   = tree.SelectedItem;
                source = "selected";
            }
            else if (fallbackIdx >= 0 && fallbackIdx < tree.Items.Count)
            {
                item   = tree.Items[fallbackIdx];
                source = $"fallback[{fallbackIdx}]";
            }
            else
            {
                item   = tree.Items[0];
                source = "fallback[0]";
            }
            if (tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                LogService.Debug($"[FOCUS] FocusTreeSelectedOrFirst({name}) {source} containerFound=true — tvi.Focus()");
                tvi.IsSelected = true;
                tvi.Focus();
                return;
            }
            LogService.Debug($"[FOCUS] FocusTreeSelectedOrFirst({name}) {source} containerFound=false — tree.Focus()");
            tree.Focus();
        }, DispatcherPriority.Input);
    }

    // Returns a compact string describing where keyboard focus currently is.
    private string FocusInfo()
    {
        if (ConversationTree.IsKeyboardFocusWithin)  return $"focus=ConvTree/{Keyboard.FocusedElement?.GetType().Name}";
        if (SenderGroupTree.IsKeyboardFocusWithin)   return $"focus=SenderTree/{Keyboard.FocusedElement?.GetType().Name}";
        if (ToGroupTree.IsKeyboardFocusWithin)        return $"focus=ToTree/{Keyboard.FocusedElement?.GetType().Name}";
        if (MessageList.IsKeyboardFocusWithin)        return $"focus=MsgList/{Keyboard.FocusedElement?.GetType().Name}";
        if (MessageBody.IsKeyboardFocusWithin)        return "focus=MsgBody";
        if (FolderList.IsKeyboardFocusWithin)         return "focus=FolderList";
        if (AccountList.IsKeyboardFocusWithin)        return "focus=AccountList";
        if (MainToolbar.IsKeyboardFocusWithin)        return "focus=Toolbar";
        return $"focus=other/{Keyboard.FocusedElement?.GetType().Name ?? "null"}";
    }

    // ── Conversation tree event handlers ────────────────────────────────────────

    // When the ConversationTree gets keyboard focus, ensure an item is highlighted.
    private void ConversationTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        LogService.Debug($"[FOCUS] ConvTree GotKeyboardFocus selectedItem={ConversationTree.SelectedItem?.GetType().Name ?? "null"} count={ConversationTree.Items.Count} from={e.OldFocus?.GetType().Name ?? "null"}");
        // If no item is selected, select and focus the first root item.
        if (ConversationTree.SelectedItem == null && ConversationTree.Items.Count > 0)
        {
            if (ConversationTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                LogService.Debug("[FOCUS]   ConvTree GotKeyboardFocus: no selection — selecting first item");
                first.IsSelected = true;
                first.Focus();
            }
        }
    }

    // Sync the ViewModel's SelectedMessage when the user navigates to a message leaf node,
    // so Reply/Forward/Delete commands always operate on the right message.
    // Also announces the new item to the screen reader — WPF ListView/TreeView do not
    // reliably fire UIA focus events on arrow-key navigation, so we use RaiseNotificationEvent.
    private void ConversationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        LogService.Debug($"[FOCUS] ConvTree SelectedItemChanged old={e.OldValue?.GetType().Name ?? "null"} new={e.NewValue?.GetType().Name ?? "null"} {FocusInfo()}");
        if (e.NewValue is MailMessageSummary msg)
            _vm.SelectedMessage = msg;

        if (ConversationTree.IsKeyboardFocusWithin)
        {
            switch (e.NewValue)
            {
                case MailMessageSummary m:
                    AccessibilityHelper.Announce(this, MessageSummaryAnnouncement(m), interrupt: true);
                    break;
                case ConversationGroup grp:
                    AccessibilityHelper.Announce(this, grp.AutomationName, interrupt: true);
                    break;
            }
        }
    }

    // Keyboard actions in the conversation tree.
    private async void ConversationTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogService.Debug($"[FOCUS] ConvTree KeyDown key={e.Key} mod={Keyboard.Modifiers} {FocusInfo()} items={ConversationTree.Items.Count} selected={ConversationTree.SelectedItem?.GetType().Name ?? "null"}");
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            SyncFolderTreeSelection(true);
            return;
        }

        if (TryGetTypeAheadKeyText(e, out var searchText) &&
            TryHandleMessageTreeTypeAhead(
                ConversationTree,
                ConversationTree.SelectedItem,
                GetVisibleConversationItems(_vm.Conversations).ToList(),
                searchText))
        {
            e.Handled = true;
            return;
        }

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
                var parentGroup = _vm.Conversations.FirstOrDefault(g => g.Messages.Contains(toDelete));
                var targetIdx   = parentGroup != null ? _vm.Conversations.IndexOf(parentGroup) : 0;
                _vm.SelectedMessage = toDelete;
                LandOnConversationAfterRebuild(targetIdx);   // register before the rebuild fires
                await _vm.DeleteMessageCommand.ExecuteAsync(null);
            }
            else if (ConversationTree.SelectedItem is ConversationGroup group)
            {
                var targetIdx = _vm.Conversations.IndexOf(group);
                LandOnConversationAfterRebuild(targetIdx);   // register before the rebuild fires
                await _vm.DeleteMessagesAsync(group.Messages);
            }
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
                 && Keyboard.Modifiers == ModifierKeys.None
                 && ConversationTree.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    // Builds the screen-reader announcement string for a single message row.
    private static string MessageSummaryAnnouncement(MailMessageSummary msg) =>
        $"{msg.ReadStatusLabel}. {msg.From}. {msg.Subject}. {msg.Preview}. {msg.DateDisplay}.";

    // After an async conversation rebuild, selects and focuses the conversation
    // at the given index (clamped to the new list size).
    private void LandOnConversationAfterRebuild(int targetIdx)
    {
        LogService.Debug($"[FOCUS] LandOnConv: registered listener targetIdx={targetIdx} {FocusInfo()}");
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.Conversations)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            LogService.Debug($"[FOCUS] LandOnConv: listener fired count={_vm.Conversations.Count} targetIdx={targetIdx} {FocusInfo()}");
            Dispatcher.InvokeAsync(() =>
            {
                if (_vm.Conversations.Count == 0)
                {
                    LogService.Debug("[FOCUS] LandOnConv: dispatch skipped — Conversations empty");
                    return;
                }
                var idx = Math.Max(0, Math.Min(targetIdx, _vm.Conversations.Count - 1));
                var conv = _vm.Conversations[idx];
                if (ConversationTree.ItemContainerGenerator.ContainerFromItem(conv) is TreeViewItem tvi)
                {
                    LogService.Debug($"[FOCUS] LandOnConv: tvi.Focus() idx={idx} {FocusInfo()}");
                    tvi.IsSelected = true;
                    tvi.Focus();
                }
                else
                {
                    LogService.Debug($"[FOCUS] LandOnConv: container not realized idx={idx} — retry at Background");
                    // Container not yet realized — retry at a lower priority.
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ConversationTree.ItemContainerGenerator.ContainerFromItem(conv) is TreeViewItem tvi2)
                        {
                            LogService.Debug($"[FOCUS] LandOnConv: retry tvi.Focus() idx={idx}");
                            tvi2.IsSelected = true;
                            tvi2.Focus();
                        }
                        else
                        {
                            LogService.Debug($"[FOCUS] LandOnConv: retry also failed idx={idx} — giving up");
                        }
                    }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    // ── SenderGroup tree focus helpers ───────────────────────────────────────

    private void FocusSenderGroupTreeFirstItem()
    {
        if (SenderGroupTree.Items.Count == 0) { SenderGroupTree.Focus(); return; }
        Dispatcher.InvokeAsync(SenderGroupTree.Focus, DispatcherPriority.Input);
    }

    private void FocusToGroupTreeFirstItem()
    {
        if (ToGroupTree.Items.Count == 0) { ToGroupTree.Focus(); return; }
        Dispatcher.InvokeAsync(ToGroupTree.Focus, DispatcherPriority.Input);
    }

    // After an async sender-group rebuild, selects and focuses the sender group
    // at the given index (clamped to the new list size).
    private void LandOnSenderGroupAfterRebuild(int targetGroupIdx)
    {
        LogService.Debug($"[FOCUS] LandOnSender: registered listener targetIdx={targetGroupIdx} {FocusInfo()}");
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.SenderGroups)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            LogService.Debug($"[FOCUS] LandOnSender: listener fired count={_vm.SenderGroups.Count} targetIdx={targetGroupIdx} {FocusInfo()}");
            Dispatcher.InvokeAsync(() =>
            {
                if (_vm.SenderGroups.Count == 0)
                {
                    LogService.Debug("[FOCUS] LandOnSender: dispatch skipped — SenderGroups empty");
                    return;
                }
                var idx   = Math.Max(0, Math.Min(targetGroupIdx, _vm.SenderGroups.Count - 1));
                var group = _vm.SenderGroups[idx];
                if (SenderGroupTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi)
                {
                    LogService.Debug($"[FOCUS] LandOnSender: tvi.Focus() idx={idx} {FocusInfo()}");
                    tvi.IsSelected = true;
                    tvi.Focus();
                }
                else
                {
                    LogService.Debug($"[FOCUS] LandOnSender: container not realized idx={idx} — retry at Background");
                    // Container not yet realized — retry at a lower priority.
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (SenderGroupTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi2)
                        {
                            LogService.Debug($"[FOCUS] LandOnSender: retry tvi.Focus() idx={idx}");
                            tvi2.IsSelected = true;
                            tvi2.Focus();
                        }
                        else
                        {
                            LogService.Debug($"[FOCUS] LandOnSender: retry also failed idx={idx} — giving up");
                        }
                    }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    // After an async to-group rebuild, selects and focuses the recipient group
    // at the given index (clamped to the new list size).
    private void LandOnToGroupAfterRebuild(int targetGroupIdx)
    {
        LogService.Debug($"[FOCUS] LandOnToGroup: registered listener targetIdx={targetGroupIdx} {FocusInfo()}");
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.ToGroups)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            LogService.Debug($"[FOCUS] LandOnToGroup: listener fired count={_vm.ToGroups.Count} targetIdx={targetGroupIdx} {FocusInfo()}");
            Dispatcher.InvokeAsync(() =>
            {
                if (_vm.ToGroups.Count == 0)
                {
                    LogService.Debug("[FOCUS] LandOnToGroup: dispatch skipped — ToGroups empty");
                    return;
                }
                var idx   = Math.Max(0, Math.Min(targetGroupIdx, _vm.ToGroups.Count - 1));
                var group = _vm.ToGroups[idx];
                if (ToGroupTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi)
                {
                    LogService.Debug($"[FOCUS] LandOnToGroup: tvi.Focus() idx={idx} {FocusInfo()}");
                    tvi.IsSelected = true;
                    tvi.Focus();
                }
                else
                {
                    LogService.Debug($"[FOCUS] LandOnToGroup: container not realized idx={idx} — retry at Background");
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ToGroupTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi2)
                        {
                            LogService.Debug($"[FOCUS] LandOnToGroup: retry tvi.Focus() idx={idx}");
                            tvi2.IsSelected = true;
                            tvi2.Focus();
                        }
                        else
                        {
                            LogService.Debug($"[FOCUS] LandOnToGroup: retry also failed idx={idx} — giving up");
                        }
                    }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    // ── SenderGroup tree event handlers ─────────────────────────────────────

    private void SenderGroupTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        LogService.Debug($"[FOCUS] SenderTree GotKeyboardFocus selectedItem={SenderGroupTree.SelectedItem?.GetType().Name ?? "null"} count={SenderGroupTree.Items.Count} from={e.OldFocus?.GetType().Name ?? "null"}");
        if (SenderGroupTree.SelectedItem == null && SenderGroupTree.Items.Count > 0)
        {
            if (SenderGroupTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                LogService.Debug("[FOCUS]   SenderTree GotKeyboardFocus: no selection — selecting first item");
                first.IsSelected = true;
                first.Focus();
            }
        }
    }

    private void SenderGroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        LogService.Debug($"[FOCUS] SenderTree SelectedItemChanged old={e.OldValue?.GetType().Name ?? "null"} new={e.NewValue?.GetType().Name ?? "null"} {FocusInfo()}");
        if (e.NewValue is MailMessageSummary msg)
            _vm.SelectedMessage = msg;

        if (SenderGroupTree.IsKeyboardFocusWithin)
        {
            switch (e.NewValue)
            {
                case MailMessageSummary m:
                    AccessibilityHelper.Announce(this, MessageSummaryAnnouncement(m), interrupt: true);
                    break;
                case SenderGroup grp:
                    AccessibilityHelper.Announce(this, grp.AutomationName, interrupt: true);
                    break;
            }
        }
    }

    private async void SenderGroupTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogService.Debug($"[FOCUS] SenderTree KeyDown key={e.Key} mod={Keyboard.Modifiers} {FocusInfo()} items={SenderGroupTree.Items.Count} selected={SenderGroupTree.SelectedItem?.GetType().Name ?? "null"}");
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            SyncFolderTreeSelection(true);
            return;
        }

        if (TryGetTypeAheadKeyText(e, out var searchText) &&
            TryHandleMessageTreeTypeAhead(
                SenderGroupTree,
                SenderGroupTree.SelectedItem,
                GetVisibleSenderItems(_vm.SenderGroups).ToList(),
                searchText))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (SenderGroupTree.SelectedItem is MailMessageSummary msg)
            {
                e.Handled = true;
                await _vm.SelectMessageCommand.ExecuteAsync(msg);
                if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                    await ShowMessageBodyAsync(_vm.MessageDetail);
            }
            else if (SenderGroupTree.SelectedItem is SenderGroup group)
            {
                e.Handled = true;
                if (group.Messages.Count == 1)
                {
                    var singleMsg = group.Messages[0];
                    _vm.SelectedMessage = singleMsg;
                    await _vm.SelectMessageCommand.ExecuteAsync(singleMsg);
                    if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                        await ShowMessageBodyAsync(_vm.MessageDetail);
                }
                else
                {
                    if (SenderGroupTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi)
                        tvi.IsExpanded = !tvi.IsExpanded;
                }
            }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (SenderGroupTree.SelectedItem is MailMessageSummary toDelete)
            {
                var parentGroup = _vm.SenderGroups.FirstOrDefault(g => g.Messages.Contains(toDelete));
                var targetIdx   = parentGroup != null ? _vm.SenderGroups.IndexOf(parentGroup) : 0;
                _vm.SelectedMessage = toDelete;
                LandOnSenderGroupAfterRebuild(targetIdx);   // register before the rebuild fires
                await _vm.DeleteMessageCommand.ExecuteAsync(null);
            }
            else if (SenderGroupTree.SelectedItem is SenderGroup group)
            {
                var targetIdx = _vm.SenderGroups.IndexOf(group);
                LandOnSenderGroupAfterRebuild(targetIdx);   // register before the rebuild fires
                await _vm.DeleteSenderGroupCommand.ExecuteAsync(group);
            }
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
                 && Keyboard.Modifiers == ModifierKeys.None
                 && SenderGroupTree.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    private void SenderGroupTree_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var visibleItems = GetVisibleSenderItems(_vm.SenderGroups).ToList();
        if (TryHandleMessageTreeTypeAhead(SenderGroupTree, SenderGroupTree.SelectedItem, visibleItems, e.Text))
            e.Handled = true;
    }

    private void SenderGroupTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is TreeViewItem tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    private void SenderGroupTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        switch (SenderGroupTree.SelectedItem)
        {
            case SenderGroup:
                SenderGroupTree.ContextMenu = (ContextMenu)FindResource("SenderGroupContextMenu");
                break;
            case MailMessageSummary:
                // Open MessageContextMenu at the tree level so PlacementTarget.DataContext
                // resolves to MainViewModel (not MailMessageSummary), giving commands
                // like ReplyCommand and DeleteMessageCommand access to the right bindings.
                SenderGroupTree.ContextMenu = (ContextMenu)FindResource("MessageContextMenu");
                break;
            default:
                e.Handled = true;
                break;
        }
    }

    // ── ToGroup tree event handlers ──────────────────────────────────────────

    private void ToGroupTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        LogService.Debug($"[FOCUS] ToTree GotKeyboardFocus selectedItem={ToGroupTree.SelectedItem?.GetType().Name ?? "null"} count={ToGroupTree.Items.Count} from={e.OldFocus?.GetType().Name ?? "null"}");
        if (ToGroupTree.SelectedItem == null && ToGroupTree.Items.Count > 0)
        {
            if (ToGroupTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                LogService.Debug("[FOCUS]   ToTree GotKeyboardFocus: no selection — selecting first item");
                first.IsSelected = true;
                first.Focus();
            }
        }
    }

    private void ToGroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        LogService.Debug($"[FOCUS] ToTree SelectedItemChanged old={e.OldValue?.GetType().Name ?? "null"} new={e.NewValue?.GetType().Name ?? "null"} {FocusInfo()}");
        if (e.NewValue is MailMessageSummary msg)
            _vm.SelectedMessage = msg;

        if (ToGroupTree.IsKeyboardFocusWithin)
        {
            switch (e.NewValue)
            {
                case MailMessageSummary m:
                    AccessibilityHelper.Announce(this, MessageSummaryAnnouncement(m), interrupt: true);
                    break;
                case SenderGroup grp:
                    AccessibilityHelper.Announce(this, grp.AutomationName, interrupt: true);
                    break;
            }
        }
    }

    private async void ToGroupTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogService.Debug($"[FOCUS] ToTree KeyDown key={e.Key} mod={Keyboard.Modifiers} {FocusInfo()} items={ToGroupTree.Items.Count} selected={ToGroupTree.SelectedItem?.GetType().Name ?? "null"}");
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            SyncFolderTreeSelection(true);
            return;
        }

        if (TryGetTypeAheadKeyText(e, out var searchText) &&
            TryHandleMessageTreeTypeAhead(
                ToGroupTree,
                ToGroupTree.SelectedItem,
                GetVisibleSenderItems(_vm.ToGroups).ToList(),
                searchText))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (ToGroupTree.SelectedItem is MailMessageSummary msg)
            {
                e.Handled = true;
                await _vm.SelectMessageCommand.ExecuteAsync(msg);
                if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                    await ShowMessageBodyAsync(_vm.MessageDetail);
            }
            else if (ToGroupTree.SelectedItem is SenderGroup group)
            {
                e.Handled = true;
                if (group.Messages.Count == 1)
                {
                    var singleMsg = group.Messages[0];
                    _vm.SelectedMessage = singleMsg;
                    await _vm.SelectMessageCommand.ExecuteAsync(singleMsg);
                    if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                        await ShowMessageBodyAsync(_vm.MessageDetail);
                }
                else
                {
                    if (ToGroupTree.ItemContainerGenerator.ContainerFromItem(group) is TreeViewItem tvi)
                        tvi.IsExpanded = !tvi.IsExpanded;
                }
            }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (ToGroupTree.SelectedItem is MailMessageSummary toDelete)
            {
                var parentGroup = _vm.ToGroups.FirstOrDefault(g => g.Messages.Contains(toDelete));
                var targetIdx   = parentGroup != null ? _vm.ToGroups.IndexOf(parentGroup) : 0;
                _vm.SelectedMessage = toDelete;
                LandOnToGroupAfterRebuild(targetIdx);
                await _vm.DeleteMessageCommand.ExecuteAsync(null);
            }
            else if (ToGroupTree.SelectedItem is SenderGroup group)
            {
                var targetIdx = _vm.ToGroups.IndexOf(group);
                LandOnToGroupAfterRebuild(targetIdx);
                await _vm.DeleteToGroupCommand.ExecuteAsync(group);
            }
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
                 && Keyboard.Modifiers == ModifierKeys.None
                 && ToGroupTree.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    private void ToGroupTree_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var visibleItems = GetVisibleSenderItems(_vm.ToGroups).ToList();
        if (TryHandleMessageTreeTypeAhead(ToGroupTree, ToGroupTree.SelectedItem, visibleItems, e.Text))
            e.Handled = true;
    }

    private void ToGroupTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is TreeViewItem tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    private void ToGroupTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        switch (ToGroupTree.SelectedItem)
        {
            case SenderGroup:
                ToGroupTree.ContextMenu = (ContextMenu)FindResource("ToGroupContextMenu");
                break;
            case MailMessageSummary:
                ToGroupTree.ContextMenu = (ContextMenu)FindResource("MessageContextMenu");
                break;
            default:
                e.Handled = true;
                break;
        }
    }

    private void OpenComposeWindow(ComposeModel composeModel)
    {
        var composeVm = new ComposeViewModel(_smtp, _accountService, _credentials, _imap);
        composeVm.Seed(composeModel);
        var window = new ComposeWindow(composeVm, _contactService) { Owner = this };
        composeVm.CloseRequested += window.Close;
        window.Show();
    }

    private void OpenAccountManager()
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap, _oauth);
        var dialog = new AccountManagerDialog(accountVm) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.RefreshAccountList();
    }

    private void OpenAccountManagerForAccount(AccountModel account)
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap, _oauth);
        var dialog    = new AccountManagerDialog(accountVm) { Owner = this };
        // Pre-select the account in the manager
        accountVm.SelectedAccount = accountVm.Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (dialog.ShowDialog() == true)
            _vm.RefreshAccountList();
    }

    // ── Folder context menu handlers ─────────────────────────────────────────

    private FolderTreeNode? GetContextMenuFolderNode(object sender)
    {
        var item = sender as MenuItem;
        var menu = item?.Parent as ContextMenu;
        return (menu?.PlacementTarget as TreeViewItem)?.DataContext as FolderTreeNode;
    }

    private async void FolderContextMenu_NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var node = GetContextMenuFolderNode(sender);
        if (node == null) return;

        // Determine the parent: if it's a header node (account), create at root; otherwise under the selected folder
        var parentFolder = node.IsHeader ? null : node.Folder;
        var accountId    = parentFolder?.AccountId
                          ?? _vm.Accounts.FirstOrDefault(a => a.AccountLabel == node.Label)?.Id
                          ?? _vm.SelectedAccount?.Id;
        if (accountId == null || accountId == Guid.Empty) return;

        var dlg = new NewFolderDialog
        {
            Owner = this,
            ParentFolderName = parentFolder?.DisplayName ?? node.Label
        };
        if (dlg.ShowDialog() != true) return;

        await _vm.CreateFolderAndRefreshAsync(accountId.Value, parentFolder?.FullName, dlg.FolderName);
    }

    private async void FolderContextMenu_MoveFolder_Click(object sender, RoutedEventArgs e)
    {
        var node = GetContextMenuFolderNode(sender);
        if (node?.Folder == null || node.IsHeader) return;

        if (_vm.CachedFolders.Count == 0) return;
        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Move Folder To") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.MoveFolderToAsync(node, picker.SelectedFolder);
    }

    private async void FolderContextMenu_CopyFolder_Click(object sender, RoutedEventArgs e)
    {
        var node = GetContextMenuFolderNode(sender);
        if (node?.Folder == null || node.IsHeader) return;

        if (_vm.CachedFolders.Count == 0) return;
        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Copy Folder To") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopyFolderToAsync(node, picker.SelectedFolder);
    }

    private async void FolderContextMenu_DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        var node = GetContextMenuFolderNode(sender);
        if (node == null) return;
        await _vm.DeleteFolderAsync(node);
    }

    // ── Message context menu handlers ────────────────────────────────────────

    private IReadOnlyList<MailMessageSummary> GetSelectedMessages()
    {
        // MessageList (standard view)
        if (MessageList.IsVisible && MessageList.SelectedItems.Count > 0)
            return MessageList.SelectedItems.OfType<MailMessageSummary>().ToList();

        // ConversationTree child node (individual message)
        if (ConversationTree.IsVisible && ConversationTree.SelectedItem is MailMessageSummary convMsg)
            return [convMsg];

        // SenderGroupTree child node (individual message)
        if (SenderGroupTree.IsVisible && SenderGroupTree.SelectedItem is MailMessageSummary senderMsg)
            return [senderMsg];

        // Fallback: VM's selected message
        if (_vm.SelectedMessage != null)
            return [_vm.SelectedMessage];

        return [];
    }

    // ── Menu bar handlers ────────────────────────────────────────────────────

    private void MenuAddressBook_Click(object sender, RoutedEventArgs e)
        => OpenAddressBook();

    private void OpenAddressBook()
    {
        var vm  = new AddressBookViewModel(_contactService);
        var win = new AddressBookWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    private void MenuGrabAddresses_Click(object sender, RoutedEventArgs e)
        => GrabAddressesFromMessage();

    private void GrabAddressesFromMessage()
    {
        if (_vm.MessageDetail is not { } detail) return;

        var list = new InternetAddressList();
        AddressParser.AddAddresses(list, detail.From);
        AddressParser.AddAddresses(list, detail.To);
        AddressParser.AddAddresses(list, detail.Cc);

        var addresses = list.OfType<MailboxAddress>()
            .Select(a => (Name: a.Name ?? string.Empty, a.Address))
            .DistinctBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0) return;

        new GrabAddressesDialog(addresses, _contactService) { Owner = this }.ShowDialog();
    }

    private void MenuCommandPalette_Click(object sender, RoutedEventArgs e)
        => OpenCommandPalette();

    private void MenuFolderPicker_Click(object sender, RoutedEventArgs e)
        => OpenFolderPicker();

    private async void MenuMoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;

        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Move to Folder") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.MoveSelectedMessagesToFolderAsync(messages, picker.SelectedFolder);

        if (_vm.IsConversationsView)
            LandOnConversationAfterRebuild(0);
        else if (_vm.IsFromView)
            LandOnSenderGroupAfterRebuild(0);
        else if (_vm.IsToView)
            LandOnToGroupAfterRebuild(0);
    }

    private async void MenuCopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;

        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Copy to Folder") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopySelectedMessagesToFolderAsync(messages, picker.SelectedFolder);
    }

    private async void MessageContextMenu_MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;

        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Move to Folder") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.MoveSelectedMessagesToFolderAsync(messages, picker.SelectedFolder);

        // Conversations/From: LandOnX waits for the async rebuild before focusing.
        // Messages view: MessageListFocusRequested in MoveSelectedMessagesToFolderAsync handles it.
        if (_vm.IsConversationsView)
            LandOnConversationAfterRebuild(0);
        else if (_vm.IsFromView)
            LandOnSenderGroupAfterRebuild(0);
        else if (_vm.IsToView)
            LandOnToGroupAfterRebuild(0);
    }

    private async void MessageContextMenu_CopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;

        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Copy to Folder") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopySelectedMessagesToFolderAsync(messages, picker.SelectedFolder);
    }

    // ── Conversation context menu handlers ───────────────────────────────────

    private async void ConversationContextMenu_MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationTree.SelectedItem is not ConversationGroup group || group.Messages.Count == 0) return;
        if (_vm.CachedFolders.Count == 0) return;

        var targetIdx = _vm.Conversations.IndexOf(group);
        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Move Conversation to Folder") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.MoveSelectedMessagesToFolderAsync(group.Messages, picker.SelectedFolder);
        LandOnConversationAfterRebuild(targetIdx);
    }

    private async void ConversationContextMenu_CopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationTree.SelectedItem is not ConversationGroup group || group.Messages.Count == 0) return;
        if (_vm.CachedFolders.Count == 0) return;

        var picker = new FolderPickerWindow(_vm.Accounts, _vm.CachedFolders, title: "Copy Conversation to Folder") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopySelectedMessagesToFolderAsync(group.Messages, picker.SelectedFolder);
    }

    // ── ConversationTree right-click helpers ─────────────────────────────────

    private void ConversationTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Walk up from the original source to find the TreeViewItem that was clicked,
        // then select it so SelectedItem is correct when ContextMenuOpening fires.
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is TreeViewItem tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    private void ConversationTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        switch (ConversationTree.SelectedItem)
        {
            case ConversationGroup:
                ConversationTree.ContextMenu = (ContextMenu)FindResource("ConversationGroupContextMenu");
                break;
            case MailMessageSummary:
                // Child-node message: the MessageContextMenu is already on the TreeViewItem style;
                // suppress the tree-level menu to avoid a double menu.
                e.Handled = true;
                break;
            default:
                e.Handled = true;
                break;
        }
    }

}
