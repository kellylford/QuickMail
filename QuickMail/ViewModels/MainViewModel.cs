using System;
using System.Collections.Concurrent;
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
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IMailService _imap;
    private readonly IChangeNotifier? _changeNotifier;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly ILocalStoreService _localStore;
    private readonly IOAuthService _oauthService;
    private readonly ISyncService _syncService;
    private readonly IContactSyncService? _contactSync;
    private readonly IConfigService _configService;
    private readonly IViewService _viewService;
    private readonly ICommandRegistry _commandRegistry;
    private readonly IRuleService _ruleService;
    private readonly ISendMailService _smtp;
    private readonly IFlagService? _flagService;
    private readonly ICalendarService? _calendarService;
    private readonly IGraphCalendarSyncService? _graphCalendarSync;

    // Distinct per-account server calendars (from the local store), cached on the UI thread so the
    // synchronous BuildFolderTree can add a grandchild node per calendar. Refreshed after each
    // calendar sync and on initial load.
    private IReadOnlyList<(Guid AccountId, string CalendarId, string CalendarName)> _calendarSources = [];
    private readonly IUpdateCheckService? _updateCheckService;
    // Windows toast notifications for new mail. Null in tests and when the OS/platform is
    // unsupported. Calling into it is an OS side-effect a service owns, not a View-layer type,
    // so it does not violate the no-UI-types-in-ViewModels rule.
    private readonly INotificationService? _notifications;
    // New-mail notification state (UI-thread-owned): threshold excludes the startup backlog;
    // the key set de-dupes across repeated IDLE fires within the session.
    private readonly DateTimeOffset _notifyThresholdUtc = DateTimeOffset.UtcNow;
    private readonly HashSet<string> _notifiedMessageKeys = new();
    // A single evaluation yielding more genuinely-new messages than this is a catch-up backlog
    // (mail that piled up while the machine slept or the connection was down), not real-time
    // arrivals — so it does not raise a toast. See MaybeNotifyNewMail.
    private const int MaxNotifyBatchSize = 5;
    // Exposes hex strings only, so consuming it here does not violate the
    // no-UI-types-in-ViewModels rule. Null in tests that don't exercise theming.
    private readonly IThemeService? _themeService;
    // UI-thread marshaller — ViewModels must not touch Dispatcher directly (CLAUDE.md MVVM rules).
    private readonly IUiDispatcher _ui;

    // Separate CTS per operation type so they can't cancel each other accidentally
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _folderCts;
    private CancellationTokenSource? _messageLoadCts;
    private CancellationTokenSource? _flagActionCts;

    // Message actions (delete/move) each get their own token linked to this shutdown source instead
    // of sharing one replaceable CTS. Sharing meant a second Delete/Move cancelled the previous one's
    // in-flight IMAP work mid-operation (issue #311: a rapid series of deletes aborted each other,
    // surfacing as "Delete may not have completed"). Cancelled only at shutdown, in Dispose.
    private readonly CancellationTokenSource _messageActionShutdownCts = new();
    private CancellationTokenSource? _prefetchCts;

    private const int PrefetchRadiusAroundOpen = 5;
    private const int PrefetchTopOnFolderLoad  = 10;
    private CancellationTokenSource? _bgSyncCts;

    // Debounced calendar harvest: re-harvests events 2s after the last FolderSynced
    // event so we don't harvest on every folder during a multi-folder sync.
    private System.Threading.Timer? _calendarHarvestTimer;

    // Periodic Graph calendar pull (read-down v1): first pass right after the startup mail sync,
    // then every 15 minutes. Callback marshals to the UI thread via _ui.Post (like the harvest
    // timer above). Disposed in Dispose; in-flight HTTP is cancelled via _graphCalSyncCts.
    private System.Threading.Timer? _graphCalendarSyncTimer;
    private CancellationTokenSource? _graphCalSyncCts;
    private bool _graphCalendarSyncRunning; // UI-thread-owned re-entrancy guard (timer vs. F5)
    private static readonly TimeSpan GraphCalendarSyncInterval = TimeSpan.FromMinutes(15);

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

    /// <summary>
    /// Called by MainWindow.OnClosed (app shutdown). Cancels all in-flight operations so
    /// background work (sync, prefetch, message loads) unwinds via OperationCanceledException
    /// instead of being killed with the process, then releases the CTS handles and timer.
    /// </summary>
    public void Dispose()
    {
        DrainCts(ref _connectCts);
        DrainCts(ref _folderCts);
        DrainCts(ref _messageLoadCts);
        try { _messageActionShutdownCts.Cancel(); _messageActionShutdownCts.Dispose(); } catch { /* best effort at shutdown */ }
        DrainCts(ref _flagActionCts);
        DrainCts(ref _prefetchCts);
        DrainCts(ref _bgSyncCts);
        foreach (var cts in _folderCountCts.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* best effort at shutdown */ }
        }
        _folderCountCts.Clear();
        _calendarHarvestTimer?.Dispose();
        _calendarHarvestTimer = null;
        _graphCalendarSyncTimer?.Dispose();
        _graphCalendarSyncTimer = null;
        DrainCts(ref _graphCalSyncCts);
        _reminderTimer?.Dispose();
        _reminderTimer = null;
        GC.SuppressFinalize(this);
    }

    private static void DrainCts(ref CancellationTokenSource? slot)
    {
        var cts = Interlocked.Exchange(ref slot, null);
        // Cancel before Dispose so in-flight tasks get OperationCanceledException
        // rather than ObjectDisposedException.
        try { cts?.Cancel(); cts?.Dispose(); } catch { /* best effort at shutdown */ }
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

    private bool _announceFlagStatus;
    private string? _activeFlagFilterId;
    private EventHandler? _onFlagDefinitionsChanged;
    private Action<Guid, bool>? _onReachabilityChanged;

    // Retains folder lists for every account that has been connected this session
    private readonly Dictionary<Guid, List<MailFolderModel>> _cachedFolders = new();
    public IReadOnlyDictionary<Guid, List<MailFolderModel>> CachedFolders => _cachedFolders;

    // Debounced folder-unread-count refresh (issue #227). Folder counts are server-authoritative
    // (IMAP STATUS), which matters for Gmail where marking one message read propagates \Seen across
    // every label/folder it belongs to. One pending refresh per account; a burst of mark-reads
    // coalesces into a single STATUS sweep after a short quiet period.
    private readonly Dictionary<Guid, CancellationTokenSource> _folderCountCts = new();
    private static readonly TimeSpan FolderCountRefreshDelay = TimeSpan.FromSeconds(1);
    // Minimum spacing between STATUS sweeps for one account, so steady reading (each open marks a
    // message read → one refresh request) doesn't fire a full folder STATUS sweep per message (#227).
    private static readonly TimeSpan FolderCountMinInterval = TimeSpan.FromSeconds(6);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFolderCountSweep = new();

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
    public static readonly MailFolderModel AllFlaggedFolder = new()
    {
        FullName    = "\u0000AllFlagged",
        DisplayName = "All Flagged"
    };

    /// <summary>Virtual folder sentinel that opens the calendar event list.</summary>
    public static readonly MailFolderModel CalendarFolder = new()
    {
        FullName    = "\u0000Calendar",
        DisplayName = "Calendar"
    };

    // Per-source calendar children under the Calendar node: " Calendar:{guid}" for one
    // account, "local" for locally-authored appointments, "all" for the merged view, and
    // "{guid}|{escapedCalId}" for a single specific calendar of that account.
    internal const string CalendarSourcePrefix = " Calendar:";

    /// <summary>True for the Calendar node or any of its per-source children.</summary>
    internal static bool IsCalendarFolderName(string? fullName) =>
        fullName != null
        && (string.Equals(fullName, CalendarFolder.FullName, StringComparison.Ordinal)
            || fullName.StartsWith(CalendarSourcePrefix, StringComparison.Ordinal));

    /// <summary>
    /// Maps a calendar folder name to the source filter it selects. Returns null only for a name that
    /// is not a per-source child (e.g. the bare Calendar node); the "all" child returns a filter that
    /// matches every source. See <see cref="CalendarSourcePrefix"/> for the tail encoding.
    /// </summary>
    internal static CalendarFilter? CalendarFilterFor(string fullName)
    {
        if (!fullName.StartsWith(CalendarSourcePrefix, StringComparison.Ordinal)) return null;
        var tail = fullName[CalendarSourcePrefix.Length..];
        if (tail == "all")   return new CalendarFilter(null, null);
        if (tail == "local") return new CalendarFilter(Guid.Empty, null);

        // "{guid}|{escapedCalId}" selects one specific calendar; "{guid}" selects all of that
        // account's calendars.
        var sep = tail.IndexOf('|');
        if (sep >= 0)
        {
            var calId = Uri.UnescapeDataString(tail[(sep + 1)..]);
            return Guid.TryParse(tail[..sep], out var gid) ? new CalendarFilter(gid, calId) : null;
        }
        return Guid.TryParse(tail, out var id) ? new CalendarFilter(id, null) : null;
    }

    /// <summary>
    /// Which calendar source(s) the calendar list shows. <see cref="Account"/> null = every source
    /// merged; <see cref="Guid.Empty"/> = locally-authored appointments only; otherwise that
    /// account's rows. <see cref="CalendarId"/> null = all of that account's calendars; otherwise the
    /// single tagged calendar.
    /// </summary>
    public sealed record CalendarFilter(Guid? Account, string? CalendarId);

    /// <summary>
    /// True for accounts with a server calendar the app can push appointments to: Microsoft
    /// (Graph backend), Google-signed-in accounts (keyed by auth type — Gmail mail is IMAP), and
    /// iCloud accounts (IMAP host imap.mail.me.com — CalDAV over the app-specific password). Plain
    /// IMAP/password accounts have no server calendar. Mirrors the sync service's per-provider
    /// eligibility; membership here also drives edit/delete write-back (ServerAccountFor).
    /// </summary>
    internal static bool IsCalendarPushAccount(AccountModel a)
        => a.SyncCalendar
           && (a.BackendKind == BackendKind.MicrosoftGraph
               || a.AuthType == AuthType.OAuth2Microsoft
               || a.AuthType == AuthType.OAuth2Google
               || a.ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase));

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
               string.Equals(folder.FullName, AllTrashFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllFlaggedFolder.FullName, StringComparison.Ordinal) ||
               IsCalendarFolderName(folder.FullName);
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
    [NotifyPropertyChangedFor(nameof(IsCalendarView))]
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
    [NotifyPropertyChangedFor(nameof(IsMessageListAreaVisible))]
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

    /// <summary>
    /// True when the calendar virtual folder is the active selection and the
    /// calendar event list is shown in place of the message list.
    /// </summary>
    public bool IsCalendarView => SelectedFolder != null &&
        IsCalendarFolderName(SelectedFolder.FullName);

    public ObservableCollection<FlagDefinition> FlagDefinitions { get; } = [];

    public bool IsFilterAll             => ActiveFilter == MessageFilter.All;
    public bool IsFilterUnread          => ActiveFilter == MessageFilter.Unread;
    public bool IsFilterRead            => ActiveFilter == MessageFilter.Read;
    public bool IsFilterWithAttachments => ActiveFilter == MessageFilter.WithAttachments;
    public bool IsFilterReplied         => ActiveFilter == MessageFilter.Replied;
    public bool IsFilterForwarded       => ActiveFilter == MessageFilter.Forwarded;
    public bool IsFilterToMe            => ActiveFilter == MessageFilter.ToMe;
    public bool IsFilterFlagged         => ActiveFilter == MessageFilter.Flagged;
    public bool IsFilterAllFlagged      => ActiveFilter == MessageFilter.Flagged && _activeFlagFilterId == null;
    public bool IsFilterActive          => ActiveFilter != MessageFilter.All;
    public bool AnnounceFlagStatus      => _announceFlagStatus;
    /// <summary>Named-flag sub-filter id, set by saved views. Null = show all flagged messages.</summary>
    public string? ActiveFlagFilterId   => _activeFlagFilterId;
    public string FilterLabel => ActiveFilter switch
    {
        MessageFilter.Unread          => "Unread",
        MessageFilter.Read            => "Read",
        MessageFilter.WithAttachments => "With Attachments",
        MessageFilter.Replied         => "Replied",
        MessageFilter.Forwarded       => "Forwarded",
        MessageFilter.ToMe            => "To Me",
        MessageFilter.Flagged         => "Flagged",
        _                             => string.Empty,
    };

    public bool IsSortDateDesc    => ActiveSort == MessageSort.DateDescending;
    public bool IsSortDateAsc     => ActiveSort == MessageSort.DateAscending;
    public bool IsSortAlphaAsc    => ActiveSort == MessageSort.AlphaAscending;
    public bool IsSortAlphaDesc   => ActiveSort == MessageSort.AlphaDescending;
    public bool IsSortCountDesc   => ActiveSort == MessageSort.CountDescending;
    public bool IsSortCountAsc    => ActiveSort == MessageSort.CountAscending;
    public bool IsSortFlaggedFirst => ActiveSort == MessageSort.FlaggedFirst;
    public bool IsCountSortAvailable => ViewMode != ViewMode.Messages;
    public string SortLabel => ActiveSort switch
    {
        MessageSort.DateAscending   => "Oldest First",
        MessageSort.AlphaAscending  => "A → Z",
        MessageSort.AlphaDescending => "Z → A",
        MessageSort.CountDescending => "Most Messages",
        MessageSort.CountAscending  => "Fewest Messages",
        MessageSort.FlaggedFirst    => "Flagged First",
        _                           => string.Empty,
    };

    /// <summary>
    /// Snapshot of current UI state (theme, view, sort) for a bug report's Environment section.
    /// Captured when the report window opens — see <see cref="Models.BugReportContext"/>.
    /// </summary>
    public Models.BugReportContext CaptureBugReportContext() => new()
    {
        Theme = _themeService?.ConfiguredThemeName ?? "Default",
        View  = ActiveView?.Name is { Length: > 0 } viewName
                    ? $"{viewName} ({ViewModeName})"
                    : ViewModeName,
        Sort  = ActiveSort switch
        {
            MessageSort.DateDescending  => "Newest First",
            MessageSort.DateAscending   => "Oldest First",
            MessageSort.AlphaAscending  => "A → Z",
            MessageSort.AlphaDescending => "Z → A",
            MessageSort.CountDescending => "Most Messages",
            MessageSort.CountAscending  => "Fewest Messages",
            MessageSort.FlaggedFirst    => "Flagged First",
            _                           => ActiveSort.ToString(),
        },
    };

    private string ViewModeName => ViewMode switch
    {
        ViewMode.Conversations => "Conversations",
        ViewMode.From          => "From",
        ViewMode.To            => "To",
        _                      => "Messages",
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

    /// <summary>
    /// Category the View should use when announcing the *current* <see cref="StatusText"/> change to a
    /// screen reader. A one-shot override: <see cref="SetStatus"/> sets it, assigns StatusText (whose
    /// change the View handles synchronously and reads this value), then resets it to
    /// <see cref="AnnouncementCategory.Status"/>. So a plain <c>StatusText = …</c> always announces as
    /// Status, while delete/archive route their chatter through <see cref="AnnouncementCategory.MessageAction"/>
    /// so it can be silenced independently (issue #317).
    /// </summary>
    public AnnouncementCategory StatusAnnouncementCategory { get; private set; } = AnnouncementCategory.Status;

    /// <summary>
    /// Sets the visible status text and tags the accompanying announcement with <paramref name="category"/>.
    /// See <see cref="StatusAnnouncementCategory"/> for why the reset is safe (StatusText's PropertyChanged
    /// fires synchronously, so the View captures the category before this method returns).
    /// </summary>
    private void SetStatus(string text, AnnouncementCategory category)
    {
        StatusAnnouncementCategory = category;
        StatusText = text;
        StatusAnnouncementCategory = AnnouncementCategory.Status;
    }

    [ObservableProperty]
    private string _rulesStatusText = string.Empty;

    [ObservableProperty]
    private string _connectionStatusText = "Offline";

    [ObservableProperty]
    private string _lastSyncText = "Never synced";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showMessageStatus;

    /// <summary>
    /// Sticky "read as plain text" preference (issue #34). Bound one-way to the View-menu
    /// check state; the View reads it when rendering a message body. Kept in sync with
    /// <see cref="ConfigModel.ReadAsPlainText"/> by the toggle command and <see cref="ApplySettings"/>.
    /// </summary>
    [ObservableProperty]
    private bool _readAsPlainText;

    // Running version for the Help "running version" entry, e.g. "0.7.9" (or "0.7.9.1" for a
    // hotfix). Shared with the About dialog and update check via AppVersion; deliberately not the
    // informational/product version, which the SDK can suffix with a git commit hash.
    private static readonly string CurrentVersion = Helpers.AppVersion.Display;

    // Resting state of the update entry: no newer release, so surface the running version instead
    // (issue #169) so the Help menu always answers "what am I running?".
    private static readonly string NoUpdateText = $"No updates available — running version {CurrentVersion}";

    // Help-menu label for the update entry. Always shown so users know the check exists and is
    // available on demand; NoUpdateText is the resting state, replaced with the version
    // string when a newer release is found. UpdateReleaseUrl being non-empty is the signal that an
    // update is actually available (drives the status-bar button and the menu's activation behavior).
    [ObservableProperty]
    private string _updateAvailableText = NoUpdateText;

    [ObservableProperty]
    private string _updateReleaseUrl = string.Empty;

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

    // ── Tab & Window Management (Phase 6) ────────────────────────────────────────

    [ObservableProperty]
    private BatchObservableCollection<TabSessionViewModel> _openTabs = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingPaneVisible))]
    [NotifyPropertyChangedFor(nameof(IsMessageListAreaVisible))]
    private TabSessionViewModel? _activeTab;

    /// <summary>
    /// True when the message-list area (flat list plus conversation and sender/recipient trees)
    /// should occupy the content region. False only when a message tab is active in Tab mode and
    /// its body is actually open — then the message fills the whole content region so the tab shows
    /// just the message, rather than a copy of the message list with the body as a sliver below it.
    /// The <see cref="IsMessageOpen"/> term matters: it keeps the list visible while the tab's
    /// message is still loading, and leaves it visible if the load fails (MessageDetail stays null),
    /// so a failed/slow open never blanks the whole pane.
    /// </summary>
    public bool IsMessageListAreaVisible =>
        !(MessageOpenMode == MessageOpenMode.Tab && ActiveTab is MessageTabViewModel && IsMessageOpen);

    /// <summary>
    /// True when a message is open in a standalone MessageWindow (Window mode).
    /// Used to suppress background-sync focus interruptions and gate commands
    /// (e.g. Grab Addresses) while the main window's reading pane is empty.
    /// </summary>
    [ObservableProperty]
    private bool _isMessageOpenInWindow;

    /// <summary>True when the tab strip should be visible.</summary>
    public bool ShowTabStrip => OpenTabs.Count > 0 || MessageOpenMode == MessageOpenMode.Tab;

    /// <summary>
    /// True when the inline reading pane should be shown.
    /// In ReadingPane mode this is driven by IsMessageOpen.
    /// In Tab/Window mode the reading pane is driven by the active tab, but the
    /// existing IsMessageOpen flag is still used as the gate (it is set when
    /// the active tab activates its message).
    /// </summary>
    public bool ReadingPaneVisible => IsMessageOpen;

    /// <summary>Current message open mode, read from config on startup.</summary>
    public MessageOpenMode MessageOpenMode { get; private set; } = MessageOpenMode.ReadingPane;

    // ── Calendar ──────────────────────────────────────────────────────────────────

    /// <summary>ViewModel for the calendar event list. Null when no calendar service is wired (e.g. tests).</summary>
    public CalendarViewModel? CalendarVm { get; private set; }

    // ── Tab commands ──────────────────────────────────────────────────────────────

    public void OpenMessageTab(MailMessageSummary summary)
    {
        EnsureMessageListTab(); // no-op unless Tab mode is active

        // Duplicate: activate the existing tab if already open.
        var existing = OpenTabs.OfType<MessageTabViewModel>()
                               .FirstOrDefault(t => t.Summary.MessageId == summary.MessageId
                                                 && t.Summary.AccountId == summary.AccountId);
        if (existing != null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new MessageTabViewModel(summary)
        {
            SourceFolderName = summary.FolderName,
            AccountId        = summary.AccountId,
        };
        tab.CloseRequested += t => CloseTab(t);
        OpenTabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(ShowTabStrip));
        var msgTabCount = OpenTabs.OfType<MessageTabViewModel>().Count();
        Announce($"Opened tab: {tab.Title}. {msgTabCount} tab{(msgTabCount == 1 ? "" : "s")} open.");
    }

    public void CloseTab(TabSessionViewModel tab)
    {
        if (tab is MessageListTabViewModel) return; // permanent tab, never closed by user

        var idx = OpenTabs.IndexOf(tab);
        if (idx < 0) return;

        OpenTabs.Remove(tab);
        OnPropertyChanged(nameof(ShowTabStrip));

        var remaining = OpenTabs.OfType<MessageTabViewModel>().Count();
        Announce($"Closed tab: {tab.Title}. {remaining} tab{(remaining == 1 ? "" : "s")} remaining.");

        if (ActiveTab == tab)
        {
            var msgListTab = OpenTabs.OfType<MessageListTabViewModel>().FirstOrDefault();
            if (OpenTabs.Count == 0 || (msgListTab != null && OpenTabs.Count == 1))
            {
                // Only the message list tab (or nothing) remains.
                ActiveTab     = msgListTab;
                IsMessageOpen = false;
                MessageDetail = null;
            }
            else
            {
                // Activate the tab at the same position, or the last one.
                ActiveTab = OpenTabs[Math.Min(idx, OpenTabs.Count - 1)];
            }
        }
    }

    /// <summary>
    /// Activates the permanent message-list tab (Tab mode), revealing the message list while
    /// leaving any open message tabs in the strip. Returns false when there is no message-list
    /// tab (i.e. not in Tab mode).
    /// </summary>
    public bool ActivateMessageListTab()
    {
        var listTab = OpenTabs.OfType<MessageListTabViewModel>().FirstOrDefault();
        if (listTab == null) return false;
        ActiveTab = listTab;
        return true;
    }

    public void ActivateNextTab()
    {
        var messageTabs = OpenTabs.OfType<MessageTabViewModel>().ToList();
        if (messageTabs.Count == 0) return;
        var cur = ActiveTab as MessageTabViewModel;
        var idx = cur == null ? 0 : (messageTabs.IndexOf(cur) + 1) % messageTabs.Count;
        ActiveTab = messageTabs[idx];
        Announce($"Tab {idx + 1} of {messageTabs.Count}: {ActiveTab.Title}.");
    }

    public void ActivatePrevTab()
    {
        var messageTabs = OpenTabs.OfType<MessageTabViewModel>().ToList();
        if (messageTabs.Count == 0) return;
        var cur = ActiveTab as MessageTabViewModel;
        var idx = cur == null ? messageTabs.Count - 1
                              : (messageTabs.IndexOf(cur) - 1 + messageTabs.Count) % messageTabs.Count;
        ActiveTab = messageTabs[idx];
        Announce($"Tab {idx + 1} of {messageTabs.Count}: {ActiveTab.Title}.");
    }

    public void ActivateTabByIndex(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > OpenTabs.Count) return;
        ActiveTab = OpenTabs[oneBasedIndex - 1];
    }

    public void ActivateLastTab()
    {
        if (OpenTabs.Count == 0) return;
        ActiveTab = OpenTabs[^1];
    }

    public void MoveTabLeft()
    {
        if (ActiveTab is not MessageTabViewModel) return;
        var idx = OpenTabs.IndexOf(ActiveTab);
        // Don't move before the message list tab (always index 0 in Tab mode).
        var minIdx = OpenTabs.OfType<MessageListTabViewModel>().Any() ? 1 : 0;
        if (idx <= minIdx) return;
        OpenTabs.Move(idx, idx - 1);
        Announce($"Tab moved to position {idx}.");
    }

    public void MoveTabRight()
    {
        if (ActiveTab is not MessageTabViewModel) return;
        var idx = OpenTabs.IndexOf(ActiveTab);
        if (idx < 0 || idx >= OpenTabs.Count - 1) return;
        OpenTabs.Move(idx, idx + 1);
        Announce($"Tab moved to position {idx + 2}.");
    }

    public void CloseAllOtherTabs()
    {
        if (ActiveTab == null || OpenTabs.Count <= 1) return;
        var toClose = OpenTabs.Where(t => t != ActiveTab && t is not MessageListTabViewModel).ToList();
        if (toClose.Count == 0) return;
        using (OpenTabs.BeginBatchScope())
            foreach (var t in toClose) OpenTabs.Remove(t);
        OnPropertyChanged(nameof(ShowTabStrip));
    }

    /// <summary>
    /// Raised to ask MainWindow to promote the given tab to a new MessageWindow.
    /// </summary>
    public event Action<MessageTabViewModel>? TabPromoteToWindowRequested;

    public void PromoteActiveTabToWindow()
    {
        if (ActiveTab is not MessageTabViewModel msgTab) return;
        TabPromoteToWindowRequested?.Invoke(msgTab);
    }

    // ── Message-list tab (Tab mode) ───────────────────────────────────────────────

    /// <summary>
    /// Ensures the permanent message-list tab is first in OpenTabs when Tab mode is active.
    /// No-op in ReadingPane or Window mode, and no-op if the tab already exists.
    /// </summary>
    public void EnsureMessageListTab()
    {
        if (MessageOpenMode != MessageOpenMode.Tab) return;
        if (OpenTabs.OfType<MessageListTabViewModel>().Any()) return;

        var tab = new MessageListTabViewModel();
        OpenTabs.Insert(0, tab);
        if (ActiveTab == null) ActiveTab = tab;
        OnPropertyChanged(nameof(ShowTabStrip));
    }

    /// <summary>Removes the message-list tab when leaving Tab mode.</summary>
    private void RemoveMessageListTab()
    {
        var tab = OpenTabs.OfType<MessageListTabViewModel>().FirstOrDefault();
        if (tab == null) return;
        OpenTabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab     = OpenTabs.Count > 0 ? OpenTabs[0] : null;
            IsMessageOpen = false;
            MessageDetail = null;
        }
        OnPropertyChanged(nameof(ShowTabStrip));
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
        ISendMailService smtpService,
        bool onlineMode = false,
        IFlagService? flagService = null,
        ICalendarService? calendarService = null,
        IChangeNotifier? changeNotifier = null,
        IUpdateCheckService? updateCheckService = null,
        IUiDispatcher? uiDispatcher = null,
        IThemeService? themeService = null,
        INotificationService? notificationService = null,
        IContactSyncService? contactSyncService = null,
        IGraphCalendarSyncService? graphCalendarSyncService = null)
    {
        _imap            = imap;
        _ui              = uiDispatcher ?? new WpfUiDispatcher();
        _changeNotifier  = changeNotifier;
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
        _flagService          = flagService;
        _calendarService      = calendarService;
        _updateCheckService   = updateCheckService;
        _themeService         = themeService;
        _notifications        = notificationService;
        _contactSync          = contactSyncService;
        _graphCalendarSync    = graphCalendarSyncService;
        OnlineMode            = onlineMode;

        var cfg = _configService.Load();
        _showMessageStatus = cfg.ShowMessageStatus;
        _readAsPlainText = cfg.ReadAsPlainText;
        _previewLines = cfg.PreviewLines;
        _showPreview = _previewLines > 0;
        _syncDays = cfg.SyncDays;
        _viewMode = ConfigModel.ParseViewMode(cfg.ViewMode);
        MessageOpenMode = cfg.Windowing.MessageOpenMode;
        EnsureMessageListTab();
        _activeSort = ConfigModel.ParseSort(cfg.Sort);
        _announceFlagStatus = cfg.AnnounceFlagStatus;

        // Calendar — only when a calendar service is wired (skipped in tests).
        if (_calendarService != null)
        {
            // The accounts provider is deferred (evaluated when the editor opens) because the
            // account list loads after this constructor. Server calendars the app can write to:
            // Microsoft (Graph backend) accounts and Google-signed-in accounts (Gmail mail is
            // IMAP — the identity provider, not the mail backend, is what makes calendar push
            // possible, mirroring calendar sync eligibility). Plain IMAP/password accounts have
            // no server calendar and are excluded.
            CalendarVm = new CalendarViewModel(_calendarService, onlineMode, cfg.ShowDeclinedEvents,
                                               cfg.CalendarListShowFieldLabels,
                                               _graphCalendarSync,
                                               () => Accounts.Where(IsCalendarPushAccount).ToList(),
                                               () => Accounts.ToList(),
                                               // Discovered calendar sources (per iCloud calendar) feed the
                                               // save-target picker so each iCloud calendar is its own target.
                                               () => _calendarSources);
            RemindersEnabled = cfg.CalendarReminders;
            ReminderLeadMinutes = cfg.CalendarReminderMinutes;
            StartReminderTimer();
        }

        _syncService.FolderSynced    += OnFolderSynced;
        _syncService.MessagesRemoved += OnMessagesRemoved;
        _syncService.RulesApplied    += OnRulesApplied;
        if (_changeNotifier != null)
            _changeNotifier.InboxNewMailDetected += OnInboxNewMailDetected;
        if (_flagService != null)
        {
            _onFlagDefinitionsChanged = (_, _) => _ = OnFlagDefinitionsChangedAsync();
            _flagService.FlagDefinitionsChanged += _onFlagDefinitionsChanged;
        }

        // Load saved views and register their commands before the UI is shown.
        LoadSavedViews();
        RegisterCommands(commandRegistry);
        RegisterThemeCommands();
        UpdateRulesStatusText();
    }

    /// <summary>
    /// Set by the composition root to bind each account to its mail backend (IMAP or Graph) in the
    /// router before the account is connected. Invoked for every account on load/refresh, so accounts
    /// added at runtime via <see cref="RefreshAccountList"/> are registered to the correct backend.
    /// </summary>
    public Action<AccountModel>? RegisterAccountBackend { get; set; }

    public void LoadAccountList(List<AccountModel>? preloaded = null)
    {
        var accounts = preloaded ?? _accountService.LoadAccounts();
        foreach (var account in accounts)
            RegisterAccountBackend?.Invoke(account);
        Accounts = new ObservableCollection<AccountModel>(accounts);
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
            _ = GestureHelper.TryParse(gesture, out defaultKey, out defaultMods);

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

    // ── Theme commands ────────────────────────────────────────────────────────────

    /// <summary>Raised when the user invokes "Manage themes"; the View opens the Theme Manager.</summary>
    public event EventHandler? ThemeManagerRequested;

    /// <summary>
    /// Registers (or re-registers) the theme commands: manager, cycle, and one
    /// hotkey-assignable apply command per available theme (like view.saved.{id}).
    /// Called at startup and again after the Theme Manager closes with changes.
    /// </summary>
    public void RegisterThemeCommands()
    {
        if (_themeService is null) return;

        var stale = _commandRegistry.GetAll()
            .Where(c => c.Id.StartsWith("theme.", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .ToList();
        foreach (var id in stale)
            _commandRegistry.Unregister(id);

        _commandRegistry.Register(new CommandDefinition(
            id: "theme.manager.open", category: "Settings", title: "Manage Themes",
            execute: () => ThemeManagerRequested?.Invoke(this, EventArgs.Empty)));

        _commandRegistry.Register(new CommandDefinition(
            id: "theme.next", category: "Settings", title: "Next Theme",
            execute: () => CycleTheme(+1)));

        _commandRegistry.Register(new CommandDefinition(
            id: "theme.previous", category: "Settings", title: "Previous Theme",
            execute: () => CycleTheme(-1)));

        var cfg = _configService.Load();
        foreach (var theme in _themeService.GetAvailableThemes())
        {
            var commandId = $"theme.apply.{theme.Id}";
            var binding = cfg.CustomHotkeys.FirstOrDefault(h => h.CommandId == commandId);
            Key defaultKey = Key.None;
            ModifierKeys defaultMods = ModifierKeys.None;
            if (!string.IsNullOrEmpty(binding?.Gesture))
                _ = GestureHelper.TryParse(binding.Gesture, out defaultKey, out defaultMods);

            var capturedId = theme.Id;
            _commandRegistry.Register(new CommandDefinition(
                id: commandId,
                category: "Settings",
                title: $"Theme: {theme.Name}",
                execute: () => ApplyThemeById(capturedId),
                defaultKey: defaultKey,
                defaultModifiers: defaultMods));
        }
    }

    /// <summary>Applies a theme and persists the choice (the service never writes config).</summary>
    public void ApplyThemeById(string themeId)
    {
        if (_themeService is null) return;
        var resolvedBefore = _themeService.ResolvedTheme.Id;
        _themeService.ApplyTheme(themeId);
        Helpers.ThemePersistence.PersistConfiguredTheme(_themeService, _configService);

        // When the selection changes but the effective palette does not (e.g.
        // System → Parchment while the OS is in light mode), ThemeChanged never
        // fires and the window's handler stays silent — announce the switch here so
        // cycling always reports the new theme. ConfiguredThemeName (not the
        // resolved name) so cycling to System announces "System", not "Parchment".
        if (_themeService.ResolvedTheme.Id == resolvedBefore)
            Announce($"Theme changed to {_themeService.ConfiguredThemeName}.", AnnouncementCategory.Status);
    }

    /// <summary>Steps to the next/previous theme in display order (System first, then built-ins, then user themes).</summary>
    private void CycleTheme(int direction)
    {
        if (_themeService is null) return;
        var themes = _themeService.GetAvailableThemes();
        if (themes.Count == 0) return;
        var index = 0;
        for (int i = 0; i < themes.Count; i++)
        {
            if (string.Equals(themes[i].Id, _themeService.ConfiguredThemeId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        var next = themes[(index + direction + themes.Count) % themes.Count];
        ApplyThemeById(next.Id);
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
            "tome"        => MessageFilter.ToMe,
            "flagged"     => MessageFilter.Flagged,
            _             => MessageFilter.All,
        };
        ActiveSort = ConfigModel.ParseSort(view.Sort);
        SetActiveFlagFilterId(string.IsNullOrEmpty(view.FlagFilterId) ? null : view.FlagFilterId);

        // Validate the flag filter id against current flag definitions.
        // If the referenced flag has been deleted, treat it as no filter
        // rather than showing an empty list with no explanation.
        if (_activeFlagFilterId != null && _flagService != null &&
            Guid.TryParse(_activeFlagFilterId, out var flagGuid))
        {
            var defs = await _flagService.LoadFlagDefinitionsAsync();
            if (!defs.Exists(d => d.Id == flagGuid))
                SetActiveFlagFilterId(null);
        }

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
                    if (vf.FolderFullName.StartsWith('\x00')) continue;
                    var msgs = await _localStore.LoadFolderSummariesAsync(vf.AccountId, vf.FolderFullName);
                    cached.AddRange(msgs);
                }
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

                await ResolveFlagNamesAsync(cached);
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
                if (vf.FolderFullName.StartsWith('\x00')) continue;
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
                        var maxKey = await _localStore.GetMaxMessageKeyAsync(vf.AccountId, vf.FolderFullName);
                        if (maxKey == "0" && _syncDays > 0)
                            msgs = await _imap.GetMessagesSinceDateAsync(
                                vf.AccountId, vf.FolderFullName, DateTime.UtcNow.AddDays(-_syncDays), ct);
                        else
                        {
                            var initialCount = _configService.Load().InitialSyncCount;
                            msgs = await _imap.GetMessagesSinceAsync(
                                vf.AccountId, vf.FolderFullName, maxKey, initialCount, ct);
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

            // A saved view can span folders (and Gmail copies), so key by global message identity
            // to collapse duplicate copies against what is already shown (issue #220).
            var existingById = Messages
                .ToDictionary(MessageDeduplicator.CollapseKeyFor, StringComparer.Ordinal);

            foreach (var msg in newMessages.OrderByDescending(m => m.Date))
            {
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;
                var key = MessageDeduplicator.CollapseKeyFor(msg);
                if (existingById.TryGetValue(key, out var prior))
                {
                    ReconcileMessageState(prior, msg);
                    continue;
                }
                if (!MatchesFilter(msg) || !MatchesDayLimit(msg)) continue;
                InsertMessageSorted(msg);
                existingById[key] = msg;
            }

            RemoveVanishedMessages(newMessages);

            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            if (!OnlineMode && newMessages.Count > 0)
                _localStore.UpsertSummariesAsync(newMessages).LogFaults("local store: upsert summaries");

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
        // Theme + vision-assist first, after the modal Settings dialog has closed —
        // ThemeChanged handlers rebuild UI and must never run inside a nested
        // message loop (CLAUDE.md modal-dialog rules). ApplyAppearance coalesces
        // both mutations into one re-publish so a combined save (theme + a vision
        // setting) raises ThemeChanged once, not twice.
        _themeService?.ApplyAppearance(cfg);

        ShowMessageStatus = cfg.ShowMessageStatus;
        ReadAsPlainText   = cfg.ReadAsPlainText;
        _announceFlagStatus = cfg.AnnounceFlagStatus;
        OnPropertyChanged(nameof(AnnounceFlagStatus));

        // Push the calendar field-labels preference live (re-stamps the event list).
        if (CalendarVm != null)
            CalendarVm.ShowFieldLabels = cfg.CalendarListShowFieldLabels;
        RemindersEnabled = cfg.CalendarReminders;
        ReminderLeadMinutes = cfg.CalendarReminderMinutes;

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

        var prevMode    = MessageOpenMode;
        MessageOpenMode = cfg.Windowing.MessageOpenMode;
        OnPropertyChanged(nameof(IsMessageListAreaVisible));
        if (prevMode != MessageOpenMode && MessageOpenMode != MessageOpenMode.ReadingPane)
        {
            // Switched away from Reading Pane — hide the inline reading pane.
            IsMessageOpen = false;
            MessageDetail = null;
        }
        if (prevMode == MessageOpenMode.Tab && MessageOpenMode != MessageOpenMode.Tab)
        {
            // Clear all tabs — both message tabs and the sentinel — so the strip
            // is not visible in the new mode with blank, unrenderable tabs.
            OpenTabs.Clear();
            ActiveTab = null;
            OnPropertyChanged(nameof(ShowTabStrip));
        }
        else
            EnsureMessageListTab();

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

        // Archive (issue #318) — moves the selection to the account's Archive folder instead of
        // deleting. Default gesture Ctrl+Shift+M. (Not Alt+Delete: that collides with the common
        // screen-reader "announce cursor position" command.) Like mail.delete this base registration
        // is overridden in MainWindow with a focus-aware guard so the message list and group trees
        // can archive the whole selection/group via their PreviewKeyDown handlers.
        registry.Register(new CommandDefinition(
            id: "mail.archive", category: "Mail", title: "Archive",
            execute: () => ArchiveMessageCommand.Execute(null),
            defaultKey: Key.M, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => HasSelectedMessage));

        registry.Register(new CommandDefinition(
            id: "mail.refresh", category: "Mail", title: "Refresh",
            // RefreshAsync itself delegates to the calendar's refresh while it's the active
            // view, so this single command is correct from every entry point (menu, toolbar,
            // Command Palette, F5) — no isAvailable disambiguation needed here.
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
            execute: () =>
            {
                // Context-aware: in the calendar this routes to appointment search (the View
                // checks IsCalendarView); only mail search uses the mail search box state.
                if (!IsCalendarView) IsSearchActive = true;
                SearchRequested?.Invoke(this, EventArgs.Empty);
            },
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
            id: "view.filterToMe", category: "View", title: "Show Messages Addressed to Me",
            execute: () => SetFilterCommand.Execute("tome")));

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
            id: "view.sortFlaggedFirst", category: "View", title: "Sort: Flagged First",
            execute: () => SetSortCommand.Execute("flaggedFirst")));

        registry.Register(new CommandDefinition(
            id: "mail.rules", category: "Mail", title: "Manage Rules",
            execute: () => OpenRulesManagerCommand.Execute(null),
            defaultKey: Key.L, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        registry.Register(new CommandDefinition(
            id: "view.calendar", category: "View", title: "Calendar",
            execute: () => OpenCalendarCommand.Execute(null),
            defaultKey: Key.C, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: () => CalendarVm != null));

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

        registry.Register(new CommandDefinition(
            id: "help.reportBug", category: "Help", title: "Report a Bug",
            execute: () => ReportBugRequested?.Invoke(this, EventArgs.Empty)));
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows All Mail from the local store immediately (no network).
    /// Called first in OnLoaded so the UI is populated before any IMAP work begins.
    /// </summary>
    public async Task InitialLoadAsync()
    {
        SelectedFolder = AllMailFolder;
        LastSyncText = "Never synced";  // Ensure sync time is visible in status bar
        if (_flagService != null)
        {
            var defs = await _flagService.LoadFlagDefinitionsAsync();
            FlagDefinitions.Clear();
            foreach (var d in defs.OrderBy(d => d.SortOrder))
                FlagDefinitions.Add(d);
        }
        if (OnlineMode)
        {
            StatusText = "Online mode — connecting…";
            ConnectionStatusText = "Connecting…";
            return;
        }
        var cached = await _localStore.LoadAllSummariesAsync();
        await ResolveFlagNamesAsync(cached);
        SetMessages(cached);
        StatusText = cached.Count > 0
            ? $"{cached.Count} messages (cached — syncing…)"
            : "Connecting and syncing…";
        ConnectionStatusText = "Connecting…";
        StartPrefetchTopOfFolder();
        // Drop calendar events left behind by accounts that no longer exist — e.g. an account removed
        // and re-added during setup gets a new id, so its old events would otherwise linger and show
        // as duplicates (one per stale id). Local events (empty account id) are kept.
        await _localStore.PurgeCalendarEventsForUnknownAccountsAsync(Accounts.Select(a => a.Id).ToList());
        await ReloadCalendarSourcesAsync(); // populate before the tree is built so calendars show at startup
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

        // Start the change-notifier watchers (Graph delta poll / IMAP IDLE) and the reachability
        // handler for whatever connected, and refresh the status labels. Runs even when nothing
        // connected yet; it is also invoked from the manual-activation and runtime-add paths, so an
        // account that connects OUTSIDE this startup pipeline still gets polled for new mail.
        WireUpWatchers();

        // Signal that the startup connect finished so a notification click that cold-started the app
        // (its account wasn't connected yet when the toast was activated) can now open its message.
        StartupConnectCompleted?.Invoke();

        // Contact sync (issue #256): best-effort, fire-and-forget, after mail accounts have connected
        // so their OAuth tokens are warm — contact-scope acquisition is then silent (no sign-in popup).
        // Runs before the early-return paths below so it happens in every mode. Throttled (12h): this
        // method also runs on manual account activation and runtime add, so an unthrottled call would
        // re-fetch every account's full contact list each time. Silent by design; the manual "Sync
        // Contacts Now" command is the one that announces and bypasses the throttle. A failure here is
        // logged and never affects mail sync.
        _contactSync?.SyncAllDueAsync(TimeSpan.FromHours(12), ct).LogFaults("startup contact sync");

        // Nothing connected — skip the heavy full sync. Watchers/labels are already handled above, and
        // WireUpWatchers will start the watcher once an account connects later.
        if (_cachedFolders.Count == 0) return;

        var accountList = Accounts.ToList();

        // Subscribe to sync progress updates.
        // Announce every 10 folders to avoid excessive screen reader chatter.
        int lastAnnouncedAt = 0;
        _syncService.SyncProgressChanged += (done, total) =>
        {
            if (total > 0)
            {
                // Announce progress every 10 folders or at the end.
                // Do not update StatusText here — it would trigger automatic screen reader
                // announcements in addition to the explicit Announce() calls, creating duplicates.
                if (done % 10 == 0 && done > lastAnnouncedAt)
                {
                    Announce($"Synced {done} of {total} folders.", AnnouncementCategory.Status);
                    lastAnnouncedAt = done;
                }
                else if (done == total && done > lastAnnouncedAt)
                {
                    Announce($"Sync complete.", AnnouncementCategory.Status);
                    lastAnnouncedAt = done;
                }
            }
        };

        if (SelectedFolder?.FullName == AllMailFolder.FullName && ViewMode == ViewMode.To)
        {
            var missingRecipients = Messages.Any(m => string.IsNullOrWhiteSpace(m.To))
                || await _localStore.HasSummariesMissingRecipientsAsync();
            if (missingRecipients)
            {
                await FetchAllMailAsync();
                StartGraphCalendarSyncTimer(); // this path skips the full sync below but still counts as "startup done"
                return;
            }
        }

        if (OnlineMode)
        {
            // In online mode there is no background sync — just load the current folder live.
            await FetchVirtualAsync(AllMailFolder);
            return;
        }

        // Apply default view now that connections are ready, so the user sees the right
        // view during sync rather than waiting ~40s for sync to complete (issue #57).
        var defaultView = SavedViews.FirstOrDefault(v => v.IsDefault);
        if (defaultView != null)
            await ApplyViewAsync(defaultView);

        StatusText = "Syncing mail…";
        ConnectionStatusText = "Syncing…";
        // If we've never synced before, show "In progress" instead of "Never synced"
        // to avoid the confusing impression that syncing will never happen
        if (LastSyncText == "Never synced")
            LastSyncText = "In progress";
        _suppressFolderSyncUpdates = true;

        // Start progress announcements for long syncs (10-second interval).
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = AnnounceLoadingProgressAsync(progressCts.Token);

        try
        {
            await _syncService.SyncAllAccountsAsync(Accounts, _cachedFolders, ct);

            // Sync done — refresh the current view/folder once so the UI reflects every
            // folder that was synced without N intermediate screen-reader announcements.
            // RefreshAsync handles all virtual-folder and saved-view types correctly.
            await RefreshAsync();

            // Refresh every account's folder unread counts to reflect reads/arrivals picked up
            // during the sync (issue #227). Debounced, so this coalesces with any per-event refreshes.
            foreach (var acct in accountList)
                ScheduleFolderCountRefresh(acct.Id);

            var count = Messages.Count;
            StatusText = $"{count} messages.";
            LastSyncText = $"Synced {DateTime.Now:t}";
            ConnectionStatusText = $"{Accounts.Count} account{(Accounts.Count == 1 ? "" : "s")} connected";
            Announce($"{count} {(count == 1 ? "message" : "messages")} loaded.", AnnouncementCategory.Status);

            // Start periodic NOOP heartbeat (10-minute interval) to keep connections alive
            // and detect mid-session drops on non-INBOX folders.
            _ = StartPeriodicNoOpAsync(accountList, ct);

            // Start the fallback mail-sync loop (issue #267) — a safety net behind IMAP IDLE that
            // periodically re-syncs inboxes on a user-configurable interval.
            _ = StartFallbackSyncAsync(ct);
        }
        catch (OperationCanceledException) { /* sync cancelled — normal */ }
        catch (Exception ex)
        {
            LogService.Log("BackgroundSync", ex);
            StatusText = $"Sync error: {ex.Message}";
            Announce($"Sync error: {ex.Message}", AnnouncementCategory.Status);
            // Only set "Connection error" if no accounts connected at all.
            if (_cachedFolders.Count == 0)
                ConnectionStatusText = "Connection error";
        }
        finally
        {
            _suppressFolderSyncUpdates = false;
            progressCts.Cancel();
            try { await progressTask.ConfigureAwait(false); } catch { }

            // Graph calendar sync: first pass now that the initial mail sync has finished (tokens
            // are warm, so acquisition is silent), then every 15 minutes. In the finally so a sync
            // error or cancellation doesn't leave the calendar permanently unsynced.
            StartGraphCalendarSyncTimer();
        }
    }

    // Tracks the connected-account set the watchers were last started for, so WireUpWatchers only
    // restarts them when the set actually changes (StartWatchers is a full stop-and-restart). Extracted
    // into a small gate so the anti-thrash contract is unit-testable (WatcherStartGateTests).
    private readonly WatcherStartGate _watcherGate = new();

    /// <summary>
    /// Ensures the change-notifier watchers (Graph delta poll / IMAP IDLE) are running for every
    /// currently-connected account, the reachability handler is subscribed against the live account
    /// list, and the connection/last-sync labels reflect reality. Idempotent and cheap — safe to call
    /// after the startup connect, a manual sign-in/activation (<see cref="SelectAccountAsync"/>), or a
    /// runtime account add (<see cref="RefreshAccountList"/>). Without this, an account that connects
    /// outside the startup pipeline is never polled for new mail and the status bar stays stuck at
    /// "Offline / Never synced".
    /// </summary>
    private void WireUpWatchers()
    {
        var connected    = Accounts.Where(a => _cachedFolders.ContainsKey(a.Id)).ToList();
        var connectedIds = connected.Select(a => a.Id).ToHashSet();

        // Only (re)start watchers when the connected set changed — StartWatchers stops and restarts
        // every watcher, so calling it on each activation would thrash the poll loops for no reason.
        if (_changeNotifier != null && _watcherGate.HasChanged(connectedIds))
        {
            // Watchers run under the background-sync lifetime. In the normal launch order
            // StartBackgroundSyncAsync runs first and creates _bgSyncCts; guard against a null/cancelled
            // token so we never start watchers against a dead one. Log rather than skip silently — a
            // silent skip would leave a connected account unpolled with no trace (#215 review). This is
            // reachable only if a connect path (SelectAccountAsync / RefreshAccountList) somehow runs
            // before the first StartBackgroundSyncAsync, which the normal startup sequence prevents.
            if (_bgSyncCts is not { IsCancellationRequested: false })
            {
                LogService.Log("WireUpWatchers: connected set changed but the background-sync token is not " +
                               "active; watchers not started (only expected if a connect path runs before " +
                               "StartBackgroundSyncAsync).");
            }
            else
            {
                _changeNotifier.StartWatchers(connected, _bgSyncCts.Token);
                _watcherGate.MarkStarted(connectedIds); // advance state only when watchers actually start

                // (Re)subscribe the reachability handler. It resolves from the LIVE Accounts collection
                // (not a snapshot), so it never goes stale (issue #126). Unsubscribe first so repeated
                // calls don't stack handlers; it fires on the ThreadPool, so marshal UI work onto the UI thread.
                if (_onReachabilityChanged != null)
                    _changeNotifier.AccountReachabilityChanged -= _onReachabilityChanged;
                _onReachabilityChanged = (accountId, isReachable) => _ui.Post(() =>
                {
                    var account = Accounts.FirstOrDefault(a => a.Id == accountId);
                    if (account != null)
                    {
                        var folders = isReachable && _cachedFolders.TryGetValue(accountId, out var f) ? f : null;
                        ApplyAccountStatus(account, folders);
                    }
                });
                _changeNotifier.AccountReachabilityChanged += _onReachabilityChanged;
            }
        }

        ConnectionStatusText = _cachedFolders.Count > 0
            ? $"{_cachedFolders.Count} account{(_cachedFolders.Count == 1 ? "" : "s")} connected"
            : "Offline";

        // Don't leave the label stuck at its pre-sync defaults once an account is actually connected;
        // the sync/poll paths (OnFolderSynced, StartBackgroundSyncAsync) keep it current from here.
        if (_cachedFolders.Count > 0 && LastSyncText is "Never synced" or "In progress")
            LastSyncText = $"Synced {DateTime.Now:t}";
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

    private async Task StartPeriodicNoOpAsync(IReadOnlyList<AccountModel> accounts, CancellationToken ct)
    {
        // 10-minute heartbeat: NOOPs one pooled connection per account to detect mid-session drops
        // and keep at least one connection warm. Other idle pooled clients may still go stale and are
        // lazily discarded on the next rent (IsClientUsable). Runs fire-and-forget; cancelled via the app ct.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                foreach (var account in accounts)
                {
                    if (ct.IsCancellationRequested) break;
                    try { await _imap.NoOpAsync(account.Id, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { LogService.Log($"NOOP for {account.AccountLabel}", ex); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Fallback mail sync behind IMAP IDLE (issue #267). IDLE is the primary new-mail signal, but if a
    // server never pushes, the held IDLE connection dies quietly, or a message's read/flag state
    // changes in another client (which IDLE never reports), nothing updates until the user acts. This
    // loop periodically re-syncs each IMAP account's inbox as the safety net. The interval is
    // user-configurable (config.MailSyncPollMinutes); 0 disables it. Re-reads the setting at the top of
    // each cycle so a Settings change applies without a restart — note the change takes effect after the
    // current wait elapses (up to one interval of lag; Off→enabled is bounded at ≤5 min). Non-online
    // only — online mode has no local store to sync into and StartBackgroundSyncAsync returns first.
    //
    // Threading: the loop body runs on a threadpool thread (ConfigureAwait(false) on the delay), so the
    // fetch + SQLite upsert never touch the UI thread. Accounts/_cachedFolders are UI-thread-owned, so
    // the work list is snapshotted via _ui.Invoke, and the UI-owned follow-ups (notify, count refresh)
    // are marshalled back via _ui.Post. This mirrors OnInboxNewMailDetected but makes the thread
    // ownership explicit rather than relying on an ambient sync context.
    private async Task StartFallbackSyncAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var minutes = _configService.Load().MailSyncPollMinutes;

                // Disabled: re-check the setting every 5 minutes so re-enabling it doesn't need a restart.
                var delay = minutes <= 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(minutes);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                if (_configService.Load().MailSyncPollMinutes <= 0) continue; // still disabled after the wait

                // Snapshot the IMAP inboxes to sync on the UI thread (Accounts/_cachedFolders are
                // UI-thread-owned). Graph accounts are skipped — they have their own delta poll.
                var jobs = new List<(AccountModel Account, MailFolderModel Inbox)>();
                _ui.Invoke(() =>
                {
                    foreach (var account in Accounts)
                    {
                        if (account.BackendKind != BackendKind.ImapSmtp) continue;
                        if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;

                        var inbox = folders.FirstOrDefault(f =>
                            f.Kind == Models.SpecialFolderKind.Inbox ||
                            string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase));
                        if (inbox != null) jobs.Add((account, inbox));
                    }
                });

                foreach (var (account, inbox) in jobs)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        // Fetch + SQLite upsert run off the UI thread; SyncOneFolderAsync marshals its
                        // FolderSynced event to the UI thread internally.
                        var incoming = await _syncService.SyncOneFolderAsync(account, inbox, ct)
                            .ConfigureAwait(false);
                        LogService.Debug($"Fallback sync [{account.AccountLabel}] inbox: {incoming.Count} fetched.");

                        // Marshal the UI-owned follow-ups back to the UI thread: notify for genuinely-new
                        // arrivals (the single-thread-owned de-dupe set prevents re-notifying mail IDLE
                        // already flagged) and refresh unread counts (debounced, STATUS-authoritative).
                        _ui.Post(() =>
                        {
                            if (incoming.Count > 0)
                                MaybeNotifyNewMail(account, incoming);
                            ScheduleFolderCountRefresh(account.Id);
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { LogService.Log($"Fallback sync {account.AccountLabel}", ex); }
                }
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

        // Update sync time whenever any folder syncs (targeted IDLE syncs, manual refreshes, etc.)
        LastSyncText = $"Synced {DateTime.Now:t}";

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
        else if (selected.FullName == AllFlaggedFolder.FullName)
        {
            // All Flagged Mail — only accept flagged incoming messages.
            relevant = incoming.Where(m => m.IsFlagged);
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

        // Build a lookup map once so dedupe and flag-reconciliation are both O(1) per incoming
        // item instead of O(n) scans — critical in All Mail views with thousands of messages.
        // In aggregate/virtual views, key by the global message identity so an incoming copy from a
        // different folder (e.g. the Gmail All Mail copy of an already-shown INBOX message) is
        // recognized as a duplicate and skipped (issue #220). Real-folder views key by per-folder UID.
        Func<MailMessageSummary, string> keyOf = IsVirtualFolder(selected)
            ? MessageDeduplicator.CollapseKeyFor
            : MessageDeduplicator.PerFolderKeyFor;
        var rawByKey = new Dictionary<string, MailMessageSummary>(_rawMessages.Count);
        foreach (var e in _rawMessages)
            rawByKey.TryAdd(keyOf(e), e);
        var seen = new HashSet<string>(rawByKey.Keys);

        // Collect truly new messages; add them to _rawMessages immediately so the
        // search pool stays in sync with what the list will eventually show.
        var toInsert = new List<MailMessageSummary>();
        // Existing messages whose read-state was reconciled from the server (#269). Their filter
        // membership may have changed (e.g. now-read messages must leave the Unread view), so their
        // presence in the visible list is reconciled after the loop.
        var readReconciled = new List<MailMessageSummary>();
        foreach (var msg in relevant.OrderByDescending(m => m.Date))
        {
            // Reconcile server-flagged state for new incoming messages: a message with
            // \Flagged set on the server but no local flag assignment gets the built-in flag
            // id so it displays correctly.  FlagName/FlagColorHex stay null until the next
            // ResolveFlagNamesAsync call, but FlagId ensures IsFlagged and MatchesFilter work.
            if (msg.IsServerFlagged && msg.FlagId == null)
                msg.FlagId = Models.FlagDefinition.BuiltInFlagId.ToString();

            var key = keyOf(msg);
            if (!seen.Add(key))
            {
                // Existing message: reconcile external state changes made by another client.
                if (rawByKey.TryGetValue(key, out var existing))
                {
                    // Read/unread (#269): a message read (or marked unread) elsewhere — e.g. Gmail
                    // web — must not keep showing the stale state here. IsRead is observable, so this
                    // refreshes the row; folder unread counts reconcile via the debounced,
                    // STATUS-authoritative refresh already scheduled on the sync path.
                    if (existing.IsRead != msg.IsRead)
                    {
                        existing.IsRead = msg.IsRead;
                        readReconciled.Add(existing); // its filter membership may have changed
                    }

                    // Flag clear (§9.3): server now reports not-flagged but we still show a flag —
                    // another client cleared it, so clear our local flag to match.
                    if (!msg.IsServerFlagged && existing.FlagId != null)
                        existing.FlagId = null;
                }
                continue;
            }
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

            // #269: reconcile the visible list for messages whose read-state changed externally. A
            // now-read message must leave the Unread view; a now-unread one must (re)appear if it
            // matches. Done inside the batch so it costs one Reset, not one event per change.
            foreach (var m in readReconciled)
            {
                var shouldShow = MatchesFilter(m) && MatchesDayLimit(m)
                    && (string.IsNullOrWhiteSpace(SearchText) || MatchesSearch(m));
                var isShown = Messages.Contains(m);
                if (shouldShow && !isShown)
                    InsertMessageSorted(m);
                else if (!shouldShow && isShown)
                    Messages.Remove(m);
            }
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

        // Debounced calendar harvest: re-harvest events 2s after the last sync event
        // so we don't harvest on every folder during a multi-folder sync.
        ScheduleCalendarHarvest();

        RebuildActiveGroupView();
    }
    // Called on a ThreadPool thread by the change notifier when new mail lands in an inbox.
    // Runs a targeted sync for that account's INBOX so the message appears in the list. Accounts and
    // _cachedFolders are UI-thread-owned, so resolve them on the UI thread before the background sync.
    private void OnInboxNewMailDetected(Guid accountId) => _ui.Post(() =>
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        if (!_cachedFolders.TryGetValue(accountId, out var folders)) return;
        var inbox = folders.FirstOrDefault(f =>
            f.Kind == Models.SpecialFolderKind.Inbox ||
            string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase));
        if (inbox is null) return;

        LogService.Log($"IDLE: new mail detected for {account.AccountLabel} INBOX — syncing.");

        // New mail changes server unread counts; refresh them (debounced, STATUS-authoritative).
        ScheduleFolderCountRefresh(accountId);

        _ = Task.Run(async () =>
        {
            try
            {
                var incoming = OnlineMode
                    ? await _syncService.SyncOneFolderOnlineAsync(account, inbox, CancellationToken.None)
                    : await _syncService.SyncOneFolderAsync(account, inbox, CancellationToken.None);

                // Notify on the UI thread so the de-dupe set stays single-thread-owned.
                if (incoming.Count > 0)
                    _ui.Post(() => MaybeNotifyNewMail(account, incoming));
            }
            catch (Exception ex)
            {
                LogService.Log("IDLE targeted sync", ex);
            }
        });
    });

    // Shows a Windows toast for genuinely-new inbox mail. Runs on the UI thread (the caller posts
    // it there) so _notifiedMessageKeys is single-thread-owned. Setting is re-read live so a
    // Settings change takes effect without a restart.
    private void MaybeNotifyNewMail(AccountModel account, IReadOnlyList<MailMessageSummary> incoming)
    {
        if (_notifications is not { IsSupported: true }) return;
        if (!_configService.Load().NotifyOnNewMail) return;

        // Bound session memory: the set only holds keys of messages we've already notified for, but
        // an always-on session could grow it without limit. Clearing risks at most a re-notify for a
        // message still inside the last-50 IDLE fetch window whose Date is after launch — negligible.
        if (_notifiedMessageKeys.Count > 10_000) _notifiedMessageKeys.Clear();

        var fresh = Helpers.NewMailFilter.SelectNew(incoming, _notifyThresholdUtc, _notifiedMessageKeys);

        // #270 diagnostics: the user reports an inflated count that re-notifies every ~30 min. This
        // line tells whether the SAME message re-notifies each cycle (dedup key not matching / session
        // reset) or a genuinely new one arrives — freshUids are the dedup key input, so they pin which
        // mechanism is at play. When a notification actually fires (fresh > 0) it is logged at Log
        // level so it is captured by the Settings → Advanced logging toggle (users don't launch with
        // /debug); the frequent no-op evaluations (fresh == 0, one per IDLE fire and poll cycle) stay
        // Debug-only so a normal log isn't flooded.
        var diag =
            $"NewMail notify [{account.AccountLabel}]: incoming={incoming.Count} " +
            $"unread={incoming.Count(m => !m.IsRead)} fresh={fresh.Count} " +
            $"notifiedSetSize={_notifiedMessageKeys.Count} threshold={_notifyThresholdUtc:o} " +
            $"freshUids=[{string.Join(",", fresh.Select(m => m.MessageId))}]";

        if (fresh.Count == 0)
        {
            LogService.Debug(diag);
            return;
        }

        // Suppress a big catch-up batch: after the machine wakes from sleep or a dropped connection
        // reconnects, all the mail that arrived during the gap is fetched at once and shows up as many
        // "fresh" messages in one evaluation. The startup backlog is already excluded by
        // _notifyThresholdUtc; this is its mid-session equivalent. SelectNew has already marked these
        // as notified (so they won't re-fire), and the count is logged — we just skip the toast so a
        // wake doesn't fire a "9 new messages" burst. Real-time arrivals (a handful at a time) notify
        // normally.
        if (fresh.Count > MaxNotifyBatchSize)
        {
            LogService.Log($"{diag}  — toast suppressed (batch > {MaxNotifyBatchSize}, likely wake/reconnect backlog)");
            return;
        }

        LogService.Log(diag);
        _notifications.ShowNewMail(account.AccountLabel, account.Id, fresh);
    }

    private void OnMessagesRemoved(IReadOnlyList<MailMessageSummary> removed)
    {
        // Build a key→item map once so each removed key is an O(1) lookup
        // instead of a Messages.FirstOrDefault scan per item.
        var byKey = new Dictionary<(string, Guid, string), MailMessageSummary>(Messages.Count);
        foreach (var e in Messages)
            byKey[(e.MessageId, e.AccountId, e.FolderName)] = e;

        // Build a key set for fast _rawMessages removal.
        var removedKeys = new HashSet<(string, Guid, string)>(removed.Count);
        foreach (var msg in removed)
            removedKeys.Add((msg.MessageId, msg.AccountId, msg.FolderName));

        // Capture which messages are still in _rawMessages before removal so we only
        // decrement inbox counts for messages we're actually removing here.  Messages
        // removed by DeleteMessagesAsync have already been cleaned from _rawMessages
        // (and their counts decremented), so they won't appear in this set.
        var rawKeys = new HashSet<(string, Guid, string)>(
            _rawMessages.Select(m => (m.MessageId, m.AccountId, m.FolderName)));
        var actuallyRemovedFromRaw = removed
            .Where(m => rawKeys.Contains((m.MessageId, m.AccountId, m.FolderName)))
            .ToList();

        _rawMessages.RemoveAll(m => removedKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));

        // Update account inbox counts for the messages we actually removed from _rawMessages.
        UpdateAccountCountsAfterRemoval(actuallyRemovedFromRaw);

        bool removedOpen = false;
        foreach (var msg in removed)
        {
            var key = (msg.MessageId, msg.AccountId, msg.FolderName);
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
        var list = messages as List<MailMessageSummary> ?? messages.ToList();
        // Aggregate/virtual views union multiple folders, so one physical message can arrive as
        // several per-folder copies (notably Gmail: INBOX + All Mail + labels). Collapse them to one
        // representative here (issue #220). Single real-folder views show their own contents as-is.
        // Note: on the very first cached load (InitialLoadAsync) _cachedFolders is not yet populated,
        // so ResolveFolderKind returns None and representative *ranking* is neutral (date/name tie-
        // break) — collapse is still correct; the preferred Inbox representative settles on first fetch.
        if (IsVirtualFolder(SelectedFolder))
            list = MessageDeduplicator.CollapseForAggregate(list, ResolveFolderKind);
        _rawMessages = list;
        if (!_showPreview)
            foreach (var m in _rawMessages) m.Preview = string.Empty;
        else
            foreach (var m in _rawMessages) m.Preview = TruncatePreview(m.Preview, _previewLines);
        ApplyFiltersAndSearch();
    }

    /// <summary>
    /// Resolves a message's source folder to its <see cref="SpecialFolderKind"/> from the cached
    /// folder list, so the deduplicator can rank representative copies (e.g. prefer the Inbox copy
    /// over a Gmail All Mail copy). Returns <see cref="SpecialFolderKind.None"/> for ordinary
    /// folders/labels or when the folder is not in the cache.
    /// </summary>
    private SpecialFolderKind ResolveFolderKind(MailMessageSummary msg)
    {
        if (_cachedFolders.TryGetValue(msg.AccountId, out var folders))
            foreach (var f in folders)
                if (string.Equals(f.FullName, msg.FolderName, StringComparison.OrdinalIgnoreCase))
                    return f.Kind;
        return SpecialFolderKind.None;
    }

    // Re-applies the status filter and search text to _rawMessages.
    // Called by SetMessages() and OnSearchTextChanged(); OnMessagesChanged()
    // automatically triggers group rebuilds when Messages is replaced.
    private void ApplyFiltersAndSearch()
    {
        IEnumerable<MailMessageSummary> result = _rawMessages;
        if (ActiveFilter != MessageFilter.All)
            result = result.Where(MatchesFilter);
        if (ActiveFilter == MessageFilter.Flagged && _activeFlagFilterId != null)
            result = result.Where(m => m.FlagId == _activeFlagFilterId);
        if (ActiveDayLimit.HasValue)
            result = result.Where(MatchesDayLimit);
        if (!string.IsNullOrWhiteSpace(SearchText))
            result = result.Where(MatchesSearch);
        result = ActiveSort switch
        {
            MessageSort.DateAscending   => result.OrderBy(m => m.Date),
            MessageSort.AlphaAscending  => result.OrderBy(m => m.Subject, StringComparer.OrdinalIgnoreCase),
            MessageSort.AlphaDescending => result.OrderByDescending(m => m.Subject, StringComparer.OrdinalIgnoreCase),
            MessageSort.FlaggedFirst    => result.OrderBy(m => m.IsFlagged ? 0 : 1).ThenByDescending(m => m.Date),
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
        MessageFilter.ToMe            => !msg.IsMailingList && Accounts.Any(a => msg.To.Contains(a.Username, StringComparison.OrdinalIgnoreCase)),
        MessageFilter.Flagged         => msg.IsFlagged,
        _                             => true,
    };

    // Returns true when no day limit is active, so callers can chain this with
    // MatchesFilter without an explicit ActiveDayLimit.HasValue guard at every site.
    private bool MatchesDayLimit(MailMessageSummary msg)
        => !ActiveDayLimit.HasValue || msg.Date >= DateTimeOffset.Now.AddDays(-ActiveDayLimit.Value);

    // Populates FlagName and FlagColorHex on messages that have a FlagId set but no
    // display name — which is the case for every cache load, since ReadSummariesAsync
    // only reads flag_id. Skips gracefully when _flagService is not wired up.
    private async Task ResolveFlagNamesAsync(IList<MailMessageSummary> messages)
    {
        if (_flagService == null) return;
        var flagged = messages.Where(m => m.FlagId != null).ToList();
        if (flagged.Count == 0) return;
        var defs = await _flagService.LoadFlagDefinitionsAsync();
        var lookup = new Dictionary<Guid, FlagDefinition>(defs.Count);
        foreach (var d in defs) lookup[d.Id] = d;
        foreach (var m in flagged)
        {
            if (m.FlagId != null && Guid.TryParse(m.FlagId, out var fid) && lookup.TryGetValue(fid, out var def))
            {
                m.FlagName     = def.Name;
                m.FlagColorHex = def.ColorHex;
            }
        }
    }

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

    // Copies server-fresh mutable state onto an existing message already in the list.
    // Observable properties fire PropertyChanged automatically so the UI updates in place
    // without removing and re-inserting the row (which would lose selection/focus).
    private static void ReconcileMessageState(MailMessageSummary existing, MailMessageSummary fresh)
    {
        existing.IsRead         = fresh.IsRead;
        existing.IsReplied      = fresh.IsReplied;
        existing.IsForwarded    = fresh.IsForwarded;
        existing.HasAttachments = fresh.HasAttachments;
        existing.FlagId         = fresh.FlagId;
        existing.FlagName       = fresh.FlagName;
        existing.FlagColorHex   = fresh.FlagColorHex;
        existing.IsServerFlagged = fresh.IsServerFlagged;
    }

    /// <summary>
    /// Removes messages that vanished from the server within the fetched range (deleted in another
    /// client). Scoped per folder to the returned date range so messages older than the fetch window
    /// — simply not returned — are not wrongly dropped. Call after an aggregate/virtual merge.
    /// </summary>
    private void RemoveVanishedMessages(IReadOnlyList<MailMessageSummary> newMessages)
    {
        // Key by global message identity, not per-folder UID: in a deduped aggregate view the shown
        // row is one representative copy, but a sibling copy in another folder keeps the message
        // present. Removing on the per-folder key alone would drop the representative when only its
        // home-folder copy vanished (e.g. a Gmail message archived out of INBOX while its All Mail
        // copy remains) — a message that merely moved, not deleted (issue #220). CollapseKeyFor
        // falls back to the per-folder key for messages with no Message-ID, preserving old behavior.
        var fetchedKeys = new HashSet<string>(StringComparer.Ordinal);
        var minDateByFolder = new Dictionary<(Guid, string), DateTimeOffset>();
        foreach (var m in newMessages)
        {
            fetchedKeys.Add(MessageDeduplicator.CollapseKeyFor(m));
            var fk = (m.AccountId, m.FolderName);
            if (!minDateByFolder.TryGetValue(fk, out var min) || m.Date < min)
                minDateByFolder[fk] = m.Date;
        }

        var vanished = Messages.Where(m =>
            minDateByFolder.TryGetValue((m.AccountId, m.FolderName), out var min) &&
            m.Date >= min &&
            !fetchedKeys.Contains(MessageDeduplicator.CollapseKeyFor(m))).ToList();
        if (vanished.Count == 0) return;

        // Mirror RemoveFromActiveViewAsync: remove from the backing _rawMessages too (else they
        // reappear when ApplyFiltersAndSearch rebuilds Messages), decrement account unread counts,
        // and clear the reading pane if the open message was one of the removed ones. Messages in the
        // visible list are always present in _rawMessages, so they are safe to count for removal.
        var vanishedKeys = new HashSet<(string, Guid, string)>(
            vanished.Select(m => (m.MessageId, m.AccountId, m.FolderName)));
        _rawMessages.RemoveAll(m => vanishedKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));

        bool removedOpen = vanished.Any(m => m == SelectedMessage);
        foreach (var m in vanished)
            Messages.Remove(m);

        UpdateAccountCountsAfterRemoval(vanished);

        if (removedOpen)
        {
            SelectedMessage = Messages.Count > 0 ? Messages[0] : null;
            MessageDetail   = null;
            IsMessageOpen   = false;
        }
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

        // Group by IMAP host: accounts sharing a server connect sequentially to stay under the
        // per-IP connection limit shared hosting enforces (same rationale as SyncService); accounts
        // on different hosts still connect in parallel.
        var resultsByHost = await Task.WhenAll(
            Accounts.GroupBy(a => a.ImapHost, StringComparer.OrdinalIgnoreCase)
                    .Select(async hostGroup =>
                    {
                        var groupResults = new List<(Guid Id, List<MailFolderModel>? Folders)>();
                        foreach (var account in hostGroup)
                            groupResults.Add(await ConnectOneAccountAsync(account));
                        return groupResults;
                    }));
        var results = resultsByHost.SelectMany(r => r).ToList();

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
        // Exclude Gmail's virtual folders (All Mail / Important / Starred): their counts overlap the
        // Inbox and labels, so summing them double-counts and inflates the account total (#227).
        account.TotalUnread = folders.Where(f => !f.SuppressUnreadCount).Sum(f => f.UnreadCount);
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
        var isOAuth = account.AuthType is Models.AuthType.OAuth2Microsoft or Models.AuthType.OAuth2Google;

        // Startup retry: up to 3 attempts with backoff (30s, 45s, 60s timeouts).
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // Timeouts increase per attempt: 30s, 45s, 60s
                int connectTimeout = attempt switch { 1 => 30, 2 => 45, _ => 60 };
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(connectTimeout));

                // Background connect must never open an interactive sign-in window: under this short
                // per-attempt timeout it would be torn down while the user is mid-sign-in (#206). For
                // OAuth accounts, obtain a token SILENTLY *first* — only reach ConnectAsync (whose own
                // GetAccessTokenAsync would otherwise fall back to interactive) once a silent token is
                // in hand, so the connect can never need a prompt. Doing this inside the loop means a
                // transient silent-check failure retries with backoff like any other, while a genuine
                // "interactive required" short-circuits immediately (caught below). If no silent token
                // is available the account is left disconnected; the user starts an (unbounded)
                // sign-in explicitly by activating it.
                if (isOAuth)
                    await _oauthService.EnsureSilentTokenAsync(account, connectCts.Token);

                await _imap.ConnectAsync(account, password, connectCts.Token);
                using var folderCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var folderList = await _imap.GetFoldersAsync(account.Id, folderCts.Token);
                return (account.Id, folderList);
            }
            catch (InteractiveSignInRequiredException)
            {
                // No usable cached token: the account needs an interactive sign-in, which must not run
                // here. Leave it disconnected (not retried — the state won't change on its own); the
                // user starts sign-in by activating the account.
                LogService.Log($"ConnectAll/{account.AccountLabel}: interactive sign-in required — leaving disconnected until the user signs in.");
                return (account.Id, null);
            }
            catch (OperationCanceledException) when (attempt < 3)
            {
                // Per-attempt timeout — retry with jittered backoff
                var delaySeconds = JitteredBackoffSeconds(attempt == 1 ? 15 : 30);
                LogService.Log($"ConnectAll/{account.AccountLabel}: attempt {attempt} timed out — retrying in {delaySeconds:F0}s");
                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None); }
                catch { /* best effort */ }
                continue;
            }
            catch (Exception ex) when (attempt < 3)
            {
                // Transient error — retry with jittered backoff
                var delaySeconds = JitteredBackoffSeconds(attempt == 1 ? 15 : 30);
                LogService.Log($"ConnectAll/{account.AccountLabel}: attempt {attempt} failed ({ex.Message}) — retrying in {delaySeconds:F0}s");
                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None); }
                catch { /* best effort */ }
                continue;
            }
            catch (OperationCanceledException)
            {
                // Outer CTS cancelled — exit immediately
                LogService.Log($"ConnectAll/{account.AccountLabel}: cancelled by user");
                return (account.Id, null);
            }
            catch (Exception ex)
            {
                // Final attempt failed
                LogService.Log($"ConnectAll/{account.AccountLabel}: final attempt failed", ex);
                return (account.Id, null);
            }
        }

        return (account.Id, null);
    }

    // ±30% jitter so multiple accounts retrying after a shared outage (e.g. a server that
    // dropped every connection at once) don't reconnect in lockstep and re-trip the limit.
    private static double JitteredBackoffSeconds(int baseSeconds) =>
        baseSeconds * (0.7 + Random.Shared.NextDouble() * 0.6);

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
        // Capture which folders the user has expanded so the rebuild (which creates fresh, collapsed
        // node objects) doesn't collapse the whole tree on a refresh.
        var expandedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (FolderTree != null)
            foreach (var n in FlattenAllNodes(FolderTree))
                if (n.IsExpanded) expandedKeys.Add(NodeKey(n));

        var roots = new List<FolderTreeNode>();

        // "Calendar" — top-level virtual folder that opens the event list.
        // Shown only when a calendar service is wired (skipped in tests / online-only builds).
        if (CalendarVm != null)
        {
            var calNode = new FolderTreeNode
            {
                Folder = CalendarFolder,
                Label  = CalendarFolder.DisplayName,
            };
            calNode.Children.Add(new FolderTreeNode
            {
                Folder = new MailFolderModel { FullName = CalendarSourcePrefix + "all", DisplayName = "All Calendars" },
                Label  = "All Calendars",
            });
            calNode.Children.Add(new FolderTreeNode
            {
                Folder = new MailFolderModel { FullName = CalendarSourcePrefix + "local", DisplayName = "Local Calendar" },
                Label  = "Local Calendar",
            });
            // Only accounts the user opted into calendar sync for (#282) get a source node.
            foreach (var acct in Accounts.Where(a => a.SyncCalendar))
            {
                var acctNode = new FolderTreeNode
                {
                    Folder = new MailFolderModel
                    {
                        FullName    = CalendarSourcePrefix + acct.Id.ToString("D"),
                        DisplayName = acct.AccountLabel,
                    },
                    Label = acct.AccountLabel,
                };

                // A grandchild per discovered calendar so the user can view Home vs. Work vs. Family.
                // With 0 or 1 calendars the account node alone suffices (no redundant single child).
                var acctCalendars = _calendarSources.Where(s => s.AccountId == acct.Id).ToList();
                if (acctCalendars.Count > 1)
                    foreach (var (_, calId, calName) in acctCalendars)
                        acctNode.Children.Add(new FolderTreeNode
                        {
                            Folder = new MailFolderModel
                            {
                                FullName    = CalendarSourcePrefix + acct.Id.ToString("D") + "|" + Uri.EscapeDataString(calId),
                                DisplayName = calName,
                            },
                            Label = calName,
                        });

                calNode.Children.Add(acctNode);
            }
            roots.Add(calNode);
        }

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
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllFlaggedFolder, Label = AllFlaggedFolder.DisplayName });
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

        // Restore expansion captured above (additive: header groups keep their built-in expanded
        // default; previously-expanded folders are re-expanded).
        foreach (var n in FlattenAllNodes(roots))
            if (expandedKeys.Contains(NodeKey(n)))
                n.IsExpanded = true;

        FolderTree = new ObservableCollection<FolderTreeNode>(roots);
    }

    private static string NodeKey(FolderTreeNode n) =>
        n.Folder != null ? $"F:{n.Folder.AccountId}:{n.Folder.FullName}" : $"H:{n.Label}";

    private static IEnumerable<FolderTreeNode> FlattenAllNodes(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in FlattenAllNodes(n.Children))
                yield return c;
        }
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
        OnPropertyChanged(nameof(IsFilterToMe));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(IsFilterFlagged));
        OnPropertyChanged(nameof(IsFilterAllFlagged));
        OnPropertyChanged(nameof(FilterLabel));
        OnPropertyChanged(nameof(WindowTitle));

        if (_suppressFilterRebuild) return;

        // Always rebuild Messages from _rawMessages; OnMessagesChanged then triggers
        // RebuildActiveGroupView automatically for Conversations/From/To view modes.
        ApplyFiltersAndSearch();
    }

    partial void OnActiveSortChanged(MessageSort value)
    {
        OnPropertyChanged(nameof(IsSortDateDesc));
        OnPropertyChanged(nameof(IsSortDateAsc));
        OnPropertyChanged(nameof(IsSortAlphaAsc));
        OnPropertyChanged(nameof(IsSortAlphaDesc));
        OnPropertyChanged(nameof(IsSortCountDesc));
        OnPropertyChanged(nameof(IsSortCountAsc));
        OnPropertyChanged(nameof(IsSortFlaggedFirst));
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
                MessageSort.FlaggedFirst    => built.OrderBy(g => g.HasFlagged ? 0 : 1).ThenByDescending(g => g.Messages.Count > 0 ? g.Messages[0].Date : DateTimeOffset.MinValue),
                _                           => built.OrderByDescending(g => g.Messages.Count > 0 ? g.Messages[0].Date : DateTimeOffset.MinValue),
            };
            var groups = ordered.ToList();
            _ui.Post(() =>
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
        }).LogFaults("conversation rebuild");
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
                MessageSort.FlaggedFirst    => built.OrderBy(g => g.HasFlagged ? 0 : 1).ThenBy(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
                _                           => built.OrderBy(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
            };
            var groups = ordered.ToList();
            _ui.Post(() =>
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
        }).LogFaults("sender group rebuild");
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
                MessageSort.FlaggedFirst    => built.OrderBy(g => g.HasFlagged ? 0 : 1).ThenBy(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
                _                           => built.OrderBy(g => g.SenderKey, StringComparer.OrdinalIgnoreCase),
            };
            var groups = ordered.ToList();
            _ui.Post(() =>
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
        }).LogFaults("to-recipient group rebuild");
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
            // Only Password accounts need a stored secret. OAuth/Graph accounts authenticate via
            // the token cache (no password by design), so don't gate them on GetPassword — doing so
            // reported "No password stored" and never attempted the OAuth reconnect.
            var password = account.AuthType == AuthType.Password ? _credentials.GetPassword(account.Id) : null;
            if (account.AuthType == AuthType.Password && string.IsNullOrEmpty(password))
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
            // Start this account's new-mail watcher and refresh the status labels — a manual
            // sign-in/activation previously connected the account but never began polling it.
            WireUpWatchers();
            StatusText = $"Connected to {account.AccountLabel}. Press Enter on a folder to load messages.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Connection cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            Announce($"Connection failed: {ex.Message}", AnnouncementCategory.Status);
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

        // Intercept the calendar virtual folder — it shows the event list, not messages.
        if (IsCalendarFolderName(folder.FullName))
        {
            await SelectCalendarAsync(folder);
            return;
        }

        _suppressFilterRebuild = true;
        ActiveFilter        = MessageFilter.All;
        ActiveDayLimit      = null;
        SetActiveFlagFilterId(null);
        SearchText          = string.Empty;
        IsSearchActive      = false;
        ActiveView          = null;
        SelectedFolder      = folder;
        MessageDetail       = null;
        IsMessageOpen       = false;
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

    /// <summary>
    /// Activates the calendar view: clears message-list state, loads calendar events,
    /// and requests focus to the event list. Called when the user selects the
    /// Calendar virtual folder from the folder tree.
    /// </summary>
    private async Task SelectCalendarAsync(MailFolderModel? folder = null)
    {
        if (CalendarVm == null) return;
        CalendarVm.SourceFilter = folder == null ? null : CalendarFilterFor(folder.FullName);

        _suppressFilterRebuild = true;
        ActiveFilter        = MessageFilter.All;
        ActiveDayLimit      = null;
        SetActiveFlagFilterId(null);
        SearchText          = string.Empty;
        IsSearchActive      = false;
        ActiveView          = null;
        SelectedFolder      = folder ?? CalendarFolder;
        MessageDetail       = null;
        IsMessageOpen       = false;
        _suppressFilterRebuild = false;

        // Clear the message list so stale messages are not announced while the
        // calendar list is visible.
        SetMessages([]);

        await CalendarVm.LoadAsync();
        CalendarPaneFocusRequested?.Invoke();
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

                await ResolveFlagNamesAsync(cached);
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
                _localStore.UpsertSummariesAsync(list).LogFaults("local store: upsert summaries");

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
                    summary.AccountId, summary.FolderName, summary.MessageId, token);
            }
            else
            {
                // Serve from cache when available; fall back to IMAP and cache the result.
                detail = await _localStore.LoadDetailAsync(
                    summary.AccountId, summary.FolderName, summary.MessageId)
                    ?? await _imap.GetMessageDetailAsync(
                        summary.AccountId, summary.FolderName, summary.MessageId, token);
                // Await (not fire-and-forget) so the detail is definitely in the cache
                // before the user acts on the message (e.g. accepts a calendar invite).
                // The calendar harvest reads calendar_ics from this row; if the store
                // hasn't completed, the event won't be harvestable and opening it from
                // the calendar list will fail with "message not found".
                await _localStore.UpsertDetailAsync(detail);
            }

            if (loadVersion != _messageLoadVersion || SelectedMessage != summary)
                return;

            MessageDetail = detail;
            // Window mode shows messages in standalone windows; never open the reading pane there.
            IsMessageOpen = MessageOpenMode != MessageOpenMode.Window;
            var wasUnread = !summary.IsRead;
            summary.IsRead = true;
            summary.HasAttachments = detail.Attachments.Count > 0;
            // Opening a message marks it read here (not via MarkMessagesReadAsync), so refresh the
            // folder unread counts on this path too — otherwise they stay stale until the next
            // manual refresh (issue #227 follow-up).
            if (wasUnread)
            {
                ScheduleFolderCountRefresh(summary.AccountId);

                // Mark read on the server explicitly rather than relying on the body fetch's
                // \Seen side effect. In cached mode the detail is usually served from the local
                // store — prefetched messages are cached without \Seen (PrefetchMessageDetailAsync
                // uses markRead: false) — so GetMessageDetailAsync, the only thing that flags the
                // server, never runs and the message stays unread in other clients (issue #225).
                // AddFlags(\Seen) is idempotent, so re-flagging on the cache-miss path is harmless.
                // Online mode already flagged it during the GetMessageDetailAsync fetch above.
                if (!OnlineMode)
                    _imap.MarkReadAsync(summary.AccountId, summary.FolderName, summary.MessageId)
                        .LogFaults("mark read on open");
            }
            if (!OnlineMode)
            {
                _localStore.UpdateIsReadAsync(summary.AccountId, summary.FolderName, summary.MessageId, true)
                    .LogFaults("local store: update is-read");

                // Extract preview and persist if not already set.
                if (string.IsNullOrEmpty(summary.Preview))
                {
                    var lines   = _configService.Load().GetPreviewLines(summary.AccountId);
                    var preview = ExtractPreview(detail.PlainTextBody, detail.HtmlBody, lines);
                    if (!string.IsNullOrEmpty(preview))
                    {
                        summary.Preview = preview;
                        _localStore.UpdatePreviewAsync(summary.AccountId, summary.FolderName, summary.MessageId, preview)
                            .LogFaults("local store: update preview");
                    }
                }
            }

            StatusText = "Message loaded.";

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
                summary.AccountId, summary.FolderName, summary.MessageId);
            if (cached != null || ct.IsCancellationRequested) return;

            var detail = await _imap.PrefetchMessageDetailAsync(
                summary.AccountId, summary.FolderName, summary.MessageId, ct);
            if (ct.IsCancellationRequested) return;
            await _localStore.UpsertDetailAsync(detail);
            LogService.Debug($"Prefetched msgId={summary.MessageId} folder={summary.FolderName}");
        }
        catch (OperationCanceledException) { /* expected on switch */ }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not connected"))
        {
            // Prefetch raced startup or a disconnect; the next prefetch trigger
            // (folder load, message open) will retry once the account is up.
            LogService.Debug($"Prefetch skipped msgId={summary.MessageId} (account not connected)");
        }
        catch (Exception ex) { LogService.Log($"Prefetch msgId={summary.MessageId}", ex); }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Delegate to the calendar's own refresh while it's the active view, so every entry
        // point (View menu, toolbar button, Command Palette, F5) agrees — none of those bind
        // through CommandRegistry, so an isAvailable guard alone can't disambiguate them.
        if (IsCalendarView)
        {
            // Pull the latest Graph calendar slice first so F5 reflects the server, then let the
            // calendar's own refresh reload from the store and announce the updated count.
            await RunGraphCalendarSyncAsync(refreshAndAnnounce: false);
            if (CalendarVm != null)
                await CalendarVm.RefreshCommand.ExecuteAsync(null);
            return;
        }

        // Pick up folders created/removed on the server since the last full sync. Only rebuilds the
        // tree when the folder set actually changed, so an ordinary refresh doesn't disturb focus.
        await RefreshAllFolderListsAsync();

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

    /// <summary>
    /// Re-fetches every connected account's folder list and rebuilds the tree only if a folder was
    /// added or removed on the server, so a manual refresh surfaces server-side folder changes.
    /// </summary>
    private async Task RefreshAllFolderListsAsync()
    {
        // Fetch every account's folder list concurrently — they're independent, and a single slow or
        // timed-out account shouldn't serialise the whole refresh (mirrors FetchAllMailAsync).
        var fetches = Accounts.ToList().Select(async account =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                return (account, folders: (List<MailFolderModel>?)await _imap.GetFoldersAsync(account.Id, cts.Token));
            }
            catch (OperationCanceledException) { return (account, folders: (List<MailFolderModel>?)null); }
            catch (Exception ex) { LogService.Log($"RefreshFolderList {account.AccountLabel}", ex); return (account, folders: (List<MailFolderModel>?)null); }
        });
        var results = await Task.WhenAll(fetches);

        // Apply results on the continuation (UI thread): mutate the cache and rebuild only if a
        // folder was actually added or removed, so an ordinary refresh doesn't disturb focus.
        var changed = false;
        foreach (var (account, folders) in results)
        {
            if (folders == null) continue;
            if (!_cachedFolders.TryGetValue(account.Id, out var prev) || FolderSetChanged(prev, folders))
            {
                _cachedFolders[account.Id] = folders;
                ApplyAccountStatus(account, folders);
                changed = true;
            }
        }
        if (changed) RebuildFolderListFromCache();
    }

    private static bool FolderSetChanged(List<MailFolderModel> previous, List<MailFolderModel> current)
    {
        if (previous.Count != current.Count) return true;
        var prevNames = previous.Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        return current.Any(f => !prevNames.Contains(f.FullName));
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

                await ResolveFlagNamesAsync(cached);
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
                    .GroupBy(m => (m.AccountId, m.FolderName, m.MessageId))
                    .Select(g => g.OrderByDescending(m => m.Date).First())
                    .OrderByDescending(m => m.Date)
                    .ToList();

                SetMessages(repaired);
                if (!OnlineMode)
                    _localStore.UpsertSummariesAsync(repaired).LogFaults("local store: upsert repaired summaries");

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

            // All Mail unions every folder, so key by global message identity — a message's INBOX
            // and Gmail All Mail/label copies collapse to the one already shown (issue #220).
            var existingById = Messages
                .ToDictionary(MessageDeduplicator.CollapseKeyFor, StringComparer.Ordinal);

            foreach (var msg in newMessages.OrderByDescending(m => m.Date))
            {
                if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                    return;

                var key = MessageDeduplicator.CollapseKeyFor(msg);
                if (existingById.TryGetValue(key, out var prior))
                {
                    ReconcileMessageState(prior, msg);
                    continue;
                }

                if (!MatchesFilter(msg) || !MatchesDayLimit(msg))
                    continue;

                InsertMessageSorted(msg);
                existingById[key] = msg;
            }

            RemoveVanishedMessages(newMessages);

            if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                return;

            if (newMessages.Count > 0)
                _localStore.UpsertSummariesAsync(newMessages).LogFaults("local store: upsert summaries");

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
                var maxKey = await _localStore.GetMaxMessageKeyAsync(account.Id, folder.FullName);
                List<MailMessageSummary> msgs;
                if (maxKey == "0" && _syncDays > 0)
                {
                    // Fresh start with a date filter: use SEARCH SINCE rather than last-500 fallback.
                    msgs = await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct);
                }
                else
                {
                    // Incremental sync: key-based is correct (fetch everything newer than last seen).
                    var initialCount = _configService.Load().InitialSyncCount;
                    msgs = await _imap.GetMessagesSinceAsync(account.Id, folder.FullName, maxKey, initialCount, ct);
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
        if (folder.FullName == AllFlaggedFolder.FullName) return FetchAllFlaggedAsync();
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

    private async Task FetchAllFlaggedAsync()
    {
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        var expectedFolder = SelectedFolder;
        Messages.Clear();
        StatusText = "Loading flagged messages…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            List<MailMessageSummary> all;
            if (OnlineMode)
            {
                // In --online mode, fetch from every non-excluded folder across all accounts
                // and filter to flagged messages client-side.
                all = new List<MailMessageSummary>();
                foreach (var account in Accounts)
                {
                    if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;
                    foreach (var folder in folders)
                    {
                        if (folder.ExcludeFromAllMail) continue;
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var msgs = _syncDays > 0
                                ? await _imap.GetMessagesSinceDateAsync(
                                    account.Id, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct)
                                : await _imap.GetMessageSummariesAsync(account.Id, folder.FullName, 50000, ct);
                            all.AddRange(msgs.Where(m => m.IsFlagged));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Log($"FetchAllFlagged online {account.DisplayName}/{folder.DisplayName}", ex);
                        }
                    }
                }
            }
            else
            {
                all = await _localStore.LoadAllSummariesAsync();
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            await ResolveFlagNamesAsync(all);
            var flagged = all.Where(m => m.IsFlagged).ToList();
            SetMessages(flagged.OrderByDescending(m => m.Date).ToList());
            var n = Messages.Count;
            StatusText = n == 0 ? "No flagged messages." : $"{n} flagged {(n == 1 ? "message" : "messages")}.";
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = "Flagged messages load cancelled.";
        }
        catch (Exception ex)
        {
            LogService.Log("FetchAllFlagged failed", ex);
            StatusText = "Could not load flagged messages.";
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    public async Task ToggleSingleFlagAsync(MailMessageSummary message)
    {
        if (_flagService == null) return;
        try
        {
            ReplaceCts(ref _flagActionCts, out var ct);
            bool wasFlagged = message.IsFlagged;
            var def = await _flagService.ToggleDefaultFlagAsync(message, ct);
            // Update in-memory model (we're on the UI thread from the command handler).
            message.FlagId       = wasFlagged ? null : def?.Id.ToString();
            message.FlagName     = def?.Name;
            message.FlagColorHex = def?.ColorHex;
            if (_announceFlagStatus)
            {
                var text = wasFlagged ? "Unflagged" : $"{message.FlagName ?? "Flagged"}";
                Announce(text, AnnouncementCategory.Result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("ToggleSingleFlag failed", ex); }
    }

    public async Task ToggleGroupFlagAsync(IReadOnlyList<MailMessageSummary> messages)
    {
        if (_flagService == null || messages.Count == 0) return;
        try
        {
            ReplaceCts(ref _flagActionCts, out var ct);
            bool anyFlagged = messages.Any(m => m.IsFlagged);
            var kFlag = await _flagService.GetKDefaultFlagAsync();
            string? targetFlagId = anyFlagged ? null : kFlag.Id.ToString();
            foreach (var msg in messages)
            {
                var def = await _flagService.SetMessageFlagAsync(msg, targetFlagId, ct: ct);
                msg.FlagId       = targetFlagId;
                msg.FlagName     = def?.Name;
                msg.FlagColorHex = def?.ColorHex;
            }
            if (_announceFlagStatus)
            {
                var text = anyFlagged
                    ? $"Unflagged {messages.Count} {(messages.Count == 1 ? "message" : "messages")}"
                    : $"Flagged {messages.Count} {(messages.Count == 1 ? "message" : "messages")}";
                Announce(text, AnnouncementCategory.Result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("ToggleGroupFlag failed", ex); }
    }

    public async Task SetMessageFlagAsync(MailMessageSummary message, string? flagId)
    {
        if (_flagService == null) return;
        try
        {
            ReplaceCts(ref _flagActionCts, out var ct);
            var def = await _flagService.SetMessageFlagAsync(message, flagId, ct: ct);
            message.FlagId       = flagId;
            message.FlagName     = def?.Name;
            message.FlagColorHex = def?.ColorHex;
            if (_announceFlagStatus)
            {
                var text = flagId == null ? "Unflagged" : (message.FlagName ?? "Flagged");
                Announce(text, AnnouncementCategory.Result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("SetMessageFlag failed", ex); }
    }

    public async Task SetGroupFlagAsync(IReadOnlyList<MailMessageSummary> messages, string? flagId)
    {
        if (_flagService == null || messages.Count == 0) return;
        try
        {
            ReplaceCts(ref _flagActionCts, out var ct);
            FlagDefinition? def = null;
            foreach (var msg in messages)
            {
                def = await _flagService.SetMessageFlagAsync(msg, flagId, ct: ct);
                msg.FlagId       = flagId;
                msg.FlagName     = def?.Name;
                msg.FlagColorHex = def?.ColorHex;
            }
            if (_announceFlagStatus)
            {
                var text = flagId == null
                    ? $"Unflagged {messages.Count} {(messages.Count == 1 ? "message" : "messages")}"
                    : $"Flagged {messages.Count} {(messages.Count == 1 ? "message" : "messages")}: {def?.Name ?? "Flagged"}";
                Announce(text, AnnouncementCategory.Result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("SetGroupFlag failed", ex); }
    }

    private async Task OnFlagDefinitionsChangedAsync()
    {
        try
        {
            if (_flagService == null) return;
            var defs = await _flagService.LoadFlagDefinitionsAsync();

            FlagDefinitions.Clear();
            foreach (var d in defs.OrderBy(d => d.SortOrder))
                FlagDefinitions.Add(d);

            var lookup = new Dictionary<Guid, FlagDefinition>(defs.Count);
            foreach (var d in defs) lookup[d.Id] = d;
            foreach (var msg in _rawMessages)
            {
                if (msg.FlagId != null && Guid.TryParse(msg.FlagId, out var fid))
                {
                    if (lookup.TryGetValue(fid, out var def))
                    {
                        msg.FlagName     = def.Name;
                        msg.FlagColorHex = def.ColorHex;
                    }
                    else
                    {
                        // Flag was deleted — clear all flag state so the message
                        // no longer appears flagged or stuck in the Flagged filter.
                        msg.FlagId       = null;
                        msg.FlagName     = null;
                        msg.FlagColorHex = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log("OnFlagDefinitionsChanged", ex);
        }
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

                await ResolveFlagNamesAsync(cached);
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

            // Per-account All Mail unions the account's folders, so key by global message identity
            // to collapse Gmail's per-folder duplicate copies against what is shown (issue #220).
            var existingById = Messages
                .ToDictionary(MessageDeduplicator.CollapseKeyFor, StringComparer.Ordinal);

            foreach (var msg in newMessages.OrderByDescending(m => m.Date))
            {
                if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

                var key = MessageDeduplicator.CollapseKeyFor(msg);
                if (existingById.TryGetValue(key, out var prior))
                {
                    ReconcileMessageState(prior, msg);
                    continue;
                }

                if (!MatchesFilter(msg) || !MatchesDayLimit(msg))
                    continue;

                InsertMessageSorted(msg);
                existingById[key] = msg;
            }

            RemoveVanishedMessages(newMessages);

            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            if (newMessages.Count > 0)
                _localStore.UpsertSummariesAsync(newMessages).LogFaults("local store: upsert summaries");

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

            await ResolveFlagNamesAsync(all);
            var sorted = all.OrderByDescending(m => m.Date).ToList();
            SetMessages(sorted);
            StatusText = sorted.Count == 0
                ? $"No messages in {displayName}."
                : $"{sorted.Count} messages in {displayName}.";
            if (!OnlineMode)
                _localStore.UpsertSummariesAsync(sorted).LogFaults("local store: upsert summaries");
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

        _localStore.UpdateIsReadBatchAsync(
                unread.Select(m => (m.AccountId, m.FolderName, m.MessageId)), true)
            .LogFaults("local store: update is-read batch");

        foreach (var group in unread.GroupBy(m => (m.AccountId, m.FolderName)))
        {
            var uids = group.Select(m => m.MessageId).ToList();
            _imap.MarkReadBatchAsync(group.Key.AccountId, group.Key.FolderName, uids)
                .LogFaults($"mark read batch ({group.Key.FolderName}, {uids.Count} messages)");
        }

        // Refresh folder unread counts once the server has the reads (issue #227). Debounced and
        // server-authoritative so Gmail's cross-label \Seen propagation is reflected in every folder.
        foreach (var accountId in unread.Select(m => m.AccountId).Distinct())
            ScheduleFolderCountRefresh(accountId);

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
        // Delete/archive progress + outcome go through the MessageAction category (issue #317) so users
        // can silence this frequent chatter — it can interrupt the screen reader reading the next message.
        SetStatus($"Deleting {label}…", AnnouncementCategory.MessageAction);
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
        var toDeleteKeys = new HashSet<(string, Guid, string)>(
            toDelete.Select(m => (m.MessageId, m.AccountId, m.FolderName)));
        _rawMessages.RemoveAll(m => toDeleteKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));

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
        var affectedFolders = new List<(Guid AccountId, MailFolderModel Folder)>();
        try
        {
            // Own token per delete — a second Delete keystroke no longer cancels this one's in-flight
            // IMAP work (which surfaced as "Delete may not have completed"). Deletes run concurrently;
            // the connection pool handles it. Cancels only at app shutdown. (#311)
            using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(_messageActionShutdownCts.Token);
            var ct = actionCts.Token;

            var groups = toDelete.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
            {
                var uids = group.Select(m => m.MessageId).ToList();

                // Messages already in Trash must be permanently deleted (expunge);
                // moving them to trash again is a no-op on most servers.
                var sourceKind = _cachedFolders.TryGetValue(group.Key.AccountId, out var acctFolders)
                    ? acctFolders.FirstOrDefault(f =>
                          f.FullName.Equals(group.Key.FolderName, StringComparison.OrdinalIgnoreCase))?.Kind
                    : null;

                var sourceFolder = acctFolders?.FirstOrDefault(f =>
                    f.FullName.Equals(group.Key.FolderName, StringComparison.OrdinalIgnoreCase));
                if (sourceFolder != null)
                    affectedFolders.Add((group.Key.AccountId, sourceFolder));

                if (sourceKind == SpecialFolderKind.Trash)
                    await _imap.PermanentlyDeleteBatchAsync(
                        group.Key.AccountId, group.Key.FolderName, uids, ct);
                else
                    await _imap.MoveToTrashBatchAsync(
                        group.Key.AccountId, group.Key.FolderName, uids, ct);

                if (!OnlineMode)
                    await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            // Now the server deletes have landed, refresh folder unread counts — but only if an
            // unread message actually left a folder (deleting a read message changes no count).
            // Scheduled here, not before the await, so the debounced STATUS sweep can't read a
            // pre-delete count and clobber the optimistic decrement (#227 follow-up).
            if (toDelete.Any(m => !m.IsRead))
                foreach (var acctId in toDelete.Select(m => m.AccountId).Distinct())
                    ScheduleFolderCountRefresh(acctId);

            var count = toDelete.Count;
            SetStatus(Messages.Count > 0
                ? $"{count} {(count == 1 ? "message" : "messages")} deleted."
                : $"{count} {(count == 1 ? "message" : "messages")} deleted. Folder is now empty.",
                AnnouncementCategory.MessageAction);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Delete cancelled.", AnnouncementCategory.MessageAction);
        }
        catch (Exception ex)
        {
            // Honest uncertainty message — the delete may have partially or fully succeeded. Kept as a
            // Result (not MessageAction) so the failure is still heard even if delete/archive chatter is off.
            SetStatus("Delete may not have completed — refreshing.", AnnouncementCategory.Result);
            LogService.Log("DeleteMessages", ex);

            // Schedule targeted sync of affected folders to reconcile the UI with server state.
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                foreach (var (accountId, folder) in affectedFolders)
                {
                    if (_bgSyncCts?.Token.IsCancellationRequested ?? true) break;
                    try
                    {
                        if (OnlineMode)
                            await _syncService.SyncOneFolderOnlineAsync(
                                Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.First(),
                                folder,
                                CancellationToken.None);
                        else
                            await _syncService.SyncOneFolderAsync(
                                Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.First(),
                                folder,
                                CancellationToken.None);
                    }
                    catch (Exception syncEx) { LogService.Log($"Delete reconciliation sync failed", syncEx); }
                }
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Archive (issue #318) ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ArchiveMessageAsync()
    {
        if (SelectedMessage == null) return;
        await ArchiveMessagesAsync([SelectedMessage]);
    }

    /// <summary>
    /// Resolves the Archive destination folder for an account (issue #318): an explicit per-account
    /// override (<see cref="AccountModel.ArchiveFolderFullName"/>) wins, otherwise the folder the
    /// server flags as <see cref="SpecialFolderKind.Archive"/>. Returns null when neither exists —
    /// the caller then guides the user to pick one rather than silently doing nothing.
    /// </summary>
    private MailFolderModel? ResolveArchiveFolder(Guid accountId)
    {
        if (!_cachedFolders.TryGetValue(accountId, out var folders)) return null;

        var overrideName = Accounts.FirstOrDefault(a => a.Id == accountId)?.ArchiveFolderFullName;
        if (!string.IsNullOrEmpty(overrideName))
        {
            var match = folders.FirstOrDefault(f =>
                f.FullName.Equals(overrideName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            // The override points at a folder that no longer exists — fall through to auto-detect.
        }

        return folders.FirstOrDefault(f => f.Kind == SpecialFolderKind.Archive);
    }

    /// <summary>True when the given account currently has a resolvable Archive folder.</summary>
    public bool HasArchiveFolder(Guid accountId) => ResolveArchiveFolder(accountId) != null;

    /// <summary>
    /// Sets (or clears, when <paramref name="fullName"/> is null/empty) the per-account Archive folder
    /// and persists it to accounts.json. Invoked from the folder tree's Set / Use-automatic Archive
    /// commands. There is deliberately no global archive folder — this is per account.
    /// </summary>
    public void SetArchiveFolder(Guid accountId, string? fullName)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return;
        account.ArchiveFolderFullName = string.IsNullOrEmpty(fullName) ? null : fullName;
        _accountService.SaveAccounts([.. Accounts]);
    }

    /// <summary>
    /// Moves the given messages to each account's Archive folder (issue #318). Mirrors the optimistic
    /// UI of <see cref="DeleteMessagesAsync"/> — messages vanish immediately, focus lands on the next
    /// item — but the server operation is a move (like <see cref="MoveSelectedMessagesToFolderAsync"/>),
    /// so folder counts are reconciled via <see cref="ScheduleFolderCountRefresh"/> and the account
    /// unread total is left untouched (archived mail still belongs to the account). Messages are
    /// grouped by (account, source folder) so a single call on a From/To/conversation group archives
    /// the whole group across every account it spans. Messages already in their Archive folder are
    /// skipped; messages whose account has no Archive folder are left in place and surface guidance.
    /// </summary>
    public async Task ArchiveMessagesAsync(IReadOnlyList<MailMessageSummary> toArchive)
    {
        if (toArchive.Count == 0) return;

        // Build the per-group plan up front so we only touch messages we can actually archive.
        var plan = new List<(IGrouping<(Guid AccountId, string FolderName), MailMessageSummary> Group, MailFolderModel Dest)>();
        bool anyMissingArchive = false;
        foreach (var group in toArchive.GroupBy(m => (m.AccountId, m.FolderName)))
        {
            var dest = ResolveArchiveFolder(group.Key.AccountId);
            if (dest == null) { anyMissingArchive = true; continue; }
            // Already in the archive folder → nothing to do.
            if (dest.FullName.Equals(group.Key.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
            plan.Add((group, dest));
        }

        if (plan.Count == 0)
        {
            if (anyMissingArchive)
                // Setup guidance stays a Result (not MessageAction): if archive silently did nothing,
                // the user must hear why even if delete/archive chatter is turned off.
                SetStatus("No Archive folder for this account. Press Shift+F10 on a folder and choose Set as Archive Folder.",
                    AnnouncementCategory.Result);
            return;
        }

        var actionable = plan.SelectMany(p => p.Group).ToList();
        var minIdx = actionable.Min(m => Messages.IndexOf(m));
        var label  = actionable.Count == 1 ? "message" : $"{actionable.Count} messages";
        SetStatus($"Archiving {label}…", AnnouncementCategory.MessageAction);
        IsBusy        = true;
        MessageDetail = null;
        IsMessageOpen = false;

        // ── Step 1: Remove from UI immediately (same optimistic pattern as delete) ──
        foreach (var msg in actionable)
            Messages.Remove(msg);

        // Drop from _rawMessages too so a filter/search re-apply before the next sync can't
        // resurrect an archived message (delete does the same).
        var actionableKeys = new HashSet<(string, Guid, string)>(
            actionable.Select(m => (m.MessageId, m.AccountId, m.FolderName)));
        _rawMessages.RemoveAll(m => actionableKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));

        RebuildActiveGroupView();

        if (ViewMode == ViewMode.Messages && Messages.Count > 0)
        {
            var landIdx = Math.Max(0, Math.Min(minIdx, Messages.Count - 1));
            SelectedMessage = Messages[landIdx];
            MessageListFocusRequested?.Invoke();
        }
        else
        {
            // From/To/Conversations focus is handled by the caller's LandOn*AfterRebuild. Clearing
            // SelectedMessage keeps HasSelectedMessage=false so the global Archive/Delete hotkeys
            // don't steal the next keypress and act on a single message. (Same rationale as delete.)
            SelectedMessage = null;
        }

        // ── Step 2: IMAP move + local store cleanup ──
        var affectedFolders = new List<(Guid AccountId, MailFolderModel Folder)>();
        try
        {
            // Own token per archive (same rationale as delete/move) — a follow-up action no longer
            // cancels this archive's in-flight IMAP work. Cancels only at app shutdown. (#311)
            using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(_messageActionShutdownCts.Token);
            var ct = actionCts.Token;

            foreach (var (group, dest) in plan)
            {
                var uids = group.Select(m => m.MessageId).ToList();

                if (_cachedFolders.TryGetValue(group.Key.AccountId, out var acctFolders))
                {
                    var sourceFolder = acctFolders.FirstOrDefault(f =>
                        f.FullName.Equals(group.Key.FolderName, StringComparison.OrdinalIgnoreCase));
                    if (sourceFolder != null)
                        affectedFolders.Add((group.Key.AccountId, sourceFolder));
                }

                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, dest.FullName, ct);

                if (!OnlineMode)
                    await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            // Archiving an unread message changes the source and destination folder counts; refresh
            // after the move lands (only when an unread message actually moved). The account unread
            // total is unchanged — the message is still in the account, just a different folder.
            if (actionable.Any(m => !m.IsRead))
                foreach (var acctId in actionable.Select(m => m.AccountId).Distinct())
                    ScheduleFolderCountRefresh(acctId);

            var count = actionable.Count;
            SetStatus($"{count} {(count == 1 ? "message" : "messages")} archived.",
                AnnouncementCategory.MessageAction);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Archive cancelled.", AnnouncementCategory.MessageAction);
        }
        catch (Exception ex)
        {
            // Kept as a Result (not MessageAction) so the failure is heard even if archive chatter is off.
            SetStatus("Archive may not have completed — refreshing.", AnnouncementCategory.Result);
            LogService.Log("ArchiveMessages", ex);

            // Reconcile the affected source folders with server state after a short delay.
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                foreach (var (accountId, folder) in affectedFolders)
                {
                    if (_bgSyncCts?.Token.IsCancellationRequested ?? true) break;
                    try
                    {
                        if (OnlineMode)
                            await _syncService.SyncOneFolderOnlineAsync(
                                Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.First(),
                                folder, CancellationToken.None);
                        else
                            await _syncService.SyncOneFolderAsync(
                                Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.First(),
                                folder, CancellationToken.None);
                    }
                    catch (Exception syncEx) { LogService.Log("Archive reconciliation sync failed", syncEx); }
                }
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Compose / accounts ───────────────────────────────────────────────────────

    public event Action<ComposeModel>? ComposeRequested;

    /// <summary>
    /// Raised before the compose window opens when forwarding a message that has attachments.
    /// The subscriber shows a selection dialog and returns the chosen subset, or null to cancel.
    /// When null (no subscriber), all attachments are included.
    /// </summary>
    public event Func<IReadOnlyList<AttachmentModel>, Task<IReadOnlyList<AttachmentModel>?>>? SelectAttachmentsForForwardRequested;
    public event Action? ManageAccountsRequested;
    public event Action? MessageListFocusRequested;
    public event EventHandler<(string Text, AnnouncementCategory Category)>? AnnouncementRequested;
    public event EventHandler? RulesManagerRequested;
    public event EventHandler<MailRule>? CreateRuleFromMessageRequested;
    public event EventHandler? TutorialRequested;
    public event EventHandler? AboutRequested;
    public event EventHandler? ReportBugRequested;

    // One-time first-run offer to add a desktop shortcut (installed copies only). The View
    // shows the actual dialog and reports the answer back via ApplyDesktopShortcutChoice.
    public event EventHandler? DesktopShortcutOfferRequested;

    /// <summary>
    /// Raised when a Properties dialog should be shown. The View subscribes and
    /// calls new PropertiesWindow(vm).ShowDialog().
    /// </summary>
    public event Action<PropertiesViewModel>? PropertiesRequested;

    /// <summary>
    /// Raises the desktop shortcut offer when it applies: running from a Velopack install,
    /// never asked before, and no shortcut already present. Called once per launch by the
    /// View after the main window has loaded.
    /// </summary>
    public void MaybeOfferDesktopShortcut()
    {
        if (!Helpers.VelopackRuntime.IsInstalled) return;
        var cfg = _configService.Load();
        if (cfg.DesktopShortcutPrompted) return;
        if (Helpers.DesktopShortcut.Exists())
        {
            cfg.DesktopShortcutPrompted = true;
            _configService.Save(cfg);
            return;
        }
        DesktopShortcutOfferRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the user's answer to the desktop shortcut offer and applies it.
    /// The offer is never repeated regardless of the answer; the choice stays editable
    /// in Settings → General.</summary>
    public void ApplyDesktopShortcutChoice(bool create)
    {
        if (create)
        {
            bool created;
            try
            {
                created = Helpers.DesktopShortcut.Create();
            }
            catch (Exception ex)
            {
                created = false;
                LogService.Debug($"Desktop shortcut: {ex.Message}");
            }
            // Create() can decline silently (no WScript.Shell class, unknown process path) —
            // only report what actually happened, and point at the retry path on failure.
            Announce(created
                ? "Desktop shortcut added."
                : "The desktop shortcut could not be created. You can try again from Settings, on the General page.");
        }
        var cfg = _configService.Load();
        cfg.DesktopShortcutPrompted = true;
        _configService.Save(cfg);
    }

    private void Announce(string text, AnnouncementCategory category = AnnouncementCategory.Result)
    {
        if (!string.IsNullOrEmpty(text))
            AnnouncementRequested?.Invoke(this, (text, category));
    }

    // Pane indices from MainWindow.GetFocusedPaneIndex():
    //   0 = Toolbar, 1 = Account list, 2 = Folder tree,
    //   3 = Message list / conversation trees, 4 = Reading pane, 5 = Status bar
    // focusedFolder overrides SelectedFolder for pane 2: the folder tree's TreeView
    // updates its internal SelectedItem on arrow-key navigation but has no
    // SelectedItemChanged handler, so SelectedFolder lags until Enter commits it.
    // focusedMessage overrides SelectedMessage for pane 3: grouped-tree OnSelectedItemChanged
    // only fires for individual MailMessageSummary items, not group headers, so SelectedMessage
    // is stale when a ConversationGroup or SenderGroup header is focused.
    public async Task ShowPropertiesAsync(int paneIndex, MailFolderModel? focusedFolder = null, MailMessageSummary? focusedMessage = null)
    {
        // pane 0 means toolbar or unknown focus (e.g. command palette has focus, or WPF
        // moved focus to the menu bar when Alt was pressed). Fall back to whichever
        // context item is most specifically selected so the command still does something
        // useful from the command palette or via Alt+Enter with menu-bar focus.
        if (paneIndex == 0)
        {
            if (focusedMessage != null || SelectedMessage != null) paneIndex = 3;
            else if (SelectedFolder != null)                       paneIndex = 2;
            else if (SelectedAccount != null)                      paneIndex = 1;
            else return;
        }

        if ((paneIndex == 3 || paneIndex == 4) && (focusedMessage ?? SelectedMessage) is { } msg)
        {
            // Load detail if not already open (detail may already be in MessageDetail
            // when the reading pane is open for this message).
            var detail = (MessageDetail?.MessageId == msg.MessageId
                          && MessageDetail?.AccountId == msg.AccountId
                          && MessageDetail?.FolderName == msg.FolderName)
                ? MessageDetail
                : await _localStore.LoadDetailAsync(msg.AccountId, msg.FolderName, msg.MessageId);

            var accountName = Accounts.FirstOrDefault(a => a.Id == msg.AccountId)?.AccountLabel
                              ?? "Unknown";
            var (title, sections) = MessagePropertiesBuilder.Build(msg, detail, accountName);
            PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
        }
        else if (paneIndex == 2 && (focusedFolder ?? SelectedFolder) is { } folder)
        {
            var accountName = Accounts.FirstOrDefault(a => a.Id == folder.AccountId)?.AccountLabel
                              ?? "Unknown";
            var (title, sections) = FolderPropertiesBuilder.Build(folder, accountName);
            PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
        }
        else if (paneIndex == 1 && SelectedAccount is { } acct)
        {
            var lastSync = _syncService.LastSyncedUtc(acct.Id);

            // Fetch cache statistics if not in --online mode.
            int cacheCount = 0;
            DateTimeOffset? oldestCached = null;
            string? syncWindow = null;

            if (!OnlineMode)
            {
                try
                {
                    cacheCount = await _localStore.CountSummariesAsync(acct.Id);
                    oldestCached = await _localStore.GetOldestMessageDateAsync(acct.Id);

                    var syncDays = _configService.Load().SyncDays;
                    syncWindow = syncDays == 0 ? "All mail" : $"Last {syncDays} days";
                }
                catch (Exception ex)
                {
                    // On database errors, skip sync section.
                    LogService.Log("ShowProperties: cache stats failed", ex);
                }
            }

            var (title, sections) = AccountPropertiesBuilder.Build(acct, lastSync, cacheCount, oldestCached, syncWindow);
            PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
        }
        // No-op for toolbar, status bar, or when nothing is selected.
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
            // Ask the user which attachments to include (if anyone is listening).
            IReadOnlyList<AttachmentModel> selected;
            if (SelectAttachmentsForForwardRequested != null)
            {
                var result = await SelectAttachmentsForForwardRequested(detail.Attachments);
                if (result == null) return; // user cancelled
                selected = result;
            }
            else
            {
                // No subscriber (e.g. in tests): include all, matching the old behaviour.
                selected = detail.Attachments;
            }

            if (selected.Count > 0)
            {
                IsBusy = true;
                try
                {
                    int total = selected.Count;
                    int downloaded = 0;
                    int failed = 0;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    for (int i = 0; i < selected.Count; i++)
                    {
                        var att = selected[i];
                        StatusText = $"Downloading {i + 1} of {total} attachment{(total == 1 ? "" : "s")}…";
                        Announce(StatusText, AnnouncementCategory.Status);
                        if (!att.IsLoaded && att.PartSpecifier != null)
                        {
                            try
                            {
                                att.Content = await _imap.DownloadAttachmentAsync(
                                    detail.AccountId, detail.FolderName, detail.MessageId,
                                    att.PartSpecifier, cts.Token);
                                downloaded++;
                            }
                            catch (OperationCanceledException)
                            {
                                failed += selected.Count - (i + 1);
                                break;
                            }
                            catch (Exception ex)
                            {
                                LogService.Log($"Forward: failed to download '{att.FileName}'", ex);
                                failed++;
                            }
                        }
                        else if (att.IsLoaded)
                        {
                            downloaded++;
                        }
                        else
                        {
                            LogService.Log($"Forward: '{att.FileName}' has no PartSpecifier and no content.");
                            failed++;
                        }
                    }

                    if (failed > 0)
                    {
                        StatusText = $"{downloaded} of {total} attachment{(total == 1 ? "" : "s")} included ({failed} could not be downloaded).";
                        Announce(StatusText, AnnouncementCategory.Status);
                    }
                    else
                    {
                        StatusText = $"{downloaded} attachment{(downloaded == 1 ? "" : "s")} ready.";
                        Announce(StatusText, AnnouncementCategory.Status);
                    }

                    model.Attachments = selected.Where(a => a.IsLoaded).ToList();
                }
                finally
                {
                    IsBusy = false;
                    StatusText = string.Empty;
                }
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
            MessageDetail.MessageId   == summary.MessageId &&
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
                summary.AccountId, summary.FolderName, summary.MessageId);

            if (detail == null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                detail = await _imap.GetMessageDetailAsync(
                    summary.AccountId, summary.FolderName, summary.MessageId, cts.Token);
                _localStore.UpsertDetailAsync(detail).LogFaults("local store: upsert detail");
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

            // Always fetch drafts from IMAP — skip the local cache so the compose-mode
            // header and the latest autosaved body are read directly from the server.
            var detail = await _imap.GetMessageDetailAsync(
                summary.AccountId, summary.FolderName, summary.MessageId, ct);

            var model = new ComposeModel
            {
                Kind            = ComposeKind.EditDraft,
                AccountId       = summary.AccountId,
                To              = detail.To,
                Cc              = detail.Cc,
                Subject         = detail.Subject,
                Body            = detail.PlainTextBody,
                Mode            = detail.DraftComposeMode,
                HtmlBody        = detail.DraftComposeMode == ComposeMode.Html ? detail.HtmlBody : null,
                DraftMessageId  = summary.MessageId,
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
                            summary.AccountId, summary.FolderName, summary.MessageId,
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
            // Post-failure verification: check if trash is actually empty on the server.
            // If it is, the operation succeeded despite the exception (TCP drop on ACK).
            LogService.Log("EmptyTrash", ex);
            try
            {
                using var verifyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                foreach (var account in accountsToEmpty)
                {
                    int remaining = await _imap.CountTrashMessagesAsync(account.Id, verifyCts.Token);
                    if (remaining == 0)
                    {
                        // Server succeeded — report success
                        trashEmptied = true;
                        LogService.Log("EmptyTrash: verification passed — trash is empty on server");
                        break;
                    }
                }
            }
            catch (Exception verifyEx)
            {
                LogService.Log("EmptyTrash: verification failed", verifyEx);
            }

            // If not verified as empty, report the error
            if (!trashEmptied)
            {
                StatusText = $"Empty trash failed: {ex.Message}";
                Announce($"Empty trash failed: {ex.Message}", AnnouncementCategory.Result);
            }
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

    /// <summary>Raised when the user chooses Exit. The View performs the actual shutdown so it can
    /// first flag the close as an explicit exit (bypassing close-to-tray). Keeping the shutdown in
    /// the View also honours the MVVM rule that VMs do not touch <c>Application</c>.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised on the UI thread once the startup connect pass has completed. Lets a deferred
    /// notification activation (cold start) open its message once the account is reachable.</summary>
    public event Action? StartupConnectCompleted;

    /// <summary>True when the account's folders are cached, i.e. it is connected and a message detail
    /// fetch by id can succeed. Used to decide whether to open a toast's message now or defer it.</summary>
    public bool IsAccountReady(Guid accountId) => _cachedFolders.ContainsKey(accountId);

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    // ── Account context menu commands ─────────────────────────────────────────

    public event Action<AccountModel>? OpenAccountSettingsRequested;

    /// <summary>
    /// Set by the View to show a Yes/No confirmation dialog.
    /// Parameters: message, title. Returns true when the user confirms.
    /// </summary>
    public Func<string, string, bool>? ConfirmationRequested { get; set; }

    /// <summary>
    /// Set by the View to show a Save File dialog (CLAUDE.md MVVM rules: Win32 dialogs
    /// are View-layer). Parameter: suggested filename. Returns the chosen full path,
    /// or null when cancelled or unwired (headless/tests).
    /// </summary>
    public Func<string, string?>? SaveFilePathRequested { get; set; }

    /// <summary>
    /// Set by the View to show a folder picker. Parameter: dialog title.
    /// Returns the chosen folder path, or null when cancelled or unwired.
    /// </summary>
    public Func<string, string?>? SaveFolderPathRequested { get; set; }

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

        if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
        {
            try   { await _oauthService.SignOutAsync(account); }
            catch (Exception ex) { LogService.Log($"DeleteAccount: failed OAuth sign-out — {ex.Message}"); }
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

    /// <summary>
    /// Schedules a debounced, server-authoritative refresh of the account's folder unread counts
    /// (issue #227). Called after events that change unread state — mark-read, new mail, sync. A
    /// pending refresh for the same account is cancelled and rescheduled, so a burst of mark-reads
    /// costs one STATUS sweep. Unlike <see cref="RefreshFolderListAsync"/> this updates counts in
    /// place and does not rebuild the tree, so folder-tree keyboard focus is preserved.
    /// </summary>
    private void ScheduleFolderCountRefresh(Guid accountId)
    {
        if (accountId == Guid.Empty) return;
        // Only IMAP accounts get STATUS sweeps: skip unknown ids and Graph accounts (which get counts
        // from a different path). Guarding null here avoids scheduling a doomed GetFoldersAsync.
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null || account.BackendKind != BackendKind.ImapSmtp) return;

        if (_folderCountCts.TryGetValue(accountId, out var old))
        {
            try { old.Cancel(); old.Dispose(); } catch { }
        }
        var cts = new CancellationTokenSource();
        _folderCountCts[accountId] = cts;
        _ = RefreshFolderCountsDebouncedAsync(accountId, cts.Token);
    }

    private async Task RefreshFolderCountsDebouncedAsync(Guid accountId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FolderCountRefreshDelay, ct);
            // Throttle: keep at least FolderCountMinInterval between sweeps for this account.
            if (_lastFolderCountSweep.TryGetValue(accountId, out var last))
            {
                var wait = FolderCountMinInterval - (DateTimeOffset.UtcNow - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { return; }

        try
        {
            var fresh = await _imap.GetFoldersAsync(accountId, ct);
            _lastFolderCountSweep[accountId] = DateTimeOffset.UtcNow;
            if (ct.IsCancellationRequested) return;
            _ui.Post(() => ApplyFolderCounts(accountId, fresh));
        }
        catch (OperationCanceledException) { /* superseded or shutting down — fine */ }
        catch (Exception ex)
        {
            LogService.Log($"RefreshFolderCounts {accountId}", ex);
        }
    }

    /// <summary>
    /// Applies freshly-queried unread counts onto the existing cached folder models and notifies
    /// the corresponding tree nodes in place (no tree rebuild). Runs on the UI thread.
    /// </summary>
    private void ApplyFolderCounts(Guid accountId, List<MailFolderModel> fresh)
    {
        if (!_cachedFolders.TryGetValue(accountId, out var cached)) return;

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fresh) byName[f.FullName] = f.UnreadCount;

        foreach (var c in cached)
            if (byName.TryGetValue(c.FullName, out var unread))
                c.UnreadCount = unread;

        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account != null)
            account.TotalUnread = cached.Where(f => !f.SuppressUnreadCount).Sum(f => f.UnreadCount);

        // Refresh the count display on the existing nodes for this account — no rebuild, so the
        // user's place in the folder tree is undisturbed.
        if (FolderTree != null)
            foreach (var n in FlattenAllNodes(FolderTree))
                if (n.Folder is { } mf && mf.AccountId == accountId)
                    n.NotifyUnreadChanged();
    }

    /// <summary>
    /// Removes a folder and its descendants from the cached folder list so the tree reflects a
    /// delete immediately, without waiting on an eventually-consistent server re-fetch. Graph models
    /// children by <see cref="MailFolderModel.ParentId"/>; IMAP encodes them in the separator path.
    /// </summary>
    private void RemoveFolderFromCacheOptimistically(Guid accountId, MailFolderModel deleted)
    {
        if (!_cachedFolders.TryGetValue(accountId, out var folders)) return;

        // Graph: collect the whole subtree transitively by ParentId.
        var removeIds = new HashSet<string>(StringComparer.Ordinal) { deleted.FullName };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var f in folders)
                if (f.ParentId != null && removeIds.Contains(f.ParentId) && removeIds.Add(f.FullName))
                    grew = true;
        }

        // IMAP: children live under "Parent/Child" or "Parent.Child", so also drop by path prefix.
        bool ShouldRemove(MailFolderModel f) =>
            removeIds.Contains(f.FullName) ||
            f.FullName.StartsWith(deleted.FullName + "/", StringComparison.OrdinalIgnoreCase) ||
            f.FullName.StartsWith(deleted.FullName + ".", StringComparison.OrdinalIgnoreCase);

        _cachedFolders[accountId] = folders.Where(f => !ShouldRemove(f)).ToList();

        // Keep the flat Folders collection in sync — it backs saved-view resolution, the folder
        // picker, and the next tree rebuild, so a stale entry there would resolve a deleted folder.
        for (int i = Folders.Count - 1; i >= 0; i--)
            if (Folders[i].AccountId == accountId && !Folders[i].IsHeader && ShouldRemove(Folders[i]))
                Folders.RemoveAt(i);

        // Re-sum the account's unread badge from the pruned folder list.
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account != null) ApplyAccountStatus(account, _cachedFolders[accountId]);
    }

    /// <summary>
    /// Removes a node from the live <see cref="FolderTree"/> in place (without a full rebuild), so the
    /// rest of the tree keeps its expansion state. Returns true if the node was found and removed.
    /// </summary>
    private bool RemoveNodeFromTree(FolderTreeNode target) => RemoveNodeFromChildren(FolderTree, target);

    private static bool RemoveNodeFromChildren(ObservableCollection<FolderTreeNode> siblings, FolderTreeNode target)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            if (ReferenceEquals(siblings[i], target)) { siblings.RemoveAt(i); return true; }
            if (RemoveNodeFromChildren(siblings[i].Children, target)) return true;
        }
        return false;
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

    // Set by CreateFolderReturningFoldersAsync (the folder-picker path) because that runs while the
    // modal picker's message loop is active — rebuilding the main-window folder tree then is the
    // documented re-entrancy crash (see CLAUDE.md "re-query the folder tree ... while the dialog's
    // loop is still active"). The rebuild is deferred to CommitPendingFolderTreeRebuild(), called
    // once the picker has closed.
    private bool _folderTreeRebuildPending;

    /// <summary>
    /// Creates a folder for the folder picker (move/copy-message flow) and returns the owning
    /// account's refreshed folder list so the picker — which holds a filtered copy of
    /// <see cref="CachedFolders"/> — can rebuild its own tree in place and select the new folder.
    /// Refreshes only the cache (not the main-window folder tree); that rebuild is deferred to
    /// <see cref="CommitPendingFolderTreeRebuild"/> because this runs inside the picker's modal
    /// loop. Returns null on failure.
    /// </summary>
    public async Task<IReadOnlyList<MailFolderModel>?> CreateFolderReturningFoldersAsync(
        Guid accountId, string? parentFolderName, string name)
    {
        StatusText = $"Creating folder '{name}'…";
        IsBusy     = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _imap.CreateFolderAsync(accountId, parentFolderName, name, cts.Token);
            var folderList = await _imap.GetFoldersAsync(accountId, cts.Token);
            _cachedFolders[accountId] = folderList;
            var account = Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account != null) ApplyAccountStatus(account, folderList);
            _folderTreeRebuildPending = true;
            StatusText = $"Folder '{name}' created.";
            return folderList;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to create folder: {ex.Message}";
            LogService.Log("CreateFolder", ex);
            return null;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Rebuilds the main-window folder tree from cache if a folder was created via the picker while
    /// a modal was open. Safe to call unconditionally; a no-op when nothing is pending. Callers must
    /// invoke this only after the modal picker has closed (its message loop is dead).
    /// </summary>
    public void CommitPendingFolderTreeRebuild()
    {
        if (!_folderTreeRebuildPending) return;
        _folderTreeRebuildPending = false;
        RebuildFolderListFromCache();
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
    /// <summary>Deletes the folder. Returns true if the deletion happened, false if it was
    /// cancelled at the confirmation prompt, pre-condition-failed, or errored.</summary>
    public async Task<bool> DeleteFolderAsync(FolderTreeNode node)
    {
        if (node.Folder == null || node.IsHeader) return false;

        if (ConfirmationRequested?.Invoke(
            $"Delete the folder '{node.Label}' and move all its messages to Trash?",
            "Delete Folder") != true) return false;

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

            // Remove the folder immediately rather than via a server re-fetch — Graph is eventually
            // consistent and can still return the just-deleted folder for a brief window. Drop it
            // from the flat cache (so a later full rebuild stays correct) and splice the node out of
            // the live tree in place, which preserves every other folder's expansion state and lets
            // the View land focus on the neighbour (a full rebuild would collapse and reset focus).
            RemoveFolderFromCacheOptimistically(node.Folder.AccountId, node.Folder);
            RemoveNodeFromTree(node);
            StatusText = $"Folder '{node.Label}' deleted.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to delete folder: {ex.Message}";
            LogService.Log("DeleteFolder", ex);
            return false;
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
            // Own token per move (same rationale as delete) — a follow-up action no longer cancels
            // this move's in-flight IMAP work. Cancels only at app shutdown. (#311)
            using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(_messageActionShutdownCts.Token);
            var ct = actionCts.Token;

            var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
            foreach (var group in groups)
            {
                var uids = group.Select(m => m.MessageId).ToList();
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids,
                    destination.FullName, ct);
                if (!OnlineMode)
                    await _localStore.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName, uids);
            }

            // Moving an unread message changes both the source and destination folder counts; refresh
            // after the server move lands (only when an unread message actually moved) (#227 follow-up).
            if (messages.Any(m => !m.IsRead))
                foreach (var acctId in messages.Select(m => m.AccountId).Distinct())
                    ScheduleFolderCountRefresh(acctId);

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
                    group.Select(m => m.MessageId).ToList(),
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

#pragma warning disable CA1822 // [RelayCommand] target must be an instance method for the MVVM Toolkit source generator
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
#pragma warning restore CA1822

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
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0]; // newest first
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAllConversationAsync(ConversationGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task ForwardConversationAsync(ConversationGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        await Forward();
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await DeleteMessagesAsync(group.Messages);
    }

    [RelayCommand]
    private async Task ArchiveConversationAsync(ConversationGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await ArchiveMessagesAsync(group.Messages);
    }

    // ── ToGroup commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteToGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await DeleteMessagesAsync(group.Messages);
    }

    [RelayCommand]
    private async Task ArchiveToGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await ArchiveMessagesAsync(group.Messages);
    }

#pragma warning disable CA1822 // [RelayCommand] target must be an instance method for the MVVM Toolkit source generator
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
#pragma warning restore CA1822

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
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAllToGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task ForwardToGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        await Forward();
    }

    // ── SenderGroup context menu commands ─────────────────────────────────────

#pragma warning disable CA1822 // [RelayCommand] target must be an instance method for the MVVM Toolkit source generator
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
#pragma warning restore CA1822

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
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAllSenderGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task ForwardSenderGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        SelectedMessage = group.Messages[0];
        await Forward();
    }

    [RelayCommand]
    private async Task DeleteSenderGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await DeleteMessagesAsync(group.Messages);
    }

    [RelayCommand]
    private async Task ArchiveSenderGroupAsync(SenderGroup? group)
    {
        if (group == null || group.Messages.Count == 0) return;
        await ArchiveMessagesAsync(group.Messages);
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

    private void SetActiveFlagFilterId(string? id)
    {
        _activeFlagFilterId = id;
        OnPropertyChanged(nameof(ActiveFlagFilterId));
        OnPropertyChanged(nameof(IsFilterAllFlagged));
    }

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
            "tome"        => MessageFilter.ToMe,
            "flagged"     => MessageFilter.Flagged,
            _             => MessageFilter.All,
        };
        // Clear any named-flag sub-filter from a previously applied saved view
        // so the user sees all flagged messages, not just one specific flag.
        SetActiveFlagFilterId(null);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SetFlagFilterAsync(string flagId)
    {
        ActiveFilter = MessageFilter.Flagged;
        SetActiveFlagFilterId(flagId);
        ApplyFiltersAndSearch();
        return Task.CompletedTask;
    }

    // ── Sort command ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetSort(string? sort)
    {
        ActiveSort = ConfigModel.ParseSort(sort);
    }

#pragma warning disable CA1822 // [RelayCommand] target must be an instance method for the MVVM Toolkit source generator
    [RelayCommand]
    private void ViewUserGuide()
    {
        // All ShellExecute launches go through the allow-list. See ExternalUriPolicy.
        Helpers.ExternalUriPolicy.TryOpenExternal("https://kellylford.github.io/QuickMail/");
    }
#pragma warning restore CA1822

    // ── Attachment commands ─────────────────────────────────────────────────────

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
                    MessageDetail.MessageId, att.PartSpecifier, cts.Token);
            }
            catch (Exception ex)
            {
                StatusText = $"Download failed: {ex.Message}";
                IsBusy = false;
                return;
            }
            IsBusy = false;
        }

        // Sanitized: a crafted server-supplied name with path separators or invalid
        // characters must not reach the dialog (or steer the write outside the chosen folder).
        var savePath = SaveFilePathRequested?.Invoke(AttachmentSafety.SanitizeFileName(att.FileName));
        if (savePath == null) return;
        await File.WriteAllBytesAsync(savePath, att.Content!);
        StatusText = $"Saved {att.FileName}.";
    }

    [RelayCommand]
    private async Task SaveAllAttachmentsAsync()
    {
        if (MessageDetail == null || MessageDetail.Attachments.Count == 0) return;

        var folder = SaveFolderPathRequested?.Invoke("Choose folder to save attachments");
        if (folder == null) return;

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
                        MessageDetail.MessageId, att.PartSpecifier, cts.Token);

                if (att.Content != null)
                {
                    // Sanitized so a crafted server-supplied filename can't write
                    // outside the chosen save folder.
                    var safeFileName = AttachmentSafety.SanitizeFileName(att.FileName);
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
                    MessageDetail.MessageId, att.PartSpecifier, cts.Token);
            }
            catch (Exception ex)
            {
                StatusText = $"Download failed: {ex.Message}";
                IsBusy = false;
                return;
            }
            IsBusy = false;
        }

        // Sanitized so a crafted server-supplied name (e.g. "../../Startup/evil.exe")
        // can't escape the temp folder.
        var safeFileName = AttachmentSafety.SanitizeFileName(att.FileName);

        if (AttachmentSafety.IsDangerousExtension(safeFileName))
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

    /// <summary>
    /// The accounts that need a (re)connect: those the backend doesn't have registered, OR whose
    /// folders aren't cached in the VM. Checking the backend (not just cached folders) is what
    /// re-registers an account dropped by a mid-session re-consent without an app restart (#219).
    /// Pure/static so the reconnect condition is unit-testable independent of the async connect loop.
    /// </summary>
    internal static List<AccountModel> AccountsNeedingConnect(
        IEnumerable<AccountModel> accounts,
        Func<Guid, bool> isBackendConnected,
        Func<Guid, bool> hasCachedFolders)
        => accounts.Where(a => !isBackendConnected(a.Id) || !hasCachedFolders(a.Id)).ToList();

    public void RefreshAccountList()
    {
        LoadAccountList();

        // Rebuild the folder tree from what's already cached so per-account changes that don't need a
        // reconnect are reflected immediately — notably a calendar-sync opt-in/out (#282), which
        // adds or removes that account's Calendar node (BuildFolderTree filters on SyncCalendar).
        // The reconnect loop below rebuilds again for any account that actually needs connecting.
        RebuildFolderListFromCache();

        // A newly added / re-opted-in account's calendars are discovered by the calendar sync pass,
        // which otherwise only runs every 15 minutes. Kick it now so its calendar node and any
        // per-calendar sub-nodes appear promptly rather than after the next pass or a restart (#282).
        TriggerCalendarSyncSoon();

        // Reconnect any account that isn't truly connected in its backend, OR whose folders aren't
        // cached in the VM (e.g. a newly added account). Checking the backend (_imap.IsConnected) —
        // not just _cachedFolders — is what catches an account that was re-consented / re-authed
        // mid-session: its VM state can look present while GraphMailService/ImapMailService dropped
        // it, so folder ops fail "…is not connected" until an app restart (#219). Reconnecting it
        // here fixes that without a restart. _cachedFolders is UI-thread-owned: read it on the UI
        // thread and marshal every write back through _ui so the background loop never touches it.
        var accountsToConnect = AccountsNeedingConnect(
            Accounts, _imap.IsConnected, id => _cachedFolders.ContainsKey(id));
        _ = Task.Run(async () =>
        {
            foreach (var account in accountsToConnect)
            {
                var result = await ConnectOneAccountAsync(account);
                _ui.Invoke(() =>
                {
                    ApplyAccountStatus(account, result.Folders);
                    if (result.Folders != null)
                    {
                        _cachedFolders[result.Id] = result.Folders;
                        RebuildFolderListFromCache();
                    }
                });
            }

            // Start the delta-poll/IDLE watcher for any newly-connected account and refresh the status
            // labels — previously a runtime-added account connected but was never polled for new mail.
            // WireUpWatchers also (re)subscribes the reachability handler against the fresh, live
            // account list, which is what the old inline block here did for issue #126.
            _ui.Invoke(WireUpWatchers);
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

    /// <summary>True when the open message contains a calendar invite that can be responded to.</summary>
    public bool HasCalendarInvite => IsMessageOpen
        && MessageDetail?.CalendarInvite != null
        && !string.Equals(MessageDetail.CalendarInvite.Method, "CANCEL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds an accessible HTML event card for display in the WebView2 reading pane.
    /// The card is prepended to the message body HTML by the View.
    /// </summary>
    public string BuildEventCardHtml()
    {
        var invite = MessageDetail?.CalendarInvite;
        if (invite == null) return string.Empty;

        // Card colors come from the resolved theme as hex strings (IThemeService
        // never exposes UI types). Fallbacks match the Parchment light palette for
        // tests that run without a theme service.
        var theme = _themeService?.ResolvedTheme;
        string Color(string token, string fallback) => theme?.ColorOf(token) ?? fallback;
        var cardBorder = Color("border", "#D8D4CC");
        var cardBg     = Color("surfaceBackground", "#F5F3EF");
        var cardText   = Color("textPrimary", "#1F2328");

        var sb = new System.Text.StringBuilder();
        sb.Append($"<div style=\"border:1px solid {cardBorder};border-radius:6px;padding:12px;margin:0 0 16px 0;background:{cardBg};color:{cardText};font-family:Segoe UI,Arial,sans-serif;font-size:13px;line-height:1.45;\" role=\"region\" aria-label=\"");
        sb.Append(System.Net.WebUtility.HtmlEncode(invite.DisplaySummary));
        sb.Append("\">");
        sb.Append("<div style=\"font-weight:bold;font-size:15px;margin-bottom:8px;\">Event Invitation</div>");

        // Cancellation notice — shown instead of the accept/decline buttons when
        // the organizer sent METHOD:CANCEL.
        var isCancel = string.Equals(invite.Method, "CANCEL", StringComparison.OrdinalIgnoreCase);
        if (isCancel)
        {
            sb.Append($"<div style=\"font-weight:bold;color:{Color("error", "#B3261E")};margin-bottom:8px;\">This event has been cancelled by the organizer.</div>");
        }

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

        // Buttons: Accept, Tentative, Decline — hidden for cancellations. Each uses
        // its status color's pale background tint with the dark status text partner
        // and a 1px status border, readable in light and dark themes alike; the
        // verb text (not color) carries the meaning.
        if (!isCancel)
        {
            void AppendButton(string href, string ariaLabel, string label, string fg, string bg, bool last = false)
            {
                sb.Append($"<a href=\"{href}\" role=\"button\" aria-label=\"{ariaLabel}\" ");
                sb.Append($"style=\"display:inline-block;padding:6px 14px;{(last ? "" : "margin-right:8px;")}margin-bottom:4px;");
                sb.Append($"background:{bg};color:{fg};border:1px solid {fg};border-radius:4px;text-decoration:none;font-weight:600;\">{label}</a>");
            }

            sb.Append("<div style=\"margin-top:8px;\">");
            AppendButton("quickmail:ics-accept", "Accept invitation", "Accept",
                Color("success", "#2E6B3E"), Color("successBackground", "#E9F3EC"));
            AppendButton("quickmail:ics-tentative", "Tentatively accept invitation", "Tentative",
                Color("warning", "#8A5A00"), Color("warningBackground", "#FBF3E2"));
            AppendButton("quickmail:ics-decline", "Decline invitation", "Decline",
                Color("error", "#B3261E"), Color("errorBackground", "#FBEAE9"), last: true);
            sb.Append("</div>");
        }

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

        await SendIcsReplyForAsync(invite, account, partStat, actionLabel,
            MessageDetail!.MessageId, MessageDetail!.FolderName);
    }

    /// <summary>
    /// Responds to a pending meeting invitation directly from the calendar list (Accept / Tentative /
    /// Decline) without opening the source email. Loads the invite from its source message (local
    /// cache first, IMAP fallback) and routes the reply through the account that RECEIVED the invite
    /// (<paramref name="evt"/>.AccountId) \u2014 never a default account (see issue #296).
    /// </summary>
    public async Task RespondToCalendarInviteAsync(CalendarEvent evt, string partStat, string actionLabel)
    {
        if (evt == null) return;

        if (string.IsNullOrEmpty(evt.SourceMessageId))
        {
            Announce("The original invitation email is no longer available, so a response can't be sent.",
                     AnnouncementCategory.Result);
            return;
        }

        // Route strictly through the account that received the invite (#296 wrong-account routing).
        var account = Accounts.FirstOrDefault(a => a.Id == evt.AccountId);
        if (account == null)
        {
            Announce("Cannot send calendar response: account not found.", AnnouncementCategory.Result);
            return;
        }

        MailMessageDetail? detail;
        try
        {
            detail = await _localStore.LoadDetailAsync(evt.AccountId, evt.SourceFolder, evt.SourceMessageId)
                     ?? await _imap.GetMessageDetailAsync(evt.AccountId, evt.SourceFolder, evt.SourceMessageId);
        }
        catch (Exception ex)
        {
            LogService.Log("RespondToCalendarInvite: load source", ex);
            Announce("The original invitation email couldn't be opened, so a response can't be sent.",
                     AnnouncementCategory.Result);
            return;
        }

        var invite = detail?.CalendarInvite;
        if (invite == null)
        {
            Announce("The original invitation email is no longer available, so a response can't be sent.",
                     AnnouncementCategory.Result);
            return;
        }

        await SendIcsReplyForAsync(invite, account, partStat, actionLabel,
            evt.SourceMessageId, evt.SourceFolder);
    }

    /// <summary>
    /// Core ICS reply logic shared by the reading-pane RSVP buttons and the calendar-list response
    /// menu. Generates the REPLY, sends it from <paramref name="account"/> (the account that RECEIVED
    /// the invite \u2014 never a default), announces the outcome, and updates the calendar row's
    /// response status so the calendar reflects the reply immediately.
    /// </summary>
    private async Task SendIcsReplyForAsync(IcsModel invite, AccountModel account, string partStat,
        string actionLabel, string sourceMessageId, string sourceFolder)
    {
        try
        {
            var attendeeName = account.SenderDisplayName;
            var attendeeEmail = account.Username;
            var icsContent = invite.GenerateReply(attendeeEmail, attendeeName, partStat);

            var password = _credentials.GetPassword(account.Id);
            await _smtp.SendIcsReplyAsync(icsContent, account, password, invite.Organizer ?? "");

            var eventTitle = invite.Summary ?? "calendar event";
            Announce($"Calendar response sent: {actionLabel} \u2014 {eventTitle}.", AnnouncementCategory.Result);

            // Update the calendar event's response status so the calendar pane reflects the reply.
            if (_calendarService != null && !string.IsNullOrEmpty(invite.Uid))
            {
                var status = partStat switch
                {
                    "ACCEPTED"  => CalendarResponseStatus.Accepted,
                    "DECLINED"  => CalendarResponseStatus.Declined,
                    "TENTATIVE" => CalendarResponseStatus.Tentative,
                    _           => CalendarResponseStatus.Pending,
                };

                // Upsert the event directly from the invite data so it appears in the
                // calendar immediately, even if the harvest hasn't run yet. The upsert
                // preserves any existing response_status (ON CONFLICT does not touch it),
                // so we set the status explicitly afterwards.
                var evt = new CalendarEvent
                {
                    Uid              = invite.Uid,
                    AccountId        = account.Id,
                    Summary          = invite.Summary ?? string.Empty,
                    Description      = invite.Description ?? string.Empty,
                    Location         = invite.Location ?? string.Empty,
                    Organizer        = invite.Organizer ?? string.Empty,
                    OrganizerName    = invite.OrganizerName ?? string.Empty,
                    StartTimeTicks   = invite.StartTime?.ToUniversalTime().Ticks,
                    EndTimeTicks     = invite.EndTime?.ToUniversalTime().Ticks,
                    Sequence         = invite.Sequence,
                    Method           = invite.Method,
                    SourceMessageId  = sourceMessageId,
                    SourceFolder     = sourceFolder,
                    ResponseStatus   = status,
                };
                await _calendarService.UpsertEventAsync(evt);
                // SetResponseStatusAsync updates the persisted row + in-memory list.
                // Needed because the upsert's ON CONFLICT clause does not overwrite
                // response_status (by design — harvest must not clobber user replies).
                await _calendarService.SetResponseStatusAsync(invite.Uid, account.Id, status);
                CalendarVm?.ApplyFiltersFromExternalUpdate();
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"SendIcsReply ({partStat})", ex);
            Announce($"Failed to send calendar response: {ex.Message}", AnnouncementCategory.Result);
        }
    }

    // ── Calendar folder activation ───────────────────────────────────────────────

    /// <summary>
    /// Raised when the calendar list should receive focus (View concern).
    /// The View subscribes and moves focus to the calendar event list.
    /// </summary>
    public event Action? CalendarPaneFocusRequested;

    /// <summary>
    /// Opens the calendar by selecting the Calendar virtual folder, exactly as if
    /// the user had pressed Enter on it in the folder tree. Bound to Ctrl+Shift+C.
    /// </summary>
    [RelayCommand]
    private async Task OpenCalendarAsync()
    {
        if (CalendarVm == null) return;
        await SelectFolderCommand.ExecuteAsync(CalendarFolder);
    }

    /// <summary>
    /// Opens the source invite message for a calendar event. Constructs a minimal
    /// <see cref="MailMessageSummary"/> stub and routes through <see cref="SelectMessageCommand"/>
    /// so the user's MessageOpenMode (ReadingPane/Tab/Window) is honored and SelectedAccount is
    /// resolved by the existing SelectMessageAsync logic (no duplicate account lookup needed here).
    /// Called by the View in response to <see cref="CalendarViewModel.OpenSourceMessageRequested"/>.
    /// </summary>
    internal void OpenCalendarSourceMessage(Guid accountId, string folder, string messageId)
    {
        LogService.Debug($"[CALENDAR] OpenCalendarSourceMessage accountId={accountId} folder={folder} messageId={messageId}");

        var summary = new MailMessageSummary
        {
            MessageId   = messageId,
            AccountId   = accountId,
            FolderName  = folder,
            Subject     = "Calendar invitation", // fallback; replaced when detail loads
        };
        SelectMessageCommand.Execute(summary);
    }

    /// <summary>
    /// Schedules a debounced calendar harvest 2 seconds after the last FolderSynced event.
    /// Runs on the UI thread via Dispatcher so the CalendarService refresh is safe.
    /// </summary>
    private void ScheduleCalendarHarvest()
    {
        if (_calendarService == null || CalendarVm == null || OnlineMode) return;

        // Reset the timer — if a previous harvest was pending, it gets pushed back 2s.
        _calendarHarvestTimer ??= new System.Threading.Timer(_ =>
        {
            _ui.Post(async () =>
            {
                if (_calendarService == null || CalendarVm == null) return;
                await _calendarService.RefreshAsync();
                // Only re-apply filters if the calendar view is active (no UI churn otherwise).
                if (IsCalendarView)
                    CalendarVm.ApplyFiltersFromExternalUpdate();
            });
        }, null, Timeout.Infinite, Timeout.Infinite);

        _calendarHarvestTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    // ── Graph calendar sync (read-down v1) ────────────────────────────────────────

    /// <summary>
    /// Starts the periodic Graph calendar pull: an immediate first pass (called after the startup
    /// mail sync completes, so OAuth tokens are warm and the token acquisition is silent), then
    /// every 15 minutes. Idempotent — a repeat call just restarts the schedule.
    /// </summary>
    private void StartGraphCalendarSyncTimer()
    {
        if (_graphCalendarSync == null || _calendarService == null || OnlineMode) return;

        _graphCalendarSyncTimer ??= new System.Threading.Timer(
            _ => _ui.Post(() => _ = RunGraphCalendarSyncAsync()), null, Timeout.Infinite, Timeout.Infinite);
        _graphCalendarSyncTimer.Change(TimeSpan.Zero, GraphCalendarSyncInterval);
    }

    /// <summary>
    /// Nudges the calendar sync timer to fire immediately (then resume its normal 15-minute
    /// cadence). Called when the account list changes so a newly added — or newly calendar-opted-in
    /// (#282) — account's calendars, and their per-calendar sub-nodes, appear right away instead of
    /// only after the next timer pass or an app restart. No-op if the timer isn't running yet
    /// (no calendar service, online mode, or startup hasn't reached StartGraphCalendarSyncTimer).
    /// </summary>
    private void TriggerCalendarSyncSoon()
        => _graphCalendarSyncTimer?.Change(TimeSpan.Zero, GraphCalendarSyncInterval);

    /// <summary>
    /// One Graph calendar sync pass: pulls every Graph account's primary calendar into the local
    /// store (replace-slice), then reloads the in-memory calendar. Announces the result (Status
    /// category) only while the calendar view is active, so background passes stay silent.
    /// Runs on the UI thread; overlapping passes (timer vs. F5) are skipped, not queued.
    /// </summary>
    /// <summary>
    /// Reloads the distinct per-account calendar sources from the local store into
    /// <see cref="_calendarSources"/> (best-effort; leaves the prior list on failure). Callers rebuild
    /// the folder tree afterward. No-op without a calendar service or in online mode.
    /// </summary>
    private async Task ReloadCalendarSourcesAsync()
    {
        if (_calendarService == null || OnlineMode) { _calendarSources = []; return; }
        try { _calendarSources = await _localStore.LoadCalendarSourcesAsync(); }
        catch (Exception ex) { LogService.Log("LoadCalendarSources", ex); }
    }

    private async Task RunGraphCalendarSyncAsync(bool refreshAndAnnounce = true)
    {
        if (_graphCalendarSync == null || _calendarService == null || OnlineMode) return;
        if (_graphCalendarSyncRunning) return;
        _graphCalendarSyncRunning = true;
        try
        {
            ReplaceCts(ref _graphCalSyncCts, out var ct);
            var result = await _graphCalendarSync.SyncAllAsync(ct);
            // Refresh the per-calendar source list and rebuild the tree so newly discovered calendars
            // appear as their own nodes (and vanished ones drop off).
            await ReloadCalendarSourcesAsync();
            BuildFolderTree();
            // Nothing eligible (no Graph accounts) or nothing pulled — leave the calendar alone.
            if (result.AccountsSynced == 0) return;
            if (!refreshAndAnnounce) return; // F5 path: CalendarVm.RefreshAsync reloads + announces

            await _calendarService.RefreshAsync(ct);
            if (IsCalendarView)
            {
                CalendarVm?.ApplyFiltersFromExternalUpdate();
                var n = result.EventsFetched;
                Announce($"Calendar sync complete. {n} event{(n == 1 ? "" : "s")}.",
                         AnnouncementCategory.Status);
            }
        }
        catch (OperationCanceledException) { /* shutdown or superseded pass — normal */ }
        catch (Exception ex)
        {
            // Best-effort background work: log, never surface a modal or break the caller.
            LogService.Log("GraphCalendarSync (background pass)", ex);
        }
        finally
        {
            _graphCalendarSyncRunning = false;
        }
    }

    // ── Calendar reminders ────────────────────────────────────────────────────────

    private System.Threading.Timer? _reminderTimer;
    private readonly HashSet<(string Uid, DateTime Start)> _firedReminders = [];
    internal bool RemindersEnabled;          // pushed from config at startup and ApplySettings
    internal int ReminderLeadMinutes = 10;

    /// <summary>Starts the once-a-minute reminder check (no-op without a calendar service).</summary>
    private void StartReminderTimer()
    {
        if (_calendarService == null || OnlineMode) return;
        _reminderTimer = new System.Threading.Timer(
            _ => _ui.Post(CheckReminders), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Fires one reminder per appointment occurrence whose start falls within the lead window.
    /// Recurring series are expanded over the window; each (uid, start) fires at most once per run
    /// of the app. Reminders are opt-in (CalendarReminders, default off).
    /// </summary>
    internal void CheckReminders()
    {
        if (!RemindersEnabled || _calendarService == null) return;

        var now = DateTime.Now;
        var windowEnd = now.AddMinutes(ReminderLeadMinutes);

        foreach (var e in _calendarService.Events)
        {
            if (!e.StartTime.HasValue) continue;
            if (e.ResponseStatus is CalendarResponseStatus.Declined or CalendarResponseStatus.Cancelled) continue;

            var rule = e.IsRecurring ? RecurrenceRule.Parse(e.RecurrenceRule) : null;
            IEnumerable<DateTime> starts;
            if (rule != null)
            {
                var excluded = new HashSet<DateTime>(e.GetExDates());
                starts = Helpers.RecurrenceExpander
                    .Expand(e.StartTime.Value, rule, now, windowEnd)
                    .Where(s => !excluded.Contains(s));
            }
            else
            {
                starts = e.StartTime.Value > now && e.StartTime.Value <= windowEnd
                    ? [e.StartTime.Value] : [];
            }

            foreach (var start in starts)
            {
                if (!_firedReminders.Add((e.Uid, start))) continue;
                var minutes = Math.Max(1, (int)Math.Round((start - now).TotalMinutes));
                var title = string.IsNullOrWhiteSpace(e.Summary) ? "Appointment" : e.Summary;
                var body = $"In {minutes} minute{(minutes == 1 ? "" : "s")}, at {start:t}"
                           + (string.IsNullOrWhiteSpace(e.Location) ? "" : $". {e.Location}");
                _notifications?.ShowInfo(title, body);
                Announce($"Reminder. {title} in {minutes} minute{(minutes == 1 ? "" : "s")}, at {start:t}.",
                         AnnouncementCategory.Result);
            }
        }

        // Keep the fired set from growing without bound across long sessions.
        if (_firedReminders.Count > 500)
            _firedReminders.RemoveWhere(f => f.Start < now.AddDays(-1));
    }

    // Releases landing page — used when no specific update is known so the always-present
    // "No updates available" Help entry still takes users somewhere useful.
    private const string ReleasesPageUrl = UpdateCheckService.ReleasesPageUrl;

    // Version string of a found update (e.g. "0.8.1"); empty when up to date. Feeds the
    // update dialog for self-updating (installed) copies.
    private string _updateVersion = string.Empty;

    /// <summary>
    /// Raised for self-updating (installed) copies when the Help update entry is activated:
    /// the View shows the QuickMail Update dialog (restart now / what's new / dismiss).
    /// Portable copies never raise this — they open the release page instead, because
    /// updating them really is a manual download.
    /// </summary>
    public event EventHandler<(string Version, string WhatsNewUrl)>? UpdateDialogRequested;

    /// <summary>
    /// Raised on the first launch after an update was applied: the View shows the
    /// "QuickMail Update Installed" dialog. Gated by the ShowUpdateInstalledAlerts setting.
    /// </summary>
    public event EventHandler<(string Version, string WhatsNewUrl)>? UpdateInstalledDialogRequested;

    /// <summary>
    /// Detects that an update was applied since the previous run (recorded LastRunVersion
    /// differs from the running version) and raises the update-installed notice. Both the
    /// recording and the notice are installed-copies-only: a portable or dev run on the same
    /// profile must neither trigger a phantom notice on the next installed launch nor leave
    /// a record that suppresses/creates one. Called once per launch by the View after the
    /// main window has loaded; <paramref name="dialogAllowed"/> is false on the no-account
    /// startup path, where the version is stamped but no dialog may stack on onboarding.
    /// </summary>
    public void MaybeShowUpdateInstalledNotice(bool dialogAllowed = true)
    {
        // Version hops outside a Velopack install are dev/portable swaps, not updates —
        // don't record them either, or an installed↔portable alternation on the shared
        // default profile announces updates that never happened.
        if (!Helpers.VelopackRuntime.IsInstalled) return;

        var cfg = _configService.Load();
        if (cfg.LastRunVersion == CurrentVersion) return;

        var previous = cfg.LastRunVersion;
        cfg.LastRunVersion = CurrentVersion;
        _configService.Save(cfg);

        if (!dialogAllowed) return;
        // No previous record: first run ever (or first run of a version that tracks this) —
        // nothing was "installed" from the user's point of view.
        if (string.IsNullOrEmpty(previous)) return;
        // Only a forward move is an update; a downgrade (rollback install) must not be
        // announced as one. Unparseable records fail closed (no dialog).
        if (!Version.TryParse(previous, out var prev) ||
            !Version.TryParse(CurrentVersion, out var current) ||
            current <= prev)
            return;
        if (!cfg.ShowUpdateInstalledAlerts) return;

        UpdateInstalledDialogRequested?.Invoke(this,
            (CurrentVersion, UpdateCheckService.ReleaseTagUrl(CurrentVersion)));
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (_updateCheckService?.SelfUpdatePending == true && !string.IsNullOrEmpty(_updateVersion))
        {
            // Installed copy: the update is already downloading/downloaded — sending the user
            // to the release page would wrongly suggest a manual download is needed.
            UpdateDialogRequested?.Invoke(this, (_updateVersion, UpdateReleaseUrl));
            return;
        }

        // The specific release when one was found; otherwise the general releases page.
        // UpdateReleaseUrl comes from the GitHub API response, so route it through the
        // external-URI allow-list like any other externally sourced link.
        var url = string.IsNullOrEmpty(UpdateReleaseUrl) ? ReleasesPageUrl : UpdateReleaseUrl;
        Helpers.ExternalUriPolicy.TryOpenExternal(url);
    }

    /// <summary>
    /// Applies the downloaded update and restarts QuickMail. On success the process exits
    /// inside this call. On failure the user hears an accurate outcome and the app keeps
    /// running; a cancellation (the update dialog was dismissed) is silent. The token lets
    /// the dialog retract a restart that is waiting on a slow download.
    /// </summary>
    public async Task RestartToUpdateAsync(CancellationToken cancellationToken)
    {
        if (_updateCheckService is null) return;
        var ok = await _updateCheckService.RestartToUpdateAsync(cancellationToken);
        if (!ok && !cancellationToken.IsCancellationRequested)
            // No promise of a next-start install: when the download failed, nothing is
            // staged — the next launch re-checks and tries the download again.
            Announce("The update could not be applied right now. QuickMail will try again the next time it starts.", AnnouncementCategory.Result);
    }

    // Startup check: silent when already up to date (announcing "no updates" on every launch
    // would be chatter). Only a found update is announced; the menu entry reflects the result either way.
    public async Task CheckForUpdateInBackgroundAsync()
    {
        if (_updateCheckService is null) return;
        try
        {
            // Scoped token gives the caller an explicit cancellation bound. The service also
            // cancels its own internal token on Dispose (app exit), so either path stops the request.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await _updateCheckService.CheckForUpdateAsync(cts.Token);
            if (info is not null)
            {
                _updateVersion      = info.Version;
                UpdateAvailableText = $"Update available: v{info.Version}";
                UpdateReleaseUrl    = info.HtmlUrl;
                // Result, not Status: a one-time discovery outcome. Users who silence background
                // Status chatter (the main reason that setting exists) must still hear this.
                Announce($"QuickMail update available: version {info.Version}. Check the Help menu.", AnnouncementCategory.Result);
            }
            else
            {
                _updateVersion      = string.Empty;
                UpdateAvailableText = NoUpdateText;
                UpdateReleaseUrl    = string.Empty;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"CheckForUpdate: {ex.Message}");
        }
    }
}
