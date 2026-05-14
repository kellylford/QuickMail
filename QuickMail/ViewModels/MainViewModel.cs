using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IImapService _imap;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly ILocalStoreService _localStore;
    private readonly ISyncService _syncService;
    private readonly IConfigService _configService;

    // UI-thread context captured at construction (ViewModel is created on the UI thread)
    private readonly SynchronizationContext _uiSyncContext;

    // Separate CTS per operation type so they can't cancel each other accidentally
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _folderCts;
    private CancellationTokenSource? _messageCts;
    private CancellationTokenSource? _bgSyncCts;

    // How many messages to fetch; increased by LoadMoreMessagesCommand
    private int _messageLimit = 100;

    // Version stamp for conversation rebuilds; latest wins, stale results discarded
    private int _conversationRebuildVersion;

    // Retains folder lists for every account that has been connected this session
    private readonly Dictionary<Guid, List<MailFolderModel>> _cachedFolders = new();
    public IReadOnlyDictionary<Guid, List<MailFolderModel>> CachedFolders => _cachedFolders;

    /// <summary>Sentinel that represents the cross-account virtual All Mail view.</summary>
    public static readonly MailFolderModel AllMailFolder = new()
    {
        FullName    = "\x00AllMail",
        DisplayName = "All Mail"
    };
    private bool IsAllMailSelected => SelectedFolder?.FullName == AllMailFolder.FullName;

    [ObservableProperty]
    private ObservableCollection<AccountModel> _accounts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount))]
    private AccountModel? _selectedAccount;

    [ObservableProperty]
    private ObservableCollection<MailFolderModel> _folders = [];

    [ObservableProperty]
    private ObservableCollection<FolderTreeNode> _folderTree = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFolder))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private MailFolderModel? _selectedFolder;

    [ObservableProperty]
    private ObservableCollection<MailMessageSummary> _messages = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMessage))]
    private MailMessageSummary? _selectedMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private MailMessageDetail? _messageDetail;

    /// <summary>True when a message body has been loaded and the reading pane should be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isMessageOpen;

    [ObservableProperty]
    private bool _isConversationView = false;

    [ObservableProperty]
    private ObservableCollection<ConversationGroup> _conversations = [];

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showMessageStatus;

    public bool HasSelectedAccount  => SelectedAccount  != null;
    public bool HasSelectedFolder   => SelectedFolder   != null;
    public bool HasSelectedMessage  => SelectedMessage  != null;

    public string WindowTitle
    {
        get
        {
            if (IsMessageOpen && !string.IsNullOrWhiteSpace(MessageDetail?.Subject))
                return $"{MessageDetail.Subject} - QuickMail";
            if (SelectedFolder != null && !SelectedFolder.IsHeader)
                return $"{SelectedFolder.DisplayName} - QuickMail";
            return "QuickMail";
        }
    }

    public MainViewModel(
        IImapService imap,
        IAccountService accountService,
        ICredentialService credentials,
        ILocalStoreService localStore,
        ISyncService syncService,
        IConfigService configService,
        ICommandRegistry commandRegistry)
    {
        _uiSyncContext  = SynchronizationContext.Current ?? new SynchronizationContext();
        _imap           = imap;
        _accountService = accountService;
        _credentials    = credentials;
        _localStore     = localStore;
        _syncService    = syncService;
        _configService  = configService;

        var cfg = _configService.Load();
        _showMessageStatus  = cfg.ShowMessageStatus;
        _isConversationView = cfg.ConversationView;

        _syncService.FolderSynced    += msgs => _uiSyncContext.Post(_ => OnFolderSynced(msgs), null);
        _syncService.MessagesRemoved += msgs => _uiSyncContext.Post(_ => OnMessagesRemoved(msgs), null);

        RegisterCommands(commandRegistry);
    }

    public void LoadAccountList()
    {
        Accounts = new ObservableCollection<AccountModel>(_accountService.LoadAccounts());
    }

    private void RegisterCommands(ICommandRegistry registry)
    {
        registry.Register(new CommandDefinition(
            id: "mail.new", category: "Mail", title: "New Message",
            execute: () => NewMessageCommand.Execute(null),
            shortcut: Keys.Control | Keys.N));

        registry.Register(new CommandDefinition(
            id: "mail.reply", category: "Mail", title: "Reply",
            execute: () => ReplyCommand.Execute(null),
            shortcut: Keys.Control | Keys.R,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.replyAll", category: "Mail", title: "Reply All",
            execute: () => ReplyAllCommand.Execute(null),
            shortcut: Keys.Control | Keys.Shift | Keys.R,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.forward", category: "Mail", title: "Forward",
            execute: () => ForwardCommand.Execute(null),
            shortcut: Keys.Control | Keys.F,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.delete", category: "Mail", title: "Delete",
            execute: () => DeleteMessageCommand.Execute(null),
            shortcut: Keys.Delete,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.refresh", category: "Mail", title: "Refresh",
            execute: () => RefreshCommand.Execute(null),
            shortcut: Keys.F5));

        registry.Register(new CommandDefinition(
            id: "mail.loadMore", category: "Mail", title: "Load More Messages",
            execute: () => LoadMoreMessagesCommand.Execute(null),
            shortcut: Keys.Control | Keys.M));

        registry.Register(new CommandDefinition(
            id: "mail.emptyTrash", category: "Mail", title: "Empty Trash",
            execute: () => EmptyTrashCommand.Execute(null),
            shortcut: Keys.Control | Keys.Shift | Keys.E));

        registry.Register(new CommandDefinition(
            id: "view.toggleConversation", category: "View", title: "Toggle Conversation View",
            execute: () => IsConversationView = !IsConversationView));

        registry.Register(new CommandDefinition(
            id: "account.manage", category: "Account", title: "Manage Accounts",
            execute: () => ManageAccountsCommand.Execute(null)));

        registry.Register(new CommandDefinition(
            id: "help.userGuide", category: "Help", title: "Open User Guide",
            execute: () => ViewUserGuideCommand.Execute(null),
            shortcut: Keys.F1));
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows All Mail from the local store immediately (no network).
    /// Called first in MainForm.Load so the UI is populated before any IMAP work begins.
    /// </summary>
    public async Task InitialLoadAsync()
    {
        SelectedFolder = AllMailFolder;
        var cached = await _localStore.LoadAllSummariesAsync();
        Messages = new ObservableCollection<MailMessageSummary>(cached);
        StatusText = cached.Count > 0
            ? $"{cached.Count} messages (cached — syncing…)"
            : "Connecting and syncing…";
        RebuildFolderListFromCache();
    }

    /// <summary>
    /// Connects all accounts then runs a background incremental sync.
    /// New messages trickle into the UI via the FolderSynced event.
    /// Fire-and-forget from Load; does not block the UI.
    /// </summary>
    public async Task StartBackgroundSyncAsync()
    {
        _bgSyncCts?.Cancel();
        _bgSyncCts = new CancellationTokenSource();
        var ct = _bgSyncCts.Token;

        await ConnectAllAccountsAsync();
        if (_cachedFolders.Count == 0) return;

        StatusText = "Syncing mail…";
        using var announceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var announceTask = AnnounceLoadingProgressAsync(announceCts.Token);
        try
        {
            await _syncService.SyncAllAccountsAsync(Accounts, _cachedFolders, ct);
            await announceCts.CancelAsync();
            await announceTask;
            var count = Messages.Count;
            StatusText = $"{count} messages.";
            Announce($"{count} {(count == 1 ? "message" : "messages")} loaded.");
        }
        catch (OperationCanceledException) { await announceCts.CancelAsync(); }
        catch (Exception ex)
        {
            await announceCts.CancelAsync();
            LogService.Log("BackgroundSync", ex);
            StatusText = $"Sync error: {ex.Message}";
        }
    }

    private async Task AnnounceLoadingProgressAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(10_000, ct);
                var count = Messages.Count;
                Announce($"{count} {(count == 1 ? "message" : "messages")} loaded so far.");
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── FolderSynced merge ───────────────────────────────────────────────────────

    // Called on the UI thread by SyncService after each folder sync.
    // Inserts truly new messages into the live collection in sorted order.
    private void OnFolderSynced(IReadOnlyList<MailMessageSummary> incoming)
    {
        if (!IsAllMailSelected) return;

        foreach (var msg in incoming.OrderByDescending(m => m.Date))
        {
            if (Messages.Any(e => e.UniqueId   == msg.UniqueId &&
                                  e.AccountId  == msg.AccountId &&
                                  e.FolderName == msg.FolderName))
                continue;

            InsertMessageSorted(msg);
        }

        StatusText = $"{Messages.Count} messages";

        if (IsConversationView)
            ScheduleConversationRebuild();
    }

    private void OnMessagesRemoved(IReadOnlyList<MailMessageSummary> removed)
    {
        bool removedOpen = false;
        foreach (var msg in removed)
        {
            var existing = Messages.FirstOrDefault(e =>
                e.UniqueId   == msg.UniqueId &&
                e.AccountId  == msg.AccountId &&
                e.FolderName == msg.FolderName);

            if (existing == null) continue;

            if (SelectedMessage == existing) removedOpen = true;
            Messages.Remove(existing);
        }

        if (removedOpen)
        {
            SelectedMessage = Messages.Count > 0 ? Messages[0] : null;
            MessageDetail   = null;
            IsMessageOpen   = false;
        }

        if (removed.Count > 0)
            StatusText = $"{Messages.Count} messages";

        if (IsConversationView)
            ScheduleConversationRebuild();
    }

    // Binary-insert into the descending-by-date Messages collection.
    private void InsertMessageSorted(MailMessageSummary msg)
    {
        int lo = 0, hi = Messages.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Messages[mid].Date >= msg.Date) lo = mid + 1;
            else hi = mid;
        }
        Messages.Insert(lo, msg);
    }

    // ── Account / folder selection ───────────────────────────────────────────────

    /// <summary>
    /// Connects every configured account in sequence, populates the cache, then
    /// rebuilds the unified folder list.  Called from StartBackgroundSyncAsync.
    /// </summary>
    public async Task ConnectAllAccountsAsync()
    {
        if (Accounts.Count == 0) return;

        StatusText = Accounts.Count == 1
            ? $"Connecting to {Accounts[0].DisplayName}…"
            : $"Connecting to {Accounts.Count} accounts…";
        IsBusy = true;

        var tasks = Accounts.Select(account => ConnectOneAccountAsync(account)).ToList();
        var results = await Task.WhenAll(tasks);

        foreach (var (id, folders) in results)
        {
            if (folders != null)
                _cachedFolders[id] = folders;
        }

        IsBusy = false;
        RebuildFolderListFromCache();
        StatusText = _cachedFolders.Count > 0
            ? $"{_cachedFolders.Count} of {Accounts.Count} account(s) connected."
            : "No accounts could be connected.";
    }

    private async Task<(Guid Id, List<MailFolderModel>? Folders)> ConnectOneAccountAsync(AccountModel account)
    {
        string? password = null;
        if (account.AuthType == Models.AuthType.Password)
        {
            password = _credentials.GetPassword(account.Id);
            if (string.IsNullOrEmpty(password)) return (account.Id, null);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _imap.ConnectAsync(account, password, cts.Token);
            var folderList = await _imap.GetFoldersAsync(account.Id, cts.Token);
            return (account.Id, folderList);
        }
        catch (OperationCanceledException)
        {
            LogService.Log($"ConnectAll/{account.DisplayName}: timed out");
            return (account.Id, null);
        }
        catch (Exception ex)
        {
            LogService.Log($"ConnectAll/{account.DisplayName}", ex);
            return (account.Id, null);
        }
    }

    private void RebuildFolderListFromCache()
    {
        var saved = SelectedFolder;
        var items = new List<MailFolderModel> { AllMailFolder };

        foreach (var account in Accounts)
        {
            if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;

            items.Add(new MailFolderModel
            {
                IsHeader    = true,
                DisplayName = account.DisplayName,
                FullName    = $"\x00Header:{account.Id}",
                AccountId   = account.Id
            });
            items.AddRange(folders);
        }

        Folders = new ObservableCollection<MailFolderModel>(items);

        if (saved != null && !saved.IsHeader)
        {
            var restored = items.FirstOrDefault(f =>
                f.FullName == saved.FullName && f.AccountId == saved.AccountId);
            if (restored != null)
                SelectedFolder = restored;
        }

        BuildFolderTree();
    }

    private void BuildFolderTree()
    {
        var roots = new List<FolderTreeNode>();

        roots.Add(new FolderTreeNode { Folder = AllMailFolder, Label = AllMailFolder.DisplayName });

        foreach (var account in Accounts)
        {
            if (_cachedFolders.TryGetValue(account.Id, out var folders) && folders.Count > 0)
            {
                var accountRoots = FolderTreeBuilder.Build(folders, account);
                roots.AddRange(accountRoots);
            }
            else
            {
                roots.Add(new FolderTreeNode
                {
                    IsHeader = true,
                    Label    = account.DisplayName,
                    Folder   = null,
                });
            }
        }

        FolderTree = new ObservableCollection<FolderTreeNode>(roots);
    }

    // ── Conversation grouping ─────────────────────────────────────────────────────

    partial void OnIsConversationViewChanged(bool value)
    {
        if (value)
            ScheduleConversationRebuild();
        else
            Conversations = [];

        var cfg = _configService.Load();
        cfg.ConversationView = value;
        _configService.Save(cfg);
    }

    partial void OnMessagesChanged(ObservableCollection<MailMessageSummary> value)
    {
        if (IsConversationView)
            ScheduleConversationRebuild();
    }

    /// <summary>
    /// Rebuilds Conversations on a background thread to avoid blocking the UI.
    /// Uses a version stamp so that rapid successive calls only apply the latest result.
    /// Must be called from the UI thread (takes a snapshot before handing off).
    /// </summary>
    private void ScheduleConversationRebuild()
    {
        var version  = Interlocked.Increment(ref _conversationRebuildVersion);
        var snapshot = Messages.ToList();
        Task.Run(() =>
        {
            var groups = ConversationBuilder.Build(snapshot);
            _uiSyncContext.Post(_ =>
            {
                if (version == _conversationRebuildVersion)
                    Conversations = new ObservableCollection<ConversationGroup>(groups);
            }, null);
        });
    }

    [RelayCommand]
    private async Task SelectAccountAsync(AccountModel? account)
    {
        if (account == null) return;
        SelectedAccount = account;
        StatusText = $"Connecting to {account.DisplayName}…";
        IsBusy = true;
        try
        {
            var password = _credentials.GetPassword(account.Id);
            if (string.IsNullOrEmpty(password))
            {
                StatusText = $"No password stored for {account.DisplayName}.";
                return;
            }
            _connectCts?.Cancel();
            _connectCts = new CancellationTokenSource();
            await _imap.ConnectAsync(account, password, _connectCts.Token);
            var folderList = await _imap.GetFoldersAsync(account.Id, _connectCts.Token);
            _cachedFolders[account.Id] = folderList;
            RebuildFolderListFromCache();
            StatusText = $"Connected to {account.DisplayName}. Press Enter on a folder to load messages.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Connection cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            LogService.Log("SelectAccount", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SelectFolderAsync(MailFolderModel? folder)
    {
        if (folder == null || folder.IsHeader) return;
        _messageLimit = 100;
        SelectedFolder = folder;
        MessageDetail  = null;
        IsMessageOpen  = false;

        if (folder.FullName == AllMailFolder.FullName)
            await FetchAllMailAsync();
        else
        {
            if (folder.AccountId != Guid.Empty)
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == folder.AccountId) ?? SelectedAccount;
            await FetchFolderAsync();
        }
    }

    [RelayCommand]
    private async Task LoadMoreMessagesAsync()
    {
        _messageLimit += 100;
        if (IsAllMailSelected)
            await FetchAllMailAsync();
        else if (SelectedFolder != null && SelectedFolder.AccountId != Guid.Empty)
            await FetchFolderAsync();
    }

    private async Task FetchFolderAsync()
    {
        if (SelectedFolder == null) return;
        var accountId = SelectedFolder.AccountId;
        if (accountId == Guid.Empty) return;
        var folder = SelectedFolder;
        Messages.Clear();
        StatusText = $"Loading {folder.DisplayName}…";
        IsBusy = true;
        try
        {
            _folderCts?.Cancel();
            _folderCts = new CancellationTokenSource();
            var list = await _imap.GetMessageSummariesAsync(accountId, folder.FullName, _messageLimit, _folderCts.Token);
            Messages = new ObservableCollection<MailMessageSummary>(list);
            StatusText = list.Count == 0 ? "No messages" : $"{list.Count} messages loaded.";
            _ = _localStore.UpsertSummariesAsync(list);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Message list load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load messages: {ex.Message}";
            LogService.Log("SelectFolder", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SelectMessageAsync(MailMessageSummary? summary)
    {
        if (summary == null) return;
        if (SelectedAccount?.Id != summary.AccountId)
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == summary.AccountId) ?? SelectedAccount;
        if (SelectedAccount == null) return;

        SelectedMessage = summary;
        MessageDetail   = null;
        IsMessageOpen   = false;
        StatusText = "Loading message…";
        IsBusy = true;
        try
        {
            _messageCts?.Cancel();
            _messageCts = new CancellationTokenSource();

            var detail = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId);

            if (detail == null)
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId, _messageCts.Token);
                _ = _localStore.UpsertDetailAsync(detail);
            }

            MessageDetail = detail;
            IsMessageOpen = true;
            summary.IsRead = true;
            summary.HasAttachments = detail.Attachments.Count > 0;
            _ = _localStore.UpdateIsReadAsync(summary.AccountId, summary.FolderName, summary.UniqueId, true);

            if (string.IsNullOrEmpty(summary.Preview))
            {
                var lines   = _configService.Load().GetPreviewLines(summary.AccountId);
                var preview = ExtractPreview(detail.PlainTextBody, detail.HtmlBody, lines);
                if (!string.IsNullOrEmpty(preview))
                {
                    summary.Preview = preview;
                    _ = _localStore.UpdatePreviewAsync(summary.AccountId, summary.FolderName, summary.UniqueId, preview);
                }
            }

            StatusText = "Message loaded. Press Escape to return to message list.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Message load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load message: {ex.Message}";
            LogService.Log("SelectMessage", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsAllMailSelected)
            await FetchAllMailAsync();
        else if (SelectedFolder != null && SelectedFolder.AccountId != Guid.Empty)
            await FetchFolderAsync();
    }

    private async Task FetchAllMailAsync()
    {
        Messages.Clear();
        StatusText = "Loading All Mail…";
        IsBusy = true;

        _folderCts?.Cancel();
        _folderCts = new CancellationTokenSource();
        var ct = _folderCts.Token;

        var all = new List<MailMessageSummary>();

        try
        {
            var perAccountTasks = Accounts
                .Where(a => _cachedFolders.ContainsKey(a.Id))
                .Select(account => FetchAccountAllFoldersAsync(account, ct));

            var accountResults = await Task.WhenAll(perAccountTasks);
            foreach (var batch in accountResults)
                all.AddRange(batch);

            var sorted = all.OrderByDescending(m => m.Date).ToList();
            Messages = new ObservableCollection<MailMessageSummary>(sorted);
            StatusText = sorted.Count == 0
                ? "No messages across connected accounts."
                : $"{sorted.Count} messages across all accounts.";
            _ = _localStore.UpsertSummariesAsync(sorted);
        }
        catch (OperationCanceledException)
        {
            StatusText = "All Mail load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load All Mail: {ex.Message}";
            LogService.Log("FetchAllMail", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<List<MailMessageSummary>> FetchAccountAllFoldersAsync(
        AccountModel account, CancellationToken ct)
    {
        var result = new List<MailMessageSummary>();
        if (!_cachedFolders.TryGetValue(account.Id, out var folders)) return result;

        foreach (var folder in folders)
        {
            if (folder.ExcludeFromAllMail) continue;
            ct.ThrowIfCancellationRequested();
            try
            {
                var msgs = await _imap.GetMessageSummariesAsync(
                    account.Id, folder.FullName, _messageLimit, ct);
                result.AddRange(msgs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"AllMail fetch {account.DisplayName}/{folder.FullName}", ex);
            }
        }
        return result;
    }

    // ── Delete / Trash ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteMessageAsync()
    {
        if (SelectedMessage == null) return;
        await DeleteMessagesAsync([SelectedMessage]);
    }

    public async Task DeleteMessagesAsync(IReadOnlyList<MailMessageSummary> toDelete)
    {
        if (toDelete.Count == 0) return;

        var minIdx = toDelete.Min(m => Messages.IndexOf(m));
        var label  = toDelete.Count == 1 ? "message" : $"{toDelete.Count} messages";
        StatusText    = $"Deleting {label}…";
        IsBusy        = true;
        MessageDetail = null;
        IsMessageOpen = false;

        try
        {
            _messageCts?.Cancel();
            _messageCts = new CancellationTokenSource();

            var groups = toDelete.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
            {
                var uids = group.Select(m => m.UniqueId).ToList();
                await _imap.MoveToTrashBatchAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, _messageCts.Token);
                await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            foreach (var msg in toDelete)
                Messages.Remove(msg);

            if (IsConversationView)
                ScheduleConversationRebuild();

            if (Messages.Count > 0)
            {
                var landIdx = Math.Max(0, Math.Min(minIdx, Messages.Count - 1));
                SelectedMessage = Messages[landIdx];
                StatusText = $"{toDelete.Count} {(toDelete.Count == 1 ? "message" : "messages")} deleted.";
                MessageListFocusRequested?.Invoke();
            }
            else
            {
                SelectedMessage = null;
                StatusText = $"{toDelete.Count} {(toDelete.Count == 1 ? "message" : "messages")} deleted. Folder is now empty.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Delete cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
            LogService.Log("DeleteMessages", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Compose / accounts ───────────────────────────────────────────────────────

    public event Action<ComposeModel>? ComposeRequested;
    public event Action? ManageAccountsRequested;
    public event Action? MessageListFocusRequested;
    public event EventHandler<string>? AnnouncementRequested;

    private void Announce(string text)
    {
        if (!string.IsNullOrEmpty(text))
            AnnouncementRequested?.Invoke(this, text);
    }

    [RelayCommand]
    private async Task Reply()
    {
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAll()
    {
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task Forward()
    {
        var detail = await EnsureDetailAsync();
        if (detail == null) return;

        var model = ComposeViewModel.CreateForward(detail, detail.AccountId);

        if (detail.Attachments.Count > 0)
        {
            IsBusy = true;
            StatusText = "Preparing forward…";
            try
            {
                var summary = SelectedMessage!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                foreach (var att in detail.Attachments)
                {
                    if (!att.IsLoaded && att.PartSpecifier != null)
                    {
                        try
                        {
                            att.Content = await _imap.DownloadAttachmentAsync(
                                summary.AccountId, summary.FolderName, summary.UniqueId,
                                att.PartSpecifier, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            LogService.Log($"Forward: failed to download '{att.FileName}'", ex);
                        }
                    }
                }
                model.Attachments = detail.Attachments;
            }
            finally
            {
                IsBusy = false;
                StatusText = string.Empty;
            }
        }

        ComposeRequested?.Invoke(model);
    }

    private async Task<MailMessageDetail?> EnsureDetailAsync()
    {
        var summary = SelectedMessage;
        if (summary == null) return null;

        if (MessageDetail != null &&
            MessageDetail.UniqueId   == summary.UniqueId &&
            MessageDetail.AccountId  == summary.AccountId &&
            MessageDetail.FolderName == summary.FolderName)
            return MessageDetail;

        if (SelectedAccount?.Id != summary.AccountId)
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == summary.AccountId) ?? SelectedAccount;
        if (SelectedAccount == null) return null;

        try
        {
            var detail = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId);

            if (detail == null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId, cts.Token);
                _ = _localStore.UpsertDetailAsync(detail);
            }

            return detail;
        }
        catch (Exception ex)
        {
            LogService.Log("EnsureDetail", ex);
            return null;
        }
    }

    [RelayCommand]
    private void NewMessage()
    {
        var account = Accounts.FirstOrDefault(a => a.IsDefault)
                      ?? SelectedAccount
                      ?? Accounts.FirstOrDefault();
        if (account == null) return;
        ComposeRequested?.Invoke(new ComposeModel { AccountId = account.Id });
    }

    /// <summary>True when the currently selected folder is a Drafts folder.</summary>
    public bool IsSelectedFolderDrafts =>
        SelectedFolder != null &&
        (SelectedFolder.DisplayName.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
         SelectedFolder.FullName.Contains("draft", StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task OpenDraftAsync()
    {
        var summary = SelectedMessage;
        if (summary == null || SelectedAccount == null) return;

        IsBusy = true;
        StatusText = "Opening draft…";
        try
        {
            _messageCts?.Cancel();
            _messageCts = new CancellationTokenSource();

            var detail = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId);

            if (detail == null)
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId, _messageCts.Token);
            }

            var model = new ComposeModel
            {
                AccountId       = summary.AccountId,
                To              = detail.To,
                Cc              = detail.Cc,
                Subject         = detail.Subject,
                Body            = detail.PlainTextBody,
                DraftUid        = summary.UniqueId,
                DraftFolderName = summary.FolderName,
            };

            foreach (var att in detail.Attachments)
            {
                if (!att.IsLoaded && att.PartSpecifier != null)
                {
                    try
                    {
                        att.Content = await _imap.DownloadAttachmentAsync(
                            summary.AccountId, summary.FolderName, summary.UniqueId,
                            att.PartSpecifier, _messageCts.Token);
                    }
                    catch (Exception ex)
                    {
                        LogService.Log($"OpenDraft: failed to hydrate attachment '{att.FileName}'", ex);
                    }
                }
            }
            model.Attachments = detail.Attachments;

            StatusText = string.Empty;
            ComposeRequested?.Invoke(model);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Draft load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open draft: {ex.Message}";
            LogService.Log("OpenDraft", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        var accountsToEmpty = IsAllMailSelected
            ? Accounts.ToList()
            : (SelectedAccount != null ? [SelectedAccount] : Accounts.Take(1).ToList());

        if (accountsToEmpty.Count == 0) return;

        StatusText = "Emptying trash…";
        IsBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            int totalDeleted = 0;
            foreach (var account in accountsToEmpty)
                totalDeleted += await _imap.EmptyTrashAsync(account.Id, cts.Token);

            var msg = totalDeleted == 1 ? "1 message deleted from trash." : $"{totalDeleted} messages deleted from trash.";
            StatusText = msg;
            Announce(msg);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Empty trash timed out.";
        }
        catch (Exception ex)
        {
            StatusText = $"Empty trash failed: {ex.Message}";
            LogService.Log("EmptyTrash", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ManageAccounts() => ManageAccountsRequested?.Invoke();

    // ── Account context menu commands ─────────────────────────────────────────

    public event Action<AccountModel>? OpenAccountSettingsRequested;

    [RelayCommand]
    private void DeleteAccount(AccountModel? account)
    {
        if (account == null) return;

        var result = MessageBox.Show(
            $"Remove the account '{account.DisplayName}'? This only removes it from QuickMail — your mail on the server is not affected.",
            "Remove Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        _credentials.DeletePassword(account.Id);
        Accounts.Remove(account);
        _accountService.SaveAccounts([.. Accounts]);

        _cachedFolders.Remove(account.Id);
        RebuildFolderListFromCache();

        if (SelectedAccount?.Id == account.Id)
        {
            SelectedAccount  = Accounts.FirstOrDefault();
            SelectedFolder   = AllMailFolder;
            Messages.Clear();
        }

        StatusText = $"Account '{account.DisplayName}' removed.";
    }

    [RelayCommand]
    private void OpenAccountSettings(AccountModel? account)
    {
        if (account != null)
            OpenAccountSettingsRequested?.Invoke(account);
    }

    // ── Folder context menu commands ──────────────────────────────────────────

    public async Task RefreshFolderListAsync(Guid accountId)
    {
        try
        {
            using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var folderList  = await _imap.GetFoldersAsync(accountId, cts.Token);
            _cachedFolders[accountId] = folderList;
            RebuildFolderListFromCache();
        }
        catch (Exception ex)
        {
            LogService.Log("RefreshFolderList", ex);
            StatusText = $"Failed to refresh folders: {ex.Message}";
        }
    }

    public async Task CreateFolderAndRefreshAsync(Guid accountId, string? parentFolderName, string name)
    {
        StatusText = $"Creating folder '{name}'…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _imap.CreateFolderAsync(accountId, parentFolderName, name, cts.Token);
            await RefreshFolderListAsync(accountId);
            StatusText = $"Folder '{name}' created.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to create folder: {ex.Message}";
            LogService.Log("CreateFolder", ex);
        }
        finally { IsBusy = false; }
    }

    public async Task MoveFolderToAsync(FolderTreeNode node, MailFolderModel destination)
    {
        if (node.Folder == null) return;
        StatusText = $"Moving folder '{node.Label}'…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _imap.RenameFolderAsync(
                node.Folder.AccountId,
                node.Folder.FullName,
                node.Folder.DisplayName,
                destination.FullName,
                cts.Token);
            await RefreshFolderListAsync(node.Folder.AccountId);
            StatusText = $"Folder '{node.Label}' moved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to move folder: {ex.Message}";
            LogService.Log("MoveFolder", ex);
        }
        finally { IsBusy = false; }
    }

    public async Task CopyFolderToAsync(FolderTreeNode node, MailFolderModel destination)
    {
        if (node.Folder == null) return;
        StatusText = $"Copying folder '{node.Label}'…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await _imap.CopyFolderAsync(
                node.Folder.AccountId,
                node.Folder.FullName,
                destination.FullName,
                cts.Token);
            await RefreshFolderListAsync(node.Folder.AccountId);
            StatusText = $"Folder '{node.Label}' copied.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to copy folder: {ex.Message}";
            LogService.Log("CopyFolder", ex);
        }
        finally { IsBusy = false; }
    }

    public async Task DeleteFolderAsync(FolderTreeNode node)
    {
        if (node.Folder == null || node.IsHeader) return;

        var result = MessageBox.Show(
            $"Delete the folder '{node.Label}' and move all its messages to Trash?",
            "Delete Folder",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;

        StatusText = $"Deleting folder '{node.Label}'…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _imap.DeleteFolderAsync(node.Folder.AccountId, node.Folder.FullName, cts.Token);

            if (SelectedFolder?.FullName == node.Folder.FullName)
            {
                SelectedFolder = AllMailFolder;
                await FetchAllMailAsync();
            }

            await RefreshFolderListAsync(node.Folder.AccountId);
            StatusText = $"Folder '{node.Label}' deleted.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to delete folder: {ex.Message}";
            LogService.Log("DeleteFolder", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Message move / copy ───────────────────────────────────────────────────

    public async Task MoveSelectedMessagesToFolderAsync(IReadOnlyList<MailMessageSummary> messages, MailFolderModel destination)
    {
        if (messages.Count == 0) return;

        var label  = messages.Count == 1 ? "message" : $"{messages.Count} messages";
        StatusText = $"Moving {label}…";
        IsBusy     = true;
        try
        {
            _messageCts?.Cancel();
            _messageCts = new CancellationTokenSource();

            var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
            {
                var uids = group.Select(m => m.UniqueId).ToList();
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids,
                    destination.FullName, _messageCts.Token);
                await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            foreach (var msg in messages)
                Messages.Remove(msg);

            if (IsConversationView)
                ScheduleConversationRebuild();

            StatusText = $"{messages.Count} {(messages.Count == 1 ? "message" : "messages")} moved to {destination.DisplayName}.";
            Announce(StatusText);
        }
        catch (OperationCanceledException) { StatusText = "Move cancelled."; }
        catch (Exception ex)
        {
            StatusText = $"Failed to move: {ex.Message}";
            LogService.Log("MoveMessages", ex);
        }
        finally { IsBusy = false; }
    }

    public async Task CopySelectedMessagesToFolderAsync(IReadOnlyList<MailMessageSummary> messages, MailFolderModel destination)
    {
        if (messages.Count == 0) return;

        var label  = messages.Count == 1 ? "message" : $"{messages.Count} messages";
        StatusText = $"Copying {label}…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var groups    = messages.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
                await _imap.CopyMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName,
                    group.Select(m => m.UniqueId).ToList(),
                    destination.FullName, cts.Token);

            StatusText = $"{messages.Count} {(messages.Count == 1 ? "message" : "messages")} copied to {destination.DisplayName}.";
            Announce(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to copy: {ex.Message}";
            LogService.Log("CopyMessages", ex);
        }
        finally { IsBusy = false; }
    }

    // ── Conversation context menu commands ────────────────────────────────────

    [RelayCommand]
    private void ExpandConversation(ConversationGroup? group)
    {
        if (group != null) group.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseConversation(ConversationGroup? group)
    {
        if (group != null) group.IsExpanded = false;
    }

    [RelayCommand]
    private void ExpandAllConversations()
    {
        foreach (var g in Conversations) g.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllConversations()
    {
        foreach (var g in Conversations) g.IsExpanded = false;
    }

    [RelayCommand]
    private async Task ReplyConversationAsync(ConversationGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAllConversationAsync(ConversationGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task ForwardConversationAsync(ConversationGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        await Forward();
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await DeleteMessagesAsync(group.Messages);
    }

    [RelayCommand]
    private void ViewUserGuide()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/kellylford/QuickMail/blob/main/USERGUIDE.md",
            UseShellExecute = true
        });
    }

    // ── Attachment commands ─────────────────────────────────────────────────────

    private static readonly string[] DangerousExtensions =
        [".exe", ".bat", ".cmd", ".ps1", ".msi", ".scr", ".vbs", ".js", ".jar"];

    [RelayCommand]
    private async Task SaveAttachmentAsync(AttachmentModel? attachment)
    {
        if (attachment == null || MessageDetail == null) return;
        var att = attachment;
        if (!att.IsLoaded)
        {
            if (att.PartSpecifier == null) return;
            IsBusy = true;
            StatusText = $"Downloading {att.FileName}…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                att.Content = await _imap.DownloadAttachmentAsync(
                    MessageDetail.AccountId, MessageDetail.FolderName,
                    MessageDetail.UniqueId, att.PartSpecifier, cts.Token);
            }
            catch (Exception ex)
            {
                StatusText = $"Download failed: {ex.Message}";
                IsBusy = false;
                return;
            }
            IsBusy = false;
        }

        using var dlg = new SaveFileDialog
        {
            FileName = att.FileName,
            Title    = "Save Attachment",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        await File.WriteAllBytesAsync(dlg.FileName, att.Content!);
        StatusText = $"Saved {att.FileName}.";
    }

    [RelayCommand]
    private async Task SaveAllAttachmentsAsync()
    {
        if (MessageDetail == null || MessageDetail.Attachments.Count == 0) return;

        using var dlg = new FolderBrowserDialog { Description = "Choose folder to save attachments" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var folder = dlg.SelectedPath;

        IsBusy = true;
        StatusText = "Saving attachments…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            foreach (var att in MessageDetail.Attachments)
            {
                if (!att.IsLoaded && att.PartSpecifier != null)
                    att.Content = await _imap.DownloadAttachmentAsync(
                        MessageDetail.AccountId, MessageDetail.FolderName,
                        MessageDetail.UniqueId, att.PartSpecifier, cts.Token);

                if (att.Content != null)
                    await File.WriteAllBytesAsync(Path.Combine(folder, att.FileName), att.Content);
            }
            StatusText = "All attachments saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save all failed: {ex.Message}";
            LogService.Log("SaveAllAttachments", ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenAttachmentAsync(AttachmentModel? attachment)
    {
        if (attachment == null || MessageDetail == null) return;
        var att = attachment;
        if (!att.IsLoaded)
        {
            if (att.PartSpecifier == null) return;
            IsBusy = true;
            StatusText = $"Downloading {att.FileName}…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                att.Content = await _imap.DownloadAttachmentAsync(
                    MessageDetail.AccountId, MessageDetail.FolderName,
                    MessageDetail.UniqueId, att.PartSpecifier, cts.Token);
            }
            catch (Exception ex)
            {
                StatusText = $"Download failed: {ex.Message}";
                IsBusy = false;
                return;
            }
            IsBusy = false;
        }

        var ext = Path.GetExtension(att.FileName).ToLowerInvariant();
        if (DangerousExtensions.Contains(ext))
        {
            var result = MessageBox.Show(
                $"'{att.FileName}' is an executable file type. Opening it could be dangerous. Continue?",
                "Security Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "QuickMail");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, att.FileName);
        await File.WriteAllBytesAsync(tempPath, att.Content!);
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    public void RefreshAccountList()
    {
        LoadAccountList();
        _ = Task.Run(async () =>
        {
            foreach (var account in Accounts)
            {
                if (_cachedFolders.ContainsKey(account.Id)) continue;
                var result = await ConnectOneAccountAsync(account);
                if (result.Folders != null)
                {
                    _cachedFolders[result.Id] = result.Folders;
                    _uiSyncContext.Post(_ => RebuildFolderListFromCache(), null);
                }
            }
        });
    }

    // ── Preview extraction ────────────────────────────────────────────────────────

    private static string ExtractPreview(string plainText, string htmlText, int maxLines)
    {
        if (maxLines <= 0) return string.Empty;
        var source = !string.IsNullOrWhiteSpace(plainText) ? plainText : StripHtml(htmlText);
        var lines  = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(maxLines);
        return string.Join(" ", lines);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")
            .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Trim();
    }
}
