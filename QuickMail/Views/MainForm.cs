using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Main three-pane window: Account list | Folder tree | Message list + reading pane.
/// All pane controls are native Win32 controls (ListBox, TreeView, ListView, WebView2)
/// for maximum accessibility with screen readers.
/// </summary>
public sealed class MainForm : Form
{
    // ── Dependencies ────────────────────────────────────────────────────────────
    private readonly MainViewModel _vm;
    private readonly ISmtpService  _smtp;
    private readonly IAccountService    _accountService;
    private readonly ICredentialService _credentials;
    private readonly IImapService  _imap;
    private readonly IOAuthService _oauth;
    private readonly ICommandRegistry _registry;

    // ── Pane layout ─────────────────────────────────────────────────────────────
    private readonly SplitContainer _outerSplit;   // accounts | (folders + message)
    private readonly SplitContainer _innerSplit;   // folders  | (message + reading)
    private readonly SplitContainer _messageSplit; // message list | reading pane

    // ── Left panel: accounts ─────────────────────────────────────────────────────
    private readonly ListBox _accountList;

    // ── Middle panel: folders ─────────────────────────────────────────────────────
    private readonly TreeView _folderTree;

    // ── Right panel: message list (flat) ─────────────────────────────────────────
    private readonly ListView _messageList;
    private readonly ColumnHeader _colStatus;
    private readonly ColumnHeader _colFrom;
    private readonly ColumnHeader _colSubject;
    private readonly ColumnHeader _colDate;
    private readonly ColumnHeader _colAttach;

    // ── Right panel: conversation tree ───────────────────────────────────────────
    private readonly TreeView _conversationTree;

    // ── Reading pane ──────────────────────────────────────────────────────────────
    private readonly Panel     _readingPane;
    private readonly Label     _rpFrom;
    private readonly Label     _rpTo;
    private readonly Label     _rpCc;
    private readonly Label     _rpSubject;
    private readonly Label     _rpDate;
    private readonly ListBox   _rpAttachList;
    private readonly WebView2  _webView;

    // ── Toolbar ───────────────────────────────────────────────────────────────────
    private readonly ToolStrip _toolStrip;
    private readonly ToolStripButton _btnNew;
    private readonly ToolStripButton _btnReply;
    private readonly ToolStripButton _btnReplyAll;
    private readonly ToolStripButton _btnForward;
    private readonly ToolStripButton _btnDelete;
    private readonly ToolStripButton _btnRefresh;
    private readonly ToolStripButton _btnAccounts;
    private readonly ToolStripButton _btnConvView;

    // ── Status bar (real HWND controls so screen readers can focus/read them) ─────
    private readonly Panel       _statusBar;
    private readonly TextBox     _statusLabel;   // ReadOnly — focusable via F6
    private readonly ProgressBar _progressBar;

    // ── State ─────────────────────────────────────────────────────────────────────
    private bool _webViewReady;
    private CoreWebView2Controller? _webViewController; // cached for IsVisible toggling
    private System.Drawing.Font _boldFont = null!;
    private System.Drawing.Font _normalFont = null!;

    public MainForm(
        MainViewModel vm,
        ISmtpService smtp,
        IAccountService accountService,
        ICredentialService credentials,
        IImapService imap,
        IOAuthService oauth,
        ICommandRegistry registry)
    {
        _vm             = vm;
        _smtp           = smtp;
        _accountService = accountService;
        _credentials    = credentials;
        _imap           = imap;
        _oauth          = oauth;
        _registry       = registry;

        Text          = "QuickMail";
        ClientSize    = new System.Drawing.Size(1000, 650);
        MinimumSize   = new System.Drawing.Size(700, 400);
        KeyPreview    = true;

        // ── Status bar — native Panel + TextBox so screen readers can focus it ───
        _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 22 };
        _progressBar = new ProgressBar
        {
            Dock    = DockStyle.Right,
            Width   = 110,
            Visible = false,
            Style   = ProgressBarStyle.Marquee,
        };
        _statusLabel = new TextBox
        {
            Dock           = DockStyle.Fill,
            ReadOnly       = true,
            BorderStyle    = BorderStyle.None,
            BackColor      = System.Drawing.SystemColors.Control,
            Text           = "Ready",
            TabStop        = true,
            AccessibleName = "Status bar",
            AccessibleRole = AccessibleRole.StatusBar,
        };
        // Add progress bar first so it docks to the right before the label fills remaining space.
        _statusBar.Controls.Add(_progressBar);
        _statusBar.Controls.Add(_statusLabel);

        // ── Toolbar ────────────────────────────────────────────────────────────
        _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        _btnNew      = MakeBtn("&New",           "New message (Ctrl+N)");
        _btnReply    = MakeBtn("&Reply",         "Reply (Ctrl+R)");
        _btnReplyAll = MakeBtn("Reply &All",     "Reply All (Ctrl+Shift+R)");
        _btnForward  = MakeBtn("&Forward",       "Forward (Ctrl+F)");
        _btnDelete   = MakeBtn("&Delete",        "Delete (Del)");
        _btnRefresh  = MakeBtn("Refres&h",       "Refresh (F5)");
        _btnAccounts = MakeBtn("&Accounts…",     "Manage email accounts (Ctrl+Shift+A)");
        _btnConvView = MakeBtn("Con&versations", "Toggle conversation view (Ctrl+Shift+C)");
        _toolStrip.Items.AddRange([_btnNew, _btnReply, _btnReplyAll, _btnForward,
                                   new ToolStripSeparator(), _btnDelete, _btnRefresh,
                                   new ToolStripSeparator(), _btnAccounts, _btnConvView]);

        // ── Account ListBox ─────────────────────────────────────────────────────
        _accountList = new ListBox
        {
            Dock           = DockStyle.Fill,
            AccessibleName = "Accounts",
            AccessibleRole = AccessibleRole.List,
            IntegralHeight = false,
        };

        // ── Folder TreeView ──────────────────────────────────────────────────────
        _folderTree = new TreeView
        {
            Dock           = DockStyle.Fill,
            AccessibleName = "Folders",
            HideSelection  = false,
            FullRowSelect  = true,
            ShowRootLines  = false,
        };

        // ── Message ListView (virtual, flat view) ─────────────────────────────────
        // Empty Text on every ColumnHeader so UIA TableItem.GetColumnHeaderItems()
        // returns nothing — screen readers would otherwise prepend "From:", "Subject:" etc.
        // before each cell value even when HeaderStyle = ColumnHeaderStyle.None.
        _colStatus = new ColumnHeader { Text = "", Width = _vm.ShowMessageStatus ? 65 : 0 };
        _colFrom   = new ColumnHeader { Text = "", Width = 160 };
        _colSubject = new ColumnHeader { Text = "", Width = 360 };
        _colDate   = new ColumnHeader { Text = "", Width = 110 };
        _colAttach = new ColumnHeader { Text = "", Width = 22 };

