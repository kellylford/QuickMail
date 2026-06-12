using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MimeKit;
using QuickMail.Helpers;
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

/// <summary>
/// Converts a string to Visibility: empty/null → Collapsed, non-empty → Visible.
/// Used to hide the last sync time status bar item when there's no sync info to display.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

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
    private readonly IMailService _imap;
    private readonly IOAuthService _oauth;
    private readonly IFeatureGate _featureGate;
    private readonly ICommandRegistry _registry;
    private bool _webViewReady;
    private CoreWebView2Environment? _webViewEnvironment;
    private string _typeAheadBuffer = string.Empty;
    private DateTime _typeAheadLastInputUtc = DateTime.MinValue;
    private object? _typeAheadScope;
    private int _messageBodyRenderVersion;

    // Tracks which pane (GetFocusedPaneIndex) was active when the window last deactivated
    // so we can restore focus on re-activation.  -1 = not yet deactivated / unknown.
    private int _paneIndexBeforeDeactivation = -1;

    // Debounces StatusText announcements so rapid per-folder sync updates ("5 messages",
    // "12 messages", …) coalesce into a single final reading by the screen reader.
    private DispatcherTimer? _statusAnnounceTimer;
    private string? _pendingStatusText;
    private static readonly TimeSpan StatusAnnounceDebounce = TimeSpan.FromMilliseconds(500);

    // Debounces search result-count announcements so rapid keystrokes coalesce.
    private DispatcherTimer? _searchAnnounceTimer;
    private string? _pendingSearchAnnounceText;
    private static readonly TimeSpan SearchAnnounceDebounce = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan WebViewNavigationTimeout = TimeSpan.FromSeconds(4);

    private readonly IContactService _contactService;
    private readonly IConfigService _configService;
    private readonly ILocalStoreService _localStore;
    private readonly IViewService _viewService;
    private readonly IRuleService _ruleService;
    private readonly ITemplateService _templateService;

    private TutorialViewModel? _tutorialVm;

    // ── Grouped-message tree controllers ──────────────────────────────────────
    private GroupedMessageTreeController? _convTreeController;
    private GroupedMessageTreeController? _senderTreeController;
    private GroupedMessageTreeController? _toTreeController;

    public MainWindow(
        MainViewModel vm,
        ISmtpService smtp,
        IAccountService accountService,
        ICredentialService credentials,
        IMailService imap,
        IOAuthService oauth,
        ICommandRegistry registry,
        IContactService contactService,
        IConfigService configService,
        ILocalStoreService localStore,
        IViewService viewService,
        IRuleService ruleService,
        ITemplateService templateService,
        IFeatureGate featureGate)
    {
        _vm = vm;
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        _oauth = oauth;
        _registry = registry;
        _contactService = contactService;
        _configService = configService;
        _localStore = localStore;
        _viewService = viewService;
        _ruleService = ruleService;
        _templateService = templateService;
        _featureGate = featureGate;

        InitializeComponent();
        DataContext = vm;

        vm.ComposeRequested += OpenComposeWindow;
        vm.ManageAccountsRequested += OpenAccountManager;
        vm.OpenAccountSettingsRequested += OpenAccountManagerForAccount;
        vm.MessageListFocusRequested += ReturnFocusToMessageList;
        vm.AnnouncementRequested += (_, args) =>
            AccessibilityHelper.Announce(this, args.Text, interrupt: true, category: args.Category);
        vm.SearchRequested += (_, _) => OpenSearch();
        vm.SaveViewRequested    += (_, _) => OpenViewManager(createMode: true);
        vm.ManageViewsRequested += (_, _) => OpenViewManager(createMode: false);
        vm.SavedViewsChanged    += (_, _) => RebuildViewsMenu();
        vm.PropertyChanged      += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveView))
                UpdateViewMenuCheckmarks();
        };
        vm.ConfirmationRequested = (message, title) =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
        vm.RulesManagerRequested += (_, _) => OpenRulesManager();
        vm.CreateRuleFromMessageRequested += (_, template) => OpenRulesManager(template);
        vm.TutorialRequested += (_, _) => ShowTutorial();
        vm.AboutRequested += (_, _) => ShowAboutDialog();
        vm.PropertiesRequested += propertiesVm =>
        {
            var win = new PropertiesWindow(propertiesVm) { Owner = this };
            win.ShowDialog();
        };

        vm.TabPromoteToWindowRequested += PromoteTabToWindow;

        vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab) && !_tabStripArrowNavInProgress)
                await OnActiveTabChangedAsync();

            if (e.PropertyName == nameof(MainViewModel.IsMessageOpen) && !_vm.IsMessageOpen && _webViewReady)
                MessageBody.CoreWebView2.NavigateToString("<html><body></body></html>");

            // Lazy-initialize the reading pane WebView2 the first time the user switches
            // away from Window mode (the HWND was intentionally skipped at startup).
            if (e.PropertyName == nameof(MainViewModel.MessageOpenMode)
                && _vm.MessageOpenMode != MessageOpenMode.Window
                && !_webViewReady
                && _webViewEnvironment != null)
                _ = InitReadingPaneWebViewAsync();
        };

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
                if (IsMenuOrToolbarFocused())
                {
                    LogService.Debug("[FOCUS]   → skipped (menu/toolbar has focus)");
                }
                else if (_vm.IsMessageOpen || _vm.IsMessageOpenInWindow)
                {
                    // Reading pane is open (or a message window is open) — the message
                    // collection changed but focus must not move.
                    LogService.Debug("[FOCUS]   → skipped (reading pane open)");
                }
                else if (vm.IsConversationsView)
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
                if (IsMenuOrToolbarFocused())
                {
                    LogService.Debug("[FOCUS] PropChanged:Conversations skipped (menu/toolbar has focus)");
                }
                else if (_vm.IsMessageOpen)
                {
                    // Reading pane is open — a background sync rebuilt the Conversations
                    // collection but focus must stay in the reading pane.
                    LogService.Debug("[FOCUS] PropChanged:Conversations skipped (reading pane open)");
                }
                else
                {
                    // Capture selected index now (before DataBind replaces items) so we can
                    // restore position after a background sync that rebuilds Conversations.
                    var oldIdx = ConversationTree.Items.IndexOf(ConversationTree.SelectedItem);
                    LogService.Debug($"[FOCUS] PropChanged:Conversations convCount={vm.Conversations.Count} oldIdx={oldIdx} {FocusInfo()}");
                    FocusTreeSelectedOrFirst(ConversationTree, oldIdx);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.SenderGroups) && IsActive && vm.IsFromView)
            {
                if (IsMenuOrToolbarFocused())
                {
                    LogService.Debug("[FOCUS] PropChanged:SenderGroups skipped (menu/toolbar has focus)");
                }
                else if (_vm.IsMessageOpen)
                {
                    // Reading pane is open — a background sync rebuilt the SenderGroups
                    // collection but focus must stay in the reading pane.
                    LogService.Debug("[FOCUS] PropChanged:SenderGroups skipped (reading pane open)");
                }
                else
                {
                    // Capture selected index now (before DataBind replaces items) so we can
                    // restore position after a background sync that rebuilds SenderGroups.
                    var oldIdx = SenderGroupTree.Items.IndexOf(SenderGroupTree.SelectedItem);
                    LogService.Debug($"[FOCUS] PropChanged:SenderGroups grpCount={vm.SenderGroups.Count} oldIdx={oldIdx} {FocusInfo()}");
                    FocusTreeSelectedOrFirst(SenderGroupTree, oldIdx);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.ToGroups) && IsActive && vm.IsToView)
            {
                if (IsMenuOrToolbarFocused())
                {
                    LogService.Debug("[FOCUS] PropChanged:ToGroups skipped (menu/toolbar has focus)");
                }
                else if (_vm.IsMessageOpen)
                {
                    // Reading pane is open — a background sync rebuilt the ToGroups
                    // collection but focus must stay in the reading pane.
                    LogService.Debug("[FOCUS] PropChanged:ToGroups skipped (reading pane open)");
                }
                else
                {
                    // Capture selected index now (before DataBind replaces items) so we can
                    // restore position after a background sync that rebuilds ToGroups.
                    var oldIdx = ToGroupTree.Items.IndexOf(ToGroupTree.SelectedItem);
                    LogService.Debug($"[FOCUS] PropChanged:ToGroups grpCount={vm.ToGroups.Count} oldIdx={oldIdx} {FocusInfo()}");
                    FocusTreeSelectedOrFirst(ToGroupTree, oldIdx);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.ViewMode))
            {
                LogService.Debug($"[FOCUS] ViewMode → {vm.ViewMode} {FocusInfo()}");
                if (!IsMenuOrToolbarFocused() && ShouldRestoreMessagePanelFocusAfterViewModeChange())
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
                QueueStatusAnnounce(vm.StatusText);

            if (e.PropertyName == nameof(MainViewModel.SearchAnnouncement) && !string.IsNullOrEmpty(vm.SearchAnnouncement))
                QueueSearchAnnounce(vm.SearchAnnouncement);
        };

        PreviewKeyDown += OnWindowKeyDown;
        MainStatusBar.PreviewKeyDown += StatusBar_PreviewKeyDown;
        Loaded      += OnLoaded;
        Deactivated += OnDeactivated;
        Activated   += OnActivated;

        // Debug: trace every SelectionChanged on the message list so we can see
        // when and why the selection is reset (only fires when /debug is active).
        MessageList.SelectionChanged += (_, args) =>
        {
            LogService.Debug(
                $"MessageList.SelectionChanged — added:{args.AddedItems.Count} removed:{args.RemovedItems.Count} " +
                $"total selected:{MessageList.SelectedItems.Count} " +
                $"focusedEl:{Keyboard.FocusedElement?.GetType().Name ?? "null"}");
        };

        // AutomationProperties.Name is set on each ListViewItem via ItemContainerStyle,
        // so WPF's built-in UIA focus events announce the selected message without a
        // separate notification event here.

        // ── Focus-enter / focus-leave traces for every message panel ────────────
        MessageList.GotKeyboardFocus       += (_, e) => LogService.Debug($"[FOCUS] GotFocus  MsgList   from={e.OldFocus?.GetType().Name ?? "null"}");
        MessageList.LostKeyboardFocus      += (_, e) => LogService.Debug($"[FOCUS] LostFocus MsgList   to={e.NewFocus?.GetType().Name ?? "null"}");
        ConversationTree.GotKeyboardFocus  += (_, e) => LogService.Debug($"[FOCUS] GotFocus  ConvTree  from={e.OldFocus?.GetType().Name ?? "null"} selectedItem={ConversationTree.SelectedItem?.GetType().Name ?? "null"}");
        ConversationTree.LostKeyboardFocus += (_, e) => LogService.Debug($"[FOCUS] LostFocus ConvTree  to={e.NewFocus?.GetType().Name ?? "null"}");
        SenderGroupTree.GotKeyboardFocus   += (_, e) => LogService.Debug($"[FOCUS] GotFocus  SenderTree from={e.OldFocus?.GetType().Name ?? "null"} selectedItem={SenderGroupTree.SelectedItem?.GetType().Name ?? "null"}");
        SenderGroupTree.LostKeyboardFocus  += (_, e) => LogService.Debug($"[FOCUS] LostFocus SenderTree to={e.NewFocus?.GetType().Name ?? "null"}");

        // ── Grouped-message tree controllers ──────────────────────────────────
        _convTreeController = new GroupedMessageTreeController(
            ConversationTree, _vm, "ConvTree",
            nameof(MainViewModel.Conversations),
            () => _vm.Conversations.Count,
            item => item is ConversationGroup g ? _vm.Conversations.IndexOf(g) : -1,
            idx => _vm.Conversations[idx],
            g => ((ConversationGroup)g).NormalizedSubject,
            key => _vm.Conversations.FirstOrDefault(c =>
                string.Equals(c.NormalizedSubject, key, StringComparison.OrdinalIgnoreCase)),
            g => ((ConversationGroup)g).Messages,
            () => GetVisibleConversationItems(_vm.Conversations),
            TryHandleMessageTreeTypeAhead);

        _senderTreeController = new GroupedMessageTreeController(
            SenderGroupTree, _vm, "SenderTree",
            nameof(MainViewModel.SenderGroups),
            () => _vm.SenderGroups.Count,
            item => item is SenderGroup g ? _vm.SenderGroups.IndexOf(g) : -1,
            idx => _vm.SenderGroups[idx],
            g => ((SenderGroup)g).SenderKey,
            key => _vm.SenderGroups.FirstOrDefault(g =>
                string.Equals(g.SenderKey, key, StringComparison.OrdinalIgnoreCase)),
            g => ((SenderGroup)g).Messages,
            () => GetVisibleSenderItems(_vm.SenderGroups),
            TryHandleMessageTreeTypeAhead);

        _toTreeController = new GroupedMessageTreeController(
            ToGroupTree, _vm, "ToTree",
            nameof(MainViewModel.ToGroups),
            () => _vm.ToGroups.Count,
            item => item is SenderGroup g ? _vm.ToGroups.IndexOf(g) : -1,
            idx => _vm.ToGroups[idx],
            g => ((SenderGroup)g).SenderKey,
            key => _vm.ToGroups.FirstOrDefault(g =>
                string.Equals(g.SenderKey, key, StringComparison.OrdinalIgnoreCase)),
            g => ((SenderGroup)g).Messages,
            () => GetVisibleSenderItems(_vm.ToGroups),
            TryHandleMessageTreeTypeAhead);
    }

    // Debounced UIA notification for rapid status-text changes during sync. Multiple
    // per-folder updates ("5 messages", "12 messages", …) are coalesced so the screen
    // reader hears only the final count once sync settles.
    private void QueueStatusAnnounce(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _pendingStatusText = text;

        if (_statusAnnounceTimer == null)
        {
            _statusAnnounceTimer = new DispatcherTimer { Interval = StatusAnnounceDebounce };
            _statusAnnounceTimer.Tick += (_, _) =>
            {
                _statusAnnounceTimer!.Stop();
                var pending = _pendingStatusText;
                _pendingStatusText = null;
                if (!string.IsNullOrEmpty(pending))
                    AccessibilityHelper.Announce(this, pending, category: AnnouncementCategory.Status);
            };
        }

        _statusAnnounceTimer.Stop();
        _statusAnnounceTimer.Start();
    }

    private void QueueSearchAnnounce(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _pendingSearchAnnounceText = text;

        if (_searchAnnounceTimer == null)
        {
            _searchAnnounceTimer = new DispatcherTimer { Interval = SearchAnnounceDebounce };
            _searchAnnounceTimer.Tick += (_, _) =>
            {
                _searchAnnounceTimer!.Stop();
                var pending = _pendingSearchAnnounceText;
                _pendingSearchAnnounceText = null;
                if (!string.IsNullOrEmpty(pending))
                    AccessibilityHelper.Announce(this, pending, interrupt: false, category: AnnouncementCategory.Result);
            };
        }

        _searchAnnounceTimer.Stop();
        _searchAnnounceTimer.Start();
    }

    private void OpenSearch()
    {
        _vm.IsSearchActive = true;
        Dispatcher.InvokeAsync(() =>
        {
            SearchBox.Focus();
            AccessibilityHelper.Announce(this, "Search box. Type to filter messages.", interrupt: true, category: AnnouncementCategory.Hint);
        }, DispatcherPriority.Input);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var count = _vm.Messages.Count;
            _vm.ClearSearchCommand.Execute(null);
            ReturnFocusToMessageList();
            var word = count == 1 ? "message" : "messages";
            AccessibilityHelper.Announce(this, $"Search cleared. {count} {word}.", interrupt: true, category: AnnouncementCategory.Result);
            e.Handled = true;
        }
        else if (e.Key == Key.Down || (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None))
        {
            ReturnFocusToMessageList();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            SyncFolderTreeSelection(true);
            e.Handled = true;
        }
    }

    // Returns true when the main menu bar or toolbar currently holds keyboard focus,
    // meaning sync-triggered focus restoration should be suppressed so the user's
    // deliberate navigation is not interrupted.
    private bool IsMenuOrToolbarFocused() =>
        MainMenuBar.IsKeyboardFocusWithin || MainToolbar.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin;

    // On startup: initialise WebView2, connect to first account, open INBOX, focus message list
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Register commands that require UI access (must run after InitializeComponent).
        _registry.Register(new CommandDefinition(
            id: "view.focusFolders", category: "View", title: "Focus Folder Tree",
            execute: FocusFolderTree,
            defaultKey: Key.D2, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "view.folderPicker", category: "View", title: "Go to Folder…",
            execute: OpenFolderPicker));

        _registry.Register(new CommandDefinition(
            id: "view.searchFolders", category: "View", title: "Search Folders…",
            execute: OpenFolderPicker,
            defaultKey: Key.F, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "view.openViewMenu", category: "View", title: "Open View Menu",
            execute: OpenViewMenu,
            defaultKey: Key.V, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "view.focusStatusBar", category: "View", title: "Focus Status Bar",
            execute: FocusStatusBar,
            defaultKey: Key.D9, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "view.showProperties", category: "View", title: "View Properties",
            execute: () =>
            {
                // Attachment list: it sits outside MessageBody in the visual tree so
                // GetFocusedPaneIndex() returns 0 for it. Handle here before the
                // pane-index path so the window handler doesn't shadow the attachment.
                if (ReadingPaneAttachmentList.IsKeyboardFocusWithin
                    && ReadingPaneAttachmentList.SelectedItem is AttachmentModel att)
                {
                    var (attTitle, attSections) = AttachmentPropertiesBuilder.Build(att);
                    new PropertiesWindow(new PropertiesViewModel(attTitle, attSections)) { Owner = this }
                        .ShowDialog();
                    return;
                }

                var pane = GetFocusedPaneIndex();

                // FolderList is a TreeView: arrow-key navigation updates TreeView.SelectedItem
                // but there is no SelectedItemChanged handler, so _vm.SelectedFolder lags
                // behind until Enter commits the selection. Pass the live node directly.
                var focusedFolder = pane == 2
                    ? (FolderList.SelectedItem as FolderTreeNode)?.Folder
                    : null;

                // Group headers in the grouped trees show group-level properties rather
                // than falling back to the first message. Individual message children
                // (SelectedItem is MailMessageSummary) fall through to ShowPropertiesAsync.
                if (pane == 3)
                {
                    if (ConversationTree.IsKeyboardFocusWithin
                        && ConversationTree.SelectedItem is ConversationGroup cg)
                    {
                        var (cgTitle, cgSections) = ConversationPropertiesBuilder.Build(cg);
                        new PropertiesWindow(new PropertiesViewModel(cgTitle, cgSections)) { Owner = this }
                            .ShowDialog();
                        return;
                    }
                    if (SenderGroupTree.IsKeyboardFocusWithin
                        && SenderGroupTree.SelectedItem is SenderGroup sg)
                    {
                        var (sgTitle, sgSections) = SenderGroupPropertiesBuilder.Build(sg);
                        new PropertiesWindow(new PropertiesViewModel(sgTitle, sgSections)) { Owner = this }
                            .ShowDialog();
                        return;
                    }
                    if (ToGroupTree.IsKeyboardFocusWithin
                        && ToGroupTree.SelectedItem is SenderGroup tg)
                    {
                        var (tgTitle, tgSections) = SenderGroupPropertiesBuilder.Build(tg, isToGroup: true);
                        new PropertiesWindow(new PropertiesViewModel(tgTitle, tgSections)) { Owner = this }
                            .ShowDialog();
                        return;
                    }
                }

                _ = _vm.ShowPropertiesAsync(pane, focusedFolder, focusedMessage: null);
            },
            defaultKey: Key.Return, defaultModifiers: ModifierKeys.Alt,
            isAvailable: () => _vm.SelectedMessage != null
                            || _vm.SelectedFolder  != null
                            || _vm.SelectedAccount != null));

        _registry.Register(new CommandDefinition(
            id: "contacts.grabAddresses", category: "Contacts", title: "Grab Addresses from Message",
            execute: GrabAddressesFromMessage,
            defaultKey: Key.G, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => _vm.IsMessageOpen || _vm.IsMessageOpenInWindow));

        _registry.Register(new CommandDefinition(
            id: "contacts.openAddressBook", category: "Contacts", title: "Address Book",
            execute: OpenAddressBook,
            defaultKey: Key.B, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "settings.toggleCustomAnnouncements", category: "Settings", title: "Toggle Custom Announcements",
            execute: ToggleCustomAnnouncements));

        _registry.Register(new CommandDefinition(
            id: "mail.markRead", category: "Mail", title: "Mark as Read",
            execute: async () => await MarkReadCommand(),
            defaultKey: Key.Q, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "mail.jumpToFirstInGroup", category: "Mail", title: "First Message in Group",
            execute: JumpToFirstMessageInGroup,
            defaultKey: Key.OemComma, defaultModifiers: ModifierKeys.Shift,
            isAvailable: IsGroupedViewActive));

        _registry.Register(new CommandDefinition(
            id: "mail.jumpToLastInGroup", category: "Mail", title: "Last Message in Group",
            execute: JumpToLastMessageInGroup,
            defaultKey: Key.OemPeriod, defaultModifiers: ModifierKeys.Shift,
            isAvailable: IsGroupedViewActive));

        _registry.Register(new CommandDefinition(
            id: "mail.selectAll", category: "Mail", title: "Select All Messages",
            execute: SelectAllMessages,
            defaultKey: Key.A, defaultModifiers: ModifierKeys.Control,
            isAvailable: IsMessageListFocused));

        // Override the VM's mail.delete with one that deletes ALL selected messages.
        // The VM registration uses DeleteMessageCommand, which deletes only SelectedMessage
        // (one item). When the message list has focus with multiple items selected, bypass
        // the registry entirely so MessageList_PreviewKeyDown handles it instead — that
        // handler reads MessageList.SelectedItems and deletes everything.
        // Also bypass when a group tree has focus: those trees have their own PreviewKeyDown
        // handlers that call LandOn* before deleting so focus lands on the next item correctly.
        _registry.Register(new CommandDefinition(
            id: "mail.delete", category: "Mail", title: "Delete",
            execute: () => _vm.DeleteMessageCommand.Execute(null),
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            isAvailable: () => _vm.HasSelectedMessage
                && !(IsMessageListFocused() && MessageList.SelectedItems.Count > 1)
                && !IsGroupTreeFocused()));

        // ── Pane navigation (Ctrl+Alt+1/2/3 — always work regardless of tab mode) ──
        _registry.Register(new CommandDefinition(
            id: "view.focusAccounts", category: "View", title: "Focus Account List",
            execute: () => AccountList.Focus(),
            defaultKey: Key.D1, defaultModifiers: ModifierKeys.Control | ModifierKeys.Alt));

        _registry.Register(new CommandDefinition(
            id: "view.focusMessages", category: "View", title: "Focus Message List",
            execute: () =>
            {
                if (_vm.IsConversationsView)      ConversationTree.Focus();
                else if (_vm.IsFromView)           SenderGroupTree.Focus();
                else if (_vm.IsToView)             ToGroupTree.Focus();
                else                               MessageList.Focus();
            },
            defaultKey: Key.D3, defaultModifiers: ModifierKeys.Control | ModifierKeys.Alt));

        _registry.Register(new CommandDefinition(
            id: "view.focusTabs", category: "View", title: "Focus Tab Strip",
            execute: () => { if (_vm.ShowTabStrip) TabStrip.Focus(); },
            defaultKey: Key.T, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => _vm.ShowTabStrip));

        // ── Tab & Window Management commands ─────────────────────────────────────
        _registry.Register(new CommandDefinition(
            id: "tabs.next", category: "View", title: "Next Tab",
            execute: () => _vm.ActivateNextTab(),
            defaultKey: Key.Tab, defaultModifiers: ModifierKeys.Control,
            isAvailable: () => _vm.OpenTabs.Count > 1));

        _registry.Register(new CommandDefinition(
            id: "tabs.previous", category: "View", title: "Previous Tab",
            execute: () => _vm.ActivatePrevTab(),
            defaultKey: Key.Tab, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => _vm.OpenTabs.Count > 1));

        _registry.Register(new CommandDefinition(
            id: "tabs.close", category: "View", title: "Close Tab",
            execute: () => { if (_vm.ActiveTab != null) _vm.CloseTab(_vm.ActiveTab); },
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control,
            isAvailable: () => _vm.ActiveTab is MessageTabViewModel));

        _registry.Register(new CommandDefinition(
            id: "mail.closeMessage", category: "Mail", title: "Close Message",
            execute: CloseReadingPane,
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control,
            isAvailable: () => _vm.IsMessageOpen && _vm.MessageOpenMode == MessageOpenMode.ReadingPane));

        _registry.Register(new CommandDefinition(
            id: "tabs.closeOthers", category: "View", title: "Close Other Tabs",
            execute: () => _vm.CloseAllOtherTabs(),
            isAvailable: () => _vm.OpenTabs.Count > 1));

        _registry.Register(new CommandDefinition(
            id: "tabs.list", category: "View", title: "Tab List…",
            execute: OpenTabList,
            defaultKey: Key.OemTilde, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => _vm.OpenTabs.Count > 0));

        _registry.Register(new CommandDefinition(
            id: "tabs.moveLeft", category: "View", title: "Move Tab Left",
            execute: () => _vm.MoveTabLeft(),
            isAvailable: () => _vm.ActiveTab != null && _vm.OpenTabs.Count > 1));

        _registry.Register(new CommandDefinition(
            id: "tabs.moveRight", category: "View", title: "Move Tab Right",
            execute: () => _vm.MoveTabRight(),
            isAvailable: () => _vm.ActiveTab != null && _vm.OpenTabs.Count > 1));

        _registry.Register(new CommandDefinition(
            id: "tabs.promote", category: "View", title: "Move Tab to New Window",
            execute: () => _vm.PromoteActiveTabToWindow(),
            isAvailable: () => _vm.ActiveTab != null));

        _registry.Register(new CommandDefinition(
            id: "mail.openInNewTab", category: "Mail", title: "Open in New Tab",
            execute: () => { if (_vm.SelectedMessage != null) _vm.OpenMessageTab(_vm.SelectedMessage); },
            isAvailable: () => _vm.SelectedMessage != null));

        _registry.Register(new CommandDefinition(
            id: "mail.openInWindow", category: "Mail", title: "Open in New Window",
            execute: () => { if (_vm.SelectedMessage != null) OpenMessageInNewWindow(_vm.SelectedMessage); },
            isAvailable: () => _vm.SelectedMessage != null));

        // Create the WebView2 environment — always needed, shared with MessageWindow instances.
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickMail", "WebView2");
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        }
        catch (Exception ex)
        {
            LogService.Log("WebView2 environment creation failed", ex);
        }

        // Initialize the reading pane WebView2 only when it will actually be used.
        // In Window mode the reading pane is never shown, and creating the browser
        // HWND in the main window lets screen readers enter browse mode for it —
        // causing Down arrow to read message content instead of navigating the list.
        // Lazy initialization fires when the user switches away from Window mode.
        if (_vm.MessageOpenMode != MessageOpenMode.Window)
            await InitReadingPaneWebViewAsync();

        var firstAccount = _vm.Accounts.FirstOrDefault();
        if (firstAccount == null)
        {
            OpenAccountManager();
            return;
        }

        // Show local cache immediately so the UI is never blank on startup.
        await _vm.InitialLoadAsync();
        FocusActiveMessagePanel();

        // Populate the Views menu from saved views loaded at startup.
        RebuildViewsMenu();

        // Connect accounts and sync new mail in the background; messages trickle in via FolderSynced.
        _ = _vm.StartBackgroundSyncAsync();
    }

    // Global key handler (PreviewKeyDown so it fires before any child can swallow the event).
    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = e.KeyboardDevice.Modifiers;
        var key = e.Key == Key.System
            ? e.SystemKey
            : e.Key == Key.ImeProcessed
                ? e.ImeProcessedKey
                : e.Key;

        // ── Tutorial interception (must run before all other handling) ────────────
        if (_tutorialVm?.IsActive == true)
        {
            if (_tutorialVm.CheckKeyPress(key, modifiers))
            {
                e.Handled = true;
                return;
            }
            // Wrong key — announce what was pressed so the user gets feedback.
            // Skip bare modifier keys (Ctrl, Shift, Alt, Win) — they aren't
            // meaningful attempts and would just be noise.
            if (!IsModifierKey(key))
            {
                var pressed = FormatKeyGesture(key, modifiers);
                AccessibilityHelper.Announce(this,
                    $"You pressed {pressed}. Try again.",
                    interrupt: true, category: AnnouncementCategory.Result);
            }
            e.Handled = true;
            return;
        }

        // ── Navigation shortcuts (hardcoded, not in the command registry) ──────────
        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.D0: ToolbarFirstButton.Focus(); e.Handled = true; return;

                case Key.D1:
                    if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(1); e.Handled = true; return; }
                    AccountList.Focus(); e.Handled = true; return;

                case Key.D2:
                    if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(2); e.Handled = true; return; }
                    FocusFolderTree(); e.Handled = true; return;

                case Key.NumPad2:
                case Key.Y:
                    FocusFolderTree(); e.Handled = true; return;

                case Key.D3:
                    if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(3); e.Handled = true; return; }
                    if (_vm.IsConversationsView) ConversationTree.Focus();
                    else if (_vm.IsFromView)      SenderGroupTree.Focus();
                    else if (_vm.IsToView)         ToGroupTree.Focus();
                    else                           MessageList.Focus();
                    e.Handled = true; return;

                case Key.D4: if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(4); e.Handled = true; return; } break;
                case Key.D5: if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(5); e.Handled = true; return; } break;
                case Key.D6: if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(6); e.Handled = true; return; } break;
                case Key.D7: if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(7); e.Handled = true; return; } break;
                case Key.D8: if (_vm.ShowTabStrip) { _vm.ActivateTabByIndex(8); e.Handled = true; return; } break;

                case Key.D9:
                    if (_vm.ShowTabStrip) { _vm.ActivateLastTab(); e.Handled = true; return; }
                    break; // falls through to registry (view.focusStatusBar)
            }
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            // Pane navigation via Ctrl+Alt+1/2/3 — always available regardless of tab mode.
            switch (key)
            {
                case Key.D1: AccountList.Focus(); e.Handled = true; return;
                case Key.D2: FocusFolderTree();   e.Handled = true; return;
                case Key.D3:
                    if (_vm.IsConversationsView) ConversationTree.Focus();
                    else if (_vm.IsFromView)      SenderGroupTree.Focus();
                    else if (_vm.IsToView)         ToGroupTree.Focus();
                    else                           MessageList.Focus();
                    e.Handled = true; return;
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
                    CloseReadingPane();
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
        if (cmd != null)
        {
            var available = cmd.IsAvailable?.Invoke() ?? true;
            LogService.Debug($"OnWindowKeyDown registry: id='{cmd.Id}' key={key} mod={modifiers} focused={Keyboard.FocusedElement?.GetType().Name} isAvailable={available}");
            if (available)
            {
                cmd.Execute();
                e.Handled = true;
            }
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

    private void FocusFolderTree()
    {
        if (FolderList.Items.Count == 0)
        {
            _vm.StatusText = "Folders are still loading.";
            return;
        }

        FolderList.Focus();
        Dispatcher.InvokeAsync(() =>
        {
            if (FolderList.SelectedItem is FolderTreeNode selected &&
                SelectTreeViewNode(FolderList, selected))
                return;

            if (FolderList.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                first.IsSelected = true;
                first.Focus();
                return;
            }

            FolderList.Focus();
        }, DispatcherPriority.Input);
    }

    private async void OpenFolderPicker()
    {
        if (_vm.CachedFolders.Count == 0)
        {
            _vm.StatusText = "Folders are still loading.";
            return;
        }
        var acctMailFolders = _vm.Accounts
            .Where(a => _vm.CachedFolders.ContainsKey(a.Id))
            .ToDictionary(a => a.Id, a => MainViewModel.CreateAccountMailVirtualFolder(a));
        var picker = new FolderPickerWindow(
            _vm.Accounts,
            _vm.CachedFolders,
            new[]
            {
                MainViewModel.AllInboxesFolder, MainViewModel.AllMailFolder,
                MainViewModel.AllDraftsFolder,  MainViewModel.AllSentFolder,
                MainViewModel.AllTrashFolder
            },
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
            await OpenMessageFromListAsync(summary);
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

        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        // < and > (Shift+, and Shift+.) jump to group boundaries in grouped views only.
        // Consume them here so they are silent no-ops in the flat message list.
        if ((e.Key == Key.OemComma || e.Key == Key.OemPeriod) && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
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
            await OpenMessageFromListAsync(summary);
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
        else if (e.Key == Key.Home && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            ExtendSelectionToTop();
        }
        else if (e.Key == Key.End && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            ExtendSelectionToBottom();
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
        => _convTreeController?.OnPreviewTextInput(sender, e);

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

    private void SelectAllMessages()
    {
        if (MessageList.Items.Count == 0) return;
        MessageList.SelectAll();
        var count = MessageList.SelectedItems.Count;
        LogService.Debug($"SelectAllMessages: items={MessageList.Items.Count} selected={count}");
        AccessibilityHelper.Announce(this,
            $"{count} message{(count == 1 ? "" : "s")} selected.",
            category: AnnouncementCategory.Result);
    }

    private bool IsMessageListFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused == MessageList
            || (focused is DependencyObject dep && IsDescendantOf(MessageList, dep));
    }

    private bool IsGroupTreeFocused()
    {
        var focused = Keyboard.FocusedElement;
        if (focused == null) return false;
        if (focused == SenderGroupTree || focused == ToGroupTree || focused == ConversationTree)
            return true;
        if (focused is DependencyObject dep)
            return IsDescendantOf(SenderGroupTree, dep)
                || IsDescendantOf(ToGroupTree, dep)
                || IsDescendantOf(ConversationTree, dep);
        return false;
    }

    private void ExtendSelectionToTop()
    {
        if (MessageList.Items.Count == 0) return;

        int anchorIndex = -1;
        if (MessageList.SelectedItems.Count > 0)
        {
            anchorIndex = MessageList.Items.Count;
            foreach (var item in MessageList.SelectedItems)
            {
                var idx = MessageList.Items.IndexOf(item);
                if (idx >= 0 && idx < anchorIndex)
                    anchorIndex = idx;
            }
        }

        if (anchorIndex < 0)
        {
            MessageList.SelectedIndex = 0;
            MessageList.ScrollIntoView(MessageList.Items[0]);
            Dispatcher.InvokeAsync(() =>
            {
                if (MessageList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem container)
                    container.Focus();
            }, DispatcherPriority.Input);
            AccessibilityHelper.Announce(this, "1 message selected.", category: AnnouncementCategory.Result);
            return;
        }

        for (int i = 0; i <= anchorIndex; i++)
        {
            var item = MessageList.Items[i];
            if (!MessageList.SelectedItems.Contains(item))
                MessageList.SelectedItems.Add(item);
        }

        MessageList.ScrollIntoView(MessageList.Items[0]);
        Dispatcher.InvokeAsync(() =>
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem container)
                container.Focus();
        }, DispatcherPriority.Input);

        var count = MessageList.SelectedItems.Count;
        AccessibilityHelper.Announce(this,
            $"{count} message{(count == 1 ? "" : "s")} selected.",
            category: AnnouncementCategory.Result);
    }

    private void ExtendSelectionToBottom()
    {
        if (MessageList.Items.Count == 0) return;

        int lastIndex = MessageList.Items.Count - 1;

        int anchorIndex = -1;
        if (MessageList.SelectedItems.Count > 0)
        {
            foreach (var item in MessageList.SelectedItems)
            {
                var idx = MessageList.Items.IndexOf(item);
                if (idx > anchorIndex)
                    anchorIndex = idx;
            }
        }

        if (anchorIndex < 0)
        {
            MessageList.SelectedIndex = lastIndex;
            MessageList.ScrollIntoView(MessageList.Items[lastIndex]);
            Dispatcher.InvokeAsync(() =>
            {
                if (MessageList.ItemContainerGenerator.ContainerFromIndex(lastIndex) is ListViewItem container)
                    container.Focus();
            }, DispatcherPriority.Input);
            AccessibilityHelper.Announce(this, "1 message selected.", category: AnnouncementCategory.Result);
            return;
        }

        for (int i = anchorIndex; i <= lastIndex; i++)
        {
            var item = MessageList.Items[i];
            if (!MessageList.SelectedItems.Contains(item))
                MessageList.SelectedItems.Add(item);
        }

        MessageList.ScrollIntoView(MessageList.Items[lastIndex]);
        Dispatcher.InvokeAsync(() =>
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(lastIndex) is ListViewItem container)
                container.Focus();
        }, DispatcherPriority.Input);

        var count = MessageList.SelectedItems.Count;
        AccessibilityHelper.Announce(this,
            $"{count} message{(count == 1 ? "" : "s")} selected.",
            category: AnnouncementCategory.Result);
    }

    // One-time setup of the reading pane WebView2. Skipped at startup in Window mode;
    // called lazily the first time the user switches to ReadingPane or Tab mode.
    private async Task InitReadingPaneWebViewAsync()
    {
        if (_webViewReady || _webViewEnvironment == null) return;
        try
        {
            await MessageBody.EnsureCoreWebView2Async(_webViewEnvironment);
            _webViewReady = true;

            MessageBody.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MessageBody.CoreWebView2.Settings.AreDevToolsEnabled = false;
            MessageBody.CoreWebView2.Settings.IsStatusBarEnabled = false;

            await MessageBody.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){"
                +"if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}"
                +"else if(e.key==='F6'){window.chrome.webview.postMessage(e.shiftKey?'shift-f6':'f6');e.preventDefault();}"
                +"else if(e.ctrlKey&&(e.key==='2'||e.key==='y'||e.key==='Y')){window.chrome.webview.postMessage('focus-folders');e.preventDefault();}"
                +"else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}"
                +"else if(e.ctrlKey&&e.key==='w'){window.chrome.webview.postMessage('ctrl-w');e.preventDefault();}"
                +"});");

            MessageBody.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == "escape")
                    Dispatcher.InvokeAsync(CloseReadingPane, DispatcherPriority.Input);
                else if (msg == "f6")
                    Dispatcher.InvokeAsync(() => _ = CycleFocusAsync(true), DispatcherPriority.Input);
                else if (msg == "shift-f6")
                    Dispatcher.InvokeAsync(() => _ = CycleFocusAsync(false), DispatcherPriority.Input);
                else if (msg == "focus-folders")
                    Dispatcher.InvokeAsync(FocusFolderTree, DispatcherPriority.Input);
                else if (msg == "shift-tab")
                    Dispatcher.InvokeAsync(FocusLastHeaderField, DispatcherPriority.Input);
                else if (msg == "ctrl-w")
                    Dispatcher.InvokeAsync(
                        () => _registry.FindByGesture(Key.W, ModifierKeys.Control)?.Execute(),
                        DispatcherPriority.Input);
            };

            MessageBody.CoreWebView2.NavigationStarting += (_, args) =>
            {
                var uri = args.Uri;
                if (string.IsNullOrEmpty(uri) ||
                    uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                    uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return;
                args.Cancel = true;
                if (uri.StartsWith("quickmail:", StringComparison.OrdinalIgnoreCase))
                {
                    HandleQuickMailUri(uri);
                    return;
                }
                OpenExternal(uri);
            };
            MessageBody.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };
            MessageBody.CoreWebView2.ProcessFailed += (_, args) =>
                LogService.Log($"[ERROR] WebView2 ProcessFailed kind={args.ProcessFailedKind} exit={args.ExitCode} reason={args.Reason}");
        }
        catch (Exception ex)
        {
            LogService.Log("WebView2 reading pane init failed", ex);
        }
    }

    // Render the message body in the browser and move focus into it
    private async Task ShowMessageBodyAsync(MailMessageDetail detail)
    {
        if (!_webViewReady) return;

        var renderVersion = Interlocked.Increment(ref _messageBodyRenderVersion);
        var html = await Task.Run(() => MessageBodyHtmlBuilder.BuildMessageHtml(detail));
        if (renderVersion != _messageBodyRenderVersion)
            return;

        // Prepend the calendar invite event card if present.
        var eventCardHtml = _vm.BuildEventCardHtml();
        if (!string.IsNullOrEmpty(eventCardHtml))
        {
            html = InjectEventCard(html, eventCardHtml);
        }

        // Wait for navigation to finish before focusing so the screen reader
        // gets the rendered document, but never let a complex sender HTML wait forever.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigated(object? s, CoreWebView2NavigationCompletedEventArgs ev)
        {
            MessageBody.CoreWebView2.NavigationCompleted -= OnNavigated;
            tcs.TrySetResult(ev.IsSuccess);
        }

        MessageBody.CoreWebView2.NavigationCompleted += OnNavigated;
        // Cancel any prior in-flight navigation before starting this one so we don't
        // queue two pending navigations on the WebView2 renderer.
        try { MessageBody.CoreWebView2.Stop(); }
        catch (Exception ex) { LogService.Log("ShowMessageBody/Stop", ex); }
        MessageBody.CoreWebView2.NavigateToString(html);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(WebViewNavigationTimeout)) == tcs.Task;
        if (!completed)
        {
            MessageBody.CoreWebView2.NavigationCompleted -= OnNavigated;
            LogService.Log($"ShowMessageBody: WebView navigation timed out for UID {detail.MessageId}");
        }

        if (renderVersion != _messageBodyRenderVersion)
            return;

        await FocusMessageBodyAsync(renderVersion, detail.Subject);
    }

    private async Task FocusMessageBodyAsync(int renderVersion, string? subject)
    {
        if (renderVersion != _messageBodyRenderVersion || !_webViewReady || !_vm.IsMessageOpen)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (!_vm.IsMessageOpen) return;
            MessageBody.Focus();
            Keyboard.Focus(MessageBody);
        }, DispatcherPriority.Input);

        var focusLabel = MessageBodyFocusLabel(subject);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (renderVersion != _messageBodyRenderVersion || !_vm.IsMessageOpen)
                return;

            await Dispatcher.InvokeAsync(FocusMessageBodyHost, DispatcherPriority.Input);

            try
            {
                if (await TryFocusMessageBodyDocumentAsync(focusLabel))
                    break;
            }
            catch (Exception ex)
            {
                if (attempt == 4)
                    LogService.Log("FocusMessageBody", ex);
            }

            await Task.Delay(100);
        }

        if (renderVersion != _messageBodyRenderVersion || !_vm.IsMessageOpen)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (!_vm.IsMessageOpen) return;
            FocusMessageBodyHost();
            AccessibilityHelper.Announce(this, focusLabel, interrupt: true, category: AnnouncementCategory.Result);
            AccessibilityHelper.Announce(this, "Press Escape to return to message list.", interrupt: false, category: AnnouncementCategory.Hint);
        }, DispatcherPriority.Input);
    }

    private void FocusMessageBodyHost()
    {
        if (!_vm.IsMessageOpen) return;

        MessageBody.Focus();
        Keyboard.Focus(MessageBody);

        try
        {
            ((IKeyboardInputSink)MessageBody).TabInto(
                new TraversalRequest(FocusNavigationDirection.First));
        }
        catch (Exception ex)
        {
            LogService.Log("FocusMessageBodyHost", ex);
        }
    }

    private async Task<bool> TryFocusMessageBodyDocumentAsync(string focusLabel)
    {
        var bodyLabel = JsonSerializer.Serialize(focusLabel);
        var result = await MessageBody.CoreWebView2.ExecuteScriptAsync(
            "(() => {" +
            "const body = document.body;" +
            "if (!body) return false;" +
            "window.focus();" +
            "body.setAttribute('tabindex','0');" +
            "body.setAttribute('role','document');" +
            $"body.setAttribute('aria-label',{bodyLabel});" +
            "body.focus({preventScroll:true});" +
            "return document.hasFocus() && document.activeElement === body;" +
            "})()");

        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string MessageBodyFocusLabel(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "Message body";

        var trimmed = subject.Trim();
        if (trimmed.Length > 120)
            trimmed = trimmed[..120] + "...";

        return $"Message body. {trimmed}";
    }

    /// <summary>Injects the event card HTML just after the opening &lt;body&gt; tag.</summary>
    private static string InjectEventCard(string html, string eventCardHtml)
    {
        var bodyTag = "<body";
        var bodyIdx = html.IndexOf(bodyTag, StringComparison.OrdinalIgnoreCase);
        if (bodyIdx < 0) return eventCardHtml + html;

        var closeIdx = html.IndexOf('>', bodyIdx);
        if (closeIdx < 0) return eventCardHtml + html;

        return html.Insert(closeIdx + 1, eventCardHtml);
    }

    /// <summary>Handles quickmail: pseudo-URIs from the event card buttons.</summary>
    private void HandleQuickMailUri(string uri)
    {
        if (uri.StartsWith("quickmail:ics-accept", StringComparison.OrdinalIgnoreCase))
            _vm.AcceptInviteCommand.Execute(null);
        else if (uri.StartsWith("quickmail:ics-tentative", StringComparison.OrdinalIgnoreCase))
            _vm.TentativeInviteCommand.Execute(null);
        else if (uri.StartsWith("quickmail:ics-decline", StringComparison.OrdinalIgnoreCase))
            _vm.DeclineInviteCommand.Execute(null);
    }

    private static void OpenExternal(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Log($"OpenExternal {uri}", ex);
        }
    }

    // When the WebView2 host receives WPF keyboard focus (e.g. Tab from a header field),
    // push focus into the HTML document body so the user can read/navigate content.
    private async void MessageBody_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_webViewReady)
        {
            try
            {
                await TryFocusMessageBodyDocumentAsync(MessageBodyFocusLabel(_vm.MessageDetail?.Subject));
            }
            catch (Exception ex)
            {
                LogService.Log("MessageBody_GotKeyboardFocus", ex);
            }
        }
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

    // Alt+Enter on a selected attachment opens Attachment Properties directly.
    private void ReadingPaneAttachmentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Return && e.KeyboardDevice.Modifiers == ModifierKeys.Alt
            && ReadingPaneAttachmentList.SelectedItem is AttachmentModel attachment)
        {
            var (title, sections) = AttachmentPropertiesBuilder.Build(attachment);
            var win = new PropertiesWindow(new PropertiesViewModel(title, sections)) { Owner = this };
            win.ShowDialog();
            e.Handled = true;
        }
    }

    // Close the reading pane safely: cancel any in-flight render/focus chain and
    // stop WebView2 navigation before flipping Visibility so the IsVisible=false
    // COM call doesn't race with a busy renderer (was a "Not Responding" stall).
    private void CloseReadingPane()
    {
        Interlocked.Increment(ref _messageBodyRenderVersion);

        if (_webViewReady)
        {
            try { MessageBody.CoreWebView2.Stop(); }
            catch (Exception ex) { LogService.Log("CloseReadingPane/Stop", ex); }
        }

        _vm.IsMessageOpen = false;
        _vm.MessageDetail = null;
        ReturnFocusToMessageList();
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

    // Records the active pane when the window loses focus (Alt+Tab away, another window
    // comes foreground, etc.) so we can restore it when the window is re-activated.
    private void OnDeactivated(object? sender, EventArgs e)
    {
        _paneIndexBeforeDeactivation = GetFocusedPaneIndex();
        LogService.Debug($"[FOCUS] Deactivated pane={_paneIndexBeforeDeactivation}");
    }

    // Restores keyboard focus to the message list / tree when the window re-activates
    // after Alt+Tab (or any other reason the window regains foreground).
    //
    // WPF's built-in focus restoration silently fails for virtualised list containers
    // (ListViewItem, TreeViewItem) because the container may have been recycled while
    // the window was inactive.  WPF leaves focus on the Window itself rather than on
    // the specific item row.  We compensate by explicitly calling ReturnFocusToMessageList,
    // which is already smart about view-mode and handles empty lists gracefully.
    //
    // For the reading pane (pane 4 / WebView2): WPF cannot focus into a WebView2 control
    // by itself, so we also restore to the message list in that case.
    //
    // All other panes (toolbar, account list, folder list, search, status bar) are
    // plain WPF controls whose focus WPF restores correctly on its own — we leave
    // those untouched.
    private void OnActivated(object? sender, EventArgs e)
    {
        LogService.Debug($"[FOCUS] Activated lastPane={_paneIndexBeforeDeactivation} {FocusInfo()}");
        if (_paneIndexBeforeDeactivation == 3 || _paneIndexBeforeDeactivation == 4)
            Dispatcher.InvokeAsync(ReturnFocusToMessageList, DispatcherPriority.Input);
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
        !IsMenuOrToolbarFocused() &&
        (MessageList.IsKeyboardFocusWithin ||
        ConversationTree.IsKeyboardFocusWithin ||
        SenderGroupTree.IsKeyboardFocusWithin ||
        ToGroupTree.IsKeyboardFocusWithin ||
        MessageBody.IsKeyboardFocusWithin ||
        ViewModeButton.IsKeyboardFocusWithin);

    // Focuses the first status bar region and announces it to screen readers.
    // The TextBox child exposes ControlType.Edit + ValuePattern, so screen readers
    // read its value natively on focus. The Announce call provides a redundant
    // spoken summary for screen readers with slow UIA update cycles.
    private void FocusStatusBar()
    {
        FocusStatusBarRegion(1);
        AccessibilityHelper.Announce(this, $"Status bar: {_vm.StatusText}",
            category: AnnouncementCategory.Result);
    }

    // ── Status bar Left/Right arrow navigation ───────────────────────────────

    /// <summary>
    /// Returns the 1-based index of the status bar region that currently has keyboard focus,
    /// or 0 if focus is not in the status bar.
    /// </summary>
    private int GetFocusedStatusBarRegion()
    {
        if (!MainStatusBar.IsKeyboardFocusWithin) return 0;

        if (StatusTextBox.IsKeyboardFocused)          return 1;
        if (ConnectionStatusTextBox.IsKeyboardFocused) return 2;
        if (RulesStatusButton.IsKeyboardFocused)       return 3;
        if (StatusProgressBar.IsKeyboardFocused)       return 4;

        return 0;
    }

    /// <summary>
    /// Moves keyboard focus to the specified status bar region (1-based index).
    /// Region 4 (ProgressBar) is skipped when not visible.
    /// </summary>
    private void FocusStatusBarRegion(int region)
    {
        switch (region)
        {
            case 1:
                StatusTextBox.Focus();
                break;
            case 2:
                ConnectionStatusTextBox.Focus();
                break;
            case 3:
                RulesStatusButton.Focus();
                break;
            case 4:
                if (StatusProgressItem.Visibility == Visibility.Visible)
                    StatusProgressBar.Focus();
                else
                    FocusStatusBarRegion(1); // fallback: wrap to first
                break;
        }
    }

    /// <summary>
    /// Moves focus to the next (forward=true) or previous (forward=false) visible
    /// status bar region. Wraps around at the boundaries.
    /// </summary>
    private void NavigateStatusBar(bool forward)
    {
        int current = GetFocusedStatusBarRegion();
        if (current == 0) { FocusStatusBarRegion(1); return; }

        // Build the ordered list of visible region indices.
        var visible = new List<int> { 1, 2, 3 };
        if (StatusProgressItem.Visibility == Visibility.Visible)
            visible.Add(4);

        int pos = visible.IndexOf(current);
        if (pos < 0) { FocusStatusBarRegion(visible[0]); return; }

        int nextPos = forward
            ? (pos + 1) % visible.Count
            : (pos - 1 + visible.Count) % visible.Count;

        FocusStatusBarRegion(visible[nextPos]);
    }

    /// <summary>
    /// Handles Left/Right arrow navigation within the status bar.
    /// Tab and Escape are allowed to bubble so they exit the status bar normally.
    /// </summary>
    private void StatusBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Right)
        {
            NavigateStatusBar(forward: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            NavigateStatusBar(forward: false);
            e.Handled = true;
        }
        // Tab, Shift+Tab, Escape, F6, Shift+F6 all bubble to OnWindowKeyDown naturally.
    }

    // ── F6 pane-cycling helpers ──────────────────────────────────────────────

    private void ToggleCustomAnnouncements()
    {
        var cfg = _configService.Load();
        cfg.CustomAnnouncements = !cfg.CustomAnnouncements;
        _configService.Save(cfg);
        var msg = cfg.CustomAnnouncements ? "Custom announcements on." : "Custom announcements off.";
        AccessibilityHelper.Announce(this, msg, interrupt: true, category: AnnouncementCategory.Result, force: true);
    }

    private void ShowTutorial()
    {
        if (_tutorialVm?.IsActive == true) return;

        _tutorialVm = new TutorialViewModel();
        _tutorialVm.TutorialCompleted += OnTutorialCompleted;
        _tutorialVm.TutorialCancelled += OnTutorialCancelled;
        TutorialOverlayControl.SetViewModel(_tutorialVm);
        _tutorialVm.Start();
    }

    private void OnTutorialCompleted()
    {
        CleanupTutorial();
        var cfg = _configService.Load();
        cfg.TutorialCompleted = true;
        _configService.Save(cfg);
        AccessibilityHelper.Announce(this, "Tutorial complete. You can replay it anytime from the Help menu.",
            interrupt: true, category: AnnouncementCategory.Result);
    }

    private void OnTutorialCancelled()
    {
        CleanupTutorial();
        AccessibilityHelper.Announce(this, "Tutorial cancelled.",
            interrupt: true, category: AnnouncementCategory.Result);
    }

    private void CleanupTutorial()
    {
        if (_tutorialVm != null)
        {
            _tutorialVm.TutorialCompleted -= OnTutorialCompleted;
            _tutorialVm.TutorialCancelled -= OnTutorialCancelled;
        }
        _tutorialVm = null;
    }

    private void MenuKeyboardTutorial_Click(object sender, RoutedEventArgs e)
    {
        ShowTutorial();
    }

    private void ShowAboutDialog()
    {
        var dlg = new AboutDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        ShowAboutDialog();
    }

    /// <summary>
    /// Formats a key + modifiers combination into a human-readable string
    /// like "Ctrl+Shift+P" or "F6".
    /// </summary>
    private static string FormatKeyGesture(Key key, ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Returns true for bare modifier keys that shouldn't trigger
    /// a "wrong key" announcement in the tutorial.</summary>
    private static bool IsModifierKey(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System => true,
        _ => false
    };

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
        if (SearchBox.IsKeyboardFocusWithin)    return 6;
        if (MessageList.IsKeyboardFocusWithin || ConversationTree.IsKeyboardFocusWithin || SenderGroupTree.IsKeyboardFocusWithin || ToGroupTree.IsKeyboardFocusWithin) return 3;
        if (TabStrip.IsKeyboardFocusWithin)     return 7; // between message list and reading pane
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
            case 6: SearchBox.Focus(); break;
            case 3: FocusActiveMessagePanel(); break;
            case 7: TabStrip.Focus(); break;
            case 4:
                if (_vm.IsMessageOpen && _webViewReady)
                {
                    await FocusMessageBodyAsync(_messageBodyRenderVersion, _vm.MessageDetail?.Subject);
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
    // The tab strip (index 7) is included when visible, between message list and reading pane.
    // The reading pane (index 4) is included only when a message is open.
    // StatusBar (index 5) is always the last stop before wrapping back to Toolbar.
    private async Task CycleFocusAsync(bool forward)
    {
        // Build the ordered list of active pane indices.
        var panes = new System.Collections.Generic.List<int> { 0, 1, 2 };
        if (_vm.IsSearchActive) panes.Add(6); // search box between folder tree and message list
        panes.Add(3);
        if (_vm.ShowTabStrip) panes.Add(7);   // tab strip between message list and reading pane
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
        => _convTreeController?.FocusFirstItem();

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

    private void ConversationTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _convTreeController?.OnGotKeyboardFocus(sender, e);

    private void ConversationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _convTreeController?.OnSelectedItemChanged(sender, e);

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

        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenSearch();
            e.Handled = true;
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
                e.Handled = true;
                await OpenMessageFromListAsync(msg);
            }
            else if (ConversationTree.SelectedItem is ConversationGroup group)
            {
                e.Handled = true;
                if (group.Messages.Count == 1)
                {
                    var singleMsg = group.Messages[0];
                    _vm.SelectedMessage = singleMsg;
                    await OpenMessageFromListAsync(singleMsg);
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
                var groupIdx    = parentGroup != null ? _vm.Conversations.IndexOf(parentGroup) : 0;
                _vm.SelectedMessage = toDelete;
                if (parentGroup != null && parentGroup.Messages.Count > 1)
                {
                    int msgIdx = 0;
                    for (int i = 0; i < parentGroup.Messages.Count; i++)
                        if (parentGroup.Messages[i] == toDelete) { msgIdx = i; break; }
                    LandOnConversationMessageAfterRebuild(parentGroup.NormalizedSubject, msgIdx, groupIdx);
                }
                else
                    LandOnConversationAfterRebuild(groupIdx);
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
        => _senderTreeController?.FocusFirstItem();

    private void FocusToGroupTreeFirstItem()
        => _toTreeController?.FocusFirstItem();

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

    // ── Group-boundary navigation (< / >) ────────────────────────────────────

    /// <summary>Returns true when the active view mode uses grouped trees.</summary>
    private bool IsGroupedViewActive() =>
        _vm.IsConversationsView || _vm.IsFromView || _vm.IsToView;

    private void JumpToFirstMessageInGroup() => JumpToGroupBoundary(first: true);
    private void JumpToLastMessageInGroup()  => JumpToGroupBoundary(first: false);

    /// <summary>
    /// Jumps to the first (newest, <paramref name="first"/>=true) or last (oldest,
    /// <paramref name="first"/>=false) message in whichever group the current selection
    /// belongs to.  Works whether a group header or a child message is selected, and
    /// expands a collapsed group automatically.
    /// </summary>
    private void JumpToGroupBoundary(bool first)
    {
        if (_vm.IsConversationsView)
        {
            var group = ConversationTree.SelectedItem switch
            {
                ConversationGroup g    => g,
                MailMessageSummary msg => _vm.Conversations.FirstOrDefault(g => g.Messages.Contains(msg)),
                _                      => null
            };
            if (group?.Messages.Count > 0)
                FocusConversationMessage(group, first ? 0 : group.Messages.Count - 1);
        }
        else if (_vm.IsFromView)
        {
            var group = SenderGroupTree.SelectedItem switch
            {
                SenderGroup g          => g,
                MailMessageSummary msg => _vm.SenderGroups.FirstOrDefault(g => g.Messages.Contains(msg)),
                _                      => null
            };
            if (group?.Messages.Count > 0)
                FocusSenderGroupMessage(group, first ? 0 : group.Messages.Count - 1);
        }
        else if (_vm.IsToView)
        {
            var group = ToGroupTree.SelectedItem switch
            {
                SenderGroup g          => g,
                MailMessageSummary msg => _vm.ToGroups.FirstOrDefault(g => g.Messages.Contains(msg)),
                _                      => null
            };
            if (group?.Messages.Count > 0)
                FocusToGroupMessage(group, first ? 0 : group.Messages.Count - 1);
        }
    }

    // ── Mark as Read ─────────────────────────────────────────────────────────

    private async Task MarkReadCommand()
    {
        // Priority: group header selected → mark whole group.
        // Then: individual message selected → mark that message.
        // Then: folder tree focused → mark all loaded messages in the current folder/view.
        IReadOnlyList<MailMessageSummary>? targets = null;

        if (_vm.IsConversationsView && ConversationTree.SelectedItem is ConversationGroup convGroup)
            targets = convGroup.Messages;
        else if (_vm.IsFromView && SenderGroupTree.SelectedItem is SenderGroup fromGroup)
            targets = fromGroup.Messages;
        else if (_vm.IsToView && ToGroupTree.SelectedItem is SenderGroup toGroup)
            targets = toGroup.Messages;
        else if (_vm.SelectedMessage != null)
            targets = [_vm.SelectedMessage];
        else if (FolderList.IsKeyboardFocusWithin && _vm.SelectedFolder != null)
            targets = _vm.LoadedMessages;

        if (targets != null)
            await _vm.MarkMessagesReadAsync(targets);
    }

    // ── Message-level focus helpers for grouped views ────────────────────────

    // Finds the ScrollViewer inside a TreeView so we can set the scroll offset directly.
    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(System.Windows.DependencyObject d)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (child is System.Windows.Controls.ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    // Scrolls a SenderGroup-based tree so that the message at msgIdx within targetGroup
    // is within the virtual viewport, ensuring its container is generated on the next layout
    // pass. Uses item-based scroll offsets (CanContentScroll="True").
    private static void ScrollSenderGroupMessageIntoView(
        TreeView tree,
        IEnumerable<SenderGroup> groups,
        SenderGroup targetGroup,
        int msgIdx)
    {
        int offset = 0;
        foreach (var g in groups)
        {
            offset++; // group header
            if (ReferenceEquals(g, targetGroup)) { offset += msgIdx; break; }
            if (g.IsExpanded) offset += g.Messages.Count;
        }
        FindScrollViewer(tree)?.ScrollToVerticalOffset(offset);
    }

    // Same for ConversationGroup-based trees.
    private static void ScrollConversationMessageIntoView(
        TreeView tree,
        IEnumerable<ConversationGroup> groups,
        ConversationGroup targetGroup,
        int msgIdx)
    {
        int offset = 0;
        foreach (var g in groups)
        {
            offset++;
            if (ReferenceEquals(g, targetGroup)) { offset += msgIdx; break; }
            if (g.IsExpanded) offset += g.Messages.Count;
        }
        FindScrollViewer(tree)?.ScrollToVerticalOffset(offset);
    }

    private void FocusSenderGroupMessage(SenderGroup group, int msgIdx, bool isRetry = false)
    {
        var target   = group.Messages[msgIdx];
        var groupTvi = SenderGroupTree.ItemContainerGenerator.ContainerFromItem(group) as TreeViewItem;
        if (groupTvi == null)
        {
            if (!isRetry)
                Dispatcher.InvokeAsync(() => FocusSenderGroupMessage(group, msgIdx, true), DispatcherPriority.Background);
            return;
        }
        if (!groupTvi.IsExpanded) groupTvi.IsExpanded = true;
        var msgTvi = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        if (msgTvi != null) { msgTvi.IsSelected = true; msgTvi.Focus(); return; }
        if (!isRetry)
        {
            // After a full rebuild the TreeView scroll resets to 0. Scroll the target into
            // the viewport now; the resulting layout pass (Render priority) generates its
            // container before the Background-priority retry runs.
            ScrollSenderGroupMessageIntoView(SenderGroupTree, _vm.SenderGroups, group, msgIdx);
            Dispatcher.InvokeAsync(() =>
            {
                var t2 = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                if (t2 != null) { t2.IsSelected = true; t2.Focus(); }
                else             { groupTvi.IsSelected = true; groupTvi.Focus(); }
            }, DispatcherPriority.Background);
        }
        else { groupTvi.IsSelected = true; groupTvi.Focus(); }
    }

    private void LandOnSenderMessageAfterRebuild(string senderKey, int msgIdx, int fallbackGroupIdx)
    {
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.SenderGroups)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            Dispatcher.InvokeAsync(() =>
            {
                var group = _vm.SenderGroups.FirstOrDefault(g =>
                    string.Equals(g.SenderKey, senderKey, StringComparison.OrdinalIgnoreCase));
                if (group != null && group.Messages.Count > 0)
                {
                    FocusSenderGroupMessage(group, Math.Min(msgIdx, group.Messages.Count - 1));
                }
                else
                {
                    if (_vm.SenderGroups.Count == 0) return;
                    var idx      = Math.Max(0, Math.Min(fallbackGroupIdx, _vm.SenderGroups.Count - 1));
                    var fallback = _vm.SenderGroups[idx];
                    if (SenderGroupTree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi)
                    { tvi.IsSelected = true; tvi.Focus(); }
                    else
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (SenderGroupTree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi2)
                            { tvi2.IsSelected = true; tvi2.Focus(); }
                        }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    private void FocusToGroupMessage(SenderGroup group, int msgIdx, bool isRetry = false)
    {
        var target   = group.Messages[msgIdx];
        var groupTvi = ToGroupTree.ItemContainerGenerator.ContainerFromItem(group) as TreeViewItem;
        if (groupTvi == null)
        {
            if (!isRetry)
                Dispatcher.InvokeAsync(() => FocusToGroupMessage(group, msgIdx, true), DispatcherPriority.Background);
            return;
        }
        if (!groupTvi.IsExpanded) groupTvi.IsExpanded = true;
        var msgTvi = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        if (msgTvi != null) { msgTvi.IsSelected = true; msgTvi.Focus(); return; }
        if (!isRetry)
        {
            ScrollSenderGroupMessageIntoView(ToGroupTree, _vm.ToGroups, group, msgIdx);
            Dispatcher.InvokeAsync(() =>
            {
                var t2 = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                if (t2 != null) { t2.IsSelected = true; t2.Focus(); }
                else             { groupTvi.IsSelected = true; groupTvi.Focus(); }
            }, DispatcherPriority.Background);
        }
        else { groupTvi.IsSelected = true; groupTvi.Focus(); }
    }

    private void LandOnToMessageAfterRebuild(string senderKey, int msgIdx, int fallbackGroupIdx)
    {
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.ToGroups)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            Dispatcher.InvokeAsync(() =>
            {
                var group = _vm.ToGroups.FirstOrDefault(g =>
                    string.Equals(g.SenderKey, senderKey, StringComparison.OrdinalIgnoreCase));
                if (group != null && group.Messages.Count > 0)
                {
                    FocusToGroupMessage(group, Math.Min(msgIdx, group.Messages.Count - 1));
                }
                else
                {
                    if (_vm.ToGroups.Count == 0) return;
                    var idx      = Math.Max(0, Math.Min(fallbackGroupIdx, _vm.ToGroups.Count - 1));
                    var fallback = _vm.ToGroups[idx];
                    if (ToGroupTree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi)
                    { tvi.IsSelected = true; tvi.Focus(); }
                    else
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (ToGroupTree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi2)
                            { tvi2.IsSelected = true; tvi2.Focus(); }
                        }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    private void FocusConversationMessage(ConversationGroup group, int msgIdx, bool isRetry = false)
    {
        var target   = group.Messages[msgIdx];
        var groupTvi = ConversationTree.ItemContainerGenerator.ContainerFromItem(group) as TreeViewItem;
        if (groupTvi == null)
        {
            if (!isRetry)
                Dispatcher.InvokeAsync(() => FocusConversationMessage(group, msgIdx, true), DispatcherPriority.Background);
            return;
        }
        if (!groupTvi.IsExpanded) groupTvi.IsExpanded = true;
        var msgTvi = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
        if (msgTvi != null) { msgTvi.IsSelected = true; msgTvi.Focus(); return; }
        if (!isRetry)
        {
            ScrollConversationMessageIntoView(ConversationTree, _vm.Conversations, group, msgIdx);
            Dispatcher.InvokeAsync(() =>
            {
                var t2 = groupTvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                if (t2 != null) { t2.IsSelected = true; t2.Focus(); }
                else             { groupTvi.IsSelected = true; groupTvi.Focus(); }
            }, DispatcherPriority.Background);
        }
        else { groupTvi.IsSelected = true; groupTvi.Focus(); }
    }

    private void LandOnConversationMessageAfterRebuild(string normalizedSubject, int msgIdx, int fallbackGroupIdx)
    {
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(_vm.Conversations)) return;
            _vm.PropertyChanged -= OnPropertyChanged;
            Dispatcher.InvokeAsync(() =>
            {
                var group = _vm.Conversations.FirstOrDefault(g =>
                    string.Equals(g.NormalizedSubject, normalizedSubject, StringComparison.OrdinalIgnoreCase));
                if (group != null && group.Messages.Count > 0)
                {
                    FocusConversationMessage(group, Math.Min(msgIdx, group.Messages.Count - 1));
                }
                else
                {
                    if (_vm.Conversations.Count == 0) return;
                    var idx      = Math.Max(0, Math.Min(fallbackGroupIdx, _vm.Conversations.Count - 1));
                    var fallback = _vm.Conversations[idx];
                    if (ConversationTree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi)
                    { tvi.IsSelected = true; tvi.Focus(); }
                    else
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (ConversationTree.ItemContainerGenerator.ContainerFromItem(fallback) is TreeViewItem tvi2)
                            { tvi2.IsSelected = true; tvi2.Focus(); }
                        }, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Input);
        }
        _vm.PropertyChanged += OnPropertyChanged;
    }

    // ── SenderGroup tree event handlers ─────────────────────────────────────

    private void SenderGroupTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _senderTreeController?.OnGotKeyboardFocus(sender, e);

    private void SenderGroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _senderTreeController?.OnSelectedItemChanged(sender, e);

    private async void SenderGroupTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogService.Debug($"[FOCUS] SenderTree KeyDown key={e.Key} mod={Keyboard.Modifiers} {FocusInfo()} items={SenderGroupTree.Items.Count} selected={SenderGroupTree.SelectedItem?.GetType().Name ?? "null"}");
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            SyncFolderTreeSelection(true);
            return;
        }

        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenSearch();
            e.Handled = true;
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
                await OpenMessageFromListAsync(msg);
            }
            else if (SenderGroupTree.SelectedItem is SenderGroup group)
            {
                e.Handled = true;
                if (group.Messages.Count == 1)
                {
                    var singleMsg = group.Messages[0];
                    _vm.SelectedMessage = singleMsg;
                    await OpenMessageFromListAsync(singleMsg);
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
                var groupIdx    = parentGroup != null ? _vm.SenderGroups.IndexOf(parentGroup) : 0;
                _vm.SelectedMessage = toDelete;
                if (parentGroup != null && parentGroup.Messages.Count > 1)
                {
                    int msgIdx = 0;
                    for (int i = 0; i < parentGroup.Messages.Count; i++)
                        if (parentGroup.Messages[i] == toDelete) { msgIdx = i; break; }
                    LandOnSenderMessageAfterRebuild(parentGroup.SenderKey, msgIdx, groupIdx);
                }
                else
                    LandOnSenderGroupAfterRebuild(groupIdx);
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
        => _senderTreeController?.OnPreviewTextInput(sender, e);

    private void SenderGroupTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => _senderTreeController?.OnPreviewMouseRightButtonDown(sender, e);

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

    // ── SenderGroup context menu handlers ────────────────────────────────────

    private async void SenderGroupContextMenu_MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SenderGroupTree.SelectedItem is not SenderGroup group || group.Messages.Count == 0) return;
        if (_vm.CachedFolders.Count == 0) return;

        var targetIdx = _vm.SenderGroups.IndexOf(group);
        var picker = BuildMessageFolderPicker(group.Messages, "Move to Folder");
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.MoveSelectedMessagesToFolderAsync(group.Messages, picker.SelectedFolder);
        LandOnSenderGroupAfterRebuild(targetIdx);
    }

    private async void SenderGroupContextMenu_CopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SenderGroupTree.SelectedItem is not SenderGroup group || group.Messages.Count == 0) return;
        if (_vm.CachedFolders.Count == 0) return;

        var picker = BuildMessageFolderPicker(group.Messages, "Copy to Folder");
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopySelectedMessagesToFolderAsync(group.Messages, picker.SelectedFolder);
    }

    // ── ToGroup tree event handlers ──────────────────────────────────────────

    private void ToGroupTree_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _toTreeController?.OnGotKeyboardFocus(sender, e);

    private void ToGroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _toTreeController?.OnSelectedItemChanged(sender, e);

    private async void ToGroupTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        LogService.Debug($"[FOCUS] ToTree KeyDown key={e.Key} mod={Keyboard.Modifiers} {FocusInfo()} items={ToGroupTree.Items.Count} selected={ToGroupTree.SelectedItem?.GetType().Name ?? "null"}");
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            SyncFolderTreeSelection(true);
            return;
        }

        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenSearch();
            e.Handled = true;
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
                await OpenMessageFromListAsync(msg);
            }
            else if (ToGroupTree.SelectedItem is SenderGroup group)
            {
                e.Handled = true;
                if (group.Messages.Count == 1)
                {
                    var singleMsg = group.Messages[0];
                    _vm.SelectedMessage = singleMsg;
                    await OpenMessageFromListAsync(singleMsg);
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
                var groupIdx    = parentGroup != null ? _vm.ToGroups.IndexOf(parentGroup) : 0;
                _vm.SelectedMessage = toDelete;
                if (parentGroup != null && parentGroup.Messages.Count > 1)
                {
                    int msgIdx = 0;
                    for (int i = 0; i < parentGroup.Messages.Count; i++)
                        if (parentGroup.Messages[i] == toDelete) { msgIdx = i; break; }
                    LandOnToMessageAfterRebuild(parentGroup.SenderKey, msgIdx, groupIdx);
                }
                else
                    LandOnToGroupAfterRebuild(groupIdx);
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
        => _toTreeController?.OnPreviewTextInput(sender, e);

    private void ToGroupTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => _toTreeController?.OnPreviewMouseRightButtonDown(sender, e);

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

    // ── ToGroup context menu handlers ─────────────────────────────────────────

    private async void ToGroupContextMenu_MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ToGroupTree.SelectedItem is not SenderGroup group || group.Messages.Count == 0) return;
        if (_vm.CachedFolders.Count == 0) return;

        var targetIdx = _vm.ToGroups.IndexOf(group);
        var picker = BuildMessageFolderPicker(group.Messages, "Move to Folder");
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.MoveSelectedMessagesToFolderAsync(group.Messages, picker.SelectedFolder);
        LandOnToGroupAfterRebuild(targetIdx);
    }

    private async void ToGroupContextMenu_CopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ToGroupTree.SelectedItem is not SenderGroup group || group.Messages.Count == 0) return;
        if (_vm.CachedFolders.Count == 0) return;

        var picker = BuildMessageFolderPicker(group.Messages, "Copy to Folder");
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopySelectedMessagesToFolderAsync(group.Messages, picker.SelectedFolder);
    }

    private void OpenComposeWindow(ComposeModel composeModel)
    {
        var composeVm = new ComposeViewModel(_smtp, _accountService, _credentials, _imap, _templateService);
        composeVm.Seed(composeModel);
        var window = new ComposeWindow(composeVm, _contactService, _templateService, _configService) { Owner = this };
        composeVm.CloseRequested += window.Close;
        window.Show();
    }

    private void OpenAccountManager()
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap, _oauth, _localStore, _configService, _featureGate);
        var dialog = new AccountManagerDialog(accountVm) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.RefreshAccountList();
    }

    private void OpenAccountManagerForAccount(AccountModel account)
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap, _oauth, _localStore, _configService, _featureGate);
        var dialog    = new AccountManagerDialog(accountVm) { Owner = this };
        // Pre-select the account in the manager
        accountVm.SelectedAccount = accountVm.Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (dialog.ShowDialog() == true)
            _vm.RefreshAccountList();
    }

    // ── Tab & Window Management handlers ────────────────────────────────────────

    /// <summary>
    /// Routes message opening based on the configured MessageOpenMode.
    /// Drafts always open in a compose window regardless of mode.
    /// </summary>
    private async Task OpenMessageFromListAsync(MailMessageSummary summary)
    {
        if (_vm.IsSelectedFolderDrafts)
        {
            await _vm.OpenDraftCommand.ExecuteAsync(null);
            return;
        }

        switch (_vm.MessageOpenMode)
        {
            case MessageOpenMode.Tab:
                _vm.OpenMessageTab(summary);
                break;

            case MessageOpenMode.Window:
                OpenMessageInNewWindow(summary);
                break;

            default: // ReadingPane
                await _vm.SelectMessageCommand.ExecuteAsync(summary);
                if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                    await ShowMessageBodyAsync(_vm.MessageDetail);
                break;
        }
    }

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: TabSessionViewModel tab }) return;
        _tabStripCloseInProgress = true;
        _vm.CloseTab(tab);
    }

    private void TabStrip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Selection changes from two sources:
        // 1. User clicks a tab → ListBox updates SelectedItem → binding updates _vm.ActiveTab
        // 2. VM updates _vm.ActiveTab (Ctrl+Tab etc) → binding updates ListBox.SelectedItem
        // In both cases the binding fires OnActiveTabChangedAsync via PropertyChanged.
        // Nothing extra needed here; the handler is wired for drag/drop future use.
    }

    private async Task OnActiveTabChangedAsync()
    {
        var version = Interlocked.Increment(ref _tabChangedVersion);

        // Capture and clear before any await so concurrent calls see a clean state.
        var closeInProgress = _tabStripCloseInProgress;
        _tabStripCloseInProgress = false;

        var tab = _vm.ActiveTab;
        if (tab == null)
        {
            _vm.IsMessageOpen = false;
            return;
        }

        // Message-list tab: show the list, hide reading pane, done.
        if (tab is MessageListTabViewModel)
        {
            _vm.IsMessageOpen = false;
            _vm.MessageDetail = null;
            if (closeInProgress)
                await Dispatcher.InvokeAsync(FocusActiveTabStripItem, DispatcherPriority.Input);
            else
                FocusActiveMessagePanel();
            return;
        }

        if (tab is not MessageTabViewModel msgTab) return;

        // Scroll active tab into view in the strip (fire-and-forget).
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (TabStrip.SelectedItem != null)
                TabStrip.ScrollIntoView(TabStrip.SelectedItem);
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        // Load via SelectMessageCommand (handles SelectedMessage, IsRead, cache, etc.)
        await _vm.SelectMessageCommand.ExecuteAsync(msgTab.Summary);
        if (version != _tabChangedVersion) return;

        // Use MessageDetail directly rather than IsMessageOpen: IsMessageOpen is set by
        // SelectMessageAsync and reflects MessageOpenMode, but tabs always need the reading
        // pane visible regardless of the mode (e.g. a tab opened via Move to Main Window
        // while in Window mode). Force it true whenever we have a loaded detail.
        if (_vm.MessageDetail != null)
        {
            _vm.IsMessageOpen = true;
            msgTab.Detail   = _vm.MessageDetail;
            msgTab.IsLoaded = true;
            await ShowMessageBodyAsync(_vm.MessageDetail);
        }
        if (version != _tabChangedVersion) return;

        // When triggered by the close button, return focus to the tab strip rather than
        // leaving it in the reading pane. This await runs after ShowMessageBodyAsync has
        // already dispatched its own Input-priority focus calls, so it wins.
        if (closeInProgress)
            await Dispatcher.InvokeAsync(FocusActiveTabStripItem, DispatcherPriority.Input);
    }

    private void OpenTabList()
    {
        var tabListWindow = new TabListWindow(_vm);
        tabListWindow.Owner = this;

        // Position the overlay near the centre/top of the main window.
        tabListWindow.Left = Left + (ActualWidth  - tabListWindow.Width)  / 2;
        tabListWindow.Top  = Top  + (ActualHeight - 400) / 3;

        tabListWindow.ShowDialog();

        // If the user activated a tab, load its message.
        if (tabListWindow.DialogResult == true)
            _ = OnActiveTabChangedAsync();
    }

    private void OpenMessageInNewWindow(MailMessageSummary summary)
    {
        var winVm = new MessageWindowViewModel
        {
            OriginalSummary = summary,
            SelectedMessage = summary,
        };

        // Populate the surrounding message list so Prev/Next navigation works (issue 41).
        foreach (var msg in _vm.Messages)
            winVm.MessageList.Add(msg);

        // Pre-populate detail if already loaded in the reading pane (issue 48).
        if (_vm.SelectedMessage?.MessageId == summary.MessageId && _vm.MessageDetail != null)
            winVm.MessageDetail = _vm.MessageDetail;

        // The reading pane must not show while the message is in a separate window.
        _vm.IsMessageOpen = false;

        // Track that a message window is open so sync guards and commands work (issue 43).
        _vm.IsMessageOpenInWindow = true;

        // Capture the originating message list item for focus restoration on close (issue 46).
        var originatingIndex = _vm.Messages.IndexOf(summary);

        // Do NOT set Owner = this. Owned windows share the main window's HWND context,
        // which causes screen readers to keep browse mode active for the message window's
        // WebView2 even after the user alt-tabs back to the main window. As an independent
        // window, the AT cleanly exits browse mode when focus leaves the message window.
        var win = new MessageWindow(winVm, _imap, _localStore, _webViewEnvironment);

        // Wire mail action delegates so the window has full message operations.
        // Each delegate syncs MainViewModel selection to the window's current message
        // before invoking the command, so navigation (Prev/Next) in the window doesn't
        // cause actions to operate on the wrong message.
        winVm.ReplyAction = () =>
        {
            _vm.SelectedMessage = winVm.SelectedMessage;
            if (winVm.MessageDetail != null) _vm.MessageDetail = winVm.MessageDetail;
            _vm.ReplyCommand.Execute(null);
        };
        winVm.ReplyAllAction = () =>
        {
            _vm.SelectedMessage = winVm.SelectedMessage;
            if (winVm.MessageDetail != null) _vm.MessageDetail = winVm.MessageDetail;
            _vm.ReplyAllCommand.Execute(null);
        };
        winVm.ForwardAction = () =>
        {
            _vm.SelectedMessage = winVm.SelectedMessage;
            if (winVm.MessageDetail != null) _vm.MessageDetail = winVm.MessageDetail;
            _vm.ForwardCommand.Execute(null);
        };
        winVm.DeleteAction = () =>
        {
            _vm.SelectedMessage = winVm.SelectedMessage;
            _ = _vm.DeleteMessageCommand.ExecuteAsync(null);
            win.Close();
        };
        winVm.MarkReadAction = () =>
        {
            if (winVm.SelectedMessage != null)
                _ = _vm.MarkMessagesReadAsync([winVm.SelectedMessage]);
        };
        winVm.GrabAddressesAction = () =>
        {
            if (winVm.MessageDetail is not { } detail) return;
            var list = new InternetAddressList();
            AddressParser.AddAddresses(list, detail.From);
            AddressParser.AddAddresses(list, detail.To);
            AddressParser.AddAddresses(list, detail.Cc);
            var addresses = list.OfType<MailboxAddress>()
                .Select(a => (Name: a.Name ?? string.Empty, a.Address))
                .DistinctBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (addresses.Count == 0) return;
            new GrabAddressesDialog(addresses, _contactService) { Owner = win }.ShowDialog();
        };

        win.MoveToMainWindowRequested += (_, vm) =>
        {
            if (vm.OriginalSummary != null)
                _vm.OpenMessageTab(vm.OriginalSummary);
        };
        win.Closed += (_, _) =>
        {
            _vm.IsMessageOpenInWindow =
                Application.Current.Windows.OfType<MessageWindow>().Any();

            // Restore focus to the originating message list item (issue 46).
            Dispatcher.InvokeAsync(() =>
            {
                FocusActiveMessagePanel();
                if (originatingIndex >= 0 && originatingIndex < MessageList.Items.Count)
                    MessageList.ScrollIntoView(MessageList.Items[originatingIndex]);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };

        // Offset from previous windows so they don't stack exactly.
        var offset = Application.Current.Windows.OfType<MessageWindow>().Count() * 24;
        win.Left = Left + 60 + offset;
        win.Top  = Top  + 40 + offset;

        win.Show();
    }

    private void PromoteTabToWindow(MessageTabViewModel tab)
    {
        _vm.CloseTab(tab);
        OpenMessageInNewWindow(tab.Summary);
    }

    // ── Tab strip keyboard navigation (issue 52 Bug 1) ─────────────────────────

    // True while the user is arrowing through the tab strip without activating a tab.
    // Prevents PropertyChanged on ActiveTab from loading the message body during navigation.
    private bool _tabStripArrowNavInProgress;

    // True when a tab was closed via its ✕ button. Causes OnActiveTabChangedAsync to
    // return focus to the tab strip instead of moving it into the reading pane or message list.
    private bool _tabStripCloseInProgress;

    // Incremented at the start of every OnActiveTabChangedAsync invocation so that
    // concurrent calls can detect they've been superseded and bail out after each await.
    private int _tabChangedVersion;

    private void FocusActiveTabStripItem()
    {
        var idx = TabStrip.SelectedIndex;
        if (idx < 0 || idx >= TabStrip.Items.Count) return;
        if (TabStrip.ItemContainerGenerator.ContainerFromIndex(idx) is ListBoxItem item)
        {
            TabStrip.ScrollIntoView(TabStrip.Items[idx]);
            item.Focus();
        }
    }

    private void TabStrip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Left:
                _tabStripArrowNavInProgress = true;
                SelectAdjacentTab(-1);
                e.Handled = true;
                break;
            case Key.Right:
                _tabStripArrowNavInProgress = true;
                SelectAdjacentTab(+1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                _tabStripArrowNavInProgress = true;
                e.Handled = true;
                break;
            case Key.Return:
            case Key.Space:
                _tabStripArrowNavInProgress = false;
                _ = OnActiveTabChangedAsync();
                e.Handled = true;
                break;
            default:
                _tabStripArrowNavInProgress = false;
                break;
        }
    }

    // Arrow navigation within the tab strip. Stops are: tab item, close button (if visible), next tab item, ...
    // PreviewKeyDown fires on the ListBox even when focus is inside a child (e.g. the close button),
    // so this method handles both cases.
    private void SelectAdjacentTab(int delta)
    {
        var focused = Keyboard.FocusedElement as UIElement;
        var isOnClose = focused is Button;

        // Find the ListBoxItem that currently owns focus.
        DependencyObject? walk = focused;
        ListBoxItem? currentItem = null;
        while (walk != null)
        {
            if (walk is ListBoxItem lbi) { currentItem = lbi; break; }
            walk = System.Windows.Media.VisualTreeHelper.GetParent(walk);
        }
        var currentIdx = currentItem != null
            ? TabStrip.ItemContainerGenerator.IndexFromContainer(currentItem)
            : TabStrip.SelectedIndex;
        if (currentIdx < 0) return;

        if (delta > 0) // Right
        {
            if (!isOnClose)
            {
                // Try the close button of the current tab first.
                var closeBtn = FindTabCloseButton(currentIdx);
                if (closeBtn != null) { closeBtn.Focus(); return; }
            }
            // Move to the next tab item.
            var nextIdx = currentIdx + 1;
            if (nextIdx >= TabStrip.Items.Count) return;
            TabStrip.SelectedIndex = nextIdx;
            (TabStrip.ItemContainerGenerator.ContainerFromIndex(nextIdx) as ListBoxItem)?.Focus();
        }
        else // Left
        {
            if (isOnClose)
            {
                // Back to the tab item itself.
                currentItem?.Focus();
                return;
            }
            // Move to the previous tab. Land on its close button if it has one.
            var prevIdx = currentIdx - 1;
            if (prevIdx < 0) return;
            TabStrip.SelectedIndex = prevIdx;
            var prevItem = TabStrip.ItemContainerGenerator.ContainerFromIndex(prevIdx) as ListBoxItem;
            var prevClose = prevItem != null ? FindTabCloseButton(prevIdx) : null;
            if (prevClose != null) prevClose.Focus();
            else prevItem?.Focus();
        }
    }

    private Button? FindTabCloseButton(int tabIndex)
    {
        if (TabStrip.ItemContainerGenerator.ContainerFromIndex(tabIndex) is not ListBoxItem item)
            return null;
        return FindVisualDescendant<Button>(item, b => b.IsVisible);
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match && (predicate == null || predicate(match))) return match;
            var found = FindVisualDescendant<T>(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private void TabStrip_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _tabStripArrowNavInProgress = false;

    private void TabStrip_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _tabStripArrowNavInProgress = false;

    private void TabStrip_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AccessibilityHelper.Announce(
            this,
            "Ctrl+Tab for next tab, Ctrl+Shift+Tab for previous, Ctrl+W to close.",
            category: AnnouncementCategory.Hint);
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
        var vm = new AddressBookViewModel(_contactService);

        // When the address book is opened standalone (not from a compose window) the
        // insert actions open a new compose window on demand.  All calls from a single
        // address-book session share the same compose window (lazily created on the
        // first insert) so picking a group inserts all members into one message.
        ComposeWindow? pending = null;
        ComposeWindow GetOrOpenCompose()
        {
            if (pending?.IsLoaded == true) return pending;
            var cvm = new ComposeViewModel(_smtp, _accountService, _credentials, _imap, _templateService);
            pending = new ComposeWindow(cvm, _contactService, _templateService, _configService) { Owner = this };
            cvm.CloseRequested += pending.Close;
            pending.Show();
            return pending;
        }
        vm.SetInsertActions(
            toAction:  c => GetOrOpenCompose().AddToAddress(c.DisplayName ?? string.Empty, c.EmailAddress),
            ccAction:  c => GetOrOpenCompose().AddCcAddress(c.DisplayName ?? string.Empty, c.EmailAddress),
            bccAction: c => GetOrOpenCompose().AddBccAddress(c.DisplayName ?? string.Empty, c.EmailAddress));

        var win = new AddressBookWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel(_configService, _registry);
        var dialog = new SettingsDialog(vm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var cfg = _configService.Load();
            _vm.ApplySettings(cfg);
            _registry.ApplyUserOverrides(cfg.CustomHotkeys);
        }
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

    private void MenuSearchFolders_Click(object sender, RoutedEventArgs e)
        => OpenFolderPicker();

    private async void MenuMoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;

        var picker = BuildMessageFolderPicker(messages, "Move to Folder");
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

        var picker = BuildMessageFolderPicker(messages, "Copy to Folder");
        if (picker.ShowDialog() != true || picker.SelectedFolder == null) return;

        await _vm.CopySelectedMessagesToFolderAsync(messages, picker.SelectedFolder);
    }

    private async void MessageContextMenu_MoveToFolder_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;

        var picker = BuildMessageFolderPicker(messages, "Move to Folder");
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
        => _convTreeController?.OnPreviewMouseRightButtonDown(sender, e);

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

    // ── View Manager ──────────────────────────────────────────────────────────────

    private void OpenViewManager(bool createMode = false)
    {
        var vmVm = new ViewManagerViewModel(
            viewService:    _viewService,
            configService:  _configService,
            registry:       _registry,
            savedViews:     _vm.SavedViews,
            currentFolder:  _vm.SelectedFolder,
            currentAccount: _vm.SelectedAccount,
            currentViewMode:  _vm.ViewMode,
            currentFilter:    _vm.ActiveFilter,
            currentSort:      _vm.ActiveSort,
            currentDayLimit:  _vm.ActiveDayLimit,
            isCreateMode:     createMode);

        var dialog = new ViewManagerWindow(vmVm, createMode) { Owner = this };
        dialog.ShowDialog();

        // Sync the main VM after the dialog is fully closed.  This is the ONLY safe
        // point to do UI-touching work (menu rebuild, folder-tree sync).
        //
        // Do NOT subscribe to vmVm.ViewsChanged and call UpdateSavedViews() from
        // inside that handler.  ViewsChanged fires while ShowDialog() is blocking
        // (e.g. the user clicks Delete or Save inside the dialog), which means
        // UpdateSavedViews() would mutate the main window's menu and folder tree
        // while the dialog's message loop is still running — a re-entrant COM
        // apartment violation that crashes the app (STATUS_CALLBACK_RETURNED_THREAD_APT_CHANGED).
        _vm.UpdateSavedViews();

        // Apply View was pressed: activate the view now that the dialog loop is dead.
        if (vmVm.ViewRequestedToApply is { } viewToApply)
            _vm.SelectViewCommand.Execute(viewToApply.Id.ToString());
    }

    private void OpenRulesManager(MailRule? template = null)
    {
        var accounts = _vm.Accounts.ToList();
        var selectedMessages = _vm.Messages.ToList();

        var rulesVm = new RulesManagerViewModel(
            _ruleService, accounts,
            prefillTemplate: template,
            selectedMessagesForTest: selectedMessages);

        var dialog = new RulesManagerWindow(rulesVm, accounts, _vm.CachedFolders) { Owner = this };
        dialog.ShowDialog();

        // Refresh the rules status text after the dialog closes
        _vm.UpdateRulesStatusText();

        // Apply rules to existing cached mail so newly created/edited rules
        // take effect immediately without waiting for the next sync.
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var removed = await _ruleService.ApplyRulesToExistingAsync(_localStore, cts.Token);
                if (removed.Count > 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _vm.RefreshCommand.Execute(null);
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Log("ApplyRulesToExisting failed", ex);
            }
        });
    }

    private void RulesStatusButton_Click(object sender, RoutedEventArgs e) => OpenRulesManager();

    /// <summary>
    /// Creates a <see cref="FolderPickerWindow"/> scoped to only the accounts that own
    /// the given messages.  This prevents the user from selecting a destination folder
    /// from a different IMAP account — the server-side MOVE command is connection-scoped
    /// and cannot reach folders on a different account, which would produce a
    /// "folder not found" error from the IMAP server.
    /// </summary>
    private FolderPickerWindow BuildMessageFolderPicker(
        IEnumerable<MailMessageSummary> messages, string title)
    {
        var ids      = messages.Select(m => m.AccountId).ToHashSet();
        var accounts = _vm.Accounts.Where(a => ids.Contains(a.Id));
        var folders  = _vm.CachedFolders
                          .Where(kv => ids.Contains(kv.Key))
                          .ToDictionary(kv => kv.Key, kv => kv.Value);
        return new FolderPickerWindow(accounts, folders, title: title) { Owner = this };
    }

    private void RebuildViewsMenu()
    {
        // Remove all dynamically-inserted view items (everything before the separator).
        while (ViewsMenuItem.Items.Count > 0 &&
               ViewsMenuItem.Items[0] is not Separator)
            ViewsMenuItem.Items.RemoveAt(0);

        var views = _vm.SavedViews;

        // Show/hide the separator.
        ViewsMenuSeparator.Visibility = views.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Insert one item per saved view, in order.
        for (int i = 0; i < views.Count; i++)
        {
            var view    = views[i];
            var item    = new MenuItem
            {
                Header      = view.Name,
                IsCheckable = true,
                IsChecked   = view.Id == _vm.ActiveView?.Id,
                Tag         = view.Id,
            };
            var viewId  = view.Id.ToString();
            item.Click += (_, _) => _vm.SelectViewCommand.Execute(viewId);

            // Show hotkey in menu if one is assigned.
            if (!string.IsNullOrEmpty(view.Hotkey))
                item.InputGestureText = view.Hotkey;

            ViewsMenuItem.Items.Insert(i, item);
        }
    }

    /// <summary>
    /// Updates IsChecked on the dynamic view menu items to reflect the currently active view.
    /// Called when ActiveView changes; faster than a full menu rebuild.
    /// </summary>
    private void UpdateViewMenuCheckmarks()
    {
        foreach (var obj in ViewsMenuItem.Items)
        {
            if (obj is MenuItem { Tag: Guid id } item)
                item.IsChecked = id == _vm.ActiveView?.Id;
        }
    }

}
