using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IMailService _imap;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly ILocalStoreService _localStore;
    private readonly IOAuthService _oauthService;
    private readonly ISyncService _syncService;
    private readonly IConfigService _configService;
    private readonly IViewService _viewService;
    private readonly ICommandRegistry _commandRegistry;
    private readonly IRuleService _ruleService;
    private readonly ISmtpService _smtp;

    // Separate CTS per operation type so they can't cancel each other accidentally
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _folderCts;
    private CancellationTokenSource? _messageLoadCts;
    private CancellationTokenSource? _messageActionCts;
    private CancellationTokenSource? _prefetchCts;

    private const int PrefetchRadiusAroundOpen = 5;
    private const int PrefetchTopOnFolderLoad  = 10;
    private CancellationTokenSource? _bgSyncCts;

    /// <summary>
    /// Cancels and disposes the old CTS, creates a new one, and outputs its token.
    /// Thread-safe: the slot is atomically replaced via Interlocked.Exchange.
    /// </summary>
    private static void ReplaceCts(ref CancellationTokenSource? slot, out CancellationToken token)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref slot, cts);
        try { previous?.Cancel(); previous?.Dispose(); } catch { /* best effort */ }
        token = cts.Token;
    }

    // How many days of mail to sync (0 = all); set via the Sync Range menu
    private int _syncDays = 30;

    // When true, FolderSynced events are ignored so that the initial startup sync
    // can run silently and update the UI once at the end instead of N times.
    private bool _suppressFolderSyncUpdates;

    // When true, OnActiveFilterChanged and OnSearchTextChanged skip ApplyFiltersAndSearch.
    // Set during SelectFolderAsync's property-reset block to prevent showing the previous
    // folder's messages through the new filter while the new folder's IMAP fetch is pending.
    private bool _suppressFilterRebuild;

    // Version stamps; latest wins, stale results discarded
    private int _folderLoadVersion;
    private int _conversationRebuildVersion;
    private int _senderGroupRebuildVersion;
    private int _toGroupRebuildVersion;

    // Version stamp for message body loads; latest selection wins.
    private int _messageLoadVersion;

    // Retains folder lists for every account that has been connected this session
    private readonly Dictionary<Guid, List<MailFolderModel>> _cachedFolders = new();
    public IReadOnlyDictionary<Guid, List<MailFolderModel>> CachedFolders => _cachedFolders;

    // ── Virtual folder sentinels ─────────────────────────────────────────────────
    // IMPORTANT: use \u0000 (Unicode escape, always exactly 4 hex digits) rather than
    // \x00 in embedded string literals.  C#'s \x escape greedily consumes up to 4 hex
    // digits, so "\x00AllMail" parses as \x00A (0x0A = LF) + "llMail", not NUL + "AllMail".
    // A-F are valid hex digits, and every virtual-folder name starts with "All…" or
    // "Account…", both beginning with A.

    /// <summary>Child under the All Mail group: all non-excluded folders across all accounts.</summary>
    public static readonly MailFolderModel AllMailFolder = new()
    {
        FullName    = "\u0000AllMail",
        DisplayName = "All Mail"
    };
    public static readonly MailFolderModel AllInboxesFolder = new()
    {
        FullName    = "\u0000AllInboxes",
        DisplayName = "All Inboxes"
    };
    public static readonly MailFolderModel AllDraftsFolder = new()
    {
        FullName    = "\u0000AllDrafts",
        DisplayName = "All Drafts"
    };
    public static readonly MailFolderModel AllSentFolder = new()
    {
        FullName    = "\u0000AllSent",
        DisplayName = "All Sent"
    };
    public static readonly MailFolderModel AllTrashFolder = new()
    {
        FullName    = "\u0000AllTrash",
        DisplayName = "All Trash"
    };

    // Sentinel prefix for per-account "All Mail" virtual folders, e.g. "\u0000AccountMail:{guid}".
    internal const string AccountMailPrefix = "\u0000AccountMail:";

    // Sentinel prefixes for saved-view virtual folders.
    internal const string ViewPrefix    = "\u0000View:";
    internal const string ViewAllPrefix = "\u0000ViewAll:";

    private static bool TryGetViewIdFromSentinel(string? fullName, out Guid viewId)
    {
        if (fullName != null &&
            fullName.StartsWith(ViewPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(fullName.AsSpan(ViewPrefix.Length), out viewId))
            return true;
        viewId = Guid.Empty;
        return false;
    }

    private static bool TryGetViewAllIdFromSentinel(string? fullName, out Guid viewId)
    {
        if (fullName != null &&
            fullName.StartsWith(ViewAllPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(fullName.AsSpan(ViewAllPrefix.Length), out viewId))
            return true;
        viewId = Guid.Empty;
        return false;
    }

    /// <summary>
    /// Creates the <see cref="MailFolderModel"/> that represents the "All Mail" virtual
    /// folder for a specific account.  Used by both the main folder tree and the folder picker.
    /// </summary>
    public static MailFolderModel CreateAccountMailVirtualFolder(AccountModel account) => new()
    {
        FullName    = $"{AccountMailPrefix}{account.Id}",
        DisplayName = $"All Mail \u2014 {account.AccountLabel}",
        AccountId   = account.Id,
    };

    /// <summary>
    /// Extracts the account GUID from a per-account "All Mail" sentinel,
    /// e.g. "\x00AccountMail:f47ac10b-…" → true, id = f47ac10b-….
    /// </summary>
    private static bool TryGetAccountIdFromSentinel(string? fullName, out Guid accountId)
    {
        if (fullName != null &&
            fullName.StartsWith(AccountMailPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(fullName.AsSpan(AccountMailPrefix.Length), out accountId))
            return true;

        accountId = Guid.Empty;
        return false;
    }

    private static bool IsVirtualFolder(MailFolderModel? folder)
    {
        if (folder == null) return false;

        // Per-account "All Mail" sentinels have a real AccountId, not Guid.Empty.
        if (TryGetAccountIdFromSentinel(folder.FullName, out _)) return true;

        // Saved-view sentinels.
        if (TryGetViewIdFromSentinel(folder.FullName, out _))    return true;
        if (TryGetViewAllIdFromSentinel(folder.FullName, out _)) return true;

        if (folder.AccountId != Guid.Empty) return false;

        return string.Equals(folder.FullName, AllMailFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllInboxesFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllDraftsFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllSentFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllTrashFolder.FullName, StringComparison.Ordinal);
    }

    // ── Saved views ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedViews))]
    private ObservableCollection<SavedView> _savedViews = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private SavedView? _activeView;

    /// <summary>
    /// When set by a view's DaysOfMail property, only messages this many days old or newer are shown.
    /// Cleared when the view is cleared or the user navigates to a folder directly.
    /// </summary>
    [ObservableProperty]
    private int? _activeDayLimit;

    public bool HasSavedViews => SavedViews.Count > 0;

    /// <summary>Raised when the view list changes so the Views menu can be rebuilt.</summary>
    public event EventHandler? SavedViewsChanged;

    /// <summary>Raised to ask MainWindow to open the View Manager dialog to create a new view from the current state.</summary>
    public event EventHandler? SaveViewRequested;

    /// <summary>Raised to ask MainWindow to open the View Manager dialog in manage mode.</summary>
    public event EventHandler? ManageViewsRequested;

    // ── Account / folder tree ─────────────────────────────────────────────────────

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
    private BatchObservableCollection<MailMessageSummary> _messages = [];

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
    private ViewMode _viewMode = ViewMode.Messages;

    [ObservableProperty]
    private MessageFilter _activeFilter = MessageFilter.All;

    [ObservableProperty]
    private MessageSort _activeSort = MessageSort.DateDescending;

    // ── Search ───────────────────────────────────────────────────────────────────

    // Raw messages before search filtering; repopulated by SetMessages().
    private List<MailMessageSummary> _rawMessages = [];

    /// <summary>All messages loaded for the current folder/view, before filtering.</summary>
    public IReadOnlyList<MailMessageSummary> LoadedMessages => _rawMessages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isSearchActive = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _searchText = string.Empty;

    // Updated by ApplyFiltersAndSearch(); the View debounces this to announce count.
    [ObservableProperty]
    private string _searchAnnouncement = string.Empty;

    /// <summary>Raised when the search box should receive focus (View concern).</summary>
    public event EventHandler? SearchRequested;

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressFilterRebuild) ApplyFiltersAndSearch();
    }

    [ObservableProperty]
    private ObservableCollection<ConversationGroup> _conversations = [];

    [ObservableProperty]
    private ObservableCollection<SenderGroup> _senderGroups = [];

    [ObservableProperty]
    private ObservableCollection<SenderGroup> _toGroups = [];

    public bool IsMessagesView      => ViewMode == ViewMode.Messages;
    public bool IsConversationsView => ViewMode == ViewMode.Conversations;
    public bool IsFromView          => ViewMode == ViewMode.From;
    public bool IsToView            => ViewMode == ViewMode.To;

    public bool IsFilterAll             => ActiveFilter == MessageFilter.All;
    public bool IsFilterUnread          => ActiveFilter == MessageFilter.Unread;
    public bool IsFilterRead            => ActiveFilter == MessageFilter.Read;
    public bool IsFilterWithAttachments => ActiveFilter == MessageFilter.WithAttachments;
    public bool IsFilterReplied         => ActiveFilter == MessageFilter.Replied;
    public bool IsFilterForwarded       => ActiveFilter == MessageFilter.Forwarded;
    public bool IsFilterActive          => ActiveFilter != MessageFilter.All;
    public string FilterLabel => ActiveFilter switch
    {
        MessageFilter.Unread          => "Unread",
        MessageFilter.Read            => "Read",
        MessageFilter.WithAttachments => "With Attachments",
        MessageFilter.Replied         => "Replied",
        MessageFilter.Forwarded       => "Forwarded",
        _                             => string.Empty,
    };

    public bool IsSortDateDesc    => ActiveSort == MessageSort.DateDescending;
    public bool IsSortDateAsc     => ActiveSort == MessageSort.DateAscending;
    public bool IsSortAlphaAsc    => ActiveSort == MessageSort.AlphaAscending;
    public bool IsSortAlphaDesc   => ActiveSort == MessageSort.AlphaDescending;
    public bool IsSortCountDesc   => ActiveSort == MessageSort.CountDescending;
    public bool IsSortCountAsc    => ActiveSort == MessageSort.CountAscending;
    public bool IsCountSortAvailable => ViewMode != ViewMode.Messages;
    public string SortLabel => ActiveSort switch
    {
        MessageSort.DateAscending   => "Oldest First",
        MessageSort.AlphaAscending  => "A → Z",
        MessageSort.AlphaDescending => "Z → A",
        MessageSort.CountDescending => "Most Messages",
        MessageSort.CountAscending  => "Fewest Messages",
        _                           => string.Empty,
    };

    public bool IsSyncDays7   => _syncDays == 7;
    public bool IsSyncDays30  => _syncDays == 30;
    public bool IsSyncDays180 => _syncDays == 180;
    public bool IsSyncDays365 => _syncDays == 365;
    public bool IsSyncDaysAll => _syncDays == 0;

    public string SyncRangeLabel => _syncDays switch
    {
        7   => "Sync: 7 Days",
        30  => "Sync: 30 Days",
        180 => "Sync: 6 Months",
        365 => "Sync: 1 Year",
        _   => "Sync: All",
    };

    public string ViewModeLabel => ViewMode switch
    {
        ViewMode.Conversations => "View: Conversations",
        ViewMode.From          => "View: From",
        ViewMode.To            => "View: To",
        _                      => "View: Messages",
    };

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _rulesStatusText = string.Empty;

    [ObservableProperty]
    private string _connectionStatusText = "Offline";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showMessageStatus;

    private bool _showPreview;
    private int  _previewLines;

    public bool HasSelectedAccount  => SelectedAccount  != null;
    public bool HasSelectedFolder   => SelectedFolder   != null;
    public bool HasSelectedMessage  => SelectedMessage  != null;

    public string WindowTitle
    {
        get
        {
            if (IsMessageOpen && !string.IsNullOrWhiteSpace(MessageDetail?.Subject))
                return $"{MessageDetail.Subject} - QuickMail";
            if (ActiveView != null)
            {
                var suffix = IsSearchActive && !string.IsNullOrWhiteSpace(SearchText)
                    ? $" — Search: {SearchText}"
                    : IsFilterActive
                    ? $" — {FilterLabel}"
                    : string.Empty;
                return $"{ActiveView.Name}{suffix} - QuickMail";
            }
            if (SelectedFolder != null && !SelectedFolder.IsHeader)
            {
                var accountLabel = SelectedFolder.AccountId != Guid.Empty
                    ? Accounts.FirstOrDefault(a => a.Id == SelectedFolder.AccountId)?.AccountLabel
                    : null;
                var folderPart = string.IsNullOrWhiteSpace(accountLabel)
                    ? SelectedFolder.DisplayName
                    : $"{SelectedFolder.DisplayName} - {accountLabel}";
                var suffix = IsSearchActive && !string.IsNullOrWhiteSpace(SearchText)
                    ? $" — Search: {SearchText}"
                    : IsFilterActive
                    ? $" — {FilterLabel}"
                    : string.Empty;
                return $"{folderPart}{suffix} - QuickMail";
            }
            return "QuickMail";
        }
    }

    /// <summary>When true the app was launched with --online: all folder/message data is fetched
    /// live from IMAP and nothing is read from or written to the local SQLite cache.</summary>
    public bool OnlineMode { get; }

    public MainViewModel(
        IMailService imap,
        IAccountService accountService,
        ICredentialService credentials,
        ILocalStoreService localStore,
        IOAuthService oauthService,
        ISyncService syncService,
        IConfigService configService,
        ICommandRegistry commandRegistry,
        IViewService viewService,
        IRuleService ruleService,
        ISmtpService smtpService,
        bool onlineMode = false)
    {
        _imap            = imap;
        _accountService  = accountService;
        _credentials     = credentials;
        _localStore      = localStore;
        _oauthService    = oauthService;
        _syncService     = syncService;
        _configService   = configService;
        _commandRegistry = commandRegistry;
        _viewService     = viewService;
        _ruleService     = ruleService;
        _smtp            = smtpService;
        OnlineMode       = onlineMode;

        var cfg = _configService.Load();
        _showMessageStatus = cfg.ShowMessageStatus;
        _previewLines = cfg.PreviewLines;
        _showPreview = _previewLines > 0;
        _syncDays = cfg.SyncDays;
        _viewMode = ConfigModel.ParseViewMode(cfg.ViewMode);
        _activeSort = ConfigModel.ParseSort(cfg.Sort);

        _syncService.FolderSynced    += OnFolderSynced;
        _syncService.MessagesRemoved += OnMessagesRemoved;
        _syncService.RulesApplied    += OnRulesApplied;
        _imap.InboxNewMailDetected += OnInboxNewMailDetected;

        // Load saved views and register their commands before the UI is shown.
        LoadSavedViews();
        RegisterCommands(commandRegistry);
        UpdateRulesStatusText();
    }

    public void LoadAccountList()
    {
        Accounts = new ObservableCollection<AccountModel>(_accountService.LoadAccounts());
    }

    // ── Saved-views lifecycle ─────────────────────────────────────────────────────

    /// <summary>Loads views from disk and registers a command for each one.</summary>
    private void LoadSavedViews()
    {
        var views = _viewService.Load();
        SavedViews = new ObservableCollection<SavedView>(views);
        RegisterViewCommands();
    }

    /// <summary>
    /// Called by the code-behind after the View Manager dialog closes with changes.
    /// Reloads views from disk, refreshes commands, and rebuilds the folder tree.
    /// </summary>
    public void UpdateSavedViews()
    {
        var views = _viewService.Load();
        SavedViews = new ObservableCollection<SavedView>(views);
        RegisterViewCommands();
        BuildFolderTree();
        SavedViewsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Registers (or re-registers) one command per saved view.</summary>
    private void RegisterViewCommands()
    {
        // Remove any commands from previous view registrations.
        var stale = _commandRegistry.GetAll()
            .Where(c => c.Id.StartsWith("view.saved.", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .ToList();
        foreach (var id in stale)
            _commandRegistry.Unregister(id);

        foreach (var view in SavedViews)
            RegisterOneViewCommand(view);

        // Sweep orphan view.saved.* entries from hotkeys.json. These accumulate when a
        // view is deleted (or views.json is lost) but the binding survives in the config
        // — they used to swallow the keypress in FindByGesture and now also crowd out the
        // gesture conflict detection in the View Manager dialog.
        PruneOrphanHotkeys();
    }

    private void PruneOrphanHotkeys()
    {
        var orphans = _commandRegistry.GetOrphanOverrideCommandIds();
        if (orphans.Count == 0) return;

        var cfg = _configService.Load();
        var before = cfg.CustomHotkeys.Count;
        cfg.CustomHotkeys.RemoveAll(h =>
            orphans.Contains(h.CommandId, StringComparer.OrdinalIgnoreCase) &&
            // Only prune our own view bindings — never touch user overrides for built-in commands
            // (the user might have an override for a command that isn't registered yet during a
            // mid-startup window).
            h.CommandId.StartsWith("view.saved.", StringComparison.OrdinalIgnoreCase));

        if (cfg.CustomHotkeys.Count == before) return;

        _configService.Save(cfg);
        _commandRegistry.ApplyUserOverrides(cfg.CustomHotkeys);
        LogService.Debug($"Pruned {before - cfg.CustomHotkeys.Count} orphan view hotkey binding(s).");
    }

    private void RegisterOneViewCommand(SavedView view)
    {
        var commandId = $"view.saved.{view.Id}";
        var cfg       = _configService.Load();
        var binding   = cfg.CustomHotkeys.FirstOrDefault(h => h.CommandId == commandId);
        var gesture   = binding?.Gesture ?? view.Hotkey;

        Key defaultKey         = Key.None;
        ModifierKeys defaultMods = ModifierKeys.None;
        if (!string.IsNullOrEmpty(gesture))
            GestureHelper.TryParse(gesture, out defaultKey, out defaultMods);

        // Capture the ID (not the SavedView reference) to avoid closure-captures
        // that would break if the collection is replaced.
        var capturedId = view.Id;
        _commandRegistry.Register(new CommandDefinition(
            id:               commandId,
            category:         "Views",
            title:            view.Name,
            execute:          () => _ = ApplyViewByIdAsync(capturedId, allFolders: false),
            defaultKey:       defaultKey,
            defaultModifiers: defaultMods));
    }

    // ── View application ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveView() => SaveViewRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ManageViews() => ManageViewsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SelectViewAsync(string? viewIdString)
    {
        if (!Guid.TryParse(viewIdString, out var id)) return;
        await ApplyViewByIdAsync(id, allFolders: false);
    }

    private async Task ApplyViewByIdAsync(Guid viewId, bool allFolders)
    {
        var view = SavedViews.FirstOrDefault(v => v.Id == viewId);
        if (view == null) return;
        await ApplyViewAsync(view, allFolders);
    }

    private async Task ApplyViewAsync(SavedView view, bool allFolders = false)
    {
        ActiveView = view;

        // Apply view's mode/filter/sort before clearing search so rebuild
        // schedulers triggered by ViewMode change operate on the right data.
        ViewMode = ConfigModel.ParseViewMode(view.ViewMode);
        ActiveFilter = view.Filter switch
        {
            "unread"      => MessageFilter.Unread,
            "read"        => MessageFilter.Read,
            "attachments" => MessageFilter.WithAttachments,
            "replied"     => MessageFilter.Replied,
            "forwarded"   => MessageFilter.Forwarded,
            _             => MessageFilter.All,
        };
        ActiveSort = ConfigModel.ParseSort(view.Sort);

        ActiveDayLimit = view.DaysOfMail;
        SearchText     = string.Empty;
        IsSearchActive = false;
        MessageDetail  = null;
        IsMessageOpen  = false;

        if (view.Folders.Count == 0)
        {
            if (!string.IsNullOrEmpty(view.VirtualFolderKey))
            {
                // VirtualFolderKey is stored without the \x00 sentinel prefix.
                // Reconstruct the full sentinel name to look up the folder.
                var sentinelName  = "\x00" + view.VirtualFolderKey;
                var virtualFolder = Folders.FirstOrDefault(f =>
                    string.Equals(f.FullName, sentinelName, StringComparison.Ordinal))
                    ?? new MailFolderModel { FullName = sentinelName, DisplayName = view.Name };
                SelectedFolder = virtualFolder;
                await FetchVirtualAsync(virtualFolder);
                return;
            }
            // Legacy view (null key or pre-fix garbled key): default to All Mail and
            // patch the key so a future Save will persist the correct value.
            view.VirtualFolderKey = AllMailFolder.FullName.Substring(1); // "AllMail"
            SelectedFolder = AllMailFolder;
            await FetchVirtualAsync(AllMailFolder);
            return;
        }

        bool multiFolder = view.Folders.Count > 1 || allFolders;

        if (!multiFolder)
        {
            // Single-folder view: navigate to the real folder so Refresh / sync work naturally.
            var vf = view.Folders[0];
            var realFolder = Folders.FirstOrDefault(f =>
                !f.IsHeader &&
                f.AccountId == vf.AccountId &&
                string.Equals(f.FullName, vf.FolderFullName, StringComparison.OrdinalIgnoreCase));

            if (realFolder != null)
            {
                SelectedFolder  = realFolder;
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == vf.AccountId) ?? SelectedAccount;
                await FetchFolderAsync();
                return;
            }
        }

        // Multi-folder view: use a view-sentinel as the selected folder.
        SelectedFolder = new MailFolderModel
        {
            FullName    = allFolders ? $"{ViewAllPrefix}{view.Id}" : $"{ViewPrefix}{view.Id}",
            DisplayName = view.Name,
        };
        await FetchViewFoldersAsync(view);
    }

    private async Task FetchViewFoldersAsync(SavedView view)
    {
        var expectedFolder = SelectedFolder;
        var loadVersion    = Interlocked.Increment(ref _folderLoadVersion);
        Messages.Clear();
        StatusText = $"Loading {view.Name}…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            if (!OnlineMode)
            {
                // ── Phase 1: show cache immediately ──────────────────────────────────
                var cached = new List<MailMessageSummary>();
                foreach (var vf in view.Folders)
                {
                    // Guard: skip any sentinel folder names accidentally stored in older views.
                    if (vf.FolderFullName.StartsWith("\x00", StringComparison.Ordinal)) continue;
                    var msgs = await _localStore.LoadFolderSummariesAsync(vf.AccountId, vf.FolderFullName);
                    cached.AddRange(msgs);
                }
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

                SetMessages(cached.OrderByDescending(m => m.Date));
                StatusText = cached.Count > 0
                    ? $"{cached.Count} cached messages (checking for new…)"
                    : $"Loading {view.Name}…";
                IsBusy = false;
            }

            // ── Phase 2: IMAP fetch ────────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            IsBusy = true;
            var newMessages = new List<MailMessageSummary>();
            foreach (var vf in view.Folders)
            {
                if (vf.FolderFullName.StartsWith("\x00", StringComparison.Ordinal)) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    List<MailMessageSummary> msgs;
                    if (OnlineMode)
                    {
                        msgs = _syncDays > 0
                            ? await _imap.GetMessagesSinceDateAsync(
                                vf.AccountId, vf.FolderFullName, DateTime.UtcNow.AddDays(-_syncDays), ct)
                            : await _imap.GetMessageSummariesAsync(vf.AccountId, vf.FolderFullName, 50000, ct);
                    }
                    else
                    {
                        var maxUid = await _localStore.GetMaxUidAsync(vf.AccountId, vf.FolderFullName);
                        if (maxUid == 0 && _syncDays > 0)
                            msgs = await _imap.GetMessagesSinceDateAsync(
                                vf.AccountId, vf.FolderFullName, DateTime.UtcNow.AddDays(-_syncDays), ct);
                        else
                        {
                            var initialCount = _configService.Load().InitialSyncCount;
                            msgs = await _imap.GetMessagesSinceAsync(
                                vf.AccountId, vf.FolderFullName, maxUid, initialCount, ct);
                        }
                    }
                    newMessages.AddRange(msgs);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Log($"ViewFolders fetch {vf.AccountDisplayName}/{vf.FolderDisplayName}", ex);
                }
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            var existingKeys = Messages
                .Select(m => (m.UniqueId, m.AccountId, m.FolderName))
                .ToHashSet();

            foreach (var msg in newMessages.OrderByDescending(m => m.Date))
            {
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;
                var key = (msg.UniqueId, msg.AccountId, msg.FolderName);
                if (!existingKeys.Add(key)) continue;
                if (!MatchesFilter(msg) || !MatchesDayLimit(msg)) continue;
                InsertMessageSorted(msg);
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            if (!OnlineMode && newMessages.Count > 0)
                _ = _localStore.UpsertSummariesAsync(newMessages);

            var count = Messages.Count;
            StatusText = count == 0
                ? $"No messages in {view.Name}."
                : $"{count} messages in {view.Name}.";

            RebuildActiveGroupView();

            StartPrefetchTopOfFolder();
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"{view.Name} load cancelled.";
        }
        catch (Exception ex)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"Failed to load {view.Name}: {ex.Message}";
            LogService.Log("FetchViewFolders", ex);
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    internal void ApplySettings(ConfigModel cfg)
    {
        ShowMessageStatus = cfg.ShowMessageStatus;

        var newPreviewLines = cfg.PreviewLines;
        var newShowPreview  = newPreviewLines > 0;
        if (_showPreview && !newShowPreview)
            foreach (var m in _rawMessages) m.Preview = string.Empty;
        else if (newShowPreview && newPreviewLines < _previewLines)
            foreach (var m in _rawMessages) m.Preview = TruncatePreview(m.Preview, newPreviewLines);
        _previewLines = newPreviewLines;
        _showPreview  = newShowPreview;

        var newMode = ConfigModel.ParseViewMode(cfg.ViewMode);
        ViewMode = newMode;

        var prevSyncDays = _syncDays;
        _syncDays = cfg.SyncDays;
        OnPropertyChanged(nameof(IsSyncDays7));
        OnPropertyChanged(nameof(IsSyncDays30));
        OnPropertyChanged(nameof(IsSyncDays180));
        OnPropertyChanged(nameof(IsSyncDays365));
        OnPropertyChanged(nameof(IsSyncDaysAll));
        OnPropertyChanged(nameof(SyncRangeLabel));

        ActiveSort = ConfigModel.ParseSort(cfg.Sort);

        if (_syncDays != prevSyncDays)
            _ = RefreshAsync();
    }

    private void RegisterCommands(ICommandRegistry registry)
    {
        registry.Register(new CommandDefinition(
            id: "mail.new", category: "Mail", title: "New Message",
            execute: () => NewMessageCommand.Execute(null),
            defaultKey: Key.N, defaultModifiers: ModifierKeys.Control));

        registry.Register(new CommandDefinition(
            id: "mail.reply", category: "Mail", title: "Reply",
            execute: () => ReplyCommand.Execute(null),
            defaultKey: Key.R, defaultModifiers: ModifierKeys.Control,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.replyAll", category: "Mail", title: "Reply All",
            execute: () => ReplyAllCommand.Execute(null),
            defaultKey: Key.R, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.forward", category: "Mail", title: "Forward",
            execute: () => ForwardCommand.Execute(null),
            defaultKey: Key.F, defaultModifiers: ModifierKeys.Control,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.delete", category: "Mail", title: "Delete",
            execute: () => DeleteMessageCommand.Execute(null),
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.refresh", category: "Mail", title: "Refresh",
            execute: () => RefreshCommand.Execute(null),
            defaultKey: Key.F5, defaultModifiers: ModifierKeys.None));

        registry.Register(new CommandDefinition(
            id: "mail.emptyTrash", category: "Mail", title: "Empty Trash",
            execute: () => EmptyTrashCommand.Execute(null),
            defaultKey: Key.E, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        registry.Register(new CommandDefinition(
            id: "view.toggleConversation", category: "View", title: "Cycle View Mode",
            execute: () => ViewMode = (ViewMode)(((int)ViewMode + 1) % 4)));

        registry.Register(new CommandDefinition(
            id: "account.manage", category: "Account", title: "Manage Accounts",
            execute: () => ManageAccountsCommand.Execute(null)));

        registry.Register(new CommandDefinition(
            id: "help.userGuide", category: "Help", title: "Open User Guide",
            execute: () => ViewUserGuideCommand.Execute(null),
            defaultKey: Key.F1, defaultModifiers: ModifierKeys.None));

        registry.Register(new CommandDefinition(
            id: "view.search", category: "View", title: "Search Messages…",
            execute: () => { IsSearchActive = true; SearchRequested?.Invoke(this, EventArgs.Empty); },
            defaultKey: Key.S, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        registry.Register(new CommandDefinition(
            id: "view.filterAll", category: "View", title: "Show All Messages",
            execute: () => SetFilterCommand.Execute("all")));

        registry.Register(new CommandDefinition(
            id: "view.filterUnread", category: "View", title: "Show Unread Only",
            execute: () => SetFilterCommand.Execute("unread")));

        registry.Register(new CommandDefinition(
            id: "view.filterRead", category: "View", title: "Show Read Only",
            execute: () => SetFilterCommand.Execute("read")));

        registry.Register(new CommandDefinition(
            id: "view.filterWithAttachments", category: "View", title: "Show Messages with Attachments",
            execute: () => SetFilterCommand.Execute("attachments")));

        registry.Register(new CommandDefinition(
            id: "view.filterReplied", category: "View", title: "Show Replied Only",
            execute: () => SetFilterCommand.Execute("replied")));

        registry.Register(new CommandDefinition(
            id: "view.filterForwarded", category: "View", title: "Show Forwarded Only",
            execute: () => SetFilterCommand.Execute("forwarded")));

        registry.Register(new CommandDefinition(
            id: "view.sortDateDesc", category: "View", title: "Sort: Newest First",
            execute: () => SetSortCommand.Execute("dateDesc")));

        registry.Register(new CommandDefinition(
            id: "view.sortDateAsc", category: "View", title: "Sort: Oldest First",
            execute: () => SetSortCommand.Execute("dateAsc")));

        registry.Register(new CommandDefinition(
            id: "view.sortAlphaAsc", category: "View", title: "Sort: A → Z",
            execute: () => SetSortCommand.Execute("alphaAsc")));

        registry.Register(new CommandDefinition(
            id: "view.sortAlphaDesc", category: "View", title: "Sort: Z → A",
            execute: () => SetSortCommand.Execute("alphaDesc")));

        registry.Register(new CommandDefinition(
            id: "view.sortCountDesc", category: "View", title: "Sort: Most Messages",
            execute: () => SetSortCommand.Execute("countDesc"),
            isAvailable: () => IsCountSortAvailable));

        registry.Register(new CommandDefinition(
            id: "view.sortCountAsc", category: "View", title: "Sort: Fewest Messages",
            execute: () => SetSortCommand.Execute("countAsc"),
            isAvailable: () => IsCountSortAvailable));

        registry.Register(new CommandDefinition(
            id: "mail.rules", category: "Mail", title: "Manage Rules",
            execute: () => OpenRulesManagerCommand.Execute(null),
            defaultKey: Key.L, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        registry.Register(new CommandDefinition(
            id: "mail.createRuleFromMessage", category: "Mail", title: "Create Rule from Message",
            execute: () => CreateRuleFromMessageCommand.Execute(null),
            defaultKey: Key.T, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.acceptInvite", category: "Mail", title: "Accept Invitation",
            execute: () => AcceptInviteCommand.Execute(null),
            isAvailable: () => HasCalendarInvite));

        registry.Register(new CommandDefinition(
            id: "mail.declineInvite", category: "Mail", title: "Decline Invitation",
            execute: () => DeclineInviteCommand.Execute(null),
            isAvailable: () => HasCalendarInvite));

        registry.Register(new CommandDefinition(
            id: "mail.tentativeInvite", category: "Mail", title: "Tentatively Accept Invitation",
            execute: () => TentativeInviteCommand.Execute(null),
            isAvailable: () => HasCalendarInvite));

        registry.Register(new CommandDefinition(
            id: "help.keyboardTutorial", category: "Help", title: "Keyboard Tutorial",
            execute: () => TutorialRequested?.Invoke(this, EventArgs.Empty)));

        registry.Register(new CommandDefinition(
            id: "help.about", category: "Help", title: "About QuickMail",
            execute: () => AboutRequested?.Invoke(this, EventArgs.Empty)));
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows All Mail from the local store immediately (no network).
    /// Called first in OnLoaded so the UI is populated before any IMAP work begins.
    /// </summary>
    public async Task InitialLoadAsync()
    {
        SelectedFolder = AllMailFolder;
        if (OnlineMode)
        {
            StatusText = "Online mode — connecting…";
            ConnectionStatusText = "Connecting…";
            return;
        }
        var cached = await _localStore.LoadAllSummariesAsync();
        SetMessages(cached);
        StatusText = cached.Count > 0
            ? $"{cached.Count} messages (cached — syncing…)"
            : "Connecting and syncing…";
        ConnectionStatusText = "Connecting…";
        StartPrefetchTopOfFolder();
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
        ReplaceCts(ref _bgSyncCts, out var ct);

        await ConnectAllAccountsAsync();
        if (_cachedFolders.Count == 0) return;

        _imap.StartIdleWatchers(Accounts.ToList(), ct);

        if (SelectedFolder?.FullName == AllMailFolder.FullName && ViewMode == ViewMode.To)
        {
            var missingRecipients = Messages.Any(m => string.IsNullOrWhiteSpace(m.To))
                || await _localStore.HasSummariesMissingRecipientsAsync();
            if (missingRecipients)
            {
                await FetchAllMailAsync();
                return;
            }
        }

        if (OnlineMode)
        {
            // In online mode there is no background sync — just load the current folder live.
            await FetchVirtualAsync(AllMailFolder);
            return;
        }

        StatusText = "Syncing mail…";
        ConnectionStatusText = "Syncing…";
        _suppressFolderSyncUpdates = true;
        try
        {
            await _syncService.SyncAllAccountsAsync(Accounts, _cachedFolders, ct);

            // Sync done — reload from the local store once so the UI reflects
            // every folder that was synced without N intermediate screen-reader
            // announcements.  Only reload if the user hasn't navigated away.
            var sel = SelectedFolder;
            if (sel != null)
            {
                List<MailMessageSummary> fresh;
                if (sel.FullName == AllMailFolder.FullName)
                    fresh = await _localStore.LoadAllSummariesAsync();
                else if (TryGetAccountIdFromSentinel(sel.FullName, out var aid))
                    fresh = await _localStore.LoadAllSummariesAsync(aid);
                else
                    fresh = null!; // user is on a specific folder — don't overwrite it

                if (fresh != null)
                    SetMessages(fresh);
            }

            var count = Messages.Count;
            StatusText = $"{count} messages.";
            ConnectionStatusText = $"{Accounts.Count} account{(Accounts.Count == 1 ? "" : "s")} connected";
            Announce($"{count} {(count == 1 ? "message" : "messages")} loaded.", AnnouncementCategory.Status);

            // Apply default view (if any) after the initial sync is complete.
            var defaultView = SavedViews.FirstOrDefault(v => v.IsDefault);
            if (defaultView != null)
                await ApplyViewAsync(defaultView);
        }
        catch (OperationCanceledException) { /* sync cancelled — normal */ }
        catch (Exception ex)
        {
            LogService.Log("BackgroundSync", ex);
            StatusText = $"Sync error: {ex.Message}";
            ConnectionStatusText = "Connection error";
        }
        finally
        {
            _suppressFolderSyncUpdates = false;
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
                Announce($"{count} {(count == 1 ? "message" : "messages")} loaded so far.", AnnouncementCategory.Status);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── FolderSynced merge ───────────────────────────────────────────────────────

    // Called on the UI thread by SyncService after each folder sync.
    // Inserts truly new messages into the live collection in sorted order.
    private void OnFolderSynced(IReadOnlyList<MailMessageSummary> incoming)
    {
        // During startup background sync the UI is updated once at the end
        // (in StartBackgroundSyncAsync) rather than folder-by-folder, so that
        // screen readers don't re-announce the focused message on every insert.
        if (_suppressFolderSyncUpdates) return;

        var selected = SelectedFolder;
        if (selected == null) return;

        IEnumerable<MailMessageSummary> relevant;
        if (selected.FullName == AllMailFolder.FullName)
        {
            // Global "All Mail" - accept messages from every account.
            relevant = incoming;
        }
        else if (TryGetAccountIdFromSentinel(selected.FullName, out var watchedAccountId))
        {
            // Per-account "All Mail" - only messages belonging to that account.
            relevant = incoming.Where(m => m.AccountId == watchedAccountId);
        }
        else if (selected.FullName == AllInboxesFolder.FullName ||
                 selected.FullName == AllDraftsFolder.FullName  ||
                 selected.FullName == AllSentFolder.FullName    ||
                 selected.FullName == AllTrashFolder.FullName)
        {
            // Kind-specific virtual folders (All Inboxes / Drafts / Sent / Trash) —
            // accept messages whose source folder has the matching SpecialFolderKind.
            // Build a lookup set once so the per-message check is O(1).
            var targetKind = selected.FullName == AllInboxesFolder.FullName ? SpecialFolderKind.Inbox
                           : selected.FullName == AllDraftsFolder.FullName  ? SpecialFolderKind.Drafts
                           : selected.FullName == AllSentFolder.FullName    ? SpecialFolderKind.Sent
                           : SpecialFolderKind.Trash;
            var matchingFolderKeys = new HashSet<(Guid, string)>(
                _cachedFolders.SelectMany(kvp => kvp.Value
                    .Where(f => f.Kind == targetKind)
                    .Select(f => (kvp.Key, f.FullName.ToUpperInvariant()))));
            relevant = incoming.Where(m =>
                matchingFolderKeys.Contains((m.AccountId, m.FolderName.ToUpperInvariant())));
        }
        else if (!selected.IsHeader && selected.AccountId != Guid.Empty)
        {
            // Regular folder — only accept messages for this specific folder.
            relevant = incoming.Where(m =>
                m.AccountId == selected.AccountId &&
                string.Equals(m.FolderName, selected.FullName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            return;
        }

        // Hash existing keys once so the dedupe check is O(1) per incoming item
        // instead of an O(n) scan per item — that would dominate a multi-thousand-message
        // All Mail view and freeze the UI thread. Use _rawMessages (not Messages) as the
        // canonical set so filtered-out messages still prevent duplicates.
        var seen = new HashSet<(uint, Guid, string)>(_rawMessages.Count);
        foreach (var e in _rawMessages)
            seen.Add((e.UniqueId, e.AccountId, e.FolderName));

        // Collect truly new messages; add them to _rawMessages immediately so the
        // search pool stays in sync with what the list will eventually show.
        var toInsert = new List<MailMessageSummary>();
        foreach (var msg in relevant.OrderByDescending(m => m.Date))
        {
            if (!seen.Add((msg.UniqueId, msg.AccountId, msg.FolderName)))
                continue;
            _rawMessages.Add(msg);
            if (!MatchesFilter(msg)) continue;
            if (!MatchesDayLimit(msg)) continue;
            if (!string.IsNullOrWhiteSpace(SearchText) && !MatchesSearch(msg)) continue;
            toInsert.Add(msg);
        }

        // Batch all inserts into a single CollectionChanged(Reset) notification.
        // Without batching, each InsertMessageSorted fires CollectionChanged(Add) which
        // causes the ListView to emit a UIA StructureChanged(ChildAdded) event per insert.
        // When new messages arrive sorted before the focused item, each event shifts the
        // focused item's UIA position, causing screen readers to re-announce it every time.
        // A single Reset notification lets WPF re-bind once and screen readers see only
        // one structural change for the whole batch.
        //
        // Side-effect: WPF's ListView TwoWay-bound SelectedItem may clear SelectedMessage
        // transiently when the Reset fires (the Selector deselects before re-validating).
        // Save the reference so it can be restored if that happens.
        var prevSelected = SelectedMessage;
        using (Messages.BeginBatchScope())
        {
            foreach (var msg in toInsert)
                InsertMessageSorted(msg);
        }
        // If WPF cleared SelectedMessage during the Reset but the message is still in
        // the list, restore it so the reading pane header and command guards stay correct.
        if (prevSelected != null && SelectedMessage == null && Messages.Contains(prevSelected))
            SelectedMessage = prevSelected;

        if (toInsert.Count > 0)
        {
            // Increment account inbox counts for messages that landed in an Inbox-kind folder.
            UpdateAccountCountsAfterInsert(toInsert);

            var n = Messages.Count;
            StatusText = n == 0 ? "No messages" : $"{n} {(n == 1 ? "message" : "messages")}";
        }

        RebuildActiveGroupView();
    }
    // Called on a ThreadPool thread by the IDLE watcher when new mail lands in an inbox.
    // Runs a targeted sync for that account's INBOX so the message appears in the list.
    private void OnInboxNewMailDetected(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        if (!_cachedFolders.TryGetValue(accountId, out var folders)) return;
        var inbox = folders.FirstOrDefault(f =>
            f.Kind == Models.SpecialFolderKind.Inbox ||
            string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase));
        if (inbox is null) return;

        LogService.Log($"IDLE: new mail detected for {account.AccountLabel} INBOX — syncing.");

        _ = Task.Run(async () =>
        {
            try
            {
                if (OnlineMode)
                    await _syncService.SyncOneFolderOnlineAsync(account, inbox, CancellationToken.None);
                else
                    await _syncService.SyncOneFolderAsync(account, inbox, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogService.Log("IDLE targeted sync", ex);
            }
        });
    }

    private void OnMessagesRemoved(IReadOnlyList<MailMessageSummary> removed)
    {
        // Build a key→item map once so each removed key is an O(1) lookup
        // instead of a Messages.FirstOrDefault scan per item.
        var byKey = new Dictionary<(uint, Guid, string), MailMessageSummary>(Messages.Count);
        foreach (var e in Messages)
            byKey[(e.UniqueId, e.AccountId, e.FolderName)] = e;

        // Build a key set for fast _rawMessages removal.
        var removedKeys = new HashSet<(uint, Guid, string)>(removed.Count);
        foreach (var msg in removed)
            removedKeys.Add((msg.UniqueId, msg.AccountId, msg.FolderName));

        // Capture which messages are still in _rawMessages before removal so we only
        // decrement inbox counts for messages we're actually removing here.  Messages
        // removed by DeleteMessagesAsync have already been cleaned from _rawMessages
        // (and their counts decremented), so they won't appear in this set.
        var rawKeys = new HashSet<(uint, Guid, string)>(
            _rawMessages.Select(m => (m.UniqueId, m.AccountId, m.FolderName)));
        var actuallyRemovedFromRaw = removed
            .Where(m => rawKeys.Contains((m.UniqueId, m.AccountId, m.FolderName)))
            .ToList();

        _rawMessages.RemoveAll(m => removedKeys.Contains((m.UniqueId, m.AccountId, m.FolderName)));

        // Update account inbox counts for the messages we actually removed from _rawMessages.
        UpdateAccountCountsAfterRemoval(actuallyRemovedFromRaw);

        bool removedOpen = false;
        foreach (var msg in removed)
        {
            var key = (msg.UniqueId, msg.AccountId, msg.FolderName);
            if (!byKey.TryGetValue(key, out var existing)) continue;

            if (SelectedMessage == existing) removedOpen = true;
            Messages.Remove(existing);
            byKey.Remove(key);
        }

        if (removedOpen)
        {
            SelectedMessage = Messages.Count > 0 ? Messages[0] : null;
            MessageDetail   = null;
            IsMessageOpen   = false;
        }

        if (removed.Count > 0)
            StatusText = $"{Messages.Count} messages";

        RebuildActiveGroupView();
    }

    private int _lastRulesMatchCount;
    private DateTime _lastRulesRunTime;

    private void OnRulesApplied(int matchCount)
    {
        _lastRulesMatchCount = matchCount;
        _lastRulesRunTime = DateTime.Now;
        UpdateRulesStatusText();
    }

    public void UpdateRulesStatusText()
    {
        var rules = _ruleService.LoadRules();
        int active = rules.Count(r => r.IsEnabled);
        int disabled = rules.Count(r => !r.IsEnabled);

        if (active == 0)
        {
            RulesStatusText = "No active rules";
            return;
        }

        var timeStr = _lastRulesRunTime == default
            ? "not yet run"
            : _lastRulesRunTime.ToString("h:mm tt");

        RulesStatusText = _lastRulesMatchCount > 0
            ? $"Rules: {active} active, {disabled} disabled — Last run: {_lastRulesMatchCount} matched ({timeStr})"
            : $"Rules: {active} active, {disabled} disabled — Last run: {timeStr}";
    }

    // Stores raw messages and applies all active filters.
    private void SetMessages(IEnumerable<MailMessageSummary> messages)
    {
        _rawMessages = messages.ToList();
        if (!_showPreview)
            foreach (var m in _rawMessages) m.Preview = string.Empty;
        else
            foreach (var m in _rawMessages) m.Preview = TruncatePreview(m.Preview, _previewLines);
        ApplyFiltersAndSearch();
    }

    // Re-applies the status filter and search text to _rawMessages.
    // Called by SetMessages() and OnSearchTextChanged(); OnMessagesChanged()
    // automatically triggers group rebuilds when Messages is replaced.
    private void ApplyFiltersAndSearch()
    {
        IEnumerable<MailMessageSummary> result = _rawMessages;
        if (ActiveFilter != MessageFilter.All)
            result = result.Where(MatchesFilter);
        if (ActiveDayLimit.HasValue)
            result = result.Where(MatchesDayLimit);
        if (!string.IsNullOrWhiteSpace(SearchText))
            result = result.Where(MatchesSearch);
        result = ActiveSort switch
        {
            MessageSort.DateAscending   => result.OrderBy(m => m.Date),
            MessageSort.AlphaAscending  => result.OrderBy(m => m.Subject, StringComparer.OrdinalIgnoreCase),
            MessageSort.AlphaDescending => result.OrderByDescending(m => m.Subject, StringComparer.OrdinalIgnoreCase),
            _                           => result.OrderByDescending(m => m.Date),
        };
        Messages = new BatchObservableCollection<MailMessageSummary>(result);

        // Keep the status bar count in sync with whatever is currently visible.
        // Folder-load methods set a more descriptive status text immediately after
        // calling SetMessages, so this value is overwritten during loads and only
        // "sticks" for user-triggered changes (filter, search, sort).
        var n = Messages.Count;
        StatusText = n == 0 ? "No messages" : $"{n} {(n == 1 ? "message" : "messages")}";

        if (IsSearchActive && !string.IsNullOrWhiteSpace(SearchText))
        {
            SearchAnnouncement = n == 0
                ? "No messages found"
                : $"{n} {(n == 1 ? "message" : "messages")} found";
        }
    }

    private bool MatchesSearch(MailMessageSummary msg)
    {
        var q = SearchText;
        return msg.From.Contains(q, StringComparison.OrdinalIgnoreCase)
            || msg.To.Contains(q, StringComparison.OrdinalIgnoreCase)
            || msg.Subject.Contains(q, StringComparison.OrdinalIgnoreCase)
            || msg.Preview.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesFilter(MailMessageSummary msg) => ActiveFilter switch
    {
        MessageFilter.Unread          => !msg.IsRead && !msg.IsReplied && !msg.IsForwarded,
        MessageFilter.Read            => msg.IsRead,
        MessageFilter.WithAttachments => msg.HasAttachments,
        MessageFilter.Replied         => msg.IsReplied,
        MessageFilter.Forwarded       => msg.IsForwarded,
        _                             => true,
    };

    // Returns true when no day limit is active, so callers can chain this with
    // MatchesFilter without an explicit ActiveDayLimit.HasValue guard at every site.
    private bool MatchesDayLimit(MailMessageSummary msg)
        => !ActiveDayLimit.HasValue || msg.Date >= DateTimeOffset.Now.AddDays(-ActiveDayLimit.Value);

    // Binary-insert into the descending-by-date Messages collection.
    private void InsertMessageSorted(MailMessageSummary msg)
    {
        if (!_showPreview) msg.Preview = string.Empty;
        else msg.Preview = TruncatePreview(msg.Preview, _previewLines);
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
        ConnectionStatusText = "Connecting…";
        IsBusy = true;

        var tasks = Accounts.Select(account => ConnectOneAccountAsync(account)).ToList();
        var results = await Task.WhenAll(tasks);

        foreach (var (id, folders) in results)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == id);
            if (account != null)
                ApplyAccountStatus(account, folders);
            if (folders != null)
                _cachedFolders[id] = folders;
        }

        IsBusy = false;
        RebuildFolderListFromCache();
        StatusText = _cachedFolders.Count > 0
            ? $"{_cachedFolders.Count} of {Accounts.Count} account(s) connected."
            : "No accounts could be connected.";
        ConnectionStatusText = _cachedFolders.Count > 0
            ? $"{_cachedFolders.Count} account(s) connected"
            : "Offline";
    }

    /// <summary>
    /// Sets IsConnected and TotalUnread on <paramref name="account"/> from the
    /// just-fetched folder list.  TotalUnread is summed across all folders.
    /// Pass <c>null</c> on connection failure to mark as disconnected.
    /// </summary>
    private static void ApplyAccountStatus(AccountModel account, List<MailFolderModel>? folders)
    {
        if (folders == null)
        {
            account.IsConnected = false;
            account.TotalUnread = 0;
            return;
        }

        account.IsConnected = true;
        account.TotalUnread = folders.Sum(f => f.UnreadCount);
    }

    /// <summary>
    /// Decrements TotalUnread on the relevant accounts for each unread message
    /// in <paramref name="removed"/>. Covers all folders.
    /// Must be called on the UI thread.
    /// </summary>
    private void UpdateAccountCountsAfterRemoval(IEnumerable<MailMessageSummary> removed)
    {
        var decrements = new Dictionary<Guid, int>();
        foreach (var msg in removed)
        {
            if (msg.IsRead) continue;
            decrements[msg.AccountId] = decrements.GetValueOrDefault(msg.AccountId) + 1;
        }

        foreach (var (id, unread) in decrements)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == id);
            if (account != null)
                account.TotalUnread = Math.Max(0, account.TotalUnread - unread);
        }
    }

    /// <summary>
    /// Increments TotalUnread on the relevant accounts for each unread message
    /// in <paramref name="inserted"/>. Covers all folders.
    /// Must be called on the UI thread.
    /// </summary>
    private void UpdateAccountCountsAfterInsert(IEnumerable<MailMessageSummary> inserted)
    {
        var increments = new Dictionary<Guid, int>();
        foreach (var msg in inserted)
        {
            if (msg.IsRead) continue;
            increments[msg.AccountId] = increments.GetValueOrDefault(msg.AccountId) + 1;
        }

        foreach (var (id, unread) in increments)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == id);
            if (account != null)
                account.TotalUnread += unread;
        }
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
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _imap.ConnectAsync(account, password, connectCts.Token);
            using var folderCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var folderList = await _imap.GetFoldersAsync(account.Id, folderCts.Token);
            return (account.Id, folderList);
        }
        catch (OperationCanceledException)
        {
            LogService.Log($"ConnectAll/{account.AccountLabel}: timed out");
            return (account.Id, null);
        }
        catch (Exception ex)
        {
            LogService.Log($"ConnectAll/{account.AccountLabel}", ex);
            return (account.Id, null);
        }
    }

    private void RebuildFolderListFromCache()
    {
        var saved = SelectedFolder;
        var items = new List<MailFolderModel>
        {
            AllMailFolder, AllInboxesFolder, AllDraftsFolder, AllSentFolder, AllTrashFolder
        };

        foreach (var account in Accounts)
        {
            if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;

            items.Add(new MailFolderModel
            {
                IsHeader    = true,
                DisplayName = account.AccountLabel,
                FullName    = $"\u0000Header:{account.Id}",
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

        // "Views" group — shown only when the user has saved at least one view.
        if (SavedViews.Count > 0)
        {
            var viewsGroup = new FolderTreeNode
            {
                IsHeader   = true,
                Label      = "Views",
                IsExpanded = true,
            };
            foreach (var view in SavedViews)
            {
                var viewFolder = new MailFolderModel
                {
                    FullName    = $"{ViewPrefix}{view.Id}",
                    DisplayName = view.Name,
                };
                var viewNode = new FolderTreeNode { Folder = viewFolder, Label = view.Name };

                if (view.Folders.Count > 1)
                {
                    // Multi-folder: add "All" child, then each constituent folder.
                    var allFolder = new MailFolderModel
                    {
                        FullName    = $"{ViewAllPrefix}{view.Id}",
                        DisplayName = $"{view.Name} — All",
                    };
                    viewNode.Children.Add(new FolderTreeNode
                    {
                        Folder = allFolder,
                        Label  = allFolder.DisplayName,
                    });
                    foreach (var vf in view.Folders)
                    {
                        var real = Folders.FirstOrDefault(f =>
                            !f.IsHeader &&
                            f.AccountId == vf.AccountId &&
                            string.Equals(f.FullName, vf.FolderFullName, StringComparison.OrdinalIgnoreCase));
                        if (real != null)
                            viewNode.Children.Add(new FolderTreeNode { Folder = real, Label = real.DisplayName });
                    }
                }
                viewsGroup.Children.Add(viewNode);
            }
            roots.Add(viewsGroup);
        }

        // "All Mail" is a top-level group header with 5 virtual sub-folder children.
        var allMailGroup = new FolderTreeNode
        {
            IsHeader   = true,
            Label      = "All Mail",
            IsExpanded = true,
        };
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllMailFolder,    Label = AllMailFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllInboxesFolder, Label = AllInboxesFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllDraftsFolder,  Label = AllDraftsFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllSentFolder,    Label = AllSentFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllTrashFolder,   Label = AllTrashFolder.DisplayName });
        roots.Add(allMailGroup);

        foreach (var account in Accounts)
        {
            if (_cachedFolders.TryGetValue(account.Id, out var folders) && folders.Count > 0)
            {
                var accountRoots = FolderTreeBuilder.Build(folders, account);

                // Inject a per-account "All Mail" virtual folder as the first child
                // of the account header node so users can see all mail for that account.
                if (accountRoots.Count > 0)
                {
                    var accountMailFolder = CreateAccountMailVirtualFolder(account);
                    accountRoots[0].Children.Insert(0, new FolderTreeNode
                    {
                        Folder = accountMailFolder,
                        Label  = accountMailFolder.DisplayName,
                    });
                }

                roots.AddRange(accountRoots);
            }
            else
            {
                // Placeholder node for accounts that have not yet loaded folders.
                roots.Add(new FolderTreeNode
                {
                    IsHeader = true,
                    Label    = account.AccountLabel,
                    Folder   = null,
                });
            }
        }

        FolderTree = new ObservableCollection<FolderTreeNode>(roots);
    }

    // ── View-mode grouping ────────────────────────────────────────────────────────

    partial void OnActiveFilterChanged(MessageFilter value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterUnread));
        OnPropertyChanged(nameof(IsFilterRead));
        OnPropertyChanged(nameof(IsFilterWithAttachments));
        OnPropertyChanged(nameof(IsFilterReplied));
        OnPropertyChanged(nameof(IsFilterForwarded));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilterLabel));
        OnPropertyChanged(nameof(WindowTitle));

        if (_suppressFilterRebuild) return;

        if (ViewMode == ViewMode.Messages)
            ApplyFiltersAndSearch();
        else
            RebuildActiveGroupView();
    }

    partial void OnActiveSortChanged(MessageSort value)
    {
        OnPropertyChanged(nameof(IsSortDateDesc));
        OnPropertyChanged(nameof(IsSortDateAsc));
        OnPropertyChanged(nameof(IsSortAlphaAsc));
        OnPropertyChanged(nameof(IsSortAlphaDesc));
        OnPropertyChanged(nameof(IsSortCountDesc));
        OnPropertyChanged(nameof(IsSortCountAsc));
        OnPropertyChanged(nameof(SortLabel));

        var cfg = _configService.Load();
        cfg.Sort = ConfigModel.ToConfigString(value);
        _configService.Save(cfg);

        if (ViewMode == ViewMode.Messages)
            ApplyFiltersAndSearch();
        else
            RebuildActiveGroupView();
    }

    partial void OnViewModeChanged(ViewMode value)
    {
        OnPropertyChanged(nameof(IsMessagesView));
        OnPropertyChanged(nameof(IsConversationsView));
        OnPropertyChanged(nameof(IsFromView));
        OnPropertyChanged(nameof(IsToView));
        OnPropertyChanged(nameof(ViewModeLabel));
        OnPropertyChanged(nameof(IsCountSortAvailable));

        if (value == ViewMode.Conversations)
            ScheduleConversationRebuild();
        else
            Conversations = [];

        if (value == ViewMode.From)
            ScheduleSenderGroupRebuild();
        else
            SenderGroups = [];

        if (value == ViewMode.To)
            ScheduleToGroupRebuild();
        else
            ToGroups = [];

        var cfg = _configService.Load();
        cfg.ViewMode = ConfigModel.ToConfigString(value);
        _configService.Save(cfg);

        if (value == ViewMode.To && SelectedFolder?.FullName == AllMailFolder.FullName && Messages.Any(m => string.IsNullOrWhiteSpace(m.To)))
            _ = RefreshAsync();
    }

    /// <summary>Called by MVVM Toolkit whenever the Messages property is replaced.</summary>
    partial void OnMessagesChanged(BatchObservableCollection<MailMessageSummary> value)
    {
        RebuildActiveGroupView();
    }

    /// <summary>
    /// Triggers a rebuild of whichever grouped view is currently active (Conversations,
    /// From, or To). Does nothing in flat Messages mode.  All sites that mutate the
    /// underlying Messages collection should call this rather than open-coding the
    /// three-branch switch — that pattern had grown to a dozen copies and at least one
    /// had drifted (missing the To branch, so moving messages didn't refresh the To view).
    /// </summary>
    private void RebuildActiveGroupView()
    {
        switch (ViewMode)
        {
            case ViewMode.Conversations: ScheduleConversationRebuild(); break;
            case ViewMode.From:          ScheduleSenderGroupRebuild();  break;
            case ViewMode.To:            ScheduleToGroupRebuild();      break;
        }
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
        var sort     = ActiveSort;
        Task.Run(() =>
        {
            var built = ConversationBuilder.Build(snapshot);
            IEnumerable<ConversationGroup> ordered = sort switch
            {
                MessageSort.DateAscending   => built.OrderBy(g => g.Messages.Count > 0 ? g.Messages[0].Date : DateTimeOffset.MinValue),
                MessageSort.AlphaAscending  => built.OrderBy(g => g.NormalizedSubject, StringComparer.OrdinalIgnoreCase),
                MessageSort.AlphaDescending => built.OrderByDescending(g => g.NormalizedSubject, StringComparer.OrdinalIgnoreCase),
                MessageSort.CountDescending => built.OrderByDescending(g => g.Count),
                MessageSort.CountAscending  => built.OrderBy(g => g.Count),
                _                           => built.OrderByDescending(g => g.Messages.Count > 0 ? g.Messages[0].Date : DateTimeOffset.MinValue),
            };
            var groups = ordered.ToList();
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version == _conversationRebuildVersion)
                {
                    var expanded = Conversations
                        .Where(g => g.IsExpanded).Select(g => g.NormalizedSubject)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in groups)
                        if (expanded.Contains(g.NormalizedSubject))
                            g.IsExpanded = true;
                    Conversations = new ObservableCollection<ConversationGroup>(groups);
                }
            });
        });
    }

    private void ScheduleSenderGroupRebuild()
    {
        var version  = Interlocked.Increment(ref _senderGroupRebuildVersion);
        var snapshot = Messages.ToList();
        var sort     = ActiveSort;
        Task.Run(() =>
        {
            var built = SenderGroupBuilder.Build(snapshot);
            IEnumerable<SenderGroup> ordered = sort switch
            {
                MessageSort.DateAscending   => built.OrderBy(g => g.Messages.Count > 0 ? g.Messages[0].Date : DateTimeOffset.MinValue),
                MessageSort.AlphaDescending => built.OrderByDescending(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
                MessageSort.CountDescending => built.OrderByDescending(g => g.Count),
                MessageSort.CountAscending  => built.OrderBy(g => g.Count),
                _                           => built.OrderBy(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
            };
            var groups = ordered.ToList();
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version == _senderGroupRebuildVersion)
                {
                    var expanded = SenderGroups
                        .Where(g => g.IsExpanded).Select(g => g.SenderKey)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in groups)
                        if (expanded.Contains(g.SenderKey))
                            g.IsExpanded = true;
                    SenderGroups = new ObservableCollection<SenderGroup>(groups);
                }
            });
        });
    }

    private void ScheduleToGroupRebuild()
    {
        var version  = Interlocked.Increment(ref _toGroupRebuildVersion);
        var snapshot = Messages.ToList();
        var sort     = ActiveSort;
        Task.Run(() =>
        {
            var built = SenderGroupBuilder.BuildByTo(snapshot);
            IEnumerable<SenderGroup> ordered = sort switch
            {
                MessageSort.DateAscending   => built.OrderBy(g => g.Messages.Count > 0 ? g.Messages[0].Date : DateTimeOffset.MinValue),
                MessageSort.AlphaDescending => built.OrderByDescending(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
                MessageSort.CountDescending => built.OrderByDescending(g => g.Count),
                MessageSort.CountAscending  => built.OrderBy(g => g.Count),
                _                           => built.OrderBy(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
            };
            var groups = ordered.ToList();
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version == _toGroupRebuildVersion)
                {
                    var expanded = ToGroups
                        .Where(g => g.IsExpanded).Select(g => g.SenderKey)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in groups)
                        if (expanded.Contains(g.SenderKey))
                            g.IsExpanded = true;
                    ToGroups = new ObservableCollection<SenderGroup>(groups);
                }
            });
        });
    }

    [RelayCommand]
    private async Task SelectAccountAsync(AccountModel? account)
    {
        if (account == null) return;
        SelectedAccount = account;
        StatusText = $"Connecting to {account.AccountLabel}…";
        IsBusy = true;
        try
        {
            var password = _credentials.GetPassword(account.Id);
            if (string.IsNullOrEmpty(password))
            {
                StatusText = $"No password stored for {account.AccountLabel}.";
                return;
            }
            _connectCts?.Cancel();
            ReplaceCts(ref _connectCts, out var ct);
            await _imap.ConnectAsync(account, password, ct);
            var folderList = await _imap.GetFoldersAsync(account.Id, ct);
            _cachedFolders[account.Id] = folderList;
            ApplyAccountStatus(account, folderList);
            RebuildFolderListFromCache();
            StatusText = $"Connected to {account.AccountLabel}. Press Enter on a folder to load messages.";
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

        // Intercept view sentinels BEFORE resetting filter/search — views set their own state.
        if (TryGetViewIdFromSentinel(folder.FullName, out var viewId))
        {
            await ApplyViewByIdAsync(viewId, allFolders: false);
            return;
        }
        if (TryGetViewAllIdFromSentinel(folder.FullName, out var viewAllId))
        {
            await ApplyViewByIdAsync(viewAllId, allFolders: true);
            return;
        }

        _suppressFilterRebuild = true;
        ActiveFilter   = MessageFilter.All;
        ActiveDayLimit = null;
        SearchText     = string.Empty;
        IsSearchActive = false;
        ActiveView     = null;
        SelectedFolder = folder;
        MessageDetail  = null;
        IsMessageOpen  = false;
        _suppressFilterRebuild = false;

        if (IsVirtualFolder(folder))
            await FetchVirtualAsync(folder);
        else
        {
            if (folder.AccountId != Guid.Empty)
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == folder.AccountId) ?? SelectedAccount;
            await FetchFolderAsync();
        }
    }

    [RelayCommand]
    private async Task SetSyncDaysAsync(string daysParam)
    {
        if (!int.TryParse(daysParam, out var days)) return;
        _syncDays = days;

        var cfg = _configService.Load();
        cfg.SyncDays = days;
        _configService.Save(cfg);

        OnPropertyChanged(nameof(IsSyncDays7));
        OnPropertyChanged(nameof(IsSyncDays30));
        OnPropertyChanged(nameof(IsSyncDays180));
        OnPropertyChanged(nameof(IsSyncDays365));
        OnPropertyChanged(nameof(IsSyncDaysAll));
        OnPropertyChanged(nameof(SyncRangeLabel));

        if (IsVirtualFolder(SelectedFolder))
            await FetchVirtualAsync(SelectedFolder!);
        else if (SelectedFolder != null && SelectedFolder.AccountId != Guid.Empty)
            await FetchFolderAsync();
    }

    private async Task FetchFolderAsync()
    {
        if (SelectedFolder == null) return;
        var accountId = SelectedFolder.AccountId;
        if (accountId == Guid.Empty) return;
        var folder = SelectedFolder;
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        IsBusy = true;
        try
        {
            ReplaceCts(ref _folderCts, out var ct);

            if (!OnlineMode)
            {
                var cached = await _localStore.LoadFolderSummariesAsync(accountId, folder.FullName);
                if (!IsCurrentFolderLoad(loadVersion, folder))
                    return;

                SetMessages(cached);
                StatusText = cached.Count > 0
                    ? $"{cached.Count} cached {(cached.Count == 1 ? "message" : "messages")} (checking for new…)"
                    : $"Loading {folder.DisplayName}…";
                if (cached.Count > 0)
                {
                    if (IsConversationsView)
                        ScheduleConversationRebuild();
                    StartPrefetchTopOfFolder();
                }
            }

            _ = RefreshFolderFromServerAsync(accountId, folder, loadVersion, ct);
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
            {
                StatusText = "Message list load cancelled.";
                IsBusy = false;
            }
        }
        catch (Exception ex)
        {
            if (loadVersion == _folderLoadVersion)
            {
                StatusText = $"Failed to load messages: {ex.Message}";
                IsBusy = false;
            }
            LogService.Log("SelectFolder", ex);
        }
    }

    private async Task RefreshFolderFromServerAsync(
        Guid accountId, MailFolderModel folder, int version, CancellationToken ct)
    {
        try
        {
            var list = _syncDays > 0
                ? await _imap.GetMessagesSinceDateAsync(accountId, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct)
                : await _imap.GetMessageSummariesAsync(accountId, folder.FullName, 50000, ct);
            if (!IsCurrentFolderLoad(version, folder))
                return;

            SetMessages(list);
            StatusText = list.Count == 0 ? "No messages" : $"{list.Count} messages loaded.";
            if (!OnlineMode)
                _ = _localStore.UpsertSummariesAsync(list);

            if (IsConversationsView)
                ScheduleConversationRebuild();
            StartPrefetchTopOfFolder();
        }
        catch (OperationCanceledException)
        {
            if (version == _folderLoadVersion)
                StatusText = "Message list load cancelled.";
        }
        catch (Exception ex)
        {
            if (version == _folderLoadVersion)
                StatusText = $"Failed to load messages: {ex.Message}";
            LogService.Log("RefreshFolderFromServer", ex);
        }
        finally
        {
            if (version == _folderLoadVersion)
                IsBusy = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectMessageAsync(MailMessageSummary? summary)
    {
        if (summary == null) return;
        if (SelectedAccount?.Id != summary.AccountId)
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == summary.AccountId) ?? SelectedAccount;
        if (SelectedAccount == null) return;

        var loadVersion = Interlocked.Increment(ref _messageLoadVersion);
        SelectedMessage = summary;
        MessageDetail   = null;
        IsMessageOpen   = false;
        StatusText = "Loading message…";
        IsBusy = true;
        try
        {
            ReplaceCts(ref _messageLoadCts, out var token);

            MailMessageDetail detail;
            if (OnlineMode)
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId, token);
            }
            else
            {
                // Serve from cache when available; fall back to IMAP and cache the result.
                detail = await _localStore.LoadDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId)
                    ?? await _imap.GetMessageDetailAsync(
                        summary.AccountId, summary.FolderName, summary.UniqueId, token);
                _ = _localStore.UpsertDetailAsync(detail);
            }

            if (loadVersion != _messageLoadVersion || SelectedMessage != summary)
                return;

            MessageDetail = detail;
            IsMessageOpen = true;
            summary.IsRead = true;
            summary.HasAttachments = detail.Attachments.Count > 0;
            if (!OnlineMode)
            {
                _ = _localStore.UpdateIsReadAsync(summary.AccountId, summary.FolderName, summary.UniqueId, true);

                // Extract preview and persist if not already set.
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
            }

            StatusText = "Message loaded. Press Escape to return to message list.";

            if (!OnlineMode)
                StartPrefetchAroundOpen(summary);
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _messageLoadVersion)
                StatusText = "Message load cancelled.";
        }
        catch (Exception ex)
        {
            if (loadVersion == _messageLoadVersion)
                StatusText = $"Failed to load message: {ex.Message}";
            LogService.Log("SelectMessage", ex);
        }
        finally
        {
            if (loadVersion == _messageLoadVersion)
                IsBusy = false;
        }
    }

    // ── Prefetch ─────────────────────────────────────────────────────────────────
    // Eagerly cache message bodies for nearby/top messages so subsequent opens are
    // instant. Uses background IMAP leases (cannot starve foreground opens) and does
    // not set the Seen flag on the server.

    private void StartPrefetchAroundOpen(MailMessageSummary current)
    {
        var snapshot = Messages.ToList();
        var idx = snapshot.IndexOf(current);
        if (idx < 0) return;

        var targets = new List<MailMessageSummary>(PrefetchRadiusAroundOpen * 2);
        for (var offset = 1; offset <= PrefetchRadiusAroundOpen; offset++)
        {
            if (idx + offset < snapshot.Count) targets.Add(snapshot[idx + offset]);
            if (idx - offset >= 0)             targets.Add(snapshot[idx - offset]);
        }
        if (targets.Count == 0) return;

        SchedulePrefetch(targets, "around-open");
    }

    private void StartPrefetchTopOfFolder()
    {
        if (OnlineMode) return;
        var snapshot = Messages.Take(PrefetchTopOnFolderLoad).ToList();
        if (snapshot.Count == 0) return;
        SchedulePrefetch(snapshot, "folder-top");
    }

    private void SchedulePrefetch(List<MailMessageSummary> targets, string reason)
    {
        ReplaceCts(ref _prefetchCts, out var ct);

        _ = Task.Run(() => RunPrefetchAsync(targets, reason, ct));
    }

    private async Task RunPrefetchAsync(List<MailMessageSummary> targets, string reason, CancellationToken ct)
    {
        LogService.Debug($"Prefetch start reason={reason} count={targets.Count}");
        var tasks = targets.Select(s => PrefetchOneAsync(s, ct)).ToList();
        try { await Task.WhenAll(tasks); }
        catch { /* per-message errors logged inside */ }
        LogService.Debug($"Prefetch end reason={reason} cancelled={ct.IsCancellationRequested}");
    }

    private async Task PrefetchOneAsync(MailMessageSummary summary, CancellationToken ct)
    {
        if (OnlineMode || ct.IsCancellationRequested) return;
        try
        {
            var cached = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId);
            if (cached != null || ct.IsCancellationRequested) return;

            var detail = await _imap.PrefetchMessageDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId, ct);
            if (ct.IsCancellationRequested) return;
            await _localStore.UpsertDetailAsync(detail);
            LogService.Debug($"Prefetched UID={summary.UniqueId} folder={summary.FolderName}");
        }
        catch (OperationCanceledException) { /* expected on switch */ }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not connected"))
        {
            // Prefetch raced startup or a disconnect; the next prefetch trigger
            // (folder load, message open) will retry once the account is up.
            LogService.Debug($"Prefetch skipped UID={summary.UniqueId} (account not connected)");
        }
        catch (Exception ex) { LogService.Log($"Prefetch UID={summary.UniqueId}", ex); }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (ActiveView != null)
        {
            await ApplyViewAsync(ActiveView);
            return;
        }
        if (IsVirtualFolder(SelectedFolder))
            await FetchVirtualAsync(SelectedFolder!);
        else if (SelectedFolder != null && SelectedFolder.AccountId != Guid.Empty)
            await FetchFolderAsync();
    }

    [RelayCommand]
    private async Task ClearViewAsync()
    {
        ActiveView     = null;
        ActiveDayLimit = null;
        if (IsVirtualFolder(SelectedFolder))
            await FetchVirtualAsync(SelectedFolder!);
        else if (SelectedFolder != null && SelectedFolder.AccountId != Guid.Empty)
            await FetchFolderAsync();
    }

    private async Task FetchAllMailAsync()
    {
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        Messages.Clear();
        StatusText = "Loading All Mail…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            List<MailMessageSummary> cached;
            if (!OnlineMode)
            {
                // ── Phase 1: show cache immediately (same data as InitialLoadAsync) ──
                // This keeps the view consistent regardless of how many times the user
                // navigates to All Mail.  The IMAP fetch in Phase 2 adds truly new messages.
                cached = await _localStore.LoadAllSummariesAsync();
                if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                    return;

                SetMessages(cached);
                StatusText = cached.Count > 0
                    ? $"{cached.Count} messages (checking for new…)"
                    : "Checking for new messages…";
                IsBusy = false;
            }
            else
            {
                cached = [];
            }

            // ── Phase 2: IMAP fetch ────────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            IsBusy = true;
            var needsRecipientRepair = !OnlineMode && ViewMode == ViewMode.To &&
                (cached.Any(m => string.IsNullOrWhiteSpace(m.To))
                    || await _localStore.HasSummariesMissingRecipientsAsync());
            var perAccountTasks = Accounts
                .Where(a => _cachedFolders.ContainsKey(a.Id))
                .Select(account => (OnlineMode || needsRecipientRepair)
                    ? FetchAccountAllFoldersAsync(account, ct)
                    : FetchAccountNewMessagesAsync(account, ct));

            var accountResults = await Task.WhenAll(perAccountTasks);
            var newMessages = accountResults.SelectMany(r => r).ToList();
            if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                return;

            if (needsRecipientRepair)
            {
                if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                    return;

                var repaired = newMessages
                    .GroupBy(m => (m.AccountId, m.FolderName, m.UniqueId))
                    .Select(g => g.OrderByDescending(m => m.Date).First())
                    .OrderByDescending(m => m.Date)
                    .ToList();

                SetMessages(repaired);
                if (!OnlineMode)
                    _ = _localStore.UpsertSummariesAsync(repaired);

                var totalCount = Messages.Count;
                StatusText = totalCount == 0
                    ? "No messages across connected accounts."
                    : $"{totalCount} messages across all accounts.";

                RebuildActiveGroupView();
                return;
            }

            if (OnlineMode)
            {
                // In online mode the list is fresh from IMAP — set directly rather than
                // merging with (empty) cache so the sorted order is correct.
                var sorted = newMessages.OrderByDescending(m => m.Date).ToList();
                SetMessages(sorted);
                var onlineCount = Messages.Count;
                StatusText = onlineCount == 0
                    ? "No messages across connected accounts."
                    : $"{onlineCount} messages across all accounts.";
                RebuildActiveGroupView();
                return;
            }

            var existingKeys = Messages
                .Select(m => (m.UniqueId, m.AccountId, m.FolderName))
                .ToHashSet();

            foreach (var msg in newMessages.OrderByDescending(m => m.Date))
            {
                if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                    return;

                var key = (msg.UniqueId, msg.AccountId, msg.FolderName);
                if (!existingKeys.Add(key))
                    continue;

                if (!MatchesFilter(msg) || !MatchesDayLimit(msg))
                    continue;

                InsertMessageSorted(msg);
            }

            if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                return;

            if (newMessages.Count > 0)
                _ = _localStore.UpsertSummariesAsync(newMessages);

            var count = Messages.Count;
            StatusText = count == 0
                ? "No messages across connected accounts."
                : $"{count} messages across all accounts.";

            RebuildActiveGroupView();

            StartPrefetchTopOfFolder();
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = "All Mail load cancelled.";
        }
        catch (Exception ex)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"Failed to load All Mail: {ex.Message}";
            LogService.Log("FetchAllMail", ex);
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
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
                var msgs = _syncDays > 0
                    ? await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct)
                    : await _imap.GetMessageSummariesAsync(account.Id, folder.FullName, 50000, ct);
                result.AddRange(msgs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"AllMail fetch {account.AccountLabel}/{folder.FullName}", ex);
            }
        }
        return result;
    }

    /// <summary>
    /// Fetches only messages newer than what is already stored locally for each
    /// non-excluded folder belonging to <paramref name="account"/>.  Used by the
    /// Phase 2 incremental update in <see cref="FetchAllMailAsync"/>.
    /// </summary>
    private async Task<List<MailMessageSummary>> FetchAccountNewMessagesAsync(
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
                var maxUid = await _localStore.GetMaxUidAsync(account.Id, folder.FullName);
                List<MailMessageSummary> msgs;
                if (maxUid == 0 && _syncDays > 0)
                {
                    // Fresh start with a date filter: use SEARCH SINCE rather than last-500 fallback.
                    msgs = await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct);
                }
                else
                {
                    // Incremental sync: UID-based is correct (fetch everything newer than last seen).
                    var initialCount = _configService.Load().InitialSyncCount;
                    msgs = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, maxUid, initialCount, ct);
                }
                result.AddRange(msgs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"AllMail new-msg fetch {account.AccountLabel}/{folder.FullName}", ex);
            }
        }
        return result;
    }


    private Task FetchVirtualAsync(MailFolderModel folder)
    {
        if (folder.FullName == AllMailFolder.FullName)    return FetchAllMailAsync();
        if (folder.FullName == AllInboxesFolder.FullName) return FetchVirtualFolderAsync(SpecialFolderKind.Inbox,  "All Inboxes");
        if (folder.FullName == AllDraftsFolder.FullName)  return FetchVirtualFolderAsync(SpecialFolderKind.Drafts, "All Drafts");
        if (folder.FullName == AllSentFolder.FullName)    return FetchVirtualFolderAsync(SpecialFolderKind.Sent,   "All Sent");
        if (folder.FullName == AllTrashFolder.FullName)   return FetchVirtualFolderAsync(SpecialFolderKind.Trash,  "All Trash");
        if (TryGetAccountIdFromSentinel(folder.FullName, out var accountId)) return FetchAccountAllMailAsync(accountId);

        // Saved-view sentinels — re-fetch without resetting mode/filter/sort
        if (TryGetViewIdFromSentinel(folder.FullName, out var viewId) ||
            TryGetViewAllIdFromSentinel(folder.FullName, out viewId))
        {
            var view = SavedViews.FirstOrDefault(v => v.Id == viewId);
            if (view != null) return FetchViewFoldersAsync(view);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads all cached messages for a single account (Phase 1), then incrementally
    /// fetches new messages from every non-excluded IMAP folder (Phase 2).
    /// Mirrors <see cref="FetchAllMailAsync"/> but scoped to one account.
    /// </summary>
    private async Task FetchAccountAllMailAsync(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return;

        var expectedFolder = SelectedFolder;
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        Messages.Clear();
        StatusText = $"Loading {account.AccountLabel}…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            if (!OnlineMode)
            {
                // ── Phase 1: show cache immediately ──────────────────────────────────
                var cached = await _localStore.LoadAllSummariesAsync(accountId);
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

                SetMessages(cached);
                StatusText = cached.Count > 0
                    ? $"{cached.Count} messages (checking for new…)"
                    : "Checking for new messages…";
                IsBusy = false;
            }

            // ── Phase 2: IMAP fetch ────────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            IsBusy = true;
            var newMessages = OnlineMode
                ? await FetchAccountAllFoldersAsync(account, ct)
                : await FetchAccountNewMessagesAsync(account, ct);
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            if (OnlineMode)
            {
                var sorted = newMessages.OrderByDescending(m => m.Date).ToList();
                SetMessages(sorted);
                var onlineCount = Messages.Count;
                StatusText = onlineCount == 0
                    ? $"No messages in {account.AccountLabel}."
                    : $"{onlineCount} messages in {account.AccountLabel}.";
                RebuildActiveGroupView();
                return;
            }

            var existingKeys = Messages
                .Select(m => (m.UniqueId, m.AccountId, m.FolderName))
                .ToHashSet();

            foreach (var msg in newMessages.OrderByDescending(m => m.Date))
            {
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

                var key = (msg.UniqueId, msg.AccountId, msg.FolderName);
                if (!existingKeys.Add(key))
                    continue;

                if (!MatchesFilter(msg) || !MatchesDayLimit(msg))
                    continue;

                InsertMessageSorted(msg);
            }

            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            if (newMessages.Count > 0)
                _ = _localStore.UpsertSummariesAsync(newMessages);

            var count = Messages.Count;
            StatusText = count == 0
                ? $"No messages in {account.AccountLabel}."
                : $"{count} messages in {account.AccountLabel}.";

            RebuildActiveGroupView();
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"{account.AccountLabel} load cancelled.";
        }
        catch (Exception ex)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"Failed to load {account.AccountLabel}: {ex.Message}";
            LogService.Log("FetchAccountAllMail", ex);
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    private async Task FetchVirtualFolderAsync(SpecialFolderKind kind, string displayName)
    {
        var expectedFolder = SelectedFolder;
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        Messages.Clear();
        StatusText = $"Loading {displayName}…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        var all = new List<MailMessageSummary>();

        try
        {
            var perAccountTasks = Accounts
                .Where(a => _cachedFolders.ContainsKey(a.Id))
                .Select(account => FetchAccountByKindAsync(account, kind, ct));

            var accountResults = await Task.WhenAll(perAccountTasks);
            foreach (var batch in accountResults)
                all.AddRange(batch);

            if (!IsCurrentFolderLoad(loadVersion, expectedFolder))
                return;

            var sorted = all.OrderByDescending(m => m.Date).ToList();
            SetMessages(sorted);
            StatusText = sorted.Count == 0
                ? $"No messages in {displayName}."
                : $"{sorted.Count} messages in {displayName}.";
            if (!OnlineMode)
                _ = _localStore.UpsertSummariesAsync(sorted);
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"{displayName} load cancelled.";
        }
        catch (Exception ex)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = $"Failed to load {displayName}: {ex.Message}";
            LogService.Log($"Fetch{displayName.Replace(" ", "")}", ex);
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    private bool IsCurrentFolderLoad(int loadVersion, MailFolderModel? expectedFolder) =>
        loadVersion == _folderLoadVersion &&
        FoldersMatch(SelectedFolder, expectedFolder);

    private static bool FoldersMatch(MailFolderModel? left, MailFolderModel? right) =>
        left != null &&
        right != null &&
        left.AccountId == right.AccountId &&
        string.Equals(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);

    private async Task<List<MailMessageSummary>> FetchAccountByKindAsync(
        AccountModel account, SpecialFolderKind kind, CancellationToken ct)
    {
        var result = new List<MailMessageSummary>();
        if (!_cachedFolders.TryGetValue(account.Id, out var folders)) return result;

        foreach (var folder in folders)
        {
            if (folder.Kind != kind) continue;
            ct.ThrowIfCancellationRequested();
            try
            {
                var msgs = _syncDays > 0
                    ? await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct)
                    : await _imap.GetMessageSummariesAsync(account.Id, folder.FullName, 50000, ct);
                result.AddRange(msgs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"VirtualFolder fetch {account.AccountLabel}/{folder.FullName}", ex);
            }
        }
        return result;
    }

    // ── Delete / Trash ───────────────────────────────────────────────────────────

    public Task MarkMessagesReadAsync(IReadOnlyList<MailMessageSummary> messages)
    {
        var unread = messages.Where(m => !m.IsRead).ToList();
        if (unread.Count == 0) return Task.CompletedTask;

        foreach (var m in unread)
            m.IsRead = true;

        var label = unread.Count == 1 ? "message" : $"{unread.Count} messages";
        StatusText = $"Marked {label} as read.";

        _ = _localStore.UpdateIsReadBatchAsync(
            unread.Select(m => (m.AccountId, m.FolderName, m.UniqueId)), true);

        foreach (var group in unread.GroupBy(m => (m.AccountId, m.FolderName)))
        {
            var uids = group.Select(m => m.UniqueId).ToList();
            _ = _imap.MarkReadBatchAsync(group.Key.AccountId, group.Key.FolderName, uids);
        }

        return Task.CompletedTask;
    }

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

        // ── Step 1: Remove from UI immediately (before IMAP call) ─────────────
        // Matches the "mark as deleted" pattern used by clients like Outlook:
        // messages vanish instantly, focus lands correctly, and the IMAP
        // move-to-trash runs afterwards. If it fails the messages will reappear
        // on the next background sync.
        int removed = 0;
        foreach (var msg in toDelete)
        {
            if (Messages.Remove(msg)) removed++;
        }

        // Remove from _rawMessages so OnMessagesRemoved (fired by background sync) won't
        // double-count these messages when updating inbox totals.
        var toDeleteKeys = new HashSet<(uint, Guid, string)>(
            toDelete.Select(m => (m.UniqueId, m.AccountId, m.FolderName)));
        _rawMessages.RemoveAll(m => toDeleteKeys.Contains((m.UniqueId, m.AccountId, m.FolderName)));

        // Immediately update account inbox counts for messages deleted from Inbox-kind folders.
        UpdateAccountCountsAfterRemoval(toDelete);

        RebuildActiveGroupView();

        if (ViewMode == ViewMode.Messages && Messages.Count > 0)
        {
            // In flat Messages view: advance selection to the next item so the
            // global Delete hotkey (HasSelectedMessage guard) stays coherent.
            var landIdx = Math.Max(0, Math.Min(minIdx, Messages.Count - 1));
            SelectedMessage = Messages[landIdx];
            MessageListFocusRequested?.Invoke();
        }
        else
        {
            // In From/To/Conversations views focus is managed by LandOnSenderGroupAfterRebuild /
            // LandOnToGroupAfterRebuild / LandOnConversationAfterRebuild.  Clearing SelectedMessage
            // here is essential:
            // leaving it set makes HasSelectedMessage=true, which causes the global Delete
            // hotkey in OnWindowKeyDown to steal the next keypress and delete just that one
            // message instead of the whole selected group.
            SelectedMessage = null;
        }

        // ── Step 2: IMAP delete + local store cleanup ────────────────────────────
        try
        {
            ReplaceCts(ref _messageActionCts, out var ct);

            var groups = toDelete.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
            {
                var uids = group.Select(m => m.UniqueId).ToList();

                // Messages already in Trash must be permanently deleted (expunge);
                // moving them to trash again is a no-op on most servers.
                var sourceKind = _cachedFolders.TryGetValue(group.Key.AccountId, out var acctFolders)
                    ? acctFolders.FirstOrDefault(f =>
                          f.FullName.Equals(group.Key.FolderName, StringComparison.OrdinalIgnoreCase))?.Kind
                    : null;

                if (sourceKind == SpecialFolderKind.Trash)
                    await _imap.PermanentlyDeleteBatchAsync(
                        group.Key.AccountId, group.Key.FolderName, uids, ct);
                else
                    await _imap.MoveToTrashBatchAsync(
                        group.Key.AccountId, group.Key.FolderName, uids, ct);

                if (!OnlineMode)
                    await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            var count = toDelete.Count;
            StatusText = Messages.Count > 0
                ? $"{count} {(count == 1 ? "message" : "messages")} deleted."
                : $"{count} {(count == 1 ? "message" : "messages")} deleted. Folder is now empty.";
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
    public event EventHandler<(string Text, AnnouncementCategory Category)>? AnnouncementRequested;
    public event EventHandler? RulesManagerRequested;
    public event EventHandler<MailRule>? CreateRuleFromMessageRequested;
    public event EventHandler? TutorialRequested;
    public event EventHandler? AboutRequested;

    private void Announce(string text, AnnouncementCategory category = AnnouncementCategory.Result)
    {
        if (!string.IsNullOrEmpty(text))
            AnnouncementRequested?.Invoke(this, (text, category));
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

        // Hydrate attachment bytes so the forwarded message can include them.
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
        var account = Accounts.FirstOrDefault(a => a.IsDefault)
                      ?? SelectedAccount
                      ?? Accounts.FirstOrDefault();
        if (account == null) return;
        ComposeRequested?.Invoke(new ComposeModel { AccountId = account.Id });
    }

    [RelayCommand]
    private void OpenRulesManager()
    {
        RulesManagerRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CreateRuleFromMessage()
    {
        var source = SelectedMessage;
        if (source == null) return;

        var template = new MailRule
        {
            Name = $"Rule for {source.From}",
            FromContains = source.From,
            SubjectContains = string.IsNullOrWhiteSpace(source.Subject) ? null : source.Subject,
            AccountId = source.AccountId,
        };

        CreateRuleFromMessageRequested?.Invoke(this, template);
    }

    /// <summary>True when the currently selected folder is a Drafts folder.</summary>
    public bool IsSelectedFolderDrafts =>
        SelectedFolder != null &&
        (SelectedFolder.Kind == SpecialFolderKind.Drafts ||
         string.Equals(SelectedFolder.FullName, AllDraftsFolder.FullName, StringComparison.Ordinal));

    [RelayCommand]
    private async Task OpenDraftAsync()
    {
        var summary = SelectedMessage;
        if (summary == null || SelectedAccount == null) return;

        IsBusy = true;
        StatusText = "Opening draft…";
        try
        {
            ReplaceCts(ref _messageLoadCts, out var ct);

            var detail = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.UniqueId);

            if (detail == null)
            {
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.UniqueId, ct);
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

            // Eagerly hydrate attachment bytes so ComposeWindow can re-send them
            foreach (var att in detail.Attachments)
            {
                if (!att.IsLoaded && att.PartSpecifier != null)
                {
                    try
                    {
                        att.Content = await _imap.DownloadAttachmentAsync(
                            summary.AccountId, summary.FolderName, summary.UniqueId,
                            att.PartSpecifier, ct);
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
        var accountsToEmpty = IsVirtualFolder(SelectedFolder)
            ? Accounts.ToList()
            : (SelectedAccount != null ? [SelectedAccount] : Accounts.Take(1).ToList());

        if (accountsToEmpty.Count == 0) return;

        bool viewingTrash = SelectedFolder?.Kind == SpecialFolderKind.Trash
                         || SelectedFolder?.FullName == AllTrashFolder.FullName;

        // Confirmation dialog (if enabled in settings).
        if (_configService.Load().ConfirmEmptyTrash && ConfirmationRequested != null)
        {
            // When the user is already viewing a trash folder, _rawMessages contains exactly
            // what is displayed — use that count.  Otherwise fall back to the cached
            // MessageCount from each account's trash folder model (zero IMAP cost).
            int trashCount = viewingTrash
                ? _rawMessages.Count
                : accountsToEmpty
                    .Where(a => _cachedFolders.TryGetValue(a.Id, out _))
                    .Sum(a => _cachedFolders[a.Id]
                        .Where(f => f.Kind == SpecialFolderKind.Trash)
                        .Sum(f => f.MessageCount));

            string countText = trashCount > 0
                ? $"This will permanently delete {trashCount:N0} {(trashCount == 1 ? "message" : "messages")} from your trash. This cannot be undone."
                : "This will permanently delete all messages in your trash. This cannot be undone.";

            if (!ConfirmationRequested(
                    countText + "\n\nYou can turn off this confirmation in Settings.",
                    "Empty Trash"))
                return;
        }

        LogService.Log($"EmptyTrash: viewingTrash={viewingTrash} folder='{SelectedFolder?.FullName}' accounts={accountsToEmpty.Count}");

        StatusText = "Emptying trash…";
        IsBusy = true;
        bool trashEmptied = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            int totalDeleted = 0;
            foreach (var account in accountsToEmpty)
                totalDeleted += await _imap.EmptyTrashAsync(account.Id, cts.Token);

            var msg = totalDeleted == 1 ? "1 message deleted from trash." : $"{totalDeleted} messages deleted from trash.";
            StatusText = msg;
            Announce(msg);
            trashEmptied = true;
            LogService.Log($"EmptyTrash: deleted {totalDeleted} messages");
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

        if (trashEmptied)
        {
            // Zero out the cached trash MessageCount for each account so that if the user
            // runs Empty Trash again in this session, the confirmation dialog shows 0 rather
            // than the stale count recorded at connection time.
            foreach (var account in accountsToEmpty)
            {
                if (_cachedFolders.TryGetValue(account.Id, out var folders))
                {
                    foreach (var f in folders.Where(f => f.Kind == SpecialFolderKind.Trash))
                        f.MessageCount = 0;
                }
            }
        }

        // Only update the message list if the user is currently looking at the trash.
        // If they're in their inbox, All Mail, etc., those messages are completely
        // unaffected — leave the view and focus exactly as they are.
        if (trashEmptied && viewingTrash)
        {
            // Clear _rawMessages alongside Messages so ApplyFiltersAndSearch cannot
            // restore the just-deleted messages if the user changes sort/filter/search
            // while still in the trash view.  (In online mode the background sync skips
            // trash folders, so _rawMessages is never cleaned up automatically.)
            _rawMessages.Clear();
            Messages.Clear();
            SelectedMessage = null;
            MessageDetail   = null;
            IsMessageOpen   = false;
            MessageListFocusRequested?.Invoke();
        }

    }

    [RelayCommand]
    private void ManageAccounts() => ManageAccountsRequested?.Invoke();

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    // ── Account context menu commands ─────────────────────────────────────────

    public event Action<AccountModel>? OpenAccountSettingsRequested;

    /// <summary>
    /// Set by the View to show a Yes/No confirmation dialog.
    /// Parameters: message, title. Returns true when the user confirms.
    /// </summary>
    public Func<string, string, bool>? ConfirmationRequested { get; set; }

    [RelayCommand]
    private async Task DeleteAccountAsync(AccountModel? account)
    {
        if (account == null) return;

        if (ConfirmationRequested?.Invoke(
            $"Remove the account '{account.AccountLabel}'? This only removes it from QuickMail — your mail on the server is not affected.",
            "Remove Account") != true) return;

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

        var config = _configService.Load();
        if (config.Accounts.Remove(account.Id))
            _configService.Save(config);

        StatusText = $"Account '{account.AccountLabel}' removed. Cleaning up local data…";

        try   { await _localStore.DeleteAccountDataAsync(account.Id); }
        catch (Exception ex) { LogService.Log($"DeleteAccount: failed to purge mail.db — {ex.Message}"); }

        if (account.AuthType == AuthType.OAuth2Microsoft)
        {
            try   { await _oauthService.SignOutAsync(account); }
            catch (Exception ex) { LogService.Log($"DeleteAccount: failed MSAL sign-out — {ex.Message}"); }
        }

        StatusText = $"Account '{account.AccountLabel}' removed.";
        ConnectionStatusText = Accounts.Count == 0 ? "Offline" : $"{Accounts.Count} account(s) connected";
    }

    [RelayCommand]
    private void OpenAccountSettings(AccountModel? account)
    {
        if (account != null)
            OpenAccountSettingsRequested?.Invoke(account);
    }

    // ── Folder context menu commands ──────────────────────────────────────────

    /// <summary>
    /// Refreshes the folder list for one account from the server.
    /// Called after any folder CRUD operation.
    /// </summary>
    public async Task RefreshFolderListAsync(Guid accountId)
    {
        try
        {
            using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var folderList  = await _imap.GetFoldersAsync(accountId, cts.Token);
            _cachedFolders[accountId] = folderList;
            var account = Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account != null) ApplyAccountStatus(account, folderList);
            RebuildFolderListFromCache();
        }
        catch (Exception ex)
        {
            LogService.Log("RefreshFolderList", ex);
            StatusText = $"Failed to refresh folders: {ex.Message}";
        }
    }

    /// <summary>Creates a new folder under the given parent and refreshes the tree.</summary>
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

    /// <summary>Moves a folder to a new parent (IMAP RENAME) and refreshes the tree.</summary>
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

    /// <summary>Copies a folder (and all its messages) to a new parent and refreshes the tree.</summary>
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

    /// <summary>
    /// Moves all messages in the folder to Trash, deletes the folder, and refreshes the tree.
    /// Shows a confirmation dialog first.
    /// </summary>
    public async Task DeleteFolderAsync(FolderTreeNode node)
    {
        if (node.Folder == null || node.IsHeader) return;

        if (ConfirmationRequested?.Invoke(
            $"Delete the folder '{node.Label}' and move all its messages to Trash?",
            "Delete Folder") != true) return;

        StatusText = $"Deleting folder '{node.Label}'…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _imap.DeleteFolderAsync(node.Folder.AccountId, node.Folder.FullName, cts.Token);

            // If the deleted folder was selected, fall back to All Mail
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

    /// <summary>Moves the given messages to a destination folder and removes them from the current view.</summary>
    public async Task MoveSelectedMessagesToFolderAsync(IReadOnlyList<MailMessageSummary> messages, MailFolderModel destination)
    {
        if (messages.Count == 0) return;

        var label  = messages.Count == 1 ? "message" : $"{messages.Count} messages";
        StatusText = $"Moving {label}…";
        IsBusy     = true;
        try
        {
            ReplaceCts(ref _messageActionCts, out var ct);

            var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
            {
                var uids = group.Select(m => m.UniqueId).ToList();
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids,
                    destination.FullName, ct);
                if (!OnlineMode)
                    await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            foreach (var msg in messages)
                Messages.Remove(msg);

            // Was missing the To-view branch before §2.1; helper covers all three.
            RebuildActiveGroupView();

            StatusText = $"{messages.Count} {(messages.Count == 1 ? "message" : "messages")} moved to {destination.DisplayName}.";
            Announce(StatusText);
            // Conversations/From: LandOnX in the view handles focus after rebuild.
            if (ViewMode == ViewMode.Messages && Messages.Count > 0)
                MessageListFocusRequested?.Invoke();
        }
        catch (OperationCanceledException) { StatusText = "Move cancelled."; }
        catch (Exception ex)
        {
            StatusText = $"Failed to move: {ex.Message}";
            LogService.Log("MoveMessages", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Copies the given messages to a destination folder without removing them from the current view.</summary>
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
        SelectedMessage = group!.Messages[0]; // newest first
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

    // ── ToGroup commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteToGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await DeleteMessagesAsync(group.Messages);
    }

    [RelayCommand]
    private void ExpandToGroup(SenderGroup? group)
    {
        if (group != null) group.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseToGroup(SenderGroup? group)
    {
        if (group != null) group.IsExpanded = false;
    }

    [RelayCommand]
    private void ExpandAllToGroups()
    {
        foreach (var g in ToGroups) g.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllToGroups()
    {
        foreach (var g in ToGroups) g.IsExpanded = false;
    }

    [RelayCommand]
    private async Task ReplyToGroupAsync(SenderGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAllToGroupAsync(SenderGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task ForwardToGroupAsync(SenderGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        await Forward();
    }

    // ── SenderGroup context menu commands ─────────────────────────────────────

    [RelayCommand]
    private void ExpandSenderGroup(SenderGroup? group)
    {
        if (group != null) group.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseSenderGroup(SenderGroup? group)
    {
        if (group != null) group.IsExpanded = false;
    }

    [RelayCommand]
    private void ExpandAllSenderGroups()
    {
        foreach (var g in SenderGroups) g.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllSenderGroups()
    {
        foreach (var g in SenderGroups) g.IsExpanded = false;
    }

    [RelayCommand]
    private async Task ReplySenderGroupAsync(SenderGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAllSenderGroupAsync(SenderGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task ForwardSenderGroupAsync(SenderGroup? group)
    {
        if (group?.Messages.Count == 0) return;
        SelectedMessage = group!.Messages[0];
        await Forward();
    }

    [RelayCommand]
    private async Task DeleteSenderGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await DeleteMessagesAsync(group.Messages);
    }

    // ── View mode command ─────────────────────────────────────────────────────

    [RelayCommand]
    private void SetViewMode(string? mode)
    {
        ViewMode = ConfigModel.ParseViewMode(mode);
    }

    // ── Search command ────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText     = string.Empty;
        IsSearchActive = false;
    }

    // ── Filter command ────────────────────────────────────────────────────────

    [RelayCommand]
    private Task SetFilterAsync(string? filter)
    {
        ActiveFilter = filter?.ToLowerInvariant() switch
        {
            "unread"      => MessageFilter.Unread,
            "read"        => MessageFilter.Read,
            "attachments" => MessageFilter.WithAttachments,
            "replied"     => MessageFilter.Replied,
            "forwarded"   => MessageFilter.Forwarded,
            _             => MessageFilter.All,
        };
        return Task.CompletedTask;
    }

    // ── Sort command ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetSort(string? sort)
    {
        ActiveSort = ConfigModel.ParseSort(sort);
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

        var dlg = new SaveFileDialog
        {
            FileName = att.FileName,
            Title    = "Save Attachment",
        };
        if (dlg.ShowDialog() != true) return;
        await File.WriteAllBytesAsync(dlg.FileName, att.Content!);
        StatusText = $"Saved {att.FileName}.";
    }

    [RelayCommand]
    private async Task SaveAllAttachmentsAsync()
    {
        if (MessageDetail == null || MessageDetail.Attachments.Count == 0) return;

        var dlg = new OpenFolderDialog { Title = "Choose folder to save attachments" };
        if (dlg.ShowDialog() != true) return;
        var folder = dlg.FolderName;

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
                {
                    // Strip directory components from the server-supplied filename to
                    // prevent path traversal writing outside the chosen save folder.
                    var safeFileName = Path.GetFileName(att.FileName);
                    if (string.IsNullOrEmpty(safeFileName)) safeFileName = "attachment";
                    await File.WriteAllBytesAsync(Path.Combine(folder, safeFileName), att.Content);
                }
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

        // Strip any directory components from the server-supplied filename to prevent
        // path traversal (e.g. a crafted name like "../../Startup/evil.exe").
        var safeFileName = Path.GetFileName(att.FileName);
        if (string.IsNullOrEmpty(safeFileName)) safeFileName = "attachment";

        var ext = Path.GetExtension(safeFileName).ToLowerInvariant();
        if (DangerousExtensions.Contains(ext))
        {
            if (ConfirmationRequested?.Invoke(
                $"'{safeFileName}' is an executable file type. Opening it could be dangerous. Continue?",
                "Security Warning") != true) return;
        }

        // Per-attachment subfolder so two messages with the same attachment name
        // (invoice.pdf, invoice.pdf) don't overwrite each other in %TEMP%\QuickMail.
        var tempDir = Path.Combine(Path.GetTempPath(), "QuickMail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, safeFileName);
        await File.WriteAllBytesAsync(tempPath, att.Content!);
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    public void RefreshAccountList()
    {
        LoadAccountList();
        // Reconnect any accounts that aren't already connected (e.g. newly added OAuth2 accounts)
        _ = Task.Run(async () =>
        {
            foreach (var account in Accounts)
            {
                if (_cachedFolders.ContainsKey(account.Id)) continue;
                var result = await ConnectOneAccountAsync(account);
                App.Current.Dispatcher.Invoke(() => ApplyAccountStatus(account, result.Folders));
                if (result.Folders != null)
                {
                    _cachedFolders[result.Id] = result.Folders;
                    App.Current.Dispatcher.Invoke(RebuildFolderListFromCache);
                }
            }
        });
    }

    // ── Preview extraction ────────────────────────────────────────────────────────

    private static string TruncatePreview(string preview, int lines)
    {
        var limit = lines * 100;
        return preview.Length <= limit ? preview : preview[..limit].TrimEnd();
    }

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

    // ── Calendar invite commands ─────────────────────────────────────────────────

    /// <summary>True when the open message contains a calendar invite.</summary>
    public bool HasCalendarInvite => IsMessageOpen && MessageDetail?.CalendarInvite != null;

    /// <summary>
    /// Builds an accessible HTML event card for display in the WebView2 reading pane.
    /// The card is prepended to the message body HTML by the View.
    /// </summary>
    public string BuildEventCardHtml()
    {
        var invite = MessageDetail?.CalendarInvite;
        if (invite == null) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append("<div style=\"border:1px solid #888;border-radius:6px;padding:12px;margin:0 0 16px 0;background:#f5f5f5;color:#222;font-family:Segoe UI,Arial,sans-serif;font-size:13px;line-height:1.45;\" role=\"region\" aria-label=\"");
        sb.Append(System.Net.WebUtility.HtmlEncode(invite.DisplaySummary));
        sb.Append("\">");
        sb.Append("<div style=\"font-weight:bold;font-size:15px;margin-bottom:8px;\">Event Invitation</div>");

        if (!string.IsNullOrWhiteSpace(invite.Summary))
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>Event:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Summary));
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.OrganizerName))
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>Organizer:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.OrganizerName));
            sb.Append("</div>");
        }
        else if (!string.IsNullOrWhiteSpace(invite.Organizer))
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>Organizer:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Organizer));
            sb.Append("</div>");
        }

        if (invite.StartTime.HasValue)
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>When:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.StartTime.Value.ToLocalTime().ToString("f")));
            if (invite.EndTime.HasValue)
            {
                sb.Append(" \u2013 ");
                sb.Append(System.Net.WebUtility.HtmlEncode(invite.EndTime.Value.ToLocalTime().ToString("t")));
            }
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.Location))
        {
            sb.Append("<div style=\"margin-bottom:8px;\"><strong>Location:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Location));
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.Description))
        {
            sb.Append("<div style=\"margin-bottom:8px;white-space:pre-wrap;\">");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Description));
            sb.Append("</div>");
        }

        // Buttons: Accept, Tentative, Decline
        sb.Append("<div style=\"margin-top:8px;\">");
        sb.Append("<a href=\"quickmail:ics-accept\" role=\"button\" aria-label=\"Accept invitation\" ");
        sb.Append("style=\"display:inline-block;padding:6px 14px;margin-right:8px;margin-bottom:4px;");
        sb.Append("background:#107c10;color:#fff;border-radius:4px;text-decoration:none;font-weight:600;\">Accept</a>");
        sb.Append("<a href=\"quickmail:ics-tentative\" role=\"button\" aria-label=\"Tentatively accept invitation\" ");
        sb.Append("style=\"display:inline-block;padding:6px 14px;margin-right:8px;margin-bottom:4px;");
        sb.Append("background:#ff8c00;color:#fff;border-radius:4px;text-decoration:none;font-weight:600;\">Tentative</a>");
        sb.Append("<a href=\"quickmail:ics-decline\" role=\"button\" aria-label=\"Decline invitation\" ");
        sb.Append("style=\"display:inline-block;padding:6px 14px;margin-bottom:4px;");
        sb.Append("background:#d13438;color:#fff;border-radius:4px;text-decoration:none;font-weight:600;\">Decline</a>");
        sb.Append("</div>");

        sb.Append("</div>");
        return sb.ToString();
    }

    [RelayCommand]
    private async Task AcceptInvite()
    {
        await SendIcsReply("ACCEPTED", "accepted");
    }

    [RelayCommand]
    private async Task DeclineInvite()
    {
        await SendIcsReply("DECLINED", "declined");
    }

    [RelayCommand]
    private async Task TentativeInvite()
    {
        await SendIcsReply("TENTATIVE", "tentatively accepted");
    }

    private async Task SendIcsReply(string partStat, string actionLabel)
    {
        var invite = MessageDetail?.CalendarInvite;
        if (invite == null) return;

        var account = Accounts.FirstOrDefault(a => a.Id == MessageDetail!.AccountId);
        if (account == null)
        {
            Announce($"Cannot send calendar response: account not found.", AnnouncementCategory.Result);
            return;
        }

        try
        {
            var attendeeName = account.SenderDisplayName;
            var attendeeEmail = account.Username;
            var icsContent = invite.GenerateReply(attendeeEmail, attendeeName, partStat);

            var password = _credentials.GetPassword(account.Id);
            await _smtp.SendIcsReplyAsync(icsContent, account, password, invite.Organizer ?? "");

            var eventTitle = invite.Summary ?? "calendar event";
            Announce($"Calendar response sent: {actionLabel} \u2014 {eventTitle}.", AnnouncementCategory.Result);
        }
        catch (Exception ex)
        {
            LogService.Log($"SendIcsReply ({partStat})", ex);
            Announce($"Failed to send calendar response: {ex.Message}", AnnouncementCategory.Result);
        }
    }
}