        _messageList = new ListView
        {
            Dock           = DockStyle.Fill,
            View           = View.Details,
            FullRowSelect  = true,
            MultiSelect    = true,
            HideSelection  = false,
            VirtualMode    = true,
            VirtualListSize = 0,
            // ColumnHeaderStyle.None destroys the Win32 header control entirely so
            // screen readers cannot read column header names ("From:", "Subject:"…)
            // as prefixes before each cell value — only the cell values are heard.
            HeaderStyle    = ColumnHeaderStyle.None,
            AccessibleName = "Messages",
            AccessibleRole = AccessibleRole.List,
        };
        _messageList.Columns.AddRange([_colStatus, _colFrom, _colSubject, _colDate, _colAttach]);

        // ── Conversation TreeView ─────────────────────────────────────────────────
        _conversationTree = new TreeView
        {
            Dock           = DockStyle.Fill,
            Visible        = false,
            AccessibleName = "Conversations",
            HideSelection  = false,
            FullRowSelect  = true,
        };

        // Panel that holds either the flat message list or the conversation tree
        var messagePanel = new Panel { Dock = DockStyle.Fill };
        messagePanel.Controls.AddRange([_conversationTree, _messageList]);

        // ── Reading pane ──────────────────────────────────────────────────────────
        _readingPane = new Panel { Dock = DockStyle.Fill };
        _webView     = new WebView2 { Dock = DockStyle.Fill, AccessibleName = "Message body" };

