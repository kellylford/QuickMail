using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private bool _isConversationView = true;

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
        IConfigService configService)
    {
        _imap           = imap;
        _accountService = accountService;
        _credentials    = credentials;
        _localStore     = localStore;
        _syncService    = syncService;
        _configService  = configService;

        var cfg = _configService.Load();
        _showMessageStatus = cfg.ShowMessageStatus;

        _syncService.FolderSynced    += OnFolderSynced;
        _syncService.MessagesRemoved += OnMessagesRemoved;
    }

    public void LoadAccountList()
    {
        Accounts = new ObservableCollection<AccountModel>(_accountService.LoadAccounts());
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows All Mail from the local store immediately (no network).
    /// Called first in OnLoaded so the UI is populated before any IMAP work begins.
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
    /// Fire-and-forget from OnLoaded; does not block the UI.
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
            // Skip if already displayed (can happen if user triggered a manual refresh mid-sync)
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
        var password = _credentials.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password)) return (account.Id, null);

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

        // "All Mail" is a synthetic leaf at the top (no children).
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
                // Placeholder node for accounts that have not yet loaded folders.
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
    }

    /// <summary>Called by MVVM Toolkit whenever the Messages property is replaced.</summary>
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
        var snapshot = Messages.ToList(); // snapshot on UI thread; safe to read on background
        Task.Run(() =>
        {
            var groups = ConversationBuilder.Build(snapshot);
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                // Discard stale results if a newer rebuild was already scheduled
                if (version == _conversationRebuildVersion)
                    Conversations = new ObservableCollection<ConversationGroup>(groups);
            });
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

            // Serve from cache when available; fall back to IMAP and cache the result.
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
            _ = _localStore.UpdateIsReadAsync(summary.AccountId, summary.FolderName, summary.UniqueId, true);

            // Extract preview and persist if not already set.
            if (string.IsNullOrEmpty(summary.Preview))
            {
                var account = Accounts.FirstOrDefault(a => a.Id == summary.AccountId);
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
        if (detail == null || SelectedAccount == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, SelectedAccount.Id));
    }

    [RelayCommand]
    private async Task ReplyAll()
    {
        var detail = await EnsureDetailAsync();
        if (detail == null || SelectedAccount == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, SelectedAccount.Id));
    }

    [RelayCommand]
    private async Task Forward()
    {
        var detail = await EnsureDetailAsync();
        if (detail == null || SelectedAccount == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateForward(detail, SelectedAccount.Id));
    }

    // Returns MessageDetail if already loaded for the selected message,
    // otherwise fetches it (cache then IMAP) so compose can always proceed.
    // Deliberately bypasses SelectMessageCommand to avoid concurrent-execution
    // guards on that command and to avoid opening the reading pane as a side-effect.
    private async Task<MailMessageDetail?> EnsureDetailAsync()
    {
        var summary = SelectedMessage;
        if (summary == null) return null;

        // Fast path: detail already loaded for this exact message.
        if (MessageDetail != null &&
            MessageDetail.UniqueId   == summary.UniqueId &&
            MessageDetail.AccountId  == summary.AccountId &&
            MessageDetail.FolderName == summary.FolderName)
            return MessageDetail;

        // Ensure the correct account is active (important in All-Mail view).
        if (SelectedAccount?.Id != summary.AccountId)
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == summary.AccountId) ?? SelectedAccount;
        if (SelectedAccount == null) return null;

        // Load from local cache first, fall back to IMAP.
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
        var account = SelectedAccount ?? Accounts.FirstOrDefault();
        if (account == null) return;
        ComposeRequested?.Invoke(new ComposeModel { AccountId = account.Id });
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

    public void RefreshAccountList() => LoadAccountList();

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