        // Header fields above the body
        var headerTable = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            ColumnCount = 2,
            Padding     = new Padding(4, 4, 4, 0),
        };
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _rpFrom    = MakeReadingLabel("From:");
        _rpTo      = MakeReadingLabel("To:");
        _rpCc      = MakeReadingLabel("Cc:");
        _rpSubject = MakeReadingLabel("Subject:");
        _rpDate    = MakeReadingLabel("Date:");
        AddRpRow(headerTable, "From:",    _rpFrom);
        AddRpRow(headerTable, "To:",      _rpTo);
        AddRpRow(headerTable, "Cc:",      _rpCc);
        AddRpRow(headerTable, "Subject:", _rpSubject);
        AddRpRow(headerTable, "Date:",    _rpDate);

        _rpAttachList = new ListBox
        {
            Dock           = DockStyle.Top,
            Height         = 0,
            AccessibleName = "Attachments",
            AccessibleRole = AccessibleRole.List,
        };
        _rpAttachList.DoubleClick += OnAttachmentDoubleClick;
        _rpAttachList.KeyDown     += OnAttachmentKeyDown;

        // Attachment context menu
        var attachMenu = new ContextMenuStrip();
        attachMenu.Items.Add("Save", null, OnAttachSave);
        attachMenu.Items.Add("Open", null, OnAttachOpen);
        _rpAttachList.ContextMenuStrip = attachMenu;

        _readingPane.Controls.AddRange([_webView, _rpAttachList, headerTable]);
        _readingPane.Visible = false;

        // ── Layout assembly ───────────────────────────────────────────────────────
        _messageSplit = new SplitContainer
        {
            Dock        = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300,
        };
        _messageSplit.Panel1.Controls.Add(messagePanel);
        _messageSplit.Panel2.Controls.Add(_readingPane);
        _messageSplit.Panel2Collapsed = true;

        _innerSplit = new SplitContainer
        {
            Dock             = DockStyle.Fill,
            SplitterDistance = 200,
        };
        _innerSplit.Panel1.Controls.Add(_folderTree);
        _innerSplit.Panel2.Controls.Add(_messageSplit);

        _outerSplit = new SplitContainer
        {
            Dock             = DockStyle.Fill,
            SplitterDistance = 140,
        };
        _outerSplit.Panel1.Controls.Add(_accountList);
        _outerSplit.Panel2.Controls.Add(_innerSplit);

        Controls.Add(_outerSplit);
        Controls.Add(_toolStrip);
        Controls.Add(_statusBar);

        // ── Wire events ───────────────────────────────────────────────────────────
        WireToolbarEvents();
        WireAccountListEvents();
        WireFolderTreeEvents();
        WireMessageListEvents();
        WireConversationTreeEvents();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Messages.CollectionChanged          += (_, _) => OnMessagesCollectionChanged();
        _vm.Conversations.CollectionChanged     += (_, _) => OnConversationsCollectionChanged();
        _vm.FolderTree.CollectionChanged        += (_, _) => RebuildFolderTree();
        _vm.Accounts.CollectionChanged          += (_, _) => RebuildAccountList();
        _vm.ComposeRequested        += OnComposeRequested;
        _vm.ManageAccountsRequested += OnManageAccounts;
        _vm.OpenAccountSettingsRequested += OnOpenAccountSettings;
        _vm.MessageListFocusRequested    += ReturnFocusToMessagePanel;
        _vm.AnnouncementRequested        += (_, text) => AccessibilityHelper.Announce(this, text, interrupt: true);

        KeyDown += OnWindowKeyDown;
        Load    += OnLoad;

        // Message list is the default active control; set before the form is shown
        // so WinForms doesn't route initial focus to the first control in tab order
        // (which would be the account list).
        ActiveControl = _messageList;
    }

    // ── Startup ────────────────────────────────────────────────────────────────

    private async void OnLoad(object? sender, EventArgs e)
    {
        _boldFont   = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold);
        _normalFont = Font;

        // Assert focus before any async work so the user sees the right pane immediately.
        _messageList.Focus();

        // Register UI-only commands that require form references
        _registry.Register(new CommandDefinition(
            id: "view.folderPicker", category: "View", title: "Go to Folder…",
            execute: OpenFolderPicker,
            shortcut: Keys.Control | Keys.Y));

        _registry.Register(new CommandDefinition(
            id: "view.toggleConversation", category: "View", title: "Toggle Conversation View",
            execute: () => _vm.IsConversationView = !_vm.IsConversationView,
            shortcut: Keys.Control | Keys.Shift | Keys.C));

        _registry.Register(new CommandDefinition(
            id: "view.focusStatusBar", category: "View", title: "Focus Status Bar",
            execute: FocusStatusBar,
            shortcut: Keys.Control | Keys.D9));

        // Initial data from local cache
        RebuildAccountList();
        RebuildFolderTree();

        // WinForms-level WebView2 events — registered unconditionally so they survive
        // any exception thrown later during CoreWebView2 initialisation.
        _webView.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) e.IsInputKey = true;
        };
        _webView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && !_messageSplit.Panel2Collapsed)
            {
                e.Handled = true;
                CloseReadingPane();
            }
        };
        _webView.GotFocus += (_, _) =>
        {
            if (!_vm.IsMessageOpen)
                BeginInvoke((Action)(() => _messageList.Focus()));
        };

        // Init WebView2
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickMail", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);
            _webViewReady = true;

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled           = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled           = false;

            // Primary Escape mechanism: CoreWebView2Controller.AcceleratorKeyPressed.
            // The WinForms control holds the controller in a private field; reach it
            // via reflection. This fires before Chromium processes the key, making it
            // the most reliable interception point for WebView2 keyboard events.
            var fi = _webView.GetType().GetField(
                "_coreWebView2Controller",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ctrl = fi?.GetValue(_webView) as CoreWebView2Controller;
            _webViewController = ctrl; // cache for CloseReadingPane / ShowMessageBodyAsync
            LogService.Log($"[Escape] AcceleratorKeyPressed setup: controller found={ctrl != null}, field found={fi != null}");
            if (ctrl != null)
            {
                ctrl.AcceleratorKeyPressed += (_, e) =>
                {
                    LogService.Log($"[Escape] AcceleratorKeyPressed vk=0x{e.VirtualKey:X2} kind={e.KeyEventKind}");
                    const uint VK_ESCAPE = 0x1B;
                    if (e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown &&
                        e.VirtualKey   == VK_ESCAPE &&
                        !_messageSplit.Panel2Collapsed)
                    {
                        e.Handled = true;
                        LogService.Log("[Escape] AcceleratorKeyPressed → CloseReadingPane");
                        BeginInvoke((Action)CloseReadingPane);
                    }
                };
            }

            // Note: keyboard relay (Escape / F6 / Shift+Tab) is injected per-navigation via
            // ExecuteScriptAsync in ShowMessageBodyAsync — that API is not subject to the
            // page's CSP, unlike AddScriptToExecuteOnDocumentCreatedAsync which is blocked
            // by the "script-src 'none'" meta-CSP we inject into HTML emails.

            _webView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                LogService.Log($"[Escape] WebMessageReceived: '{msg}'");
                BeginInvoke((Action)(() =>
                {
                    if      (msg == "escape")    CloseReadingPane();
                    else if (msg == "f6")        _ = CycleFocusAsync(true);
                    else if (msg == "shift-f6")  _ = CycleFocusAsync(false);
                    else if (msg == "shift-tab") FocusLastReadingPaneField();
                }));
            };
        }
        catch (Exception ex)
        {
            LogService.Log("WebView2 init failed", ex);
        }

        if (!_vm.Accounts.Any())
        {
            OnManageAccounts();
            return;
        }

        await _vm.InitialLoadAsync();
        _ = _vm.StartBackgroundSyncAsync();
        // Defer focus until the next message-pump cycle so WebView2 HWND init
        // cannot steal it back after we set it.
        BeginInvoke(FocusActiveMessagePanel);
    }

    // ── Toolbar helpers ────────────────────────────────────────────────────────

    private static ToolStripButton MakeBtn(string text, string tooltip) => new()
    {
        Text         = text,
        ToolTipText  = tooltip,
        DisplayStyle = ToolStripItemDisplayStyle.Text,
    };

    private void WireToolbarEvents()
    {
        _btnNew.Click      += (_, _) => _vm.NewMessageCommand.Execute(null);
        _btnReply.Click    += (_, _) => _vm.ReplyCommand.Execute(null);
        _btnReplyAll.Click += (_, _) => _vm.ReplyAllCommand.Execute(null);
        _btnForward.Click  += (_, _) => _vm.ForwardCommand.Execute(null);
        _btnDelete.Click   += async (_, _) =>
        {
            var toDelete = GetSelectedMessages();
            if (toDelete.Count > 0)
            {
                await _vm.DeleteMessagesAsync(toDelete);
                FocusActiveMessagePanel();
            }
        };
        _btnRefresh.Click  += (_, _) => _vm.RefreshCommand.Execute(null);
        _btnAccounts.Click += (_, _) => _vm.ManageAccountsCommand.Execute(null);
        _btnConvView.Click += (_, _) => { _vm.IsConversationView = !_vm.IsConversationView; };
    }

    // ── Account list ─────────────────────────────────────────────────────────────

    private void RebuildAccountList()
    {
        if (InvokeRequired) { BeginInvoke(RebuildAccountList); return; }
        _accountList.BeginUpdate();
        _accountList.Items.Clear();
        foreach (var acct in _vm.Accounts)
            _accountList.Items.Add(acct.DisplayName);
        _accountList.EndUpdate();

        var idx = _vm.SelectedAccount != null
            ? _vm.Accounts.IndexOf(_vm.SelectedAccount)
            : -1;
        if (idx >= 0) _accountList.SelectedIndex = idx;
    }

    private void WireAccountListEvents()
    {
        // SelectedIndexChanged fires on arrow-key navigation — only announce, never connect.
        _accountList.SelectedIndexChanged += (_, _) =>
        {
            var idx = _accountList.SelectedIndex;
            if (idx >= 0 && idx < _vm.Accounts.Count)
                AccessibilityHelper.Announce(this, _vm.Accounts[idx].DisplayName);
        };

        // Mouse click → switch account
        _accountList.Click += async (_, _) =>
        {
            var idx = _accountList.SelectedIndex;
            if (idx >= 0 && idx < _vm.Accounts.Count)
                await _vm.SelectAccountCommand.ExecuteAsync(_vm.Accounts[idx]);
        };

        // Enter key → switch account
        _accountList.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                var idx = _accountList.SelectedIndex;
                if (idx >= 0 && idx < _vm.Accounts.Count)
                    await _vm.SelectAccountCommand.ExecuteAsync(_vm.Accounts[idx]);
            }
        };

        _accountList.GotFocus += (_, _) =>
        {
            if (_accountList.SelectedIndex < 0 && _accountList.Items.Count > 0)
                _accountList.SelectedIndex = 0;
        };

        // Right-click context menu on accounts
        var accountMenu = new ContextMenuStrip();
        accountMenu.Items.Add("Account Settings", null, (_, _) =>
        {
            var idx = _accountList.SelectedIndex;
            if (idx >= 0 && idx < _vm.Accounts.Count)
                _vm.OpenAccountSettingsCommand.Execute(_vm.Accounts[idx]);
        });
        accountMenu.Items.Add("Remove Account", null, (_, _) =>
        {
            var idx = _accountList.SelectedIndex;
            if (idx >= 0 && idx < _vm.Accounts.Count)
                _vm.DeleteAccountCommand.Execute(_vm.Accounts[idx]);
        });
        _accountList.ContextMenuStrip = accountMenu;
    }

    // ── Folder tree ───────────────────────────────────────────────────────────────

    private void RebuildFolderTree()
    {
        if (InvokeRequired) { BeginInvoke(RebuildFolderTree); return; }
        _folderTree.BeginUpdate();
        _folderTree.Nodes.Clear();

        foreach (var node in _vm.FolderTree)
            _folderTree.Nodes.Add(FolderNodeToTreeNode(node));

        _folderTree.ExpandAll();
        _folderTree.EndUpdate();
    }

    private static TreeNode FolderNodeToTreeNode(FolderTreeNode fn)
    {
        var tn = new TreeNode(fn.Label) { Tag = fn, Name = fn.AutomationName ?? fn.Label };
        foreach (var child in fn.Children)
            tn.Nodes.Add(FolderNodeToTreeNode(child));
        return tn;
    }

    private void WireFolderTreeEvents()
    {
        // AfterSelect fires on arrow-key navigation too — only announce, never load.
        _folderTree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is FolderTreeNode fn && !fn.IsHeader)
                AccessibilityHelper.Announce(this, fn.Label);
        };

        // Mouse click → load messages
        _folderTree.NodeMouseClick += async (_, e) =>
        {
            if (e.Node?.Tag is FolderTreeNode fn && fn.Folder != null && !fn.IsHeader)
            {
                _folderTree.SelectedNode = e.Node;
                await _vm.SelectFolderCommand.ExecuteAsync(fn.Folder);
                FocusActiveMessagePanel();
            }
        };

        // Enter key → load messages for the currently-highlighted folder
        _folderTree.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                var fn = _folderTree.SelectedNode?.Tag as FolderTreeNode;
                if (fn?.Folder != null && !fn.IsHeader)
                {
                    await _vm.SelectFolderCommand.ExecuteAsync(fn.Folder);
                    FocusActiveMessagePanel();
                }
            }
        };

        _folderTree.GotFocus += (_, _) =>
        {
            if (_folderTree.SelectedNode == null && _folderTree.Nodes.Count > 0)
                _folderTree.SelectedNode = _folderTree.Nodes[0];
        };

        // First-letter navigation
        _folderTree.KeyPress += OnFolderTreeKeyPress;

        // Context menu for folder operations
        var folderMenu = new ContextMenuStrip();
        folderMenu.Items.Add("New Folder…",    null, OnFolderMenuNew);
        folderMenu.Items.Add("Move Folder…",   null, OnFolderMenuMove);
        folderMenu.Items.Add("Copy Folder…",   null, OnFolderMenuCopy);
        folderMenu.Items.Add("Delete Folder",  null, OnFolderMenuDelete);
        _folderTree.ContextMenuStrip = folderMenu;
    }

    private void OnFolderTreeKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar)) return;

        var allNodes = GetVisibleTreeNodes(_folderTree.Nodes).ToList();
        if (allNodes.Count == 0) return;

        var current  = _folderTree.SelectedNode;
        var startIdx = current != null ? allNodes.IndexOf(current) : -1;

        for (int i = 1; i <= allNodes.Count; i++)
        {
            var candidate = allNodes[(startIdx + i) % allNodes.Count];
            var label     = (candidate.Tag as FolderTreeNode)?.Label ?? candidate.Text;
            if (label.StartsWith(e.KeyChar.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _folderTree.SelectedNode = candidate;
                candidate.EnsureVisible();
                e.Handled = true;
                return;
            }
        }
    }

    private static IEnumerable<TreeNode> GetVisibleTreeNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            if (node.IsExpanded)
                foreach (var child in GetVisibleTreeNodes(node.Nodes))
                    yield return child;
        }
    }

    // ── Message list ──────────────────────────────────────────────────────────────

    private void OnMessagesCollectionChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnMessagesCollectionChanged); return; }
        // Remember where the cursor was *before* we resize the virtual list.
        // When a message is deleted at index N the ListView still has N selected
        // (until VirtualListSize is reduced), so this captures the right row.
        int prevIndex = _messageList.SelectedIndices.Count > 0
            ? _messageList.SelectedIndices[0]
            : 0;
        _messageList.VirtualListSize = _vm.Messages.Count;
        _messageList.Invalidate();
        // Restore cursor: land on the same row (or the new last row) so Delete
        // advances to the next message rather than jumping to the top of the list.
        if (_vm.Messages.Count > 0 && _messageList.SelectedIndices.Count == 0)
            SelectMessageListItem(Math.Min(prevIndex, _vm.Messages.Count - 1));
    }

    private void WireMessageListEvents()
    {
        _messageList.RetrieveVirtualItem += (_, e) =>
        {
            if (e.ItemIndex >= 0 && e.ItemIndex < _vm.Messages.Count)
                e.Item = BuildListViewItem(_vm.Messages[e.ItemIndex], e.ItemIndex);
            else
                e.Item = new ListViewItem();
        };

        _messageList.SelectedIndexChanged += (_, _) =>
        {
            if (_messageList.SelectedIndices.Count == 1)
            {
                var idx = _messageList.SelectedIndices[0];
                if (idx >= 0 && idx < _vm.Messages.Count)
                    _vm.SelectedMessage = _vm.Messages[idx];
                // The ListView's native accessibility (column values) is read by the
                // screen reader automatically — no custom Announce needed here.
            }
        };

        _messageList.Click += async (_, _) =>
        {
            await OpenSelectedMessageAsync();
        };

        _messageList.KeyDown += async (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (_vm.IsMessageOpen)
                    {
                        e.Handled = true;
                        CloseReadingPane();
                    }
                    break;

                case Keys.Enter:
                    e.Handled = true;
                    await OpenSelectedMessageAsync();
                    break;

                case Keys.Delete:
                    e.Handled = true;
                    var toDelete = GetSelectedMessages();
                    if (toDelete.Count > 0)
                    {
                        await _vm.DeleteMessagesAsync(toDelete);
                        FocusActiveMessagePanel();
                    }
                    break;

                case Keys.Up or Keys.Down when e.Shift:
                    e.Handled = true;
                    ExtendMessageSelection(e.KeyCode == Keys.Down ? 1 : -1);
                    break;
            }
        };

        _messageList.GotFocus += (_, _) =>
        {
            if (_messageList.SelectedIndices.Count == 0 && _messageList.Items.Count > 0)
                SelectMessageListItem(0);
        };

        // Context menu
        var msgMenu = new ContextMenuStrip();
        msgMenu.Items.Add("Move to Folder…", null, OnMsgMenuMove);
        msgMenu.Items.Add("Copy to Folder…", null, OnMsgMenuCopy);
        msgMenu.Items.Add("-");
        msgMenu.Items.Add("Delete",          null, async (_, _) =>
        {
            var msgs = GetSelectedMessages();
            if (msgs.Count > 0) await _vm.DeleteMessagesAsync(msgs);
        });
        _messageList.ContextMenuStrip = msgMenu;
    }

    private ListViewItem BuildListViewItem(MailMessageSummary msg, int index)
    {
        var item = new ListViewItem(msg.StatusDisplay);
        item.SubItems.Add(msg.From ?? string.Empty);
        item.SubItems.Add(BuildSubjectCell(msg));
        item.SubItems.Add(msg.DateDisplay ?? string.Empty);
        item.SubItems.Add(msg.HasAttachments ? "+" : string.Empty);
        item.Font = msg.IsRead ? _normalFont : _boldFont;
        return item;
    }

    private static string BuildSubjectCell(MailMessageSummary msg)
    {
        var subject = msg.Subject ?? "(no subject)";
        if (!string.IsNullOrEmpty(msg.Preview))
            return $"{subject} — {msg.Preview}";
        return subject;
    }

    private void SelectMessageListItem(int index)
    {
        if (index < 0 || index >= _vm.Messages.Count) return;
        _messageList.SelectedIndices.Clear();
        _messageList.SelectedIndices.Add(index);
        _messageList.EnsureVisible(index);
        _messageList.FocusedItem = _messageList.Items[index];
    }

    private void ExtendMessageSelection(int direction)
    {
        if (_messageList.SelectedIndices.Count == 0) return;
        int last   = _messageList.SelectedIndices[^1];
        int target = Math.Clamp(last + direction, 0, _vm.Messages.Count - 1);
        if (target == last) return;

        if (_messageList.SelectedIndices.Contains(target))
            _messageList.SelectedIndices.Remove(last);
        else
            _messageList.SelectedIndices.Add(target);

        _messageList.EnsureVisible(target);
        _messageList.FocusedItem = _messageList.Items[target];
    }

    private async Task OpenSelectedMessageAsync()
    {
        if (_messageList.SelectedIndices.Count != 1) return;
        var idx = _messageList.SelectedIndices[0];
        if (idx < 0 || idx >= _vm.Messages.Count) return;
        var msg = _vm.Messages[idx];

        if (_vm.IsSelectedFolderDrafts)
        {
            _vm.SelectedMessage = msg;
            await _vm.OpenDraftCommand.ExecuteAsync(null);
        }
        else
        {
            await _vm.SelectMessageCommand.ExecuteAsync(msg);
            if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                await ShowMessageBodyAsync(_vm.MessageDetail);
        }
    }

    private IReadOnlyList<MailMessageSummary> GetSelectedMessages()
    {
        if (_vm.IsConversationView)
        {
            if (_conversationTree.SelectedNode?.Tag is MailMessageSummary m) return [m];
            return [];
        }
        return _messageList.SelectedIndices.Cast<int>()
            .Where(i => i >= 0 && i < _vm.Messages.Count)
            .Select(i => _vm.Messages[i])
            .ToList();
    }

    // ── Conversation tree ─────────────────────────────────────────────────────────

    private void OnConversationsCollectionChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnConversationsCollectionChanged); return; }
        RebuildConversationTree();
    }

    private void RebuildConversationTree()
    {
        _conversationTree.BeginUpdate();
        _conversationTree.Nodes.Clear();

        foreach (var group in _vm.Conversations)
            _conversationTree.Nodes.Add(ConversationGroupToTreeNode(group));

        _conversationTree.EndUpdate();
    }

    private static TreeNode ConversationGroupToTreeNode(ConversationGroup group)
    {
        var tn = new TreeNode(group.Subject ?? "(no subject)") { Tag = group, Name = group.AutomationName };

        foreach (var msg in group.Messages)
        {
            var label = $"{msg.From} — {msg.DateDisplay}";
            var child = new TreeNode(label) { Tag = msg, Name = MessageAnnouncement(msg) };
            tn.Nodes.Add(child);
        }

        if (group.IsExpanded) tn.Expand();
        return tn;
    }

    private void WireConversationTreeEvents()
    {
        _conversationTree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is MailMessageSummary msg)
            {
                _vm.SelectedMessage = msg;
                AccessibilityHelper.Announce(this, MessageAnnouncement(msg), interrupt: true);
            }
            else if (e.Node?.Tag is ConversationGroup grp)
            {
                AccessibilityHelper.Announce(this, grp.AutomationName, interrupt: true);
            }
        };

        _conversationTree.GotFocus += (_, _) =>
        {
            if (_conversationTree.SelectedNode == null && _conversationTree.Nodes.Count > 0)
            {
                _conversationTree.SelectedNode = _conversationTree.Nodes[0];
                _conversationTree.SelectedNode.EnsureVisible();
            }
        };

        _conversationTree.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _vm.IsMessageOpen)
            {
                e.Handled = true;
                CloseReadingPane();
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                var node = _conversationTree.SelectedNode;
                if (node?.Tag is MailMessageSummary msg)
                {
                    _vm.SelectedMessage = msg;
                    await _vm.SelectMessageCommand.ExecuteAsync(msg);
                    if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                        await ShowMessageBodyAsync(_vm.MessageDetail);
                }
                else if (node?.Tag is ConversationGroup grp)
                {
                    if (grp.Messages.Count == 1)
                    {
                        _vm.SelectedMessage = grp.Messages[0];
                        await _vm.SelectMessageCommand.ExecuteAsync(grp.Messages[0]);
                        if (_vm.IsMessageOpen && _vm.MessageDetail != null)
                            await ShowMessageBodyAsync(_vm.MessageDetail);
                    }
                    else
                    {
                        if (node.IsExpanded) node.Collapse(); else node.Expand();
                    }
                }
            }
            else if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                var node = _conversationTree.SelectedNode;
                if (node?.Tag is MailMessageSummary msg)
                {
                    _vm.SelectedMessage = msg;
                    await _vm.DeleteMessageCommand.ExecuteAsync(null);
                }
                else if (node?.Tag is ConversationGroup grp)
                {
                    await _vm.DeleteMessagesAsync(grp.Messages);
                }
            }
        };

        var convMenu = new ContextMenuStrip();
        convMenu.Items.Add("Move to Folder…", null, OnMsgMenuMove);
        convMenu.Items.Add("Copy to Folder…", null, OnMsgMenuCopy);
        _conversationTree.ContextMenuStrip = convMenu;
    }

    // ── Reading pane ──────────────────────────────────────────────────────────────

    private async Task ShowMessageBodyAsync(MailMessageDetail detail)
    {
        if (!_webViewReady) return;

        UpdateReadingPaneHeader(detail);
        _readingPane.Visible           = true;
        _messageSplit.Panel2Collapsed  = false;
        if (_webViewController != null) _webViewController.IsVisible = true;

        var encodedSubject = WebUtility.HtmlEncode(detail.Subject ?? string.Empty);
        var titleTag = $"<title>{encodedSubject}</title>";

        string html;
        if (!string.IsNullOrWhiteSpace(detail.HtmlBody))
        {
            const string cspTag =
                "<meta http-equiv=\"Content-Security-Policy\" " +
                "content=\"script-src 'none'; object-src 'none'; frame-src 'none';\">";
            var body = detail.HtmlBody;
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
            var encoded = WebUtility.HtmlEncode(detail.PlainTextBody ?? string.Empty);
            html =
                "<!DOCTYPE html>\n<html lang=\"en\">\n" +
                $"<head><meta charset=\"utf-8\">{titleTag}<style>\n" +
                "html,body{{margin:0;padding:8px 12px;font-family:Segoe UI,Arial,sans-serif;" +
                "font-size:13px;white-space:pre-wrap;word-break:break-word;" +
                "background:Window;color:WindowText;outline:none;}}\n" +
                "</style></head>\n" +
                "<body tabindex=\"0\">" + encoded + "</body>\n</html>";
        }

        var tcs = new TaskCompletionSource<bool>();
        void OnNavigated(object? s, CoreWebView2NavigationCompletedEventArgs ev)
        {
            _webView.CoreWebView2.NavigationCompleted -= OnNavigated;
            tcs.TrySetResult(ev.IsSuccess);
        }
        _webView.CoreWebView2.NavigationCompleted += OnNavigated;
        _webView.CoreWebView2.NavigateToString(html);
        await tcs.Task;

        // Inject keyboard relay after every navigation.  ExecuteScriptAsync bypasses the
        // page's Content-Security-Policy (unlike AddScriptToExecuteOnDocumentCreatedAsync),
        // so this works even for HTML emails that carry "script-src 'none'" in their CSP.
        LogService.Log("[Escape] Injecting keyboard relay via ExecuteScriptAsync");
        await _webView.CoreWebView2.ExecuteScriptAsync(
            "window.addEventListener('keydown',function(e){"
            + "if(e.key==='Escape'){window.chrome.webview.postMessage('escape');e.preventDefault();}"
            + "else if(e.key==='F6'){window.chrome.webview.postMessage(e.shiftKey?'shift-f6':'f6');e.preventDefault();}"
            + "else if(e.key==='Tab'&&e.shiftKey){window.chrome.webview.postMessage('shift-tab');e.preventDefault();}"
            + "},true);");

        _webView.Focus();
        await _webView.CoreWebView2.ExecuteScriptAsync("document.body.focus()");
    }

    private void UpdateReadingPaneHeader(MailMessageDetail detail)
    {
        _rpFrom.Text    = detail.From    ?? string.Empty;
        _rpTo.Text      = detail.To      ?? string.Empty;
        _rpCc.Text      = detail.Cc      ?? string.Empty;
        _rpSubject.Text = detail.Subject ?? string.Empty;
        _rpDate.Text    = detail.Date.ToLocalTime().ToString("f");

        _rpAttachList.Items.Clear();
        foreach (var att in detail.Attachments)
            _rpAttachList.Items.Add(att);
        _rpAttachList.DisplayMember = nameof(AttachmentModel.FileName);
        _rpAttachList.Height = detail.Attachments.Count > 0
            ? Math.Min(detail.Attachments.Count * 18 + 4, 72)
            : 0;
    }

    private void FocusLastReadingPaneField()
    {
        if (_rpAttachList.Items.Count > 0)
            _rpAttachList.Focus();
        else
            _rpDate.Focus();
    }

    // ── Attachment actions ────────────────────────────────────────────────────────

    private void OnAttachmentDoubleClick(object? sender, EventArgs e)
    {
        if (_rpAttachList.SelectedItem is AttachmentModel att)
            _vm.OpenAttachmentCommand.Execute(att);
    }

    private void OnAttachmentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && _rpAttachList.SelectedItem is AttachmentModel att)
        {
            _vm.OpenAttachmentCommand.Execute(att);
            e.Handled = true;
        }
    }

    private void OnAttachSave(object? sender, EventArgs e)
    {
        if (_rpAttachList.SelectedItem is AttachmentModel att)
            _vm.SaveAttachmentCommand.Execute(att);
    }

    private void OnAttachOpen(object? sender, EventArgs e)
    {
        if (_rpAttachList.SelectedItem is AttachmentModel att)
            _vm.OpenAttachmentCommand.Execute(att);
    }

    // ── VM → form sync ────────────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // PropertyChanged can fire from a background thread if the VM's
        // _uiSyncContext was not a WindowsFormsSynchronizationContext (happens
        // when the VM is constructed before Application.Run installs it).
        // Always marshal to the UI thread before touching any WinForms control.
        if (InvokeRequired)
        {
            BeginInvoke(() => OnVmPropertyChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(_vm.WindowTitle):
                Text = _vm.WindowTitle;
                break;

            case nameof(_vm.StatusText):
                _statusLabel.Text = _vm.StatusText;
                if (!string.IsNullOrEmpty(_vm.StatusText))
                    AccessibilityHelper.Announce(this, _vm.StatusText);
                break;

            case nameof(_vm.IsBusy):
                _progressBar.Visible = _vm.IsBusy;
                break;

            case nameof(_vm.IsMessageOpen):
                _messageSplit.Panel2Collapsed = !_vm.IsMessageOpen;
                _readingPane.Visible = _vm.IsMessageOpen;
                break;

            case nameof(_vm.IsConversationView):
                _messageList.Visible      = !_vm.IsConversationView;
                _conversationTree.Visible = _vm.IsConversationView;
                if (_vm.IsConversationView)
                    RebuildConversationTree();
                break;

            case nameof(_vm.ShowMessageStatus):
                _colStatus.Width = _vm.ShowMessageStatus ? 65 : 0;
                break;

            case nameof(_vm.Messages):
                _vm.Messages.CollectionChanged += (_, _) => OnMessagesCollectionChanged();
                OnMessagesCollectionChanged();
                break;

            case nameof(_vm.Conversations):
                _vm.Conversations.CollectionChanged += (_, _) => OnConversationsCollectionChanged();
                OnConversationsCollectionChanged();
                break;

            case nameof(_vm.FolderTree):
                _vm.FolderTree.CollectionChanged += (_, _) => RebuildFolderTree();
                RebuildFolderTree();
                break;

            case nameof(_vm.Accounts):
                _vm.Accounts.CollectionChanged += (_, _) => RebuildAccountList();
                RebuildAccountList();
                break;

            case nameof(_vm.SelectedAccount):
                var selAcctIdx = _vm.SelectedAccount != null
                    ? _vm.Accounts.IndexOf(_vm.SelectedAccount)
                    : -1;
                if (selAcctIdx >= 0 && _accountList.SelectedIndex != selAcctIdx)
                    _accountList.SelectedIndex = selAcctIdx;
                break;
        }
    }

    // ── Global keyboard handler ───────────────────────────────────────────────────

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Hardcoded pane-navigation shortcuts
        switch (e.KeyData)
        {
            case Keys.Control | Keys.D0:
                _toolStrip.Focus();
                e.Handled = true; return;

            case Keys.Control | Keys.D1:
                _accountList.Focus();
                e.Handled = true; return;

            case Keys.Control | Keys.D2:
                _folderTree.Focus();
                e.Handled = true; return;

            case Keys.Control | Keys.D3:
                FocusActiveMessagePanel();
                e.Handled = true; return;
        }

        if (e.KeyCode == Keys.F6 && !e.Control && !e.Alt)
        {
            e.Handled = true;
            await CycleFocusAsync(!e.Shift);
            return;
        }

        // Escape when focus is in a WinForms control (message list, folder tree, etc.)
        // and the reading pane is open — KeyPreview ensures we see this before the control.
        if (e.KeyCode == Keys.Escape && !_messageSplit.Panel2Collapsed)
        {
            CloseReadingPane();
            e.Handled = true;
            return;
        }

        if (e.KeyData == (Keys.Control | Keys.Shift | Keys.P))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        // Registry-based commands
        var cmd = _registry.FindByGesture(e.KeyData);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            cmd.Execute();
            e.Handled = true;
        }
    }

    // ── Focus management ──────────────────────────────────────────────────────────

    private void FocusActiveMessagePanel()
    {
        if (_vm.IsConversationView)
        {
            _conversationTree.Focus();
            if (_conversationTree.SelectedNode == null && _conversationTree.Nodes.Count > 0)
                _conversationTree.SelectedNode = _conversationTree.Nodes[0];
        }
        else
        {
            _messageList.Focus();
            if (_vm.Messages.Count > 0)
            {
                // Always re-select (not just when empty) so the UIA SelectionItem.Select
                // pattern fires and screen readers announce the focused row reliably.
                int idx = _messageList.SelectedIndices.Count > 0
                    ? _messageList.SelectedIndices[0]
                    : 0;
                SelectMessageListItem(Math.Min(idx, _vm.Messages.Count - 1));
            }
        }
    }

    private void ReturnFocusToMessagePanel() => FocusActiveMessagePanel();

    private void CloseReadingPane()
    {
        LogService.Log($"[Escape] CloseReadingPane: Panel2Collapsed={_messageSplit.Panel2Collapsed}");

        // Setting IsVisible=false on the CoreWebView2Controller (not just collapsing
        // the WinForms panel) is the correct way to signal accessibility tools that
        // the WebView2 is gone.  Collapsing the SplitterPanel hides the pixels but
        // leaves the UIA/accessibility subtree live; screen readers (NVDA, JAWS) keep
        // their browse-mode virtual cursor anchored there.  IsVisible=false tells
        // WebView2 to mark itself invisible in UIA, so readers immediately exit browse
        // mode and follow Win32 focus wherever it goes next.
        if (_webViewController != null) _webViewController.IsVisible = false;

        // Move Win32 focus to the message list while WebView2 is still visible so
        // Chromium must release keyboard focus before we hide its HWND.
        FocusActiveMessagePanel();

        _vm.IsMessageOpen = false;
        _vm.MessageDetail = null;
        _messageSplit.Panel2Collapsed = true;
        _readingPane.Visible = false;

        // Queue a second focus + announcement after the pump settles.
        // The Announce is needed because virtual-mode ListView UIA events do not
        // always fire reliably when focus is restored programmatically.
        BeginInvoke(() =>
        {
            FocusActiveMessagePanel();
            var msg = _vm.SelectedMessage;
            var text = msg != null ? MessageAnnouncement(msg) : "Message list.";
            AccessibilityHelper.Announce(this, text, interrupt: true);
        });

        LogService.Log($"[Escape] CloseReadingPane done: Panel2Collapsed={_messageSplit.Panel2Collapsed}");
    }

    private void FocusStatusBar()
    {
        _statusLabel.Focus();
        _statusLabel.SelectAll();
        AccessibilityHelper.Announce(this, $"Status bar: {_vm.StatusText}");
    }

    private int GetFocusedPaneIndex()
    {
        if (_toolStrip.ContainsFocus)       return 0;
        if (_accountList.ContainsFocus)     return 1;
        if (_folderTree.ContainsFocus)      return 2;
        if (_messageList.ContainsFocus || _conversationTree.ContainsFocus) return 3;
        if (_webView.ContainsFocus)         return 4;
        if (_statusBar.ContainsFocus)       return 5;
        return 0;
    }

    private async Task CycleFocusAsync(bool forward)
    {
        var panes = new List<int> { 0, 1, 2, 3 };
        if (_vm.IsMessageOpen && _webViewReady) panes.Add(4);
        panes.Add(5);

        int current    = GetFocusedPaneIndex();
        int currentPos = panes.IndexOf(current);
        if (currentPos < 0) currentPos = 0;
        int nextPos = forward
            ? (currentPos + 1) % panes.Count
            : (currentPos - 1 + panes.Count) % panes.Count;

        switch (panes[nextPos])
        {
            case 0: _toolStrip.Focus(); break;
            case 1: _accountList.Focus(); break;
            case 2: _folderTree.Focus(); break;
            case 3: FocusActiveMessagePanel(); break;
            case 4:
                if (_vm.IsMessageOpen && _webViewReady)
                {
                    _webView.Focus();
                    await _webView.CoreWebView2.ExecuteScriptAsync("document.body.focus()");
                }
                else FocusStatusBar();
                break;
            case 5: FocusStatusBar(); break;
        }
    }

    // ── Open dialogs ──────────────────────────────────────────────────────────────

    private void OpenCommandPalette()
    {
        CommandDefinition? cmd;
        using (var dlg = new CommandPaletteForm(_registry) { Owner = this })
        {
            dlg.ShowDialog();
            cmd = dlg.SelectedCommand;
        }
        // Form is fully closed and disposed before Execute runs, so
        // commands that open modal dialogs get a clean owner (this form).
        cmd?.Execute();
        FocusActiveMessagePanel();
    }

    private async void OpenFolderPicker()
    {
        if (_vm.CachedFolders.Count == 0) return;
        using var picker = new FolderPickerForm(_vm.Accounts, _vm.CachedFolders, MainViewModel.AllMailFolder)
            { Owner = this };
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedFolder == null) return;

        if (picker.SelectedAccount != null && picker.SelectedAccount.Id != _vm.SelectedAccount?.Id)
            await _vm.SelectAccountCommand.ExecuteAsync(picker.SelectedAccount);

        var target = _vm.Folders.FirstOrDefault(f =>
                         !f.IsHeader &&
                         f.FullName.Equals(picker.SelectedFolder.FullName, StringComparison.OrdinalIgnoreCase) &&
                         (picker.SelectedFolder.AccountId == Guid.Empty || f.AccountId == picker.SelectedFolder.AccountId))
                     ?? picker.SelectedFolder;
        await _vm.SelectFolderCommand.ExecuteAsync(target);
        FocusActiveMessagePanel();
    }

    private void OnComposeRequested(ComposeModel model)
    {
        var composeVm = new ComposeViewModel(_smtp, _accountService, _credentials, _imap);
        composeVm.Seed(model);
        var form = new ComposeForm(composeVm) { Owner = this };
        composeVm.CloseRequested += form.Close;
        form.Show();
    }

    private void OnManageAccounts()
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap, _oauth);
        using var dlg = new AccountManagerForm(accountVm) { Owner = this };
        dlg.ShowDialog();
        _vm.RefreshAccountList();
    }

    private void OnOpenAccountSettings(AccountModel account)
    {
        var accountVm = new AccountManagerViewModel(_accountService, _credentials, _imap, _oauth);
        using var dlg = new AccountManagerForm(accountVm) { Owner = this };
        accountVm.SelectedAccount = accountVm.Accounts.FirstOrDefault(a => a.Id == account.Id);
        dlg.ShowDialog();
        _vm.RefreshAccountList();
    }

    // ── Folder context menu handlers ──────────────────────────────────────────────

    private FolderTreeNode? GetSelectedFolderNode() =>
        _folderTree.SelectedNode?.Tag as FolderTreeNode;

    private async void OnFolderMenuNew(object? sender, EventArgs e)
    {
        var node = GetSelectedFolderNode();
        if (node == null) return;

        var parentFolder = node.IsHeader ? null : node.Folder;
        var accountId    = parentFolder?.AccountId
                          ?? _vm.Accounts.FirstOrDefault(a => a.DisplayName == node.Label)?.Id
                          ?? _vm.SelectedAccount?.Id;
        if (accountId == null || accountId == Guid.Empty) return;

        using var dlg = new NewFolderForm { Owner = this };
        dlg.ParentFolderName = parentFolder?.DisplayName ?? node.Label;
        if (dlg.ShowDialog() != DialogResult.OK) return;

        await _vm.CreateFolderAndRefreshAsync(accountId.Value, parentFolder?.FullName, dlg.FolderName);
    }

    private async void OnFolderMenuMove(object? sender, EventArgs e)
    {
        var node = GetSelectedFolderNode();
        if (node?.Folder == null || node.IsHeader) return;
        if (_vm.CachedFolders.Count == 0) return;

        using var picker = new FolderPickerForm(_vm.Accounts, _vm.CachedFolders, title: "Move Folder To") { Owner = this };
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedFolder == null) return;
        await _vm.MoveFolderToAsync(node, picker.SelectedFolder);
    }

    private async void OnFolderMenuCopy(object? sender, EventArgs e)
    {
        var node = GetSelectedFolderNode();
        if (node?.Folder == null || node.IsHeader) return;
        if (_vm.CachedFolders.Count == 0) return;

        using var picker = new FolderPickerForm(_vm.Accounts, _vm.CachedFolders, title: "Copy Folder To") { Owner = this };
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedFolder == null) return;
        await _vm.CopyFolderToAsync(node, picker.SelectedFolder);
    }

    private async void OnFolderMenuDelete(object? sender, EventArgs e)
    {
        var node = GetSelectedFolderNode();
        if (node != null)
            await _vm.DeleteFolderAsync(node);
    }

    // ── Message context menu handlers ─────────────────────────────────────────────

    private async void OnMsgMenuMove(object? sender, EventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;
        using var picker = new FolderPickerForm(_vm.Accounts, _vm.CachedFolders, title: "Move to Folder") { Owner = this };
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedFolder == null) return;
        await _vm.MoveSelectedMessagesToFolderAsync(messages, picker.SelectedFolder);
    }

    private async void OnMsgMenuCopy(object? sender, EventArgs e)
    {
        var messages = GetSelectedMessages();
        if (messages.Count == 0 || _vm.CachedFolders.Count == 0) return;
        using var picker = new FolderPickerForm(_vm.Accounts, _vm.CachedFolders, title: "Copy to Folder") { Owner = this };
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedFolder == null) return;
        await _vm.CopySelectedMessagesToFolderAsync(messages, picker.SelectedFolder);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static Label MakeReadingLabel(string prefix) => new()
    {
        Dock      = DockStyle.Fill,
        AutoSize  = false,
        TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        Text      = string.Empty,
    };

    private static void AddRpRow(TableLayoutPanel table, string caption, Label field)
    {
        var lbl = new Label
        {
            Text      = caption,
            Dock      = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleRight,
            Font      = new System.Drawing.Font(SystemFonts.DefaultFont!, System.Drawing.FontStyle.Bold),
        };
        table.Controls.Add(lbl);
        table.Controls.Add(field);
    }

    private static string MessageAnnouncement(MailMessageSummary msg) =>
        $"{msg.ReadStatusLabel}. {msg.From}. {msg.Subject}. {msg.DateDisplay}.";

    // Catch Escape at the WM_KEYDOWN level — works for all WinForms controls and
    // for keys that WebView2 relays back to the host as accelerators.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && !_messageSplit.Panel2Collapsed)
        {
            LogService.Log("[Escape] ProcessCmdKey → CloseReadingPane");
            CloseReadingPane();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
