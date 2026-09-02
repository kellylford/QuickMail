using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    // Verifies, independently of the app's own connection state, whether an account shown as
    // disconnected really is. Optional so tests and the stub-service constructors are unaffected.
    private readonly ConnectionTruthProbe? _truthProbe;
    private readonly IScreenshotCaptureService? _screenshotCapture;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly ILocalStoreService _localStore;
    private readonly IOAuthService _oauthService;
    private readonly ISyncService _syncService;
    private readonly IContactSyncService? _contactSync;
    private readonly IConfigService _configService;
    private readonly IViewService _viewService;
    /// <summary>Per-folder presentation memory (#520). Null in tests that do not exercise it.</summary>
    private readonly IFolderViewStateService? _folderViewState;
    private readonly ICommandRegistry _commandRegistry;
    private readonly IRuleService _ruleService;
    private readonly ISendMailService _smtp;
    private readonly IFlagService? _flagService;
    // Null in tests that construct the VM without it; every use site treats a null service as
    // "nothing is watched", mirroring how _flagService degrades in ResolveFlagNamesAsync.
    private readonly IWatchService? _watchService;
    private readonly ICalendarService? _calendarService;
    private readonly IGraphCalendarSyncService? _graphCalendarSync;

    // Distinct per-account server calendars (from the local store), cached on the UI thread so the
    // synchronous BuildFolderTree can add a grandchild node per calendar. Refreshed after each
    // calendar sync and on initial load.
    private IReadOnlyList<CalendarSourceInfo> _calendarSources = [];
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
    /// How long after a pull opening the calendar will not start another one. Opening the calendar
    /// is a deliberate action a user can repeat freely — out to the mail list and back — and each
    /// one would otherwise be a fresh round of Graph and Google requests.
    /// </summary>
    private static readonly TimeSpan CalendarOpenSyncThrottle = TimeSpan.FromSeconds(30);
    private DateTime _lastCalendarPullUtc = DateTime.MinValue;

    /// <summary>What a finished calendar pull should do to the list the user is looking at.</summary>
    private enum CalendarSyncFollowUp
    {
        /// <summary>Reload and announce the count — the background timer's pass.</summary>
        RefreshAndAnnounce,

        /// <summary>Nothing: the caller reloads and announces for itself (the F5 path).</summary>
        CallerHandlesIt,

        /// <summary>
        /// Reload, and speak only if the reload actually changed the list — the open-the-calendar
        /// pass.
        ///
        /// <para>
        /// Opening the calendar has just announced the view and how many events it holds, so
        /// repeating that number a moment later is chatter. Staying silent unconditionally is not
        /// right either: when the pull does bring something down, the count the user was just given
        /// is now wrong and the list has grown underneath them, which is not something the platform
        /// reports. So: nothing at all in the common case where the server had nothing new, and one
        /// Status announcement when there is something to say.
        /// </para>
        /// </summary>
        RefreshAndAnnounceIfChanged,
    }

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
    private void OnScreenshotCaptureEnabledChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(WindowTitle));

    public void Dispose()
    {
        if (_screenshotCapture != null)
            _screenshotCapture.EnabledChanged -= OnScreenshotCaptureEnabledChanged;
        if (_outbox != null)
        {
            _outbox.Changed        -= OnOutboxChanged;
            _outbox.FlushCompleted -= OnOutboxFlushCompleted;
        }
        UnsubscribeConnectivity();
        DrainCts(ref _offlineRetryCts);
        if (_rowLayoutService != null && _onRowLayoutsChanged != null)
        {
            _rowLayoutService.LayoutsChanged -= _onRowLayoutsChanged;
            _onRowLayoutsChanged = null;
        }
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

    // Spoken field layouts for list rows. Optional so the stub-service test constructors and
    // --probe runs are unaffected; a null service means "shipped defaults".
    private readonly IRowLayoutService? _rowLayoutService;
    private EventHandler? _onRowLayoutsChanged;

    private string? _activeFlagFilterId;
    private EventHandler? _onFlagDefinitionsChanged;
    private Action<Guid, bool>? _onReachabilityChanged;

    // Folder lists per account. Since #516 this is ALSO seeded from the local store at startup, so
    // presence here no longer implies the account connected — see _connectedAccountIds below.
    private readonly Dictionary<Guid, List<MailFolderModel>> _cachedFolders = new();
    public IReadOnlyDictionary<Guid, List<MailFolderModel>> CachedFolders => _cachedFolders;

    /// <summary>
    /// Accounts that have actually connected this session. Before #516, <c>_cachedFolders.Count</c>
    /// carried this meaning — an account appeared there only after <c>GetFoldersAsync</c> succeeded.
    /// Now the dictionary is pre-filled from SQLite at launch so the startup folder can be resolved
    /// and the tree drawn offline, which would make that count report "connected" for accounts that
    /// never came up. Anything asking "did we connect?" must use this set, not the dictionary.
    /// </summary>
    private readonly HashSet<Guid> _connectedAccountIds = [];

    /// <summary>
    /// Set when <see cref="InitialLoadAsync"/> had a startup folder configured but no persisted
    /// folder list to resolve it against — the first launch after upgrading (the migration has just
    /// written one), a fresh <c>--profileDir</c>, or a rebuilt <c>mail.db</c>. Consumed once by
    /// <see cref="StartBackgroundSyncAsync"/> after the connect pass, so the user's choice is
    /// honoured in that same session rather than only from the next launch.
    /// </summary>
    private bool _startupFolderNeedsRetry;

    /// <summary>
    /// Records an account's folder list, marks it connected, and persists it so the next launch can
    /// resolve the startup folder and draw the tree before any network call (#516). Every write to
    /// <see cref="_cachedFolders"/> that comes from a live server fetch goes through here.
    /// The save is fire-and-forget: a failed cache write must never break the folder list in hand.
    /// </summary>
    private void SetCachedFolders(Guid accountId, List<MailFolderModel> folders)
    {
        _cachedFolders[accountId] = folders;
        _connectedAccountIds.Add(accountId);

        // Never let an empty result overwrite a good cache. SaveFoldersAsync is replace-all, so
        // persisting [] erases the account's folders — and an empty list is reachable from a
        // SUCCESSFUL call: ProbeOfflineMailService returns one by design, and a server can answer a
        // LIST with nothing during a partial outage. The in-memory cache still takes the empty value
        // (this session genuinely has no folders for that account), but the last good copy on disk
        // survives to be restored at the next launch. A real "this account has no folders at all" is
        // indistinguishable here and not worth the cost of getting the common case wrong.
        if (folders.Count == 0)
        {
            LogService.Debug($"SetCachedFolders: {accountId} returned no folders — keeping the stored list.");
            return;
        }

        // --online never initializes the local store, and the connection string is ReadWriteCreate,
        // so writing here would create an empty mail.db in the profile and then throw "no such
        // table: Folder" on every account. Every other store write in this class is guarded the
        // same way.
        if (OnlineMode) return;

        _localStore.SaveFoldersAsync(accountId, folders).LogFaults("persist folder list");
    }

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

    /// <summary>
    /// Every account's Archive destination merged into one list (issue #452). Unlike the other
    /// kind-scoped aggregates this resolves through <see cref="ResolveArchiveFolder"/>, so it lists
    /// exactly the folders <c>Move to Archive</c> writes to — including a per-account override that
    /// points at a folder the server does not flag as Archive.
    /// </summary>
    public static readonly MailFolderModel AllArchiveFolder = new()
    {
        FullName    = "\u0000AllArchive",
        DisplayName = "All Archive"
    };
    public static readonly MailFolderModel AllFlaggedFolder = new()
    {
        FullName    = "\u0000AllFlagged",
        DisplayName = "All Flagged"
    };

    /// <summary>
    /// Every message belonging to a conversation the user chose to watch (Ctrl+Shift+W), across all
    /// accounts and folders. Membership is a predicate over <see cref="IWatchService"/> rather than
    /// per-message state, so a reply that has not arrived yet is already a member — that is what
    /// makes a watch a subscription rather than a second kind of flag.
    /// </summary>
    public static readonly MailFolderModel AllWatchedFolder = new()
    {
        FullName    = "\u0000AllWatched",
        DisplayName = "Watched Conversations"
    };

    /// <summary>Virtual folder sentinel that opens the calendar event list.</summary>
    public static readonly MailFolderModel CalendarFolder = new()
    {
        FullName    = "\u0000Calendar",
        DisplayName = "Calendar"
    };

    // Per-source calendar children under the Calendar node: "\u0000Calendar:{guid}" for one
    // account, "local" for locally-authored appointments, "all" for the merged view, and
    // "{guid}|{escapedCalId}" for a single specific calendar of that account.
    internal const string CalendarSourcePrefix = "\u0000Calendar:";

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

    // ── Default calendar for new appointments (issue #497) ────────────────────────
    //
    // Stored as the calendar tree node's tail encoding ("local", "{guid}", "{guid}|{escapedCalId}")
    // rather than as a pair of fields, so the one string the user picked in the tree round-trips
    // through CalendarFilterFor with no second parser to keep in step. Empty = no preference.
    private string _defaultCalendarSource = string.Empty;

    /// <summary>The chosen default calendar as a filter, or null when the user has set no default.</summary>
    private CalendarFilter? DefaultCalendarFilter =>
        string.IsNullOrEmpty(_defaultCalendarSource)
            ? null
            : CalendarFilterFor(CalendarSourcePrefix + _defaultCalendarSource);

    /// <summary>
    /// Makes <paramref name="node"/> the calendar new appointments are created on, and returns the
    /// sentence the View reports. Refuses the nodes that are not one calendar: the bare Calendar
    /// node, which selects nothing, and All Calendars, which selects every source at once and so
    /// names no single place to save to.
    /// </summary>
    public string SetDefaultCalendar(FolderTreeNode? node)
    {
        if (node?.Folder is not { } folder || !IsCalendarFolderName(folder.FullName))
            return "Select a calendar in the folder tree first.";

        var tail = folder.FullName.StartsWith(CalendarSourcePrefix, StringComparison.Ordinal)
            ? folder.FullName[CalendarSourcePrefix.Length..]
            : string.Empty;
        if (tail.Length == 0 || tail == "all")
            return $"'{node.Label}' is not a single calendar, so it cannot be the default. "
                 + "Choose Local Calendar, an account, or one of its calendars.";

        _defaultCalendarSource = tail;
        PersistDefaultCalendar();
        return $"New appointments will be created on {node.Label}.";
    }

    /// <summary>
    /// Drops the default calendar, so the appointment editor opens on the local calendar again.
    /// Returns the sentence the View reports.
    /// </summary>
    public string ClearDefaultCalendar()
    {
        if (string.IsNullOrEmpty(_defaultCalendarSource))
            return "No default calendar is set. New appointments already start on Local Calendar.";
        _defaultCalendarSource = string.Empty;
        PersistDefaultCalendar();
        return "Default calendar cleared. New appointments will start on Local Calendar.";
    }

    /// <summary>
    /// Makes the given folder-tree node the startup folder (#516). Returns the sentence the View
    /// reports — status bar plus a Result announcement, the pairing every folder context-menu
    /// outcome uses.
    ///
    /// <para>Unlike the move/copy guard this accepts a top-level virtual aggregate: "open me in All
    /// Inboxes" is the single most-requested form of this setting. What it must still reject is
    /// anything that is not a place mail lives — account headers, calendar nodes, and the
    /// per-account All Mail sentinels — and it says why rather than doing nothing, because a menu
    /// item that silently no-ops is the dead end #250 was about.</para>
    /// </summary>
    public string SetStartupFolder(FolderTreeNode? node)
    {
        if (node is null || node.IsHeader || node.Folder is not { } folder)
            return "Select a folder in the folder tree first.";

        if (IsCalendarFolderName(folder.FullName))
            return $"'{node.Label}' is a calendar, not a mail folder, so QuickMail cannot start there.";

        var isVirtual = AllVirtualFolders.Any(v =>
            string.Equals(v.FullName, folder.FullName, StringComparison.Ordinal));

        if (!isVirtual &&
            (folder.AccountId == Guid.Empty || folder.FullName.Length == 0 || folder.FullName[0] == '\0'))
            return $"'{node.Label}' is a view, not a folder, so it cannot be the startup folder.";

        var cfg = _configService.Load();
        if (isVirtual)
        {
            // Stored without the NUL sentinel prefix — an INI file cannot carry one.
            cfg.StartupFolder        = folder.FullName[1..];
            cfg.StartupFolderAccount = string.Empty;
        }
        else
        {
            cfg.StartupFolder        = folder.FullName;
            cfg.StartupFolderAccount = folder.AccountId.ToString();
        }
        cfg.StartupFolderLabel = folder.DisplayName;
        _configService.Save(cfg);

        return $"QuickMail will open in {folder.DisplayName}.";
    }

    /// <summary>
    /// Drops the startup folder, so QuickMail opens in All Mail again. Returns the sentence the
    /// View reports.
    /// </summary>
    public string ClearStartupFolder()
    {
        var cfg = _configService.Load();
        if (string.IsNullOrEmpty(cfg.StartupFolder))
            return "No startup folder is set. QuickMail already opens in All Mail.";

        var previous = string.IsNullOrWhiteSpace(cfg.StartupFolderLabel)
            ? cfg.StartupFolder : cfg.StartupFolderLabel;
        cfg.StartupFolder        = string.Empty;
        cfg.StartupFolderAccount = string.Empty;
        cfg.StartupFolderLabel   = string.Empty;
        _configService.Save(cfg);

        return $"Startup folder '{previous}' cleared. QuickMail will open in All Mail.";
    }

    // ── Folder tree expansion (#590) ─────────────────────────────────────────
    // Expanding and collapsing was arrow-keys-only: there was no way to fold a whole account away,
    // and no way to reach either action from a menu, the context menu, or the Command Palette.
    //
    // Both operate on the branch, not one level. Right and Left arrow already do one level, so a
    // command that did the same would be a duplicate; what neither arrow can do is fold a folder
    // with nested subfolders back to a single line, or open one all the way down.

    /// <summary>
    /// Expands or collapses <paramref name="node"/> and every node beneath it.
    /// </summary>
    public static void SetFolderBranchExpanded(FolderTreeNode? node, bool expanded)
    {
        if (node == null) return;
        foreach (var n in FlattenAllNodes([node]))
            n.IsExpanded = expanded;
    }

    /// <summary>
    /// Expands or collapses every node in the folder tree, account headers included — collapsing
    /// all is how a many-account tree gets back to a list of accounts.
    /// </summary>
    public void SetAllFoldersExpanded(bool expanded)
    {
        if (FolderTree == null) return;
        foreach (var n in FlattenAllNodes(FolderTree))
            n.IsExpanded = expanded;
    }

    /// <summary>Whether the folder tree holds a node whose expansion state could be changed.</summary>
    public bool HasExpandableFolders =>
        FolderTree != null && FlattenAllNodes(FolderTree).Any(n => n.Children.Count > 0);

    private void PersistDefaultCalendar()
    {
        var cfg = _configService.Load();
        cfg.DefaultCalendarSource = _defaultCalendarSource;
        _configService.Save(cfg);
        if (CalendarVm != null) CalendarVm.DefaultCalendar = DefaultCalendarFilter;
        // The marker moves between existing node objects rather than rebuilding the tree, which
        // would replace every node and throw keyboard focus out of the item the user just acted on.
        MarkDefaultCalendarNodes();
    }

    /// <summary>Puts the "(default)" marker on the one calendar node that matches the setting.</summary>
    private void MarkDefaultCalendarNodes()
    {
        if (FolderTree == null) return;
        foreach (var n in FlattenAllNodes(FolderTree))
            if (n.IsCalendarNode && n.Folder is { } f)
                n.IsDefaultCalendar = _defaultCalendarSource.Length > 0
                    && string.Equals(f.FullName, CalendarSourcePrefix + _defaultCalendarSource,
                                     StringComparison.Ordinal);
    }

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
               || ProviderCatalog.IsICloud(a));

    // Sentinel prefix for per-account "All Mail" virtual folders, e.g. "\u0000AccountMail:{guid}".
    internal const string AccountMailPrefix = "\u0000AccountMail:";

    // Sentinel prefixes for saved-view virtual folders.
    internal const string ViewPrefix    = "\u0000View:";
    internal const string ViewAllPrefix = "\u0000ViewAll:";

    // Sentinel prefix for the "mail from / to this contact" results view launched from the
    // address book (issue #370), e.g. ContactMailPrefix + "from|bob%40example.com". The address
    // is percent-escaped so a '|' inside it can never be mistaken for the field separator.
    internal const string ContactMailPrefix = "\u0000ContactMail:";

    /// <summary>Which header the contact-mail results view matches an address against.</summary>
    public enum ContactMailDirection
    {
        /// <summary>Mail the contact sent (matches the From header).</summary>
        From,
        /// <summary>Mail addressed to the contact (matches the To header).</summary>
        To,
    }

    /// <summary>
    /// Builds the virtual folder that shows every cached message from (or to) one address.
    /// <paramref name="label"/> is the contact's display name when there is one; it appears in
    /// the window title and status bar, while the sentinel always carries the raw address.
    /// </summary>
    public static MailFolderModel CreateContactMailVirtualFolder(
        string address, ContactMailDirection direction, string? label = null)
    {
        var who  = string.IsNullOrWhiteSpace(label) ? address : label!.Trim();
        var kind = direction == ContactMailDirection.From ? "from" : "to";
        return new MailFolderModel
        {
            FullName    = $"{ContactMailPrefix}{kind}|{Uri.EscapeDataString(address)}",
            DisplayName = $"Mail {kind} {who}",
        };
    }

    /// <summary>
    /// Extracts the address and direction from a contact-mail sentinel. Returns false for any
    /// other folder name, so callers can use it as the branch test for this view.
    /// </summary>
    private static bool TryGetContactMailFromSentinel(
        string? fullName, out string address, out ContactMailDirection direction)
    {
        address   = string.Empty;
        direction = ContactMailDirection.From;
        if (fullName == null || !fullName.StartsWith(ContactMailPrefix, StringComparison.Ordinal))
            return false;

        var tail = fullName[ContactMailPrefix.Length..];
        var sep  = tail.IndexOf('|', StringComparison.Ordinal);
        if (sep <= 0) return false;

        direction = tail[..sep].Equals("to", StringComparison.Ordinal)
            ? ContactMailDirection.To
            : ContactMailDirection.From;
        address = Uri.UnescapeDataString(tail[(sep + 1)..]);
        return address.Length > 0;
    }

    /// <summary>
    /// True when the message's From (or To) header names this address. The headers are display
    /// strings — a name plus an address, and a whole list of them for To — so the address is
    /// looked for inside them rather than compared whole.
    /// </summary>
    private static bool MatchesContactAddress(
        MailMessageSummary msg, string address, ContactMailDirection direction) =>
        HeaderNamesAddress(
            direction == ContactMailDirection.From ? msg.From : msg.To,
            address);

    /// <summary>
    /// True when <paramref name="header"/> contains <paramref name="address"/> as a whole address
    /// rather than as part of a longer one. A plain substring test would report a match for
    /// "bob@example.com" in both "notbob@example.com" and "bob@example.com.au", so each hit has to
    /// sit on an address boundary — anything but an address character on either side, which is what
    /// the surrounding "&lt;", "&gt;", quotes, commas, and spaces in a header supply.
    /// </summary>
    internal static bool HeaderNamesAddress(string header, string address)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(address)) return false;

        var i = header.IndexOf(address, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var end = i + address.Length;
            if ((i == 0 || !IsAddressChar(header[i - 1])) &&
                (end >= header.Length || !IsAddressChar(header[end])))
                return true;
            if (i + 1 >= header.Length) break;
            i = header.IndexOf(address, i + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // Characters that can appear inside an address (RFC 5322 atext plus '.' and '@'). Seeing one
    // immediately before or after a hit means the hit is only part of a longer address.
    private static bool IsAddressChar(char c) =>
        char.IsLetterOrDigit(c) ||
        c is '.' or '@' or '-' or '_' or '+' or '%' or '!' or '#' or '$' or '&' or '\''
          or '*' or '/' or '=' or '?' or '^' or '`' or '{' or '|' or '}' or '~';

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

    /// <summary>True for the synthetic folder a multi-folder saved view selects.</summary>
    private static bool IsViewSentinel(string? fullName) =>
        TryGetViewIdFromSentinel(fullName, out _) || TryGetViewAllIdFromSentinel(fullName, out _);

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

        // Address-book "mail from / to this contact" results (#370).
        if (TryGetContactMailFromSentinel(folder.FullName, out _, out _)) return true;

        if (folder.AccountId != Guid.Empty) return false;

        return string.Equals(folder.FullName, AllMailFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllInboxesFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllDraftsFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllSentFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllTrashFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllArchiveFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllFlaggedFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, OutboxFolder.FullName, StringComparison.Ordinal) ||
               string.Equals(folder.FullName, AllWatchedFolder.FullName, StringComparison.Ordinal) ||
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

    // ── List presentation state (#520) ────────────────────────────────────────────
    //
    // Grouping, filter, flag sub-filter, sort and day limit are resolved from three layers,
    // highest first:
    //
    //   1. the active saved view   — an explicit, temporary overlay
    //   2. the folder's own memory — what this folder was last given
    //   3. the global default      — config.ini ViewMode + Sort
    //
    // Every navigation and every deactivation applies the resolved record wholesale, so a
    // partial restore cannot be written by accident. Before this, "clear view" reset two of the
    // six properties "apply view" had set, which is issue #520.

    /// <summary>Set while a resolved state is being applied. Nothing persists to disk while it
    /// is set, and an active view is not detached — a programmatic apply is never a preference.</summary>
    private bool _applyingListState;

    /// <summary>Mirrors <c>ConfigModel.RememberViewPerFolder</c>; when false layer 2 is skipped.</summary>
    private bool _rememberViewPerFolder = true;

    /// <summary>Layer 3. Filter, flag sub-filter and day limit have no global form — they always
    /// start clear — so only Mode and Sort are read from config.</summary>
    private ListState _defaultListState = ListState.Default;

    private static ListState DefaultListStateFrom(ConfigModel cfg) => new(
        ConfigModel.ParseViewMode(cfg.ViewMode),
        MessageFilter.All,
        null,
        ConfigModel.ParseSort(cfg.Sort),
        null);

    /// <summary>The presentation currently on screen.</summary>
    private ListState CurrentListState =>
        new(ViewMode, ActiveFilter, _activeFlagFilterId, ActiveSort, ActiveDayLimit);

    private static ListState ToListState(SavedView view) => new(
        ConfigModel.ParseViewMode(view.ViewMode),
        ConfigModel.ParseFilter(view.Filter),
        string.IsNullOrEmpty(view.FlagFilterId) ? null : view.FlagFilterId,
        ConfigModel.ParseSort(view.Sort),
        view.DaysOfMail);

    /// <summary>Walks the three layers for a folder. Callers must set <see cref="ActiveView"/> to
    /// its intended value first — clearing a view means clearing it, then resolving.</summary>
    private ListState ResolveListState(MailFolderModel? folder)
    {
        if (ActiveView != null) return ToListState(ActiveView);

        if (_rememberViewPerFolder && _folderViewState != null && folder != null &&
            !string.IsNullOrEmpty(folder.FullName))
        {
            var remembered = _folderViewState.Recall(folder.AccountId, folder.FullName);
            if (remembered.HasValue) return DropDeletedFlagFilter(remembered.Value);
        }

        return _defaultListState;
    }

    /// <summary>
    /// Drops a named-flag sub-filter whose flag has since been deleted, the same degradation
    /// <see cref="ApplyViewStateAsync"/> performs for saved views. Without it a folder remembered
    /// as "flagged Waiting" opens empty for ever once "Waiting" is deleted, with nothing on screen
    /// to say why — the silent-empty-state failure CLAUDE.md's feature checklist calls out.
    ///
    /// Checked against the loaded definitions rather than re-reading them, so this stays
    /// synchronous; an empty collection means they have not loaded yet, and a filter is kept
    /// rather than wrongly discarded.
    /// </summary>
    private ListState DropDeletedFlagFilter(ListState state)
    {
        if (state.FlagFilterId == null || FlagDefinitions.Count == 0) return state;
        if (!Guid.TryParse(state.FlagFilterId, out var id)) return state with { FlagFilterId = null };

        return FlagDefinitions.Any(d => d.Id == id) ? state : state with { FlagFilterId = null };
    }

    /// <summary>
    /// Applies a resolved state as one unit. Does not rebuild the list — every caller either
    /// fetches straight afterwards (which rebuilds) or calls <c>ApplyFiltersAndSearch</c> itself.
    ///
    /// Both guards are saved and restored rather than set and cleared: <c>SelectFolderAsync</c>
    /// already holds <c>_suppressFilterRebuild</c> when it calls in here.
    /// </summary>
    private void ApplyListState(ListState state)
    {
        var prevApplying = _applyingListState;
        var prevSuppress = _suppressFilterRebuild;
        _applyingListState     = true;
        _suppressFilterRebuild = true;
        try
        {
            ViewMode     = state.Mode;
            ActiveFilter = state.Filter;
            SetActiveFlagFilterId(state.FlagFilterId);
            ActiveSort     = state.Sort;
            ActiveDayLimit = state.DayLimit;
        }
        finally
        {
            _suppressFilterRebuild = prevSuppress;
            _applyingListState     = prevApplying;
        }
    }

    /// <summary>Which presentation field a change handler owns. Passed to
    /// <see cref="NoteListStateChanged"/> so a detach keeps only what the user actually chose.</summary>
    private enum ListField { Mode, Filter, Sort, DayLimit }

    /// <summary>
    /// Called by every presentation-property change handler. A programmatic apply returns early;
    /// a user gesture detaches any active view and records the folder's new state.
    ///
    /// The global default is deliberately NOT written here — each of ViewMode and Sort is written
    /// by its own handler, so only the field the user actually changed moves. Writing the whole
    /// record here would mean that changing the sort while a Conversations view is active promotes
    /// that view's grouping to the global default, which is issue #520 in miniature.
    /// </summary>
    private void NoteListStateChanged(ListField changed)
    {
        if (_applyingListState) return;

        if (ActiveView != null)
            DetachFromActiveView(changed);

        RememberCurrentListState();
    }

    /// <summary>
    /// Leaves the active view, keeping the field the user just changed and dropping the rest back
    /// to what this folder shows on its own.
    ///
    /// Carrying the whole on-screen state across was wrong: the view's filter and day limit were
    /// never chosen for this folder, and writing them into its memory left the user pinned to them
    /// with no way out — Clear View resolves to that same stored record and so changes nothing, and
    /// there is no UI control for the day limit at all. This is the same principle as the per-field
    /// global default in <see cref="PersistGlobalViewMode"/>: the field you touched is the field
    /// you chose, and nothing else follows it.
    /// </summary>
    private void DetachFromActiveView(ListField changed)
    {
        var kept = CurrentListState;
        ActiveView = null;                                  // resolver now skips the view layer
        var basis = ResolveListState(SelectedFolder);

        ApplyListState(changed switch
        {
            ListField.Mode     => basis with { Mode = kept.Mode },
            ListField.Sort     => basis with { Sort = kept.Sort },
            ListField.Filter   => basis with { Filter = kept.Filter, FlagFilterId = kept.FlagFilterId },
            ListField.DayLimit => basis with { DayLimit = kept.DayLimit },
            _                  => basis,
        });

        // ApplyListState suppresses the rebuild, and the fields it just changed are not the ones
        // the calling handler is about to rebuild for — so drive one here.
        ApplyFiltersAndSearch();
    }

    private void RememberCurrentListState()
    {
        if (!_rememberViewPerFolder || _folderViewState == null) return;

        var folder = SelectedFolder;
        if (folder == null || folder.IsHeader || string.IsNullOrEmpty(folder.FullName)) return;

        // The calendar replaces the message list entirely; it has no presentation to remember.
        if (IsCalendarFolderName(folder.FullName)) return;

        // A multi-folder view's sentinel folder is never resolved through ResolveListState —
        // SelectFolderAsync intercepts view sentinels and routes to ApplyViewByIdAsync before the
        // resolver runs — so an entry written against one could never be read back. Storing it
        // would only add rows to a file nothing prunes.
        if (IsViewSentinel(folder.FullName)) return;

        _folderViewState.Remember(folder.AccountId, folder.FullName, CurrentListState);
    }

    /// <summary>
    /// Selects a folder on a startup path and applies its remembered presentation.
    ///
    /// The startup paths assign <see cref="SelectedFolder"/> directly rather than going through
    /// <c>SelectFolderAsync</c> (they load from the cache, not the network), so without this the
    /// one folder the user opens into would be the one folder that ignored its own settings.
    /// CLAUDE.md's startup rule: anything affecting what the user first sees must be applied in
    /// the initial load, not after sync.
    /// </summary>
    private void SelectStartupFolder(MailFolderModel folder)
    {
        SelectedFolder = folder;
        ApplyListState(ResolveListState(folder));
    }

    /// <summary>
    /// Lands on All Mail when the current folder or account has been deleted out from under the
    /// selection. Both callers used to assign <see cref="SelectedFolder"/> bare, which left the
    /// deleted folder's grouping and filter applied to All Mail — and, worse, live, so the next
    /// change the user made wrote them into All Mail's own record.
    /// </summary>
    private void FallBackToAllMail()
    {
        ActiveView     = null;
        SelectedFolder = AllMailFolder;
        ApplyListState(ResolveListState(AllMailFolder));
    }

    private void PersistGlobalViewMode(ViewMode value)
    {
        var cfg = _configService.Load();
        cfg.ViewMode = ConfigModel.ToConfigString(value);
        _configService.Save(cfg);
        _defaultListState = _defaultListState with { Mode = value };
    }

    private void PersistGlobalSort(MessageSort value)
    {
        var cfg = _configService.Load();
        cfg.Sort = ConfigModel.ToConfigString(value);
        _configService.Save(cfg);
        _defaultListState = _defaultListState with { Sort = value };
    }

    /// <summary>Raised when the view list changes so the Views menu can be rebuilt.</summary>
    public event EventHandler? SavedViewsChanged;

    /// <summary>Raised to ask MainWindow to open the View Manager dialog to create a new view from the current state.</summary>
    public event EventHandler? SaveViewRequested;

    /// <summary>Raised to ask MainWindow to open the View Manager dialog in manage mode.</summary>
    public event EventHandler? ManageViewsRequested;

    // ── Account / folder tree ─────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<AccountModel> _accounts = [];

    // #31: a thread-safe id→account snapshot for the OAuthService shared-mailbox token resolver, which
    // runs on background sweep threads (GraphClient acquires the token deep in a ConfigureAwait(false)
    // flow). Accounts is a UI-thread-owned ObservableCollection — enumerating it off the UI thread while
    // the user adds/removes an account throws "Collection was modified". Instead the resolver reads this
    // volatile reference to an immutable map, rebuilt on the UI thread whenever the collection changes
    // (reassignment via the generated OnAccountsChanged, and in-place add/remove via CollectionChanged).
    private volatile Dictionary<Guid, AccountModel> _accountsById = new();
    private ObservableCollection<AccountModel>? _accountsSubscription;

    /// <summary>Thread-safe by-id account lookup for the shared-mailbox token resolver (App wires this to
    /// <see cref="Services.OAuthService.ResolveAccount"/>). Safe to call from any thread.</summary>
    public AccountModel? ResolveAccountById(Guid id) => _accountsById.TryGetValue(id, out var a) ? a : null;

    partial void OnAccountsChanged(ObservableCollection<AccountModel> value)
    {
        // Re-subscribe when the whole collection is replaced (LoadAccountList), so in-place add/remove on
        // the new instance keeps the snapshot current.
        if (_accountsSubscription is not null) _accountsSubscription.CollectionChanged -= OnAccountsCollectionChanged;
        _accountsSubscription = value;
        if (value is not null) value.CollectionChanged += OnAccountsCollectionChanged;
        RebuildAccountsById();
    }

    private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAccountsById();

    // Runs on the UI thread (collection mutations are UI-thread-owned). Publishes a fresh immutable map;
    // readers on other threads see the new reference atomically via the volatile field. Last-wins on the
    // (unexpected) duplicate-id case so a rebuild never throws.
    private void RebuildAccountsById()
    {
        var map = new Dictionary<Guid, AccountModel>(Accounts.Count);
        foreach (var a in Accounts) map[a.Id] = a;
        _accountsById = map;
    }

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
    [NotifyPropertyChangedFor(nameof(IsContactMailView))]
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
    public bool IsFilterWatched         => ActiveFilter == MessageFilter.Watched;
    public bool IsFilterAllFlagged      => ActiveFilter == MessageFilter.Flagged && _activeFlagFilterId == null;
    public bool IsFilterActive          => ActiveFilter != MessageFilter.All;
    public bool AnnounceFlagStatus      => _announceFlagStatus;

    /// <summary>
    /// The spoken field layout every list row binds to (see <c>Views.RowSpeech</c>). Assigning a
    /// new instance and raising PropertyChanged is what pushes a layout change out to rows that
    /// are already on screen — no reload, no re-fetch, virtualized containers included.
    /// </summary>
    public RowSpeechSettings RowSpeech { get; private set; } = RowSpeechSettings.Default;

    /// <summary>Re-reads the layouts and the labels preference and re-speaks every realized row.</summary>
    public void ReloadRowSpeech()
    {
        if (_rowLayoutService is null) return;
        var showLabels = false;
        try { showLabels = _configService.Load().MessageListShowFieldLabels; }
        catch { /* config unreadable — fall back to unlabelled */ }
        RowSpeech = new RowSpeechSettings(_rowLayoutService.Load(), showLabels);
        OnPropertyChanged(nameof(RowSpeech));
    }
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
        MessageFilter.Watched         => "Watched",
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
        MessageOpenMode = MessageOpenMode switch
        {
            Models.MessageOpenMode.ReadingPane => "Reading pane",
            Models.MessageOpenMode.Tab         => "Tab",
            Models.MessageOpenMode.Window      => "Window",
            _                                  => MessageOpenMode.ToString(),
        },
        Accounts = DescribeAccounts(this.Accounts),
    };

    /// <summary>
    /// The account line for a bug report's Environment section: how many accounts are configured
    /// and which protocols they connect over — <c>"2 (IMAP, Microsoft 365)"</c>, or
    /// <c>"1 (Microsoft 365), plus 2 shared mailboxes"</c>. Backend now changes behaviour in draft
    /// handling, folder semantics, rules, and attachment fetch, so a report that omits it costs a
    /// source read to triage (#639).
    /// <para>Protocol kind only — no address, host name, or display name goes near this string: it
    /// is published verbatim into a public issue. Kinds are listed in <see cref="BackendKind"/>
    /// order rather than account order so the same setup always produces the same line; the
    /// examples above are in that order (ImapSmtp precedes MicrosoftGraph), and
    /// <c>DescribeAccounts_OrdersKindsIndependentlyOfAccountOrder</c> pins it.</para>
    /// Pure/static so the redaction boundary is unit-testable without standing up the view model.
    /// </summary>
    internal static string DescribeAccounts(IEnumerable<AccountModel>? accounts)
    {
        var all = accounts?.ToList() ?? [];
        if (all.Count == 0) return "0";

        var distinct = all.Select(a => a.BackendKind).Distinct().OrderBy(k => k).Select(k => k switch
        {
            BackendKind.MicrosoftGraph => "Microsoft 365",
            BackendKind.Pop3Smtp       => "POP3",
            BackendKind.ImapSmtp       => "IMAP",
            _                          => k.ToString(),
        });

        // Shared mailboxes are counted apart from the user's own accounts (#31). Folding them into
        // one number is both misleading — three "accounts" can be one account and two mailboxes
        // someone shared with it — and a wasted signal: a shared mailbox reads through its parent's
        // token and diverges from an ordinary account in ways that are worth knowing up front.
        var shared = all.Count(a => a.IsShared);
        var line   = $"{all.Count - shared} ({string.Join(", ", distinct)})";

        return shared switch
        {
            0 => line,
            1 => line + ", plus 1 shared mailbox",
            _ => line + $", plus {shared} shared mailboxes",
        };
    }

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

    /// <summary>
    /// True when this x64 copy is running under emulation on an ARM64 device, so a native
    /// ARM build would be faster (issue #18). Drives the Help menu entry's visibility.
    /// Evaluated once — the hardware does not change underneath us — and kept an instance
    /// property because the menu binds to it through the DataContext.
    /// </summary>
    public bool IsNativeArmAvailable { get; } = Helpers.ProcessArchitectureInfo.IsEmulatedOnArm64;

    private bool _showPreview;
    private int  _previewLines;

    public bool HasSelectedAccount  => SelectedAccount  != null;
    public bool HasSelectedFolder   => SelectedFolder   != null;
    public bool HasSelectedMessage  => SelectedMessage  != null;

    public string WindowTitle
    {
        get
        {
            // The suffix lives in the getter (not applied externally) so any
            // WindowTitle recompute keeps the capture warning while enabled.
            var title = ComputeWindowTitle();
            return _screenshotCapture?.Enabled == true
                ? title + IScreenshotCaptureService.TitleSuffix
                : title;
        }
    }

    private string ComputeWindowTitle()
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
        IGraphCalendarSyncService? graphCalendarSyncService = null,
        ConnectionTruthProbe? truthProbe = null,
        IScreenshotCaptureService? screenshotCapture = null,
        IRowLayoutService? rowLayoutService = null,
        IWatchService? watchService = null,
        IFolderViewStateService? folderViewState = null,
        IOutboxService? outboxService = null,
        IConnectivityService? connectivity = null)
    {
        _folderViewState = folderViewState;
        _outbox = outboxService;
        _connectivity = connectivity;
        SubscribeConnectivity();
        if (_outbox != null)
        {
            _outbox.Changed        += OnOutboxChanged;
            _outbox.FlushCompleted += OnOutboxFlushCompleted;
        }
        _watchService = watchService;
        _rowLayoutService = rowLayoutService;
        _truthProbe = truthProbe;
        _screenshotCapture = screenshotCapture;
        if (_screenshotCapture != null)
            _screenshotCapture.EnabledChanged += OnScreenshotCaptureEnabledChanged;
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
        _readAsPlainText = cfg.ReadAsPlainText;
        _previewLines = cfg.PreviewLines;
        _showPreview = _previewLines > 0;
        _syncDays = cfg.SyncDays;
        _viewMode = ConfigModel.ParseViewMode(cfg.ViewMode);
        _listDensity = cfg.AppearanceListDensity == "compact" ? "compact" : "comfortable";
        MessageOpenMode = cfg.Windowing.MessageOpenMode;
        EnsureMessageListTab();
        _activeSort = ConfigModel.ParseSort(cfg.Sort);
        _rememberViewPerFolder = cfg.RememberViewPerFolder;
        _defaultListState      = DefaultListStateFrom(cfg);
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
                                               // Each account's calendars feed the save-target picker, so
                                               // every calendar the user can write to is its own target.
                                               () => _calendarSources);
            _defaultCalendarSource = cfg.DefaultCalendarSource ?? string.Empty;
            CalendarVm.DefaultCalendar = DefaultCalendarFilter;
            RemindersEnabled = cfg.CalendarReminders;
            ReminderLeadMinutes = cfg.CalendarReminderMinutes;
            StartReminderTimer();
        }

        _syncService.FolderSynced    += OnFolderSynced;
        _syncService.MessagesRemoved += OnMessagesRemoved;
        _syncService.FolderReadStatesReconciled += OnFolderReadStatesReconciled;
        _syncService.RulesApplied    += OnRulesApplied;
        if (_changeNotifier != null)
        {
            _changeNotifier.InboxNewMailDetected += OnInboxNewMailDetected;
            _changeNotifier.InboxMessagesRemoved += OnInboxMessagesRemoved;
        }
        if (_flagService != null)
        {
            _onFlagDefinitionsChanged = (_, _) => _ = OnFlagDefinitionsChangedAsync();
            _flagService.FlagDefinitionsChanged += _onFlagDefinitionsChanged;
        }
        if (_rowLayoutService != null)
        {
            // Saved on every mutation in the Message List Fields window, so rows re-speak while
            // it is still open — the user hears the new order without closing anything.
            _onRowLayoutsChanged = (_, _) => ReloadRowSpeech();
            _rowLayoutService.LayoutsChanged += _onRowLayoutsChanged;
            ReloadRowSpeech();
        }

        // #31: subscribe the initial account collection so ResolveAccountById reflects add/remove even
        // before the first LoadAccountList reassigns it (which re-subscribes the new instance).
        OnAccountsChanged(Accounts);

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

    /// <summary>
    /// #529 step 4: the Account Manager just converted an account to the Graph backend (it flipped its
    /// own copy, persisted the <see cref="AccountModel.GraphConversionPending"/> marker, and purged the
    /// local cache). Bring THIS window's live copy in line so nothing re-syncs the account over IMAP this
    /// session: disconnect the IMAP session, flip our copy to Graph, seed the in-session rule-refire
    /// baseline (so the Graph re-download does not run client rules over the pre-existing mail — the #454
    /// safeguard, matching the startup seed), and rebind the router to the Graph backend. The normal sync
    /// then re-downloads the account over Graph. The marker clear and folder-reference remap are the
    /// follow-up; the marker keeps it crash-safe until then.
    /// </summary>
    public async Task OnAccountConvertedToGraphAsync(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        // Best effort — the IMAP session may already be down; either way stop it before rebinding.
        try { await _imap.DisconnectAsync(accountId); } catch { }

        account.GraphConversionPending = true;
        account.BackendKind = BackendKind.MicrosoftGraph;
        _syncService.SeedRebuildBaseline([accountId]);
        RegisterAccountBackend?.Invoke(account);

        // Drop this account's cached folder models. They are the pre-convert IMAP list — the purge
        // cleared only the SQLite rows — and their FullNames are IMAP paths, which are not valid Graph
        // folder ids. Left in place they draw a tree of folders that no longer resolve, so selecting
        // one lands on nothing. Assigning the cache directly rather than calling SetCachedFolders,
        // which would also mark the account connected (it is not — the IMAP session just went down and
        // Graph has not connected yet) and skip an empty write anyway.
        //
        // The tree is deliberately NOT rebuilt here: this runs inside the Account Manager's modal
        // message loop, and rebuilding parent-window UI from there is exactly the re-entrancy the
        // modal-dialog rule forbids. RefreshAccountList rebuilds from this cache the moment the dialog
        // closes, and FinishGraphConversionAsync refills it with the real Graph folders when they land.
        _cachedFolders[accountId] = [];

        // Drive the re-download + folder-reference remap + marker clear in the background so the convert
        // returns promptly. If the app closes before it finishes, the persisted marker makes startup
        // resume it (see StartBackgroundSyncAsync).
        FinishGraphConversionAsync(accountId, CancellationToken.None).LogFaults("FinishGraphConversion");
    }

    /// <summary>
    /// #529 step 4: completes a conversion — connect the now-Graph account, force its Inbox to sync so the
    /// rule-refire baseline is consumed, remap the account's folder-referencing settings to the new Graph
    /// folder ids (§5.2), and finally clear the <see cref="AccountModel.GraphConversionPending"/> marker.
    /// Runs both from the in-session handoff and from the startup crash-resume; idempotent — a re-run
    /// re-connects and re-syncs harmlessly, and it no-ops once the marker is clear.
    /// </summary>
    private async Task FinishGraphConversionAsync(Guid accountId, CancellationToken ct)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null || !account.GraphConversionPending) return;

        // One finisher per account. Converting while the startup sweep is still running reaches this
        // twice for the same account: the in-session handoff fires immediately, and StartBackgroundSyncAsync's
        // resume loop then sees the same live AccountModel still carrying the marker when SyncAllAccountsAsync
        // returns. Both would connect and force-sync the same Inbox concurrently. Dropping the second is
        // right rather than merely cheaper — the first is already driving the account to the same end state,
        // and if it fails the marker survives for the next launch either way.
        lock (_graphConversionsInFlight)
        {
            if (!_graphConversionsInFlight.Add(accountId)) return;
        }

        try
        {
            await FinishGraphConversionCoreAsync(account, accountId, ct);
        }
        finally
        {
            lock (_graphConversionsInFlight) _graphConversionsInFlight.Remove(accountId);
        }
    }

    /// <summary>Accounts with a <see cref="FinishGraphConversionAsync"/> already running, so the
    /// in-session handoff and the startup resume loop cannot both drive the same one (#529 step 4).</summary>
    private readonly HashSet<Guid> _graphConversionsInFlight = [];

    /// <summary>The body of <see cref="FinishGraphConversionAsync"/>, which owns the one-at-a-time guard.</summary>
    private async Task FinishGraphConversionCoreAsync(AccountModel account, Guid accountId, CancellationToken ct)
    {
        // Connect the Graph account and take the folders it returns DIRECTLY, never a read-back through
        // _cachedFolders. The remap has to run against folders this account genuinely exposes now: a
        // transient no-folders connect must retry next launch rather than remap against whatever the
        // cache happens to hold. (The handoff clears that cache of its pre-convert IMAP models, but the
        // startup-resume path does not go through the handoff, so this cannot rely on it being empty.)
        var (_, folders) = await ConnectOneAccountAsync(account);
        if (folders is not { Count: > 0 }) return;
        SetCachedFolders(accountId, folders);
        var graphFolders = folders;

        // Force an Inbox sync so its rule-refire baseline is consumed BEFORE the marker clears (client
        // rules only ever run on the Inbox, so it is the one that matters). No Inbox exposed yet → do NOT
        // clear the marker; retry next launch, because clearing without a baselined Inbox and then crashing
        // would leave the next launch re-firing rules over the pre-existing mail (#454, §5.3).
        var inbox = graphFolders.FirstOrDefault(f => f.Kind == SpecialFolderKind.Inbox);
        if (inbox is null) return;
        await _syncService.SyncFolderFullAsync(account, inbox, ct);

        // Remap folder-referencing settings now that the Graph folders exist.
        var rules = _ruleService.LoadRules();
        var views = _viewService.Load();
        var cfg = _configService.Load();
        var report = FolderReferenceRemapper.Remap(accountId, graphFolders, rules, views, cfg);
        if (report.AnythingChanged)
        {
            _ruleService.SaveRules(rules);
            _viewService.Save(views);
            _configService.Save(cfg);
        }

        // Clear the marker — resolve the CURRENT instance (a reload may have replaced it) and persist.
        var current = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (current != null)
        {
            current.GraphConversionPending = false;
            _accountService.SaveAccounts([.. Accounts]);
        }

        var summary = report.Summary();
        Announce(
            $"{account.AccountLabel} finished converting to Microsoft 365." + (summary.Length > 0 ? " " + summary : ""),
            AnnouncementCategory.Result);
    }

    public void LoadAccountList(List<AccountModel>? preloaded = null)
    {
        var accounts = preloaded ?? _accountService.LoadAccounts();

        // Carry live connection state across the reload.
        //
        // These AccountModel objects are rebuilt from accounts.json, and IsConnected / TotalUnread
        // are runtime state that is deliberately never persisted — so every reloaded account arrives
        // reporting "disconnected" regardless of what its backend is actually doing. Replacing the
        // Accounts collection then makes every account in the list read as disconnected at once.
        //
        // That is the whole "adding an account disconnects the others" symptom. Nothing disconnects:
        // the pools, the IDLE watchers and the sockets are untouched (the journal shows
        // watchers-reconciled stopping=0 across an add), and RefreshAccountList deliberately skips
        // reconnecting accounts that are already healthy — so nothing ever sets IsConnected back to
        // true for them, and the false status sticks until the next restart.
        //
        // This was invisible to the status instrumentation because the state is lost by object
        // replacement, not by assignment: ApplyAccountStatus is genuinely the only code that writes
        // IsConnected, so no write is ever observed. See docs/planning/connection-drop-diagnostics.md.
        // Not ToDictionary: a duplicated id in accounts.json would throw, turning a tolerable data
        // oddity into a UI-thread crash on every Manage Accounts close. Last one wins, as before.
        var previous = new Dictionary<Guid, AccountModel>();
        foreach (var existing in Accounts) previous[existing.Id] = existing;

        var carried = 0;
        foreach (var account in accounts)
        {
            if (!previous.TryGetValue(account.Id, out var prior)) continue;

            // Only carry status when the connection itself is unchanged. If the user just edited the
            // host, port, login or security settings, the pooled connections are for the OLD server:
            // reporting "connected" would vouch for a connection that no longer matches the account.
            // Leaving it disconnected is both honest and useful — it puts the account into
            // AccountsNeedingConnect so the reconnect pass actually re-establishes it.
            if (!SameConnectionIdentity(prior, account)) continue;

            account.IsConnected = prior.IsConnected;
            account.TotalUnread = prior.TotalUnread;
            carried++;
        }

        foreach (var account in accounts)
            RegisterAccountBackend?.Invoke(account);
        Accounts = new ObservableCollection<AccountModel>(accounts);

        // An account deleted while it was showing disconnected never receives a NoteConnected call,
        // so its verification loop would otherwise keep probing a mailbox the user has removed.
        _truthProbe?.RetainOnly(accounts.Select(a => a.Id));

        if (previous.Count > 0)
        {
            var carriedCount = carried;
            ConnectionJournal.Record(
                ConnectionEventKind.Status, "-", "-", "accounts-reloaded",
                () => $"rebuilt {accounts.Count} account object(s) from disk; " +
                      $"carried live status for {carriedCount}; " +
                      $"{accounts.Count - carriedCount} new, removed or reconfigured");
        }
    }

    /// <summary>
    /// Whether two snapshots of an account describe the same server connection. Used to decide
    /// whether live connection status may be carried across a reload — a changed host, port, login
    /// or security setting means the existing connections no longer belong to this account.
    /// </summary>
    private static bool SameConnectionIdentity(AccountModel left, AccountModel right) =>
        left.BackendKind == right.BackendKind &&
        left.Username == right.Username &&
        left.LoginUsername == right.LoginUsername &&
        left.AuthType == right.AuthType &&
        // The INCOMING leg, whichever protocol serves it: reading the IMAP fields directly missed a
        // repointed POP3 account entirely (its IMAP fields never change), so live "connected" status
        // was carried over to a connection that no longer existed.
        left.IncomingHost == right.IncomingHost &&
        left.IncomingPort == right.IncomingPort &&
        left.IncomingUseSsl == right.IncomingUseSsl &&
        left.RequireStartTls == right.RequireStartTls &&
        left.IncomingAcceptInvalidCert == right.IncomingAcceptInvalidCert;

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
        await ApplyViewStateAsync(view);

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

        await ApplyViewFoldersAsync(view, allFolders);
    }

    /// <summary>
    /// Applies a saved view's presentation state — mode, filter, sort, flag filter, day limit — and
    /// clears search and open-message state. Split out of <see cref="ApplyViewAsync"/> so the startup
    /// path can adopt a view's state while loading its messages from the local store instead of the
    /// network (#516); everything here is pure VM state with no fetch of its own.
    /// </summary>
    private async Task ApplyViewStateAsync(SavedView view)
    {
        ActiveView = view;

        var state = ToListState(view);

        // Validate the flag filter id against current flag definitions before applying.
        // If the referenced flag has been deleted, treat it as no filter rather than
        // showing an empty list with no explanation.
        if (state.FlagFilterId != null && _flagService != null &&
            Guid.TryParse(state.FlagFilterId, out var flagGuid))
        {
            var defs = await _flagService.LoadFlagDefinitionsAsync();
            if (!defs.Exists(d => d.Id == flagGuid))
                state = state with { FlagFilterId = null };
        }

        // Apply the view's mode/filter/sort before clearing search so rebuild schedulers
        // triggered by the ViewMode change operate on the right data. Applying through
        // ApplyListState is what keeps the view from writing itself into the user's
        // preferences — see NoteListStateChanged.
        ApplyListState(state);

        SearchText     = string.Empty;
        IsSearchActive = false;
        MessageDetail  = null;
        IsMessageOpen  = false;
    }

    /// <summary>
    /// Selects and fetches the folder(s) a saved view covers. Assumes the view's presentation state
    /// has already been applied by <see cref="ApplyViewStateAsync"/>.
    /// </summary>
    private async Task ApplyViewFoldersAsync(SavedView view, bool allFolders)
    {
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
                    // Aggregate view — stamp each message with the stored view's plain folder name as a
                    // fallback; ApplyFolderDisplayNames below overwrites it with the account-qualified
                    // form for folders known to the cache (#423). Single-folder loads don't come here.
                    foreach (var m in msgs) m.FolderDisplayName = vf.FolderDisplayName;
                    newMessages.AddRange(msgs);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Log($"ViewFolders fetch {vf.AccountDisplayName}/{vf.FolderDisplayName}", ex);
                }
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            // #423 (Kelly round 2): Phase 2 inserts via InsertMessageSorted (not SetMessages), so these
            // freshly-fetched rows must be account-qualified here too — otherwise a multi-account saved
            // view shows qualified cached rows next to unqualified fresh ones. Keeps the plain stamp
            // above as the miss fallback for folders not in the cache.
            ApplyFolderDisplayNames(newMessages);
            // Same reason, same rows: these bypass SetMessages, so the derived watch flag has to be
            // stamped here too or the newest rows speak no watch state while the cached ones do.
            StampWatchedFlags(newMessages);

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

        ApplyConnectionDiagnosticsSetting(cfg.ConnectionDiagnostics);

        // Keep the View menu's density check marks in sync with a Settings save.
        ListDensity = cfg.AppearanceListDensity == "compact" ? "compact" : "comfortable";

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

        // Settings is the one place the global default is edited directly. Take the new default,
        // then re-resolve so a folder with its own memory (or an active view) keeps precedence.
        // Only Mode and Sort are taken from the resolution: Settings must not clear a filter or
        // day limit the user applied in this session, which is what a whole-record apply would do.
        _rememberViewPerFolder = cfg.RememberViewPerFolder;
        _defaultListState      = DefaultListStateFrom(cfg);
        var resolved = ResolveListState(SelectedFolder);
        ApplyListState(CurrentListState with { Mode = resolved.Mode, Sort = resolved.Sort });

        var prevSyncDays = _syncDays;
        _syncDays = cfg.SyncDays;
        OnPropertyChanged(nameof(IsSyncDays7));
        OnPropertyChanged(nameof(IsSyncDays30));
        OnPropertyChanged(nameof(IsSyncDays180));
        OnPropertyChanged(nameof(IsSyncDays365));
        OnPropertyChanged(nameof(IsSyncDaysAll));
        OnPropertyChanged(nameof(SyncRangeLabel));

        // (Sort is applied above, through the resolver — a bare assignment from cfg here would
        // override the folder's own remembered sort every time Settings was closed.)

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

        // These gate on CanActOnSelection, not HasSelectedMessage: a selected group header is a
        // target too, and gating on the selected message alone left them unavailable — the hotkey
        // doing nothing at all — whenever no message had been selected yet (issue #566).
        registry.Register(new CommandDefinition(
            id: "mail.reply", category: "Mail", title: "Reply",
            execute: () => ReplyCommand.Execute(null),
            defaultKey: Key.R, defaultModifiers: ModifierKeys.Control,
            isAvailable: CanActOnSelection));

        registry.Register(new CommandDefinition(
            id: "mail.replyAll", category: "Mail", title: "Reply All",
            execute: () => ReplyAllCommand.Execute(null),
            defaultKey: Key.R, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: CanActOnSelection));

        registry.Register(new CommandDefinition(
            id: "mail.forward", category: "Mail", title: "Forward",
            execute: () => ForwardCommand.Execute(null),
            defaultKey: Key.F, defaultModifiers: ModifierKeys.Control,
            isAvailable: CanActOnSelection));

        registry.Register(new CommandDefinition(
            id: "mail.delete", category: "Mail", title: "Delete",
            execute: () => DeleteMessageCommand.Execute(null),
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            isAvailable: CanActOnSelection));

        // Archive (issue #318) — moves the selection to the account's Archive folder instead of
        // deleting. Default gesture Ctrl+Shift+M. (Not Alt+Delete: that collides with the common
        // screen-reader "announce cursor position" command.) Like mail.delete this base registration
        // is overridden in MainWindow with a focus-aware guard so the message list and group trees
        // can archive the whole selection/group via their PreviewKeyDown handlers.
        registry.Register(new CommandDefinition(
            id: "mail.archive", category: "Mail", title: "Move to Archive",
            execute: () => ArchiveMessageCommand.Execute(null),
            defaultKey: Key.M, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: CanActOnSelection));

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

        // No default key: a rare, deliberate action that the Outbox drains on its own anyway (#637).
        registry.Register(new CommandDefinition(
            id: "mail.sendOutboxNow", category: "Mail", title: "Send Outbox Now",
            description: "Try to send queued messages and upload queued drafts right away",
            execute: () => SendOutboxNowCommand.Execute(null),
            isAvailable: () => ShowOutboxFolder));

        // Ctrl+Shift+W, not Ctrl+W: Ctrl+W already closes a tab / the reading pane / a child window.
        registry.Register(new CommandDefinition(
            id: "mail.toggleWatch", category: "Mail", title: "Watch Conversation",
            description: "Watch or unwatch the selected message's conversation",
            execute: ToggleWatchConversation,
            defaultKey: Key.W, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            // Not HasSelectedMessage: on a From/To group header the resolver returns null even
            // though a stale message is still selected, and the command must be unavailable there.
            // Deliberately reads the target without storing it — an availability query is polled
            // (command palette, every keystroke) and must not mutate VM state or raise change
            // notifications while it runs.
            isAvailable: () => _watchService != null && HasWatchableSubject(ResolveWatchTarget())));

        registry.Register(new CommandDefinition(
            id: "view.toggleConversation", category: "View", title: "Cycle View Mode",
            execute: () => ViewMode = (ViewMode)(((int)ViewMode + 1) % 4)));

        // Density (#421): the same setting the Settings dialog persists, adjustable
        // from the View menu, the palette, and (via these registrations) a hotkey.
        registry.Register(new CommandDefinition(
            id: "view.density.comfortable", category: "View", title: "Density: Comfortable",
            execute: () => SetListDensity("comfortable")));

        registry.Register(new CommandDefinition(
            id: "view.density.compact", category: "View", title: "Density: Compact",
            execute: () => SetListDensity("compact")));

        // Clear View has existed on the View menu since saved views shipped but was never
        // registered, so it was absent from the palette and could not be bound to a key.
        registry.Register(new CommandDefinition(
            id: "view.clearView", category: "View", title: "Clear View",
            execute: () => ClearViewCommand.Execute(null),
            isAvailable: () => ActiveView != null));

        registry.Register(new CommandDefinition(
            id: "view.resetFolderView", category: "View", title: "Reset Folder View",
            execute: () => ResetFolderViewCommand.Execute(null),
            isAvailable: () => SelectedFolder != null && !SelectedFolder.IsHeader));

        registry.Register(new CommandDefinition(
            id: "account.manage", category: "Account", title: "Manage Accounts",
            execute: () => ManageAccountsCommand.Execute(null)));

        registry.Register(new CommandDefinition(
            id: "help.userGuide", category: "Help", title: "Open User Guide",
            execute: () => ViewUserGuideCommand.Execute(null),
            defaultKey: Key.F1, defaultModifiers: ModifierKeys.None));

        // Registered unconditionally so the palette listing does not vary by machine, matching
        // how every other Help command is registered. The menu entry is what hides on
        // non-ARM hardware; running this on an x64 PC simply opens the releases page.
        registry.Register(new CommandDefinition(
            id: "help.armVersion", category: "Help", title: "Get the ARM Version",
            execute: () => OpenArmDownloadPageCommand.Execute(null)));

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
            id: "view.filterWatched", category: "View", title: "Show Watched Conversations Only",
            execute: () => SetFilterCommand.Execute("watched")));

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
            isAvailable: CanActOnSelection));

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

        // #607: a Global Admin grants QuickMail's Graph permissions org-wide in one screen. No default
        // hotkey — it is a rare, one-time setup action, discoverable via the Help menu and the palette.
        registry.Register(new CommandDefinition(
            id: "help.adminConsent", category: "Help",
            title: "Grant Admin Consent for Your Organization",
            execute: () => AdminConsentRequested?.Invoke(this, EventArgs.Empty)));

        // help.connectionDiagnostics is deliberately NOT registered here. It is registered and
        // unregistered by ApplyConnectionDiagnosticsSetting so the command palette only offers it
        // while the feature is switched on, matching the Help menu. No default hotkey either way.
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    /// <summary>Set by App startup when the one-time Graph immutable-id cache rebuild (#366) cleared
    /// cached mail; InitialLoadAsync announces the resulting re-sync so the empty inbox isn't a mystery.</summary>
    public bool ImmutableIdRebuildAnnouncePending { get; set; }

    // The one-time re-sync notice, shared between the immediate announce and the visible status.
    private const string ImmutableIdRebuildNotice =
        "Microsoft 365 mail is doing a one-time re-sync — this may take a few minutes.";

    /// <summary>
    /// The saved view a <c>view:{guid}</c> startup value points at, or null. Only the one-time
    /// migration off <c>SavedView.IsDefault</c> writes that form (#516).
    /// </summary>
    private SavedView? StartupView(ConfigModel cfg) =>
        cfg.StartupFolder.StartsWith("view:", StringComparison.Ordinal) &&
        Guid.TryParse(cfg.StartupFolder["view:".Length..], out var id)
            ? SavedViews.FirstOrDefault(v => v.Id == id)
            : null;

    /// <summary>
    /// The folder the configured startup folder names, or null to mean All Mail. Resolves entirely
    /// against <see cref="_cachedFolders"/> — restored from the local store moments earlier — so it
    /// works with no network, which is the whole point: applying the choice after connect is what
    /// produced the All Mail flash users complained about.
    /// <paramref name="fallbackReason"/> is set when a folder was configured but could not be
    /// resolved, so the caller can say why rather than silently showing the wrong thing.
    /// </summary>
    private MailFolderModel? ResolveStartupFolder(ConfigModel cfg, out string? fallbackReason)
    {
        fallbackReason = null;
        var key = cfg.StartupFolder;
        if (string.IsNullOrWhiteSpace(key)) return null;    // empty == All Mail, the default

        var label = string.IsNullOrWhiteSpace(cfg.StartupFolderLabel) ? key : cfg.StartupFolderLabel;

        // A real folder on a specific account.
        if (!string.IsNullOrWhiteSpace(cfg.StartupFolderAccount))
        {
            if (!Guid.TryParse(cfg.StartupFolderAccount, out var accountId) ||
                !Accounts.Any(a => a.Id == accountId))
            {
                fallbackReason = $"The account for startup folder '{label}' is no longer set up — showing All Mail.";
                return null;
            }
            // The trimmed comparison is the fallback, not the rule. config.ini is hand-parsed and
            // trims every value, so a mailbox legitimately named "Work " (IMAP permits it) comes
            // back as "Work" and would otherwise never resolve again — the user would see the
            // fallback notice on every launch with no way to fix it from the UI, since re-picking
            // writes the same untrimmable value.
            var match = _cachedFolders.TryGetValue(accountId, out var folders)
                ? folders.FirstOrDefault(f => string.Equals(f.FullName, key, StringComparison.OrdinalIgnoreCase))
                  ?? folders.FirstOrDefault(f => string.Equals(f.FullName.Trim(), key, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match == null)
            {
                fallbackReason = $"Startup folder '{label}' was not found — showing All Mail.";
                return null;
            }
            return match;
        }

        // A saved view (migration-only form). Its own folders are resolved by the caller.
        if (key.StartsWith("view:", StringComparison.Ordinal))
        {
            if (StartupView(cfg) != null) return null;      // handled separately, not a folder
            fallbackReason = $"The startup view '{label}' no longer exists — showing All Mail.";
            return null;
        }

        // A virtual folder, stored without the NUL sentinel prefix (an INI cannot carry one).
        var sentinel = "\x00" + key;
        var virtualFolder = Folders.FirstOrDefault(f =>
            string.Equals(f.FullName, sentinel, StringComparison.Ordinal));
        if (virtualFolder != null) return virtualFolder;

        fallbackReason = $"Startup folder '{label}' is no longer available — showing All Mail.";
        return null;
    }

    /// <summary>
    /// The startup folder for <c>--online</c>, where there is no local store and so no folder cache
    /// to match against. Virtual keys resolve to their sentinel singletons; a real folder is
    /// fabricated from the configured account, name, and label, which is enough for the fetch that
    /// follows connect. Returns null to mean All Mail.
    /// </summary>
    private MailFolderModel? ResolveOnlineStartupFolder(ConfigModel cfg)
    {
        var key = cfg.StartupFolder;
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("view:", StringComparison.Ordinal))
            return null;

        if (!string.IsNullOrWhiteSpace(cfg.StartupFolderAccount))
        {
            if (!Guid.TryParse(cfg.StartupFolderAccount, out var accountId) ||
                !Accounts.Any(a => a.Id == accountId))
                return null;
            return new MailFolderModel
            {
                AccountId   = accountId,
                FullName    = key,
                DisplayName = string.IsNullOrWhiteSpace(cfg.StartupFolderLabel) ? key : cfg.StartupFolderLabel,
            };
        }

        var sentinel = "\x00" + key;
        return AllVirtualFolders.FirstOrDefault(f =>
            string.Equals(f.FullName, sentinel, StringComparison.Ordinal));
    }

    /// <summary>Every top-level virtual aggregate, in folder-tree order. Shared by the startup
    /// resolver, the context-menu guard, and the Settings picker. (RebuildFolderListFromCache and
    /// BuildFolderTree still list them separately — worth unifying, but not in #516.)</summary>
    internal static readonly MailFolderModel[] AllVirtualFolders =
    [
        AllMailFolder, AllInboxesFolder, AllDraftsFolder, AllSentFolder,
        AllArchiveFolder, AllTrashFolder, AllFlaggedFolder, AllWatchedFolder,
    ];

    /// <summary>
    /// Cached messages for a saved view's folders, read from the local store only. Used at startup
    /// for the migration-only <c>view:{guid}</c> form, where a multi-folder view has no single
    /// folder to select (#516).
    /// </summary>
    private async Task<List<MailMessageSummary>> LoadViewSummariesAsync(SavedView view)
    {
        if (view.Folders.Count == 0)
            return ExcludeSharedMail(await _localStore.LoadAllSummariesAsync());

        var all = new List<MailMessageSummary>();
        foreach (var vf in view.Folders)
        {
            // Legacy views could carry a virtual sentinel in Folders; it names no real folder and
            // would query the store for a row that cannot exist. Same guard FetchViewFoldersAsync has.
            if (string.IsNullOrEmpty(vf.FolderFullName) || vf.FolderFullName[0] == '\0') continue;
            all.AddRange(await _localStore.LoadFolderSummariesAsync(vf.AccountId, vf.FolderFullName));
        }
        return [.. all.OrderByDescending(m => m.Date)];
    }

    /// <summary>
    /// Cached messages for the startup selection, read from the local store only — no network.
    /// All Mail keeps loading the whole cache as it always has; a real folder reads just its own
    /// rows; a folder-scoped aggregate unions the folders it spans, which is resolvable offline
    /// now that folder kinds are persisted (#516).
    /// </summary>
    private async Task<List<MailMessageSummary>> LoadStartupSummariesAsync(MailFolderModel folder)
    {
        if (string.Equals(folder.FullName, AllMailFolder.FullName, StringComparison.Ordinal))
            return ExcludeSharedMail(await _localStore.LoadAllSummariesAsync());   // #31

        if (IsFolderScopedAggregate(folder.FullName))
        {
            var all = new List<MailMessageSummary>();
            foreach (var (account, source) in FolderScopedAggregateSources(folder.FullName))
                all.AddRange(await _localStore.LoadFolderSummariesAsync(account.Id, source.FullName));
            return [.. all.OrderByDescending(m => m.Date)];
        }

        if (folder.AccountId != Guid.Empty && folder.FullName.Length > 0 && folder.FullName[0] != '\0')
            return await _localStore.LoadFolderSummariesAsync(folder.AccountId, folder.FullName);

        // The Outbox is local by definition (#637); its listing needs no network at any time.
        if (string.Equals(folder.FullName, OutboxFolder.FullName, StringComparison.Ordinal))
            return await BuildOutboxSummariesAsync();

        // Any other sentinel (All Flagged, Watched, a view, contact mail…) has no cheap cached
        // form here; fall back to the whole cache and let the post-connect refresh narrow it.
        return ExcludeSharedMail(await _localStore.LoadAllSummariesAsync());
    }

    /// <summary>
    /// Shows the startup folder's cached mail immediately (no network) — All Mail unless the user
    /// chose otherwise. Called first in OnLoaded so the UI is populated before any IMAP work begins.
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
        var rebuildNotice = ImmutableIdRebuildAnnouncePending;
        if (rebuildNotice)
        {
            // One-time: cached Microsoft 365 mail was cleared to switch to immutable ids; the sync
            // below repopulates it. Say so rather than showing a silent empty inbox. Announce it via
            // the IMMEDIATE (interrupt) path, not SetStatus — the debounced status announce would be
            // overwritten within 500 ms by the "Connecting and syncing…" write below and never spoken
            // (review N1). This announce channel is the one that reliably reaches the user. (The
            // visible status bar shows the notice briefly too — set below when the cache is empty — but
            // StartBackgroundSyncAsync overwrites it with a connect/sync status moments later, so don't
            // rely on the status bar for the explanation; review F1.)
            ImmutableIdRebuildAnnouncePending = false;
            Announce(ImmutableIdRebuildNotice, AnnouncementCategory.Status);
        }
        var startupCfg = _configService.Load();

        if (OnlineMode)
        {
            // Online mode never initializes the local store, so there is no folder cache to resolve
            // against. Select what the config names anyway — the fetch that follows connect reads
            // SelectedFolder — so the choice is honoured here too. It was not before: the old
            // default-view application sat after an early return on this path.
            SelectStartupFolder(ResolveOnlineStartupFolder(startupCfg) ?? AllMailFolder);
            if (IsKnownOffline)
            {
                // No store to fall back on: say so at once rather than "connecting…" (#637).
                StatusText = "Online mode — offline. Nothing to show until the connection returns.";
                AnnounceOfflineOnce();
                SetConnectionPhase(ConnectionPhase.Idle);
                return;
            }
            StatusText = "Online mode — connecting…";
            SetConnectionPhase(ConnectionPhase.Connecting);
            return;
        }

        // Drop calendar events left behind by accounts that no longer exist — e.g. an account removed
        // and re-added during setup gets a new id, so its old events would otherwise linger and show
        // as duplicates (one per stale id). Local events (empty account id) are kept.
        var knownAccountIds = Accounts.Select(a => a.Id).ToList();
        await _localStore.PurgeCalendarEventsForUnknownAccountsAsync(knownAccountIds);

        // Restore the folder list from the local store (#516) — BEFORE resolving the startup folder
        // and loading messages, because both depend on it. Until this landed, _cachedFolders was
        // empty until ConnectAllAccountsAsync returned, so the tree showed only the virtual
        // aggregates and nothing could tell which folders were Inboxes — which is why a startup
        // folder could not be honoured before the network came up. Purge first so an account removed
        // while the app was closed does not reappear in the tree. Failure here is not fatal: the
        // cache repopulates on connect, exactly as it did before, and the startup folder falls back
        // to All Mail.
        try
        {
            await _localStore.PurgeFoldersForUnknownAccountsAsync(knownAccountIds);
            foreach (var (accountId, folders) in await _localStore.LoadFoldersAsync())
                if (folders.Count > 0)
                    _cachedFolders[accountId] = folders;   // NOT SetCachedFolders — nothing has connected yet
        }
        catch (Exception ex)
        {
            LogService.Log("InitialLoad: restoring cached folder list", ex);
        }

        // Build the folder list now: ResolveStartupFolder matches virtual sentinels against Folders,
        // and the tree is worth drawing before the message load either way.
        await ReloadCalendarSourcesAsync(); // populate before the tree is built so calendars show at startup
        RebuildFolderListFromCache();

        // Apply the startup folder here, not after sync. CLAUDE.md's startup rule is explicit about
        // this, and the alternative is what users reported: All Mail on screen for the first seconds,
        // then a jarring switch once connections came up.
        var startupView   = StartupView(startupCfg);
        var startupFolder = ResolveStartupFolder(startupCfg, out var fallbackReason);
        if (startupView != null)
        {
            await ApplyViewStateAsync(startupView);        // mode/filter/sort only — no fetch
            // Select the view sentinel, the same shape ApplyViewFoldersAsync uses for a multi-folder
            // view, so RefreshAsync and the post-sync reload resolve it like any other selection.
            SelectedFolder = new MailFolderModel
            {
                FullName    = $"{ViewPrefix}{startupView.Id}",
                DisplayName = startupView.Name,
            };
        }
        else
        {
            SelectStartupFolder(startupFolder ?? AllMailFolder);
        }

        var cached = startupView != null
            ? await LoadViewSummariesAsync(startupView)
            : await LoadStartupSummariesAsync(SelectedFolder);
        await ResolveFlagNamesAsync(cached);
        SetMessages(cached);

        var where = SelectedFolder == AllMailFolder && startupView == null
            ? null : startupView?.Name ?? SelectedFolder.DisplayName;
        // Launched with no network (#637): say so now, before the user touches anything, rather
        // than showing "syncing…" for a sync that cannot start.
        if (IsKnownOffline)
        {
            StatusText = cached.Count > 0
                ? where == null
                    ? $"{cached.Count} messages (cached — offline)"
                    : $"{cached.Count} messages in {where} (cached — offline)"
                : "Offline — no cached messages.";
            AnnounceOfflineOnce();
        }
        else
        {
            StatusText = cached.Count > 0
                ? where == null
                    ? $"{cached.Count} messages (cached — syncing…)"
                    : $"{cached.Count} messages in {where} (cached — syncing…)"
                : rebuildNotice ? ImmutableIdRebuildNotice : "Connecting and syncing…";
        }

        // A configured startup folder that no longer resolves must say so. Silently showing All Mail
        // looks like the setting was ignored, and the user has no way to tell which it was.
        //
        // Unless we simply had nothing to resolve against. On the FIRST launch after upgrading —
        // exactly when the migration writes a startup folder — the Folder table is still empty,
        // because nothing has ever persisted it. Same for a fresh --profileDir or a rebuilt mail.db.
        // Announcing "not found" there would be wrong twice over: it is not missing, and the old
        // post-connect application (deleted in #516) did honour it in that session. So defer instead
        // and retry once the folder lists arrive.
        if (fallbackReason != null && _cachedFolders.Count == 0)
        {
            _startupFolderNeedsRetry = true;
            LogService.Log("InitialLoad: no cached folder list yet — deferring the startup folder " +
                           "until the first connect completes.");
        }
        else if (fallbackReason != null)
        {
            StatusText = fallbackReason;
            Announce(fallbackReason, AnnouncementCategory.Status);
            LogService.Log($"InitialLoad: {fallbackReason}");
        }

        SetConnectionPhase(IsKnownOffline ? ConnectionPhase.Idle : ConnectionPhase.Connecting);
        StartPrefetchTopOfFolder();
    }

    /// <summary>
    /// Applies the startup folder that <see cref="InitialLoadAsync"/> had to defer because no folder
    /// list was cached yet (#516). Runs at most once per session, after the connect pass has filled
    /// the cache. If it still cannot resolve, the folder really is gone and the user is told so —
    /// the message deferred earlier.
    /// </summary>
    private async Task RetryStartupFolderIfDeferredAsync()
    {
        if (!_startupFolderNeedsRetry) return;
        _startupFolderNeedsRetry = false;

        var cfg      = _configService.Load();
        var resolved = ResolveStartupFolder(cfg, out var fallbackReason);
        var view     = StartupView(cfg);

        if (view == null && resolved == null)
        {
            if (fallbackReason == null) return;      // nothing was configured after all
            StatusText = fallbackReason;
            Announce(fallbackReason, AnnouncementCategory.Status);
            LogService.Log($"StartupFolder retry: {fallbackReason}");
            return;
        }

        if (view != null) await ApplyViewAsync(view);
        else
        {
            SelectStartupFolder(resolved!);
            await RefreshAsync();
        }
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

        // Anything queued while the app was closed goes out now that accounts are up (#637). The
        // reconnect-driven drain cannot see a cold start, so this is its startup counterpart.
        _outbox?.FlushAsync(ct: ct).LogFaults("startup Outbox drain");

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
        // Must ask _connectedAccountIds, not _cachedFolders: since #516 the folder cache is restored
        // from SQLite at launch, so it is non-empty even when every connect failed. Testing the
        // dictionary here would send the full sync at accounts that have no connection (#516).
        if (_connectedAccountIds.Count == 0)
        {
            // A launch into a dead or captive network: keep trying on a backoff, so the user is not
            // stuck offline until a restart or an F5 (#637). The network-returned handler covers the
            // no-NIC case; this covers the NIC-up-but-nothing-answers case.
            if (Accounts.Count > 0) StartOfflineRetryLoop();
            return;
        }

        // Connected accounts only. Everything downstream of this list needs a live connection —
        // the full sync, the folder-count STATUS sweep, and the NOOP heartbeat — and since #516 the
        // folder cache no longer implies one.
        var accountList = Accounts.Where(a => _connectedAccountIds.Contains(a.Id)).ToList();

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
            // RefreshAsync rather than a hardcoded All Mail fetch: InitialLoadAsync has already
            // selected the startup folder, and it handles every sentinel and view shape (#516).
            await RefreshAsync();
            return;
        }

        // The startup folder is applied in InitialLoadAsync now, before this method runs — no
        // post-connect view switch here. Applying it at this point is what produced the All Mail
        // flash: #57 moved it from post-sync to post-connect, which shortened the flash without
        // removing it. #516 moved it to the cached load, which removes it.
        //
        // The one exception is the launch where there was no persisted folder list to resolve
        // against, so InitialLoadAsync could not apply it at all — see _startupFolderNeedsRetry.
        // Now that the folder lists are in, resolve once. This is the first launch after upgrade,
        // which is precisely when a migrated setting must be seen to work.
        await RetryStartupFolderIfDeferredAsync();

        StatusText = "Syncing mail…";
        SetConnectionPhase(ConnectionPhase.Syncing);
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
            await _syncService.SyncAllAccountsAsync(accountList, _cachedFolders, ct);

            // #529 step 4 crash-resume: an account whose convert didn't finish (marker still set, e.g. the
            // app closed mid-convert) had its rule-refire baseline seeded before this sweep. Complete it
            // now — remap its folder references and clear the marker. Fire-and-forget so a slow account
            // doesn't hold up the rest of startup; the marker keeps it recoverable if this is interrupted.
            foreach (var acct in accountList.Where(a => a.GraphConversionPending))
                FinishGraphConversionAsync(acct.Id, ct).LogFaults("resume Graph conversion");

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
            // Report accounts that connected, not accounts configured — this label read
            // "3 accounts connected" with two of them offline.
            SetConnectionPhase(ConnectionPhase.Idle);
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
            // With nothing connected the derived label reads "Offline", which is the truth the old
            // "Connection error" was gesturing at (#637).
            SetConnectionPhase(ConnectionPhase.Idle);
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
        // Connected means "this session reached the server", not "we have folders for it" — the
        // folder cache is restored from SQLite at launch (#516), so keying off it here would start
        // watchers against accounts that never connected.
        //
        // #31: a shared mailbox never gets a live watcher. A Graph shared mailbox's .Shared scopes can't
        // hold change-notification subscriptions (and delta over them is out of scope), so its only
        // freshness is the #456 sweep (which still includes it — it filters on the folder flag, not
        // IsShared). An IMAP-parent shared mailbox (PR 3) reads through its parent's connection and gets
        // no watcher of its own either. Excluding here covers every notifier type in one place.
        var connected    = Accounts.Where(a => _connectedAccountIds.Contains(a.Id) && !a.IsShared).ToList();
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
                        ApplyAccountStatus(account, folders, "reachability-event");
                    }
                });
                _changeNotifier.AccountReachabilityChanged += _onReachabilityChanged;
            }
        }

        SetConnectionPhase(ConnectionPhase.Idle);

        // Don't leave the label stuck at its pre-sync defaults once an account is actually connected;
        // the sync/poll paths (OnFolderSynced, StartBackgroundSyncAsync) keep it current from here.
        if (_connectedAccountIds.Count > 0 && LastSyncText is "Never synced" or "In progress")
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
            long sweepCycle = 0;   // #462: numbers the "Sweep cycle" log lines
            while (!ct.IsCancellationRequested)
            {
                var minutes = _configService.Load().MailSyncPollMinutes;

                // Disabled: re-check the setting every 5 minutes so re-enabling it doesn't need a restart.
                var delay = minutes <= 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(minutes);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (_configService.Load().MailSyncPollMinutes <= 0) continue; // still disabled after the wait

                // Time the whole cycle body — reconcile + snapshot + every folder — because the question
                // #462 asks is "does one full cycle run longer than the interval?", and that's the whole
                // loop, not just the folder fetches. Start above ReconcileCurrentFolderAsync. Debug-only,
                // so a normal run pays nothing at the cycle level either.
                var sweepTimer = LogService.DebugMode ? System.Diagnostics.Stopwatch.StartNew() : null;

                // Reconcile the folder the user is currently viewing (#366). It's the one folder with
                // no other live removal signal — a custom folder (any backend) the user is staring at
                // while a message is deleted/moved elsewhere. Reconcile-on-open catches it on
                // navigation; this catches it while they sit on it. Skips virtual/aggregate folders
                // (no single server folder to list) and online mode (no store). One id-only listing.
                //
                // This MUST stay below the poll-disabled check: the setting reads "Off (server push
                // only)" in Settings, so a user who picks it has been told QuickMail will not contact
                // the server on a timer. Reconciling here regardless would make that label untrue for
                // anyone who chose it to avoid exactly that (metered or locked-down networks).
                await ReconcileCurrentFolderAsync(ct).ConfigureAwait(false);

                // Queued mail rides the same timer (#637): a network that came back without Windows
                // noticing, or an account that reconnected quietly, still drains within one poll.
                await DrainOutboxQuietlyAsync(ct).ConfigureAwait(false);

                // Snapshot EVERY non-excluded folder for EVERY account (all backends). Non-Inbox folders
                // have no live watcher — Graph's delta poll and IMAP's IDLE both cover only the Inbox —
                // so mail a server-side rule files into a custom folder at delivery is invisible until the
                // folder is opened or the app restarts (#366). This periodic sweep syncs each folder fully
                // (fetch new + reconcile deletions). The Inbox is included but cheap (kept current by the
                // live watcher). Accounts/_cachedFolders are UI-thread-owned, so snapshot on the UI thread.
                var jobs = new List<(AccountModel Account, MailFolderModel Folder, bool IsInbox)>();
                _ui.Invoke(() =>
                {
                    foreach (var account in Accounts)
                    {
                        // Sweep only accounts that actually connected — the folder cache is restored
                        // from SQLite at launch (#516), so it lists folders for offline accounts too
                        // and every job queued for one of those would just fail.
                        if (!_connectedAccountIds.Contains(account.Id)) continue;
                        if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;
                        foreach (var folder in folders)
                        {
                            if (folder.ExcludeFromAllMail) continue;
                            var isInbox = folder.Kind == Models.SpecialFolderKind.Inbox ||
                                          string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase);
                            jobs.Add((account, folder, isInbox));
                        }
                    }
                });

                // Per-folder cached item counts for the instrumentation (#462, /debug only): one grouped
                // query per account, snapshotted here with the cycle timer PAUSED so the measurement never
                // measures itself (review A). Per folder is then a dictionary lookup, not a query.
                Dictionary<Guid, Dictionary<string, int>>? folderCounts = null;
                if (sweepTimer != null)
                {
                    sweepTimer.Stop();
                    folderCounts = new Dictionary<Guid, Dictionary<string, int>>();
                    foreach (var acctId in jobs.Select(j => j.Account.Id).Distinct())
                        folderCounts[acctId] = await _localStore.CountSummariesByFolderAsync(acctId).ConfigureAwait(false);
                    sweepTimer.Start();
                }

                var sweepNew = 0;
                var pacedDelays = 0;

                foreach (var (account, folder, isInbox) in jobs)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        // Per-folder cost instrumentation (#462) — /debug only, so normal runs pay nothing.
                        // Times this one folder and counts the Graph requests it makes, scoped to this async
                        // flow via `using` (so a throwing folder can't leak the scope, and the concurrent
                        // delta poll / navigation isn't misattributed here). The counter is Graph-only, so an
                        // IMAP folder shows "req n/a" rather than a misleading 0 next to a slow time.
                        var debug = LogService.DebugMode;
                        var isGraph = account.BackendKind == BackendKind.MicrosoftGraph;
                        var folderTimer = debug ? Stopwatch.StartNew() : null;
                        using var reqScope = (debug && isGraph) ? Services.Graph.GraphClient.BeginRequestCount() : null;

                        // Fetch new (FolderSynced merges them into the current view) + reconcile
                        // deletions (MessagesRemoved). Runs off the UI thread; the events marshal back.
                        var incoming = await _syncService.SyncFolderFullAsync(account, folder, ct)
                            .ConfigureAwait(false);
                        sweepNew += incoming.Count;

                        if (folderTimer != null)
                        {
                            folderTimer.Stop();
                            var items = folderCounts != null && folderCounts.TryGetValue(account.Id, out var byFolder)
                                ? byFolder.GetValueOrDefault(folder.FullName) : 0;
                            var reqStr = reqScope != null ? $"{reqScope.Count} req" : "req n/a";
                            LogService.Debug(
                                $"Sweep folder [{account.AccountLabel}]/{folder.FullName}: " +
                                $"{folderTimer.Elapsed.TotalSeconds:F1}s, {reqStr}, {items} items, {incoming.Count} new");
                        }

                        if (incoming.Count > 0)
                            _ui.Post(() =>
                            {
                                // Watched conversations first, and in EVERY folder — a watched thread's
                                // next message can land anywhere, which is the point of the folder.
                                // Ordering is load-bearing: both paths share _notifiedMessageKeys, so
                                // running this first means a watched inbox message gets the watched
                                // toast (which says more) rather than the generic one, and gets it once.
                                MaybeNotifyWatchedMail(account, incoming);
                                // Toast only for the Inbox (as before) — filtered mail in custom folders
                                // shouldn't pop notifications — but refresh counts for any folder change.
                                if (isInbox)
                                    MaybeNotifyNewMail(account, incoming);
                                ScheduleFolderCountRefresh(account.Id);
                            });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { LogService.Log($"Sweep sync {account.AccountLabel}/{folder.FullName}", ex); }

                    // Pace between folders so a full sweep of a large mailbox doesn't burst Graph into a
                    // 503 "application request queue is full". Sequential + a short gap keeps it gentle.
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); pacedDelays++; }
                    catch (OperationCanceledException) { break; }
                }

                // Per-cycle summary at Debug (a measurement aid, not always-on telemetry; #462). The paced
                // figure is the count of delays actually executed × 250 ms — measured, not derived from the
                // folder count, so a cycle cancelled partway doesn't report paced time it never spent (review C).
                if (sweepTimer != null)
                {
                    sweepTimer.Stop();
                    var sweepAccounts = jobs.Select(j => j.Account.Id).Distinct().Count();
                    LogService.Debug(
                        $"Sweep cycle {sweepCycle}: {jobs.Count} folder(s) / {sweepAccounts} account(s), " +
                        $"{sweepTimer.Elapsed.TotalSeconds:F1}s total ({pacedDelays * 0.250:F1}s paced), {sweepNew} new.");
                }
                sweepCycle++;
            }
        }
        catch (OperationCanceledException) { }
    }

    // Reconciles the single real folder currently displayed against the server, purging any message
    // removed elsewhere from the store and the view (#366). Snapshots the UI-thread-owned SelectedFolder
    // /Accounts, then does the id-listing off the UI thread. No-op for virtual/aggregate folders (no one
    // server folder to list) and online mode (no store to reconcile).
    private async Task ReconcileCurrentFolderAsync(CancellationToken ct)
    {
        if (OnlineMode) return;

        AccountModel? account = null;
        MailFolderModel? folder = null;
        _ui.Invoke(() =>
        {
            var f = SelectedFolder;
            if (f == null || f.AccountId == Guid.Empty || IsVirtualFolder(f)) return;
            account = Accounts.FirstOrDefault(a => a.Id == f.AccountId);
            folder  = f;
        });
        if (account == null || folder == null) return;

        try { await _syncService.ReconcileFolderAsync(account, folder, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Log("Periodic reconcile of current folder", ex); }
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
            // Global "All Mail" - accept messages from every account except shared mailboxes (#31).
            relevant = incoming.Where(m => !IsSharedAccountId(m.AccountId));
        }
        else if (TryGetAccountIdFromSentinel(selected.FullName, out var watchedAccountId))
        {
            // Per-account "All Mail" - only messages belonging to that account.
            relevant = incoming.Where(m => m.AccountId == watchedAccountId);
        }
        else if (selected.FullName == AllFlaggedFolder.FullName)
        {
            // All Flagged Mail — only accept flagged incoming messages (shared mailboxes excluded, #31).
            relevant = incoming.Where(m => m.IsFlagged && !IsSharedAccountId(m.AccountId));
        }
        else if (selected.FullName == AllWatchedFolder.FullName)
        {
            // Watched Conversations — accept arrivals belonging to a watched conversation. This
            // branch IS the feature: a reply to a watched thread joins the open folder during sync
            // with no user action. It must stay above the real-folder branch below, which would
            // otherwise never let an aggregate see these messages. Shared mailboxes are excluded here
            // too (#31), so the live path agrees with the cache read in FetchWatchedAsync.
            relevant = incoming.Where(m => IsWatchedMessage(m) && !IsSharedAccountId(m.AccountId));
        }
        else if (selected.FullName == OutboxFolder.FullName)
        {
            // The Outbox lists the local queue, never synced mail (#637).
            relevant = [];
        }
        else if (IsFolderScopedAggregate(selected.FullName))
        {
            // Folder-scoped virtual folders (All Inboxes / Drafts / Sent / Trash / Archive) —
            // accept messages that came from one of the real folders the aggregate spans.
            // Build a lookup set once so the per-message check is O(1). Sourcing it from the
            // same helper the fetch uses keeps live arrivals and the loaded list in agreement.
            var matchingFolderKeys = new HashSet<(Guid, string)>(
                FolderScopedAggregateSources(selected.FullName)
                    .Select(s => (s.Account.Id, s.Folder.FullName.ToUpperInvariant())));
            relevant = incoming.Where(m =>
                matchingFolderKeys.Contains((m.AccountId, m.FolderName.ToUpperInvariant())));
        }
        else if (TryGetContactMailFromSentinel(selected.FullName, out var contactAddress, out var contactDirection))
        {
            // Contact mail results (#370) — only accept messages that still match the address, and
            // never from a shared mailbox (#31), so the live path agrees with the cache read.
            relevant = incoming.Where(m =>
                MatchesContactAddress(m, contactAddress, contactDirection) && !IsSharedAccountId(m.AccountId));
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

        // #423: live arrivals into an aggregate view must announce their source folder too.
        if (IsVirtualFolder(selected))
            ApplyFolderDisplayNames(toInsert);

        // Watch state is derived and these rows have never been through SetMessages, so stamp them
        // here — in every folder, for the same reason SetMessages does.
        StampWatchedFlags(toInsert);

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

    // ── Read/unread reconcile (#462) ──────────────────────────────────────────────
    // Called on the UI thread by SyncService when the periodic sweep finds a cached message whose
    // read/unread state was changed by another client (e.g. read on the phone). The store is already
    // updated; here we refresh the matching visible rows and the folder unread counts. This is the live
    // update the pre-#462 full-window re-fetch used to deliver as a side effect (via OnFolderSynced/#269).
    // Deliberately separate from OnFolderSynced: no new-mail inserts, no toast, no flag reconcile.
    private void OnFolderReadStatesReconciled(IReadOnlyList<MailMessageSummary> changed)
    {
        if (_suppressFolderSyncUpdates) return;
        if (changed.Count == 0) return;

        // Refresh the matching visible rows when a folder is selected. Match by per-folder identity
        // (account, folder, uid) — which the changed summaries carry — so a real-folder view and an
        // aggregate view (whose rows keep their own folder identity) both find the row. The one case this
        // misses is a cross-folder Message-ID duplicate whose displayed representative was collapsed onto
        // a different folder's copy (Gmail labels): the row's indicator then refreshes on next load.
        if (SelectedFolder != null)
        {
            var newReadByKey = new Dictionary<string, bool>(changed.Count);
            foreach (var m in changed)
                newReadByKey[MessageDeduplicator.PerFolderKeyFor(m)] = m.IsRead;

            var affected = new List<MailMessageSummary>();
            foreach (var existing in _rawMessages)
                if (newReadByKey.TryGetValue(MessageDeduplicator.PerFolderKeyFor(existing), out var isRead)
                    && existing.IsRead != isRead)
                {
                    existing.IsRead = isRead;
                    affected.Add(existing);
                }

            if (affected.Count > 0)
            {
                // A now-read message must leave the Unread view; a now-unread one must (re)appear if it
                // matches. Batched so it costs one Reset, not one event per change (screen-reader-friendly).
                using (Messages.BeginBatchScope())
                {
                    foreach (var m in affected)
                    {
                        var shouldShow = MatchesFilter(m) && MatchesDayLimit(m)
                            && (string.IsNullOrWhiteSpace(SearchText) || MatchesSearch(m));
                        var isShown = Messages.Contains(m);
                        if (shouldShow && !isShown) InsertMessageSorted(m);
                        else if (!shouldShow && isShown) Messages.Remove(m);
                    }
                }
                RebuildActiveGroupView();
            }
        }

        // Nudge the folder-tree unread badges. NOTE this only acts on IMAP/SMTP accounts — Graph folder
        // counts are server-sourced (GetFoldersAsync) and refresh on interaction, so a quiet Graph
        // folder's badge corrects on next folder open rather than live. The message-list row and the
        // cache are corrected above regardless. (Pre-existing: the sweep's count refresh has always been
        // IMAP-only; this is not introduced by #462.)
        var accountIds = new HashSet<Guid>();
        foreach (var m in changed) accountIds.Add(m.AccountId);
        foreach (var acctId in accountIds)
            ScheduleFolderCountRefresh(acctId);
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
                // Watched first — see MaybeNotifyWatchedMail; the two share a dedup set, so order
                // decides which toast a watched inbox message gets, and guarantees it gets one.
                if (incoming.Count > 0)
                    _ui.Post(() =>
                    {
                        MaybeNotifyWatchedMail(account, incoming);
                        MaybeNotifyNewMail(account, incoming);
                    });
            }
            catch (Exception ex)
            {
                LogService.Log("IDLE targeted sync", ex);
            }
        });
    });

    // Called on a ThreadPool thread by the change notifier when the Graph delta poll observes inbox
    // messages removed elsewhere (deleted or moved out by another client — Outlook web/desktop/mobile
    // or a server-side rule). The live add-only sync paths never see removals, so without this the
    // rows linger until the next startup reconcile (#366). Deletes them from the store and drops the
    // rows from any view currently showing them, reusing the same UI-removal handler as full-sync
    // reconciliation. Ids match the store because the poll and folder sync read the same backend id
    // type. Resolve UI-thread-owned state (Accounts, _cachedFolders) on the UI thread first.
    private void OnInboxMessagesRemoved(Guid accountId, IReadOnlyList<string> ids) => _ui.Post(() =>
    {
        if (ids.Count == 0) return;
        if (!_cachedFolders.TryGetValue(accountId, out var folders)) return;
        var inbox = folders.FirstOrDefault(f =>
            f.Kind == Models.SpecialFolderKind.Inbox ||
            string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase));
        if (inbox is null) return;

        LogService.Log($"Delta: {ids.Count} inbox message(s) removed elsewhere for {accountId} — dropping from cache/view.");

        // Purge the cache so a later cache-served folder open doesn't resurrect the ghost.
        if (!OnlineMode)
            _localStore.DeleteSummariesAsync(accountId, inbox.FullName, ids)
                .LogFaults("local store: delete delta-removed summaries");

        var removed = ids
            .Select(id => new MailMessageSummary { MessageId = id, AccountId = accountId, FolderName = inbox.FullName })
            .ToList();
        OnMessagesRemoved(removed);

        // Removals change server unread counts; refresh them (debounced, STATUS-authoritative).
        ScheduleFolderCountRefresh(accountId);
    });

    // Shows a Windows toast for genuinely-new inbox mail. Runs on the UI thread (the caller posts
    // it there) so _notifiedMessageKeys is single-thread-owned. Setting is re-read live so a
    // Settings change takes effect without a restart.
    internal void MaybeNotifyNewMail(AccountModel account, IReadOnlyList<MailMessageSummary> incoming)
    {
        // #31: a shared mailbox is excluded from new-mail toasts by default — it is often a high-volume role
        // mailbox and the #456 sweep would toast for each arrival. The per-account opt-in (PR 5) adds it back
        // in; the global master switch below still governs, so a shared mailbox toasts only when both are on.
        if (account.IsShared && !account.NotifyOnNewMail) return;
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

    /// <summary>
    /// Toasts messages that arrived in a watched conversation, in any folder — unlike
    /// <see cref="MaybeNotifyNewMail"/>, which is deliberately inbox-only.
    /// <para><b>Must be called before</b> <see cref="MaybeNotifyNewMail"/>, and is deliberately
    /// passed only the watched subset. Both share <c>_notifiedMessageKeys</c>, and
    /// <c>NewMailFilter.SelectNew</c> <i>consumes</i> everything it is shown — handing it the full
    /// incoming list here would mark ordinary inbox mail as already-notified and silently suppress
    /// the new-mail toast entirely.</para>
    /// </summary>
    // Internal so the ordering contract above can be tested directly — it is the whole correctness
    // argument for this pair of methods and is not observable from any public surface.
    internal void MaybeNotifyWatchedMail(AccountModel account, IReadOnlyList<MailMessageSummary> incoming)
    {
        // #31: a shared mailbox is off by default here too; its per-account opt-in (PR 5) covers watched
        // conversations as well as new mail. The global watched-conversation switch below still governs.
        if (account.IsShared && !account.NotifyOnNewMail) return;
        if (_notifications is not { IsSupported: true }) return;
        if (_watchService == null) return;
        if (!_configService.Load().NotifyOnWatchedConversation) return;

        if (_notifiedMessageKeys.Count > 10_000) _notifiedMessageKeys.Clear();

        var watched = incoming.Where(IsWatchedMessage).ToList();
        if (watched.Count == 0) return;

        var fresh = Helpers.NewMailFilter.SelectNew(watched, _notifyThresholdUtc, _notifiedMessageKeys);
        if (fresh.Count == 0) return;

        // Same wake/reconnect-backlog guard as the new-mail path: SelectNew has already claimed
        // these, so they will not re-fire; only the toast is skipped.
        if (fresh.Count > MaxNotifyBatchSize)
        {
            LogService.Log($"Watched notify [{account.AccountLabel}]: {fresh.Count} fresh " +
                           $"— toast suppressed (batch > {MaxNotifyBatchSize}).");
            return;
        }

        LogService.Log($"Watched notify [{account.AccountLabel}]: {fresh.Count} fresh in watched conversations.");
        _notifications.ShowWatchedMail(account.AccountLabel, account.Id, fresh);
    }

    // CA1859: the parameter type is fixed by the MessagesRemoved event delegate
    // (Action<IReadOnlyList<MailMessageSummary>>) this handler is bound to in the ctor — narrowing it
    // to List<> would break that method-group subscription. The analyzer only flags it because
    // OnInboxMessagesRemoved (#366 delta path) also calls it directly with a List; that call site can't
    // dictate the delegate-bound signature.
#pragma warning disable CA1859
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
#pragma warning restore CA1859

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
        // Note: _cachedFolders is restored from the local store before the first cached load since
        // #516, so ResolveFolderKind usually has real kinds even at launch and the preferred Inbox
        // representative is picked correctly straight away. On a profile that has never persisted a
        // folder list (first run, fresh --profileDir, rebuilt mail.db) it is still empty, ranking is
        // neutral (date/name tie-break), and the representative settles on the first fetch — collapse
        // is correct either way.
        if (IsVirtualFolder(SelectedFolder))
        {
            list = MessageDeduplicator.CollapseForAggregate(list, ResolveFolderKind);
            ApplyFolderDisplayNames(list);
        }
        _rawMessages = list;
        // Watch state is derived, not persisted, so it has to be stamped onto every freshly
        // materialized row — in every folder, not just the watched one. The row's spoken "watched"
        // field is meant to be readable wherever the message appears.
        StampWatchedFlags(_rawMessages);
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

    /// <summary>
    /// #423: stamps each row's source location so the accessible name can announce it in aggregate/
    /// virtual views. Account-qualified as "&lt;account&gt; -- &lt;folder&gt;" (e.g. "icanbrew -- Inbox")
    /// ONLY when the current VIEW spans more than one account — decided by
    /// <see cref="AggregateSpansMultipleAccounts"/> from the view identity (per-account All Mail vs
    /// global aggregate vs the saved view's folder set), NOT from the visible rows, so a global All Mail
    /// dominated by one account's mail still qualifies. A single-account view (per-account All Mail, a
    /// single-account saved view) announces the folder alone ("Inbox") to avoid repeating the account.
    /// <see cref="MailMessageSummary.FolderName"/> is a raw backend id (a Graph folder id, or an IMAP
    /// path), mapped through the cached folder list to the display name. On a lookup miss the existing
    /// value is left untouched (empty for fresh rows, or a caller-supplied plain-name fallback) — never
    /// a raw id. Only called for virtual views; single-folder views leave
    /// <see cref="MailMessageSummary.FolderDisplayName"/> empty (folder implied).
    /// </summary>
    private void ApplyFolderDisplayNames(List<MailMessageSummary> list)
    {
        if (list.Count == 0) return;

        // Precompute lookups once so this is O(messages), not O(messages × folders) (Kelly's review):
        // account id → label, and (account, folder full-name) → folder display name.
        var accountLabels = new Dictionary<Guid, string>();
        foreach (var a in Accounts) accountLabels[a.Id] = a.AccountLabel;
        var folderNames = new Dictionary<(Guid, string), string>();
        foreach (var kvp in _cachedFolders)
            foreach (var f in kvp.Value)
                folderNames[(kvp.Key, f.FullName)] = f.DisplayName;

        StampFolderDisplayNames(list, AggregateSpansMultipleAccounts(), accountLabels, folderNames);
    }

    /// <summary>
    /// Whether the current aggregate/virtual view spans more than one account — the account prefix is
    /// added only then (#423). Decided by the VIEW, not by which accounts happen to be visible: global
    /// All Mail spans every account even if one dominates the list and the others are barely present,
    /// while per-account All Mail is a single account whose name is already implied. A single-account
    /// saved view stays folder-only; a multi-account one qualifies.
    /// </summary>
    private bool AggregateSpansMultipleAccounts()
        => AggregateSpansMultipleAccounts(SelectedFolder?.FullName, SavedViews, Accounts.Count);

    /// <summary>
    /// Pure decision for the account-qualification (#423), extracted so it's directly testable — this
    /// is the piece that regressed once (a content-based version dropped the prefix when one account
    /// dominated the list). Decided by the VIEW identity, never by row content: per-account All Mail →
    /// one account (false); a saved view → the distinct accounts among its folders; global aggregates
    /// (All Mail / All Inboxes / Sent / Trash / Flagged) and contact-mail → <paramref name="accountCount"/>
    /// &gt; 1, since they span every account.
    /// </summary>
    internal static bool AggregateSpansMultipleAccounts(
        string? viewFullName, IEnumerable<SavedView> savedViews, int accountCount)
    {
        if (viewFullName == null) return false;

        // Per-account All Mail → the single account is already implied by the view itself.
        if (TryGetAccountIdFromSentinel(viewFullName, out _)) return false;

        // Saved view → count the distinct accounts among its folders.
        if (TryGetViewIdFromSentinel(viewFullName, out var viewId) ||
            TryGetViewAllIdFromSentinel(viewFullName, out viewId))
        {
            var sv = savedViews.FirstOrDefault(v => v.Id == viewId);
            if (sv != null)
                return sv.Folders.Select(f => f.AccountId).Distinct().Count() > 1;
        }

        return accountCount > 1;
    }

    /// <summary>
    /// Pure stamping logic for <see cref="ApplyFolderDisplayNames"/>, extracted so the format and its
    /// fallbacks are directly testable (#423). Sets each row's
    /// <see cref="MailMessageSummary.FolderDisplayName"/> to the folder alone ("Inbox"), or account-
    /// qualified "&lt;account&gt; -- &lt;folder&gt;" when <paramref name="qualifyAccount"/> is set.
    /// </summary>
    /// <param name="list">The batch to stamp.</param>
    /// <param name="qualifyAccount">True to prefix "&lt;account&gt; -- " (view spans &gt;1 account).</param>
    /// <param name="accountLabels">account id → display label.</param>
    /// <param name="folderNames">(account, folder full-name) → folder display name; case-sensitive
    /// (Ordinal): FolderName is captured from the same folder.FullName (a Graph id / IMAP path).</param>
    internal static void StampFolderDisplayNames(
        List<MailMessageSummary> list,
        bool qualifyAccount,
        IReadOnlyDictionary<Guid, string> accountLabels,
        IReadOnlyDictionary<(Guid, string), string> folderNames)
    {
        if (list.Count == 0) return;

        foreach (var m in list)
        {
            // Folder must be known — never announce a raw backend id. On a miss leave whatever's there
            // (empty for fresh rows, or a caller's plain-name fallback), don't clobber it to empty.
            if (!folderNames.TryGetValue((m.AccountId, m.FolderName), out var folder)
                || string.IsNullOrEmpty(folder))
                continue;

            m.FolderDisplayName = qualifyAccount
                && accountLabels.TryGetValue(m.AccountId, out var label) && !string.IsNullOrEmpty(label)
                ? $"{label} -- {folder}"
                : folder;
        }
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
        MessageFilter.Watched         => IsWatchedMessage(msg),
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
        SetConnectionPhase(ConnectionPhase.Connecting);
        IsBusy = true;

        // Group by incoming host: accounts sharing a server connect sequentially to stay under the
        // per-IP connection limit shared hosting enforces (same rationale as SyncService); accounts
        // on different hosts still connect in parallel.
        //
        // IncomingHost, not ImapHost — a POP3 account's IMAP host is empty, so grouping on it put
        // every POP3 account in one bucket regardless of its real server, and failed to serialize a
        // POP3 account against an IMAP account on the same host. Serializing matters more for POP3
        // than for IMAP: RFC 1939 gives a session an exclusive lock on the maildrop.
        var resultsByHost = await Task.WhenAll(
            // #31: a Graph-parent shared mailbox connects in PR 2 — it borrows the parent's token
            // (OAuthService resolver) and reads /users/{SharedAddress}. An IMAP-parent shared mailbox
            // stays deferred to PR 3 (XOAUTH2 user=), so it is still skipped here.
            Accounts.Where(a => !a.IsShared || a.BackendKind == BackendKind.MicrosoftGraph)
                    .GroupBy(a => a.IncomingHost, StringComparer.OrdinalIgnoreCase)
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
                ApplyAccountStatus(account, folders, "initial-connect");
            if (folders != null)
                SetCachedFolders(id, folders);
        }

        IsBusy = false;
        RebuildFolderListFromCache();
        // Count connections, not cache entries: since #516 _cachedFolders is pre-filled from the
        // local store at launch, so its size would report accounts that never connected.
        // Known offline: keep the cached-count text InitialLoadAsync set; "No accounts could be
        // connected" would only restate what the status label already says (#637).
        if (_connectedAccountIds.Count > 0)
            StatusText = $"{_connectedAccountIds.Count} of {Accounts.Count} account(s) connected.";
        else if (!IsKnownOffline)
            StatusText = "No accounts could be connected.";
        SetConnectionPhase(ConnectionPhase.Idle);
    }

    /// <summary>
    /// Sets IsConnected and TotalUnread on <paramref name="account"/> from the
    /// just-fetched folder list.  TotalUnread is summed across all folders.
    /// Pass <c>null</c> on connection failure to mark as disconnected.
    /// </summary>
    /// <param name="source">
    /// Short tag naming the calling site (e.g. "reachability-event", "folder-load-failed").
    /// The account list showing "disconnected" with no way to tell which of this method's nine
    /// callers decided that is the symptom under investigation, so every transition is journaled
    /// with its origin, and a flip to disconnected starts an independent reachability check.
    /// </param>
    private void ApplyAccountStatus(AccountModel account, List<MailFolderModel>? folders, string source)
    {
        var was = account.IsConnected;

        if (folders == null)
        {
            account.IsConnected = false;
            account.TotalUnread = 0;
            if (was)
            {
                ConnectionJournal.Record(
                    ConnectionEventKind.Status, account.AccountLabel, account.ImapHost,
                    "ui-shows-disconnected",
                    $"source={source} — the account list now shows this account as disconnected");
            }
            _truthProbe?.NoteDisconnected(account.Id, source);
            // This is the one place the app's online/offline verdict is fed from (#637). Most callers
            // report a real transport outcome; a connect that never reached the server (no stored
            // password, a sign-in the user has to do) says nothing about the network, and a server
            // that answered and refused is, by definition, reachable.
            NoteConnectOutcome(account.Id, source);
            return;
        }

        if (!was)
        {
            ConnectionJournal.Record(
                ConnectionEventKind.Status, account.AccountLabel, account.ImapHost,
                "ui-shows-connected", $"source={source}");
        }
        _truthProbe?.NoteConnected(account.Id, source);
        _lastConnectFailure.TryRemove(account.Id, out _);
        _connectivity?.NoteAccountReachable(account.Id, source);

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
            if (string.IsNullOrEmpty(password))
            {
                // Nothing was tried, so this says nothing about the network (#637).
                _lastConnectFailure[account.Id] = ConnectFailureKind.NotAttempted;
                return (account.Id, null);
            }
        }
        var isOAuth = account.AuthType is Models.AuthType.OAuth2Microsoft or Models.AuthType.OAuth2Google;

        // No network at all: three timed attempts would only turn "Offline" into three minutes of
        // "Connecting…" (#637). The reconnect runs when Windows reports the network back. A
        // loopback server (a local bridge or proxy) needs no network, so it is always tried.
        var loopback = IsLoopbackHost(account.IncomingHost);
        if (!loopback && _connectivity is { IsNetworkAvailable: false })
        {
            LogService.Log($"ConnectAll/{account.AccountLabel}: no network — not attempting.");
            _lastConnectFailure[account.Id] = ConnectFailureKind.Transport;
            return (account.Id, null);
        }

        // Startup retry: up to 3 attempts with backoff (30s, 45s, 60s timeouts).
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1 && !loopback && _connectivity is { IsNetworkAvailable: false })
            {
                LogService.Log($"ConnectAll/{account.AccountLabel}: network went away — giving up until it returns.");
                _lastConnectFailure[account.Id] = ConnectFailureKind.Transport;
                return (account.Id, null);
            }
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
                _lastConnectFailure[account.Id] = ConnectFailureKind.NotAttempted;
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
                _lastConnectFailure[account.Id] = ConnectFailureKind.Transport;
                return (account.Id, null);
            }
            catch (Exception ex)
            {
                // Final attempt failed. Remember what kind of failure it was: a server that refused
                // (bad credentials, a rejected login) is reachable, and must not make the app say
                // "Offline" and stop opening folders (#637).
                LogService.Log($"ConnectAll/{account.AccountLabel}: final attempt failed", ex);
                _lastConnectFailure[account.Id] = ClassifyConnectFailure(ex);
                return (account.Id, null);
            }
        }

        _lastConnectFailure[account.Id] = ConnectFailureKind.Transport;
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
            AllMailFolder, AllInboxesFolder, AllDraftsFolder, AllSentFolder,
            // All Flagged was missing here (and from the folder picker) while every other
            // aggregate was present. ApplyViewAsync resolves a saved view's VirtualFolderKey
            // against this list, so a view saved over All Flagged fell through to a fabricated
            // folder instead of the live singleton.
            AllArchiveFolder, AllTrashFolder, AllFlaggedFolder, AllWatchedFolder
        };
        if (ShowOutboxFolder) items.Add(OutboxFolder);

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

        // The Outbox count is a local read, so it is right from the first tree the user sees (#637).
        RefreshOutboxCountAsync().LogFaults("Outbox count");
    }

    private void BuildFolderTree()
    {
        // Capture the expansion state of every current node so the rebuild (which creates fresh
        // node objects) doesn't undo what the user did. Both directions are remembered, not just
        // the expanded ones: an account header is built expanded, so remembering only expansions
        // silently re-opened every account the user had collapsed on the next folder refresh —
        // which made Collapse All Folders (#590) look like it had not worked.
        var expansion = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (FolderTree != null)
            foreach (var n in FlattenAllNodes(FolderTree))
                expansion[NodeKey(n)] = n.IsExpanded;

        var roots = new List<FolderTreeNode>();

        // "Calendar" — top-level virtual folder that opens the event list.
        // Shown only when a calendar service is wired (skipped in tests / online-only builds).
        if (CalendarVm != null)
        {
            var calNode = new FolderTreeNode
            {
                Folder = CalendarFolder,
                Label  = CalendarFolder.DisplayName,
                IsCalendarNode = true,
            };
            calNode.Children.Add(new FolderTreeNode
            {
                Folder = new MailFolderModel { FullName = CalendarSourcePrefix + "all", DisplayName = "All Calendars" },
                Label  = "All Calendars",
                IsCalendarNode = true,
            });
            calNode.Children.Add(new FolderTreeNode
            {
                Folder = new MailFolderModel { FullName = CalendarSourcePrefix + "local", DisplayName = "Local Calendar" },
                Label  = "Local Calendar",
                IsCalendarNode = true,
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
                    IsCalendarNode = true,
                };

                // A grandchild per discovered calendar so the user can view Home vs. Work vs. Family.
                // With 0 or 1 calendars the account node alone suffices (no redundant single child).
                var acctCalendars = _calendarSources.Where(s => s.AccountId == acct.Id).ToList();
                if (acctCalendars.Count > 1)
                    foreach (var cal in acctCalendars)
                        acctNode.Children.Add(new FolderTreeNode
                        {
                            Folder = new MailFolderModel
                            {
                                FullName    = CalendarSourcePrefix + acct.Id.ToString("D") + "|" + Uri.EscapeDataString(cal.CalendarId),
                                DisplayName = cal.CalendarName,
                            },
                            Label = cal.CalendarName,
                            IsCalendarNode = true,
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

        // "All Mail" is a top-level group header with 7 virtual sub-folder children.
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
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllArchiveFolder, Label = AllArchiveFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllTrashFolder,   Label = AllTrashFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllFlaggedFolder, Label = AllFlaggedFolder.DisplayName });
        allMailGroup.Children.Add(new FolderTreeNode { Folder = AllWatchedFolder, Label = AllWatchedFolder.DisplayName });
        if (ShowOutboxFolder)
            allMailGroup.Children.Add(new FolderTreeNode { Folder = OutboxFolder, Label = OutboxFolder.DisplayName });
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
                // Placeholder node for accounts that have not yet loaded folders. A shared mailbox (#31)
                // stays here through PR 1 (no backend access yet) — a navigable top-level node with no
                // children — so it must carry the account id + shared flag for the node key and name.
                roots.Add(new FolderTreeNode
                {
                    IsHeader = true,
                    Label    = account.AccountLabel,
                    Folder   = null,
                    AccountId       = account.Id,
                    IsSharedAccount = account.IsShared,
                });
            }
        }

        // Restore the state captured above. A node the tree has not seen before keeps the default
        // it was built with (header groups expanded, folders collapsed).
        foreach (var n in FlattenAllNodes(roots))
            if (expansion.TryGetValue(NodeKey(n), out var wasExpanded))
                n.IsExpanded = wasExpanded;

        FolderTree = new ObservableCollection<FolderTreeNode>(roots);
        // Fresh node objects start unmarked; restore the default calendar's marker on the rebuild.
        MarkDefaultCalendarNodes();
    }

    internal static string NodeKey(FolderTreeNode n) =>
        n.Folder != null ? $"F:{n.Folder.AccountId}:{n.Folder.FullName}"
        : n.AccountId is { } id ? $"H:{id}:{n.Label}"   // #31: disambiguate same-named account/shared headers
        : $"H:{n.Label}";

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
        OnPropertyChanged(nameof(IsFilterWatched));
        OnPropertyChanged(nameof(IsFilterAllFlagged));
        OnPropertyChanged(nameof(FilterLabel));
        OnPropertyChanged(nameof(WindowTitle));

        // Before the early return below: _suppressFilterRebuild suppresses the *rebuild*, not the
        // record of what the user chose. Putting this after the return would silently drop
        // per-folder writes on every path that batches its changes.
        NoteListStateChanged(ListField.Filter);

        if (_suppressFilterRebuild) return;

        // Always rebuild Messages from _rawMessages; OnMessagesChanged then triggers
        // RebuildActiveGroupView automatically for Conversations/From/To view modes.
        ApplyFiltersAndSearch();
    }

    /// <summary>Day limit has no global-default form, so it records against the folder only.</summary>
    partial void OnActiveDayLimitChanged(int? value) => NoteListStateChanged(ListField.DayLimit);

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

        // Sort only — never ViewMode. See NoteListStateChanged.
        if (!_applyingListState) PersistGlobalSort(value);
        NoteListStateChanged(ListField.Sort);

        // Honour _suppressFilterRebuild like OnActiveFilterChanged does. SelectFolderAsync now
        // sets the sort while navigating, and rebuilding here would sort and re-announce the
        // PREVIOUS folder's messages under the new folder's name — exactly what the flag exists
        // to prevent. The fetch that follows rebuilds with the right data.
        if (_suppressFilterRebuild) return;

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

        // The stale collections are always cleared; only the rebuilds are suppressed. Leaving
        // the previous folder's groups in place while navigating is what would be read out.
        Conversations = value == ViewMode.Conversations ? Conversations : [];
        SenderGroups  = value == ViewMode.From          ? SenderGroups  : [];
        ToGroups      = value == ViewMode.To            ? ToGroups      : [];

        if (!_suppressFilterRebuild)
        {
            if (value == ViewMode.Conversations) ScheduleConversationRebuild();
            if (value == ViewMode.From)          ScheduleSenderGroupRebuild();
            if (value == ViewMode.To)            ScheduleToGroupRebuild();
        }

        // ViewMode only — never Sort. See NoteListStateChanged.
        if (!_applyingListState) PersistGlobalViewMode(value);
        NoteListStateChanged(ListField.Mode);

        // Not while navigating: SelectFolderAsync is about to start its own fetch, and this one
        // would race it. Reachable now that a folder can be remembered in To mode.
        if (!_suppressFilterRebuild &&
            value == ViewMode.To && SelectedFolder?.FullName == AllMailFolder.FullName && Messages.Any(m => string.IsNullOrWhiteSpace(m.To)))
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
            SetCachedFolders(account.Id, folderList);
            ApplyAccountStatus(account, folderList, "select-account");
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
        SearchText          = string.Empty;
        IsSearchActive      = false;
        ActiveView          = null;
        SelectedFolder      = folder;
        MessageDetail       = null;
        IsMessageOpen       = false;
        // ActiveView and SelectedFolder are set first: the resolver reads both.
        ApplyListState(ResolveListState(folder));
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
        SearchText          = string.Empty;
        IsSearchActive      = false;
        ActiveView          = null;
        SelectedFolder      = folder ?? CalendarFolder;
        MessageDetail       = null;
        IsMessageOpen       = false;
        // The calendar replaces the message list, so there is no folder presentation to resolve —
        // reset to the plain default. Returning to a mail folder resolves normally.
        ApplyListState(ListState.Default);
        _suppressFilterRebuild = false;

        // Clear the message list so stale messages are not announced while the
        // calendar list is visible.
        SetMessages([]);

        await CalendarVm.LoadAsync();
        CalendarPaneFocusRequested?.Invoke();

        // LoadAsync reads the local store and nothing else, so an appointment added on the server
        // side — in Gmail, in Outlook, on a phone — was not here until the 15-minute timer came
        // round or the user pressed F5. That is issue #519 exactly: "add an event to your Gmail
        // calendar... it will not be there. Press F5... now it will show up."
        //
        // Deliberately not awaited: the calendar opens on the cache immediately, at the speed it
        // always did, and the pull folds anything new in when it lands. RunGraphCalendarSyncAsync
        // swallows its own failures, so nothing here can surface an error or break the open.
        //
        // Its continuations stay on the UI thread without an explicit _ui.Post hop, unlike the
        // timer pass which needs one: this path is only ever reached through SelectFolderCommand,
        // dispatched from the View, so the WPF SynchronizationContext is already in place when the
        // awaits resume — which matters because the pull calls BuildFolderTree().
        _ = SyncCalendarOnOpenAsync();
    }

    /// <summary>
    /// The server pull that opening the calendar starts. Throttled, because opening the calendar is
    /// something a user does repeatedly; quiet, because the view has just announced itself.
    /// </summary>
    private async Task SyncCalendarOnOpenAsync()
    {
        if (_graphCalendarSync == null || _calendarService == null || OnlineMode) return;
        if (DateTime.UtcNow - _lastCalendarPullUtc < CalendarOpenSyncThrottle) return;

        await RunGraphCalendarSyncAsync(CalendarSyncFollowUp.RefreshAndAnnounceIfChanged);
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

                // Known offline: the cache is the answer, and a server refresh would only turn
                // "Loading…" into a raw socket error twenty seconds later (#637). The reconnect path
                // refreshes the folder when something answers again.
                if (IsKnownOffline)
                {
                    StatusText = cached.Count > 0
                        ? CachedCountText(cached.Count)
                        : $"Offline — no cached messages in {folder.DisplayName}.";
                    if (cached.Count > 0 && IsConversationsView)
                        ScheduleConversationRebuild();
                    IsBusy = false;
                    return;
                }

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

            // Reconcile-on-open (#366): RefreshFolderFromServerAsync replaces the *displayed* list with
            // server truth, but only upserts the store — it never deletes rows for messages removed
            // elsewhere, so those ghosts persist in the cache and resurface in aggregate views (All
            // Mail) and on the next cache-load. Purge them with a full-id reconcile against the server.
            // Cached mode only (online keeps no store, and its UI is already server-truth). Fire-and-
            // forget on the same load token so navigating away cancels it; idempotent and cheap.
            if (!OnlineMode)
            {
                var account = Accounts.FirstOrDefault(a => a.Id == accountId);
                if (account != null)
                    _syncService.ReconcileFolderAsync(account, folder, ct)
                        .LogFaults("reconcile on folder open");
            }
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

    /// <summary>
    /// How long a folder listing may take before the user is told the server is slow (#637). A bound
    /// on the wait, not a connectivity verdict: a large folder on a thin link can legitimately take
    /// this long, so a timeout here never marks the account unreachable.
    /// </summary>
    private static readonly TimeSpan FolderRefreshTimeout = TimeSpan.FromSeconds(45);

    private async Task RefreshFolderFromServerAsync(
        Guid accountId, MailFolderModel folder, int version, CancellationToken ct)
    {
        // Bounded: a black-holed network otherwise leaves "Loading {folder}…" on screen until
        // MailKit's own two-minute timeout, or forever when the account never connected (#637).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FolderRefreshTimeout);
        var token = timeoutCts.Token;
        try
        {
            var list = _syncDays > 0
                ? await _imap.GetMessagesSinceDateAsync(accountId, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), token)
                : await _imap.GetMessageSummariesAsync(accountId, folder.FullName, 50000, token);
            _connectivity?.NoteAccountReachable(accountId, "folder-loaded");
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
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (version == _folderLoadVersion)
                StatusText = "Message list load cancelled.";
            LogService.Debug($"RefreshFolderFromServer: cancelled ({ex.Message})");
        }
        catch (OperationCanceledException)
        {
            // The bound above fired: slow, not necessarily gone. Say so and leave the verdict to a
            // real transport failure, or the next attempt.
            if (version == _folderLoadVersion)
                StatusText = !OnlineMode && Messages.Count > 0
                    ? $"Showing {Messages.Count} cached {(Messages.Count == 1 ? "message" : "messages")} — the server is slow to answer."
                    : $"{folder.DisplayName} is taking a long time to load. The server is slow to answer.";
            LogService.Log($"RefreshFolderFromServer: {folder.DisplayName} did not answer within {FolderRefreshTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            _connectivity?.NoteOperationOutcome(accountId, ex, "folder-load-failed", ct);
            if (version == _folderLoadVersion)
                StatusText = OfflineOrErrorStatus(ex, ct,
                    () => OnlineMode ? $"Offline — could not load {folder.DisplayName}."
                        : Messages.Count > 0 ? CachedCountText(Messages.Count)
                        : $"Offline — no cached messages in {folder.DisplayName}.",
                    () => $"Failed to load messages: {ex.Message}");
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
        // An Outbox row has no server message behind it and no reading-pane form; Enter opens it in
        // the compose window (#637).
        if (IsOutboxRow(summary))
        {
            SelectedMessage = summary;
            MessageDetail   = null;
            IsMessageOpen   = false;
            return;
        }
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
                // Serve from cache when available; fall back to IMAP and cache the result. Two scopes,
                // per the standard fetch pattern (docs/ARCHITECTURE.md): a store failure must fall
                // through to the server, not skip it (#637).
                MailMessageDetail? cached = null;
                try
                {
                    cached = await _localStore.LoadDetailAsync(
                        summary.AccountId, summary.FolderName, summary.MessageId);
                }
                catch (Exception ex)
                {
                    LogService.Log("SelectMessage: local store unavailable — falling back to the server", ex);
                }
                if (cached != null)
                {
                    detail = cached;
                }
                else
                {
                    detail = await _imap.GetMessageDetailAsync(
                        summary.AccountId, summary.FolderName, summary.MessageId, token);
                    _connectivity?.NoteAccountReachable(summary.AccountId, "message-loaded");
                }
                // A pre-from_addr cache row carries a bare display name where the sender's address
                // belongs (issue #636); re-fetch it so the header and any reply get a real address.
                detail = await RepairMissingFromAddressAsync(detail, background: false, token);
                // Await (not fire-and-forget) so the detail is definitely in the cache
                // before the user acts on the message (e.g. accepts a calendar invite).
                // The calendar harvest reads calendar_ics from this row; if the store
                // hasn't completed, the event won't be harvestable and opening it from
                // the calendar list will fail with "message not found".
                try { await _localStore.UpsertDetailAsync(detail); }
                catch (Exception ex) { LogService.Log("SelectMessage: cache write failed", ex); }
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
            _connectivity?.NoteOperationOutcome(summary.AccountId, ex, "message-load-failed");
            if (loadVersion == _messageLoadVersion)
                StatusText = OfflineOrErrorStatus(ex, CancellationToken.None,
                    () => "This message is not available offline.",
                    () => $"Failed to load message: {ex.Message}");
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
            if (ct.IsCancellationRequested) return;
            if (cached != null)
            {
                // Cached, but possibly a pre-from_addr row whose From is a bare display name
                // (issue #636). Repairing it here means the reading pane and a reply get a real
                // address without the user waiting for a fetch at open time.
                await RepairMissingFromAddressAsync(cached, background: true, ct);
                return;
            }

            var detail = await _imap.PrefetchMessageDetailAsync(
                summary.AccountId, summary.FolderName, summary.MessageId, ct);
            if (ct.IsCancellationRequested) return;
            await _localStore.UpsertDetailAsync(detail);
            LogService.Debug($"Prefetched msgId={summary.MessageId} folder={summary.FolderName}");
        }
        catch (OperationCanceledException) { /* expected on switch */ }
        catch (AccountNotConnectedException)
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
            await RunGraphCalendarSyncAsync(CalendarSyncFollowUp.CallerHandlesIt);
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

            // Capture the previous set BEFORE updating the cache — SetCachedFolders replaces it.
            var isNew = !_cachedFolders.TryGetValue(account.Id, out var prev) || FolderSetChanged(prev, folders);

            // A successful GetFoldersAsync means this account IS connected — the client pool
            // connects lazily on demand — so record that regardless of whether the tree needs
            // rebuilding. Since #516 the folder cache is restored from disk at launch, so an account
            // that was down at startup and has come back returns a folder set identical to the
            // persisted one, making isNew false. Marking connected only inside that branch left such
            // an account absent from _connectedAccountIds for the rest of the session: no IDLE
            // watcher and so no new-mail notifications, skipped by the sweep and by All Mail,
            // undercounted in the status bar. F5 used to recover it completely.
            SetCachedFolders(account.Id, folders);

            if (isNew)
            {
                ApplyAccountStatus(account, folders, "refresh-folder-lists");
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

    /// <summary>
    /// Deactivates the current saved view and restores whatever the folder would show on its own.
    /// Every field the view set is restored, not a subset — that asymmetry was issue #520.
    /// </summary>
    [RelayCommand]
    private async Task ClearViewAsync()
    {
        // A multi-folder view leaves SelectedFolder on a \0View:{id} sentinel whose message set
        // *is* the view, so restoring presentation alone would change only the window title.
        // Go home instead, which is also what the menu item has always claimed to do.
        if (SelectedFolder != null && IsViewSentinel(SelectedFolder.FullName))
        {
            await SelectFolderAsync(AllMailFolder);
            return;
        }

        ActiveView = null;
        ApplyListState(ResolveListState(SelectedFolder));

        if (IsVirtualFolder(SelectedFolder))
            await FetchVirtualAsync(SelectedFolder!);
        else if (SelectedFolder != null && SelectedFolder.AccountId != Guid.Empty)
            await FetchFolderAsync();
        else
            ApplyFiltersAndSearch();
    }

    /// <summary>
    /// Forgets this folder's remembered presentation so it goes back to the global default.
    /// The escape hatch for per-folder memory (#520) — a customised folder is otherwise pinned.
    /// </summary>
    [RelayCommand]
    private void ResetFolderView()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsHeader || string.IsNullOrEmpty(folder.FullName)) return;

        // Nothing is ever stored for the calendar or for a view's sentinel folder, so there is
        // nothing to reset — and confirming a reset that could not have happened is worse than
        // staying quiet. The menu item is bound to a command, not to IsAvailable, so this is the
        // gate that actually runs.
        if (IsCalendarFolderName(folder.FullName) || IsViewSentinel(folder.FullName)) return;

        _folderViewState?.Forget(folder.AccountId, folder.FullName);

        ActiveView = null;
        ApplyListState(_defaultListState);
        ApplyFiltersAndSearch();

        // No control reflects the deletion of stored state, so this is the only confirmation.
        Announce("Folder view reset.", AnnouncementCategory.Result);
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
                cached = ExcludeSharedMail(await _localStore.LoadAllSummariesAsync()); // #31: All Mail excludes shared
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
                // Connected, not merely cached: this phase issues live IMAP fetches, and since #516
                // the folder cache is populated before any account connects.
                .Where(a => _connectedAccountIds.Contains(a.Id) && !a.IsShared)   // #31: shared excluded from All Mail
                .Select(account => (OnlineMode || needsRecipientRepair)
                    ? FetchAccountAllFoldersAsync(account, ct)
                    : FetchAccountNewMessagesAsync(account, ct));

            var accountResults = await Task.WhenAll(perAccountTasks);
            var newMessages = accountResults.SelectMany(r => r).ToList();
            if (!IsCurrentFolderLoad(loadVersion, AllMailFolder))
                return;

            // #423: All Mail's incremental adds go through InsertMessageSorted (not SetMessages), so
            // stamp the source folder here too — otherwise newly-fetched rows announce no folder.
            ApplyFolderDisplayNames(newMessages);
            // Same reason, same rows: these bypass SetMessages, so the derived watch flag has to be
            // stamped here too or the newest rows speak no watch state while the cached ones do.
            StampWatchedFlags(newMessages);

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
        if (IsFolderScopedAggregate(folder.FullName))     return FetchVirtualFolderAsync(folder.FullName);
        if (folder.FullName == AllFlaggedFolder.FullName) return FetchAllFlaggedAsync();
        if (folder.FullName == AllWatchedFolder.FullName) return FetchWatchedAsync();
        if (folder.FullName == OutboxFolder.FullName)     return FetchOutboxAsync();
        if (TryGetAccountIdFromSentinel(folder.FullName, out var accountId)) return FetchAccountAllMailAsync(accountId);
        if (TryGetContactMailFromSentinel(folder.FullName, out var contactAddress, out var contactDirection))
            return FetchContactMailAsync(contactAddress, contactDirection);

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
    /// True when this message belongs to a conversation the user is watching. The watch set is the
    /// single source of truth — there is deliberately no persisted per-message watch state to
    /// disagree with it. A null service means the feature is not wired up (tests), so nothing is
    /// watched.
    /// </summary>
    private bool IsWatchedMessage(MailMessageSummary msg) =>
        _watchService?.IsWatched(msg.Subject) == true;

    /// <summary>
    /// Stamps <see cref="MailMessageSummary.IsWatched"/> from the watch set. Called after every load
    /// into the watched folder and after every toggle. IsWatched is observable, so re-stamping
    /// refreshes the affected rows in place without rebuilding the list and losing focus.
    /// </summary>
    private void StampWatchedFlags(IEnumerable<MailMessageSummary> messages)
    {
        if (_watchService == null) return;
        foreach (var m in messages)
            m.IsWatched = _watchService.IsWatched(m.Subject);
    }

    /// <summary>
    /// Loads the Watched Conversations virtual folder: every cached message across all accounts and
    /// folders whose conversation is watched. Structurally the same predicate-aggregate shape as
    /// <see cref="FetchAllFlaggedAsync"/> and <see cref="FetchContactMailAsync"/> — see the spec's
    /// §5.3 for why the three are deliberately not yet unified.
    /// </summary>
    private async Task FetchWatchedAsync()
    {
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        var expectedFolder = SelectedFolder;
        Messages.Clear();
        StatusText = "Loading watched conversations…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            List<MailMessageSummary> all;
            if (OnlineMode)
            {
                // In --online mode there is no cache to search, so sweep every non-excluded folder
                // across all accounts and match client-side — same shape as All Flagged.
                all = new List<MailMessageSummary>();
                foreach (var account in Accounts)
                {
                    if (account.IsShared) continue;   // #31: shared excluded from All Watched
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
                            all.AddRange(msgs.Where(IsWatchedMessage));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Log($"FetchWatched online {account.DisplayName}/{folder.DisplayName}", ex);
                        }
                    }
                }
            }
            else
            {
                all = ExcludeSharedMail(await _localStore.LoadAllSummariesAsync()); // #31: aggregate excludes shared
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            var watched = all.Where(IsWatchedMessage).ToList();
            await ResolveFlagNamesAsync(watched);
            SetMessages(watched.OrderByDescending(m => m.Date).ToList());
            var n = Messages.Count;
            StatusText = n == 0
                ? "No watched conversations. Press Ctrl+Shift+W on a message to watch its conversation."
                : $"{n} watched {(n == 1 ? "message" : "messages")}.";
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = "Watched conversations load cancelled.";
        }
        catch (Exception ex)
        {
            LogService.Log("FetchWatched failed", ex);
            StatusText = "Could not load watched conversations.";
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
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
                    if (account.IsShared) continue;   // #31: shared excluded from All Flagged
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
                all = ExcludeSharedMail(await _localStore.LoadAllSummariesAsync()); // #31: aggregate excludes shared
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

    /// <summary>
    /// Opens the "mail from / to this contact" results view for an address (issue #370): every
    /// message in the local cache whose From (or To) header mentions it, across all accounts and
    /// folders. Called from the address book; also the entry point for any future caller.
    /// Reuses <see cref="SelectFolderAsync"/> so filter/search/view state is reset exactly the
    /// same way navigating to any other folder resets it.
    /// </summary>
    public Task ShowContactMailAsync(string address, ContactMailDirection direction, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return Task.CompletedTask;
        // Remember where to go back to when the results are closed. Searching again from
        // inside a results view keeps the original folder as the destination, so one Escape
        // always lands back in real mail rather than in the previous search.
        if (!IsContactMailView) _contactMailReturnFolder = SelectedFolder;
        return SelectFolderAsync(
            CreateContactMailVirtualFolder(address.Trim(), direction, label));
    }

    /// <summary>True while the message list is showing contact-mail results (issue #370).</summary>
    public bool IsContactMailView =>
        SelectedFolder != null && TryGetContactMailFromSentinel(SelectedFolder.FullName, out _, out _);

    // The folder the user was in when the contact-mail search started; null when the search
    // began before any folder was open (a fresh profile), in which case closing goes to All Mail.
    private MailFolderModel? _contactMailReturnFolder;

    /// <summary>
    /// Closes the contact-mail results and returns to the folder the search started from
    /// (All Mail if there wasn't one), re-fetching it exactly as selecting it would.
    /// </summary>
    [RelayCommand]
    public async Task CloseContactMailAsync()
    {
        if (!IsContactMailView) return;
        var back = _contactMailReturnFolder ?? AllMailFolder;
        _contactMailReturnFolder = null;
        await SelectFolderAsync(back);
    }

    private async Task FetchContactMailAsync(string address, ContactMailDirection direction)
    {
        var loadVersion    = Interlocked.Increment(ref _folderLoadVersion);
        var expectedFolder = SelectedFolder;
        var kind           = direction == ContactMailDirection.From ? "from" : "to";
        Messages.Clear();
        StatusText = $"Searching for mail {kind} {address}…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            List<MailMessageSummary> all;
            if (OnlineMode)
            {
                // In --online mode there is no cache to search, so sweep every non-excluded
                // folder across all accounts and match client-side — same shape as All Flagged.
                all = new List<MailMessageSummary>();
                foreach (var account in Accounts)
                {
                    if (account.IsShared) continue;   // #31: shared excluded from contact-mail results
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
                            all.AddRange(msgs.Where(m => MatchesContactAddress(m, address, direction)));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Log($"FetchContactMail online {account.DisplayName}/{folder.DisplayName}", ex);
                        }
                    }
                }
            }
            else
            {
                all = ExcludeSharedMail(await _localStore.LoadAllSummariesAsync()); // #31: aggregate excludes shared
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            var matches = all.Where(m => MatchesContactAddress(m, address, direction)).ToList();
            await ResolveFlagNamesAsync(matches);
            SetMessages(matches.OrderByDescending(m => m.Date).ToList());
            var n = Messages.Count;
            StatusText = n == 0
                ? $"No messages {kind} {address}."
                : $"{n} {(n == 1 ? "message" : "messages")} {kind} {address}.";
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = "Contact mail search cancelled.";
        }
        catch (Exception ex)
        {
            LogService.Log("FetchContactMail failed", ex);
            StatusText = $"Could not search for mail {kind} {address}.";
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    /// <summary>
    /// Supplied by the View: resolves which conversation the watch toggle should act on.
    /// <para>It cannot simply be <see cref="SelectedMessage"/>.<c>Subject</c>, because selecting a
    /// group header in the Conversations / From / To trees does not update
    /// <see cref="SelectedMessage"/> (see <c>GroupedMessageTreeController.OnSelectedItemChanged</c>);
    /// reading the selected message while a header is selected would watch whatever thread happened
    /// to be selected before — a silent wrong-target bug. Only the View knows which tree is showing
    /// and what is selected in it. Null falls back to the selected message, which is correct for any
    /// host without group trees.</para>
    /// </summary>
    public Func<string?>? WatchTargetResolver { get; set; }

    /// <summary>The subject whose conversation the watch toggle acts on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatchTargetWatched))]
    [NotifyPropertyChangedFor(nameof(HasWatchTarget))]
    private string? _watchTargetSubject;

    // Pure read of the current target. Kept separate from RefreshWatchTarget so availability can be
    // polled without mutating state.
    private string? ResolveWatchTarget() =>
        WatchTargetResolver != null ? WatchTargetResolver() : SelectedMessage?.Subject;

    private static bool HasWatchableSubject(string? subject) =>
        ConversationBuilder.NormalizeSubject(subject ?? string.Empty).Length > 0;

    /// <summary>Recomputes <see cref="WatchTargetSubject"/> from the View's resolver.</summary>
    public void RefreshWatchTarget() => WatchTargetSubject = ResolveWatchTarget();

    /// <summary>True when there is something for the watch toggle to act on.</summary>
    public bool HasWatchTarget => _watchService != null && HasWatchableSubject(WatchTargetSubject);

    /// <summary>
    /// Supplied by the View: the messages of the group header selected in the active grouped view,
    /// newest first, or null when the selection is a single message.
    /// <para>Same shape and same reason as <see cref="WatchTargetResolver"/> — selecting a group
    /// header does not update <see cref="SelectedMessage"/> (see
    /// <c>GroupedMessageTreeController.OnSelectedItemChanged</c>), so only the View can tell that a
    /// whole conversation, sender, or recipient is selected. Without it every action below reads
    /// the message that was selected before the user arrowed onto the header, and acts on that —
    /// silently the wrong one. That was issue #566.</para>
    /// </summary>
    public Func<IReadOnlyList<MailMessageSummary>?>? SelectedGroupResolver { get; set; }

    /// <summary>The selected group's messages, or null when a group header is not the selection.</summary>
    private IReadOnlyList<MailMessageSummary>? SelectedGroupMessages() =>
        SelectedGroupResolver?.Invoke() is { Count: > 0 } messages ? messages : null;

    /// <summary>
    /// True when a message action has something to act on: a selected message, or a selected group
    /// header. The availability gate for every command that acts on the mail selection, so the
    /// hotkey, the menu bar and the Command Palette agree on what is possible (issue #566).
    /// </summary>
    public bool CanActOnSelection() => HasSelectedMessage || SelectedGroupMessages() != null;

    /// <summary>
    /// Backs the Message menu's dimming — a property, because the menu binds to it. Recomputed as
    /// the menu opens (<see cref="RefreshMessageTarget"/>), because a header selection changes it
    /// without changing <see cref="SelectedMessage"/>.
    /// </summary>
    [ObservableProperty]
    private bool _hasMessageTarget;

    /// <summary>Recomputes <see cref="HasMessageTarget"/> from the View's resolver.</summary>
    public void RefreshMessageTarget() => HasMessageTarget = CanActOnSelection();

    /// <summary>
    /// Points Reply, Reply All and Forward at the newest message of a selected group: one reply to
    /// the latest word in the thread, not one per message. The group context menus already work
    /// this way — they set <see cref="SelectedMessage"/> to <c>Messages[0]</c> before composing —
    /// and this is the same rule for the menu bar, the hotkeys and the Command Palette.
    /// </summary>
    private void RetargetToGroupNewest()
    {
        if (SelectedGroupMessages() is { } messages) SelectedMessage = messages[0];
    }

    /// <summary>
    /// Opens the Watched Conversations folder and selects the newest message of one conversation.
    /// Called by the manager's "go to conversation". Selecting the folder rather than filtering
    /// keeps every other view control (mode, sort, fields) behaving exactly as it does when the
    /// user navigates there themselves.
    /// </summary>
    public async Task ShowWatchedConversationAsync(string normalizedSubject)
    {
        await SelectFolderCommand.ExecuteAsync(AllWatchedFolder);

        var key = ConversationBuilder.NormalizeSubject(normalizedSubject ?? string.Empty);
        if (key.Length == 0) return;

        // Messages is already date-descending, so the first match is the newest.
        var target = Messages.FirstOrDefault(m =>
            string.Equals(ConversationBuilder.NormalizeSubject(m.Subject), key,
                          StringComparison.OrdinalIgnoreCase));
        if (target == null) return;

        SelectedMessage = target;
        MessageListFocusRequested?.Invoke();
    }

    /// <summary>True when the current watch target's conversation is watched — drives the menu check.</summary>
    public bool IsWatchTargetWatched =>
        WatchTargetSubject != null && _watchService?.IsWatched(WatchTargetSubject) == true;

    /// <summary>
    /// Watches or unwatches <see cref="WatchTargetSubject"/>'s whole conversation (Ctrl+Shift+W).
    /// One command does both directions so there is no separate unwatch action to discover.
    /// </summary>
    public void ToggleWatchConversation()
    {
        RefreshWatchTarget();
        ToggleWatchConversationFor(WatchTargetSubject);
    }

    /// <summary>
    /// Watches or unwatches a named conversation. The entry point for callers that already know
    /// the subject and must not go through the View's resolver — a separate message window, and
    /// the Watched Conversations manager.
    /// </summary>
    public void ToggleWatchConversationFor(string? subject)
    {
        // Keep the menu's check state pointed at what was just acted on, so a toggle from a
        // message window does not leave the main window's menu describing a different thread.
        WatchTargetSubject = subject;
        // Every path below ends by re-raising IsWatchTargetWatched, including the early returns:
        // the menu item is IsCheckable, so WPF has already flipped its own check state by the time
        // this runs, and a path that returns without notifying would leave it reporting a lie.
        try
        {
            if (_watchService == null || subject == null) return;

            if (_watchService.IsWatched(subject))
            {
                var label = DescribeConversation(subject);
                _watchService.Unwatch(subject);
                RefreshWatchState(subject);
                Announce($"Stopped watching: {label}", AnnouncementCategory.Result);
                return;
            }

            if (!_watchService.Watch(subject))
            {
                // The only way Watch fails for a conversation that is not already watched: the
                // subject normalizes to empty. An empty key would match every blank-subject message
                // in every account, so it is refused rather than silently stored. Said in the status
                // bar as well as announced, so the refusal is not invisible to a user who has turned
                // result announcements off.
                StatusText = "Cannot watch a conversation with no subject.";
                Announce("Cannot watch a conversation with no subject.", AnnouncementCategory.Result);
                return;
            }

            RefreshWatchState(subject);
            Announce($"Watching conversation: {DescribeConversation(subject)}",
                     AnnouncementCategory.Result);
        }
        finally
        {
            OnPropertyChanged(nameof(IsWatchTargetWatched));
        }
    }

    // The spoken name of a conversation: its normalized subject, which is what the watch actually
    // covers (so "Re: Budget" and "Budget" announce identically, as one conversation).
    private static string DescribeConversation(string subject)
    {
        var normalized = ConversationBuilder.NormalizeSubject(subject);
        return normalized.Length > 0 ? normalized : "(no subject)";
    }

    /// <summary>
    /// Re-syncs everything derived from the watch list for one conversation, after something other
    /// than this VM changed it (today: the Watched Conversations manager, which is modeless and so
    /// can prune while the watched folder is visible behind it).
    /// </summary>
    public void RefreshWatchStateFor(string subject) => RefreshWatchState(subject);

    /// <summary>
    /// Re-stamps <see cref="MailMessageSummary.IsWatched"/> after a watch toggle, and — when the
    /// Watched Conversations folder is open — drops the rows that just stopped qualifying, since
    /// this folder's membership is exactly the watch predicate.
    /// </summary>
    private void RefreshWatchState(string subject)
    {
        OnPropertyChanged(nameof(IsWatchTargetWatched));

        var key = ConversationBuilder.NormalizeSubject(subject);
        if (key.Length == 0) return;

        bool SameConversation(MailMessageSummary m) =>
            string.Equals(ConversationBuilder.NormalizeSubject(m.Subject), key,
                          StringComparison.OrdinalIgnoreCase);

        StampWatchedFlags(_rawMessages.Where(SameConversation));
        StampWatchedFlags(Messages.Where(SameConversation));

        if (SelectedFolder?.FullName != AllWatchedFolder.FullName)
        {
            // The Watched filter is the same predicate applied to an ordinary folder, so a toggle
            // changes what qualifies there too. Without this the list keeps showing messages it
            // claims to have filtered out, until something else happens to re-apply.
            if (ActiveFilter == MessageFilter.Watched)
                ApplyFiltersAndSearch();
            return;
        }
        if (_watchService?.IsWatched(subject) == true) return;

        // Unwatched while viewing the watched folder: the whole conversation leaves, not just the
        // focused row. Focus moves to the row that follows the removed block so the user is not
        // stranded at the top of the list.
        var leaving = Messages.Where(SameConversation).ToList();
        if (leaving.Count == 0) return;

        var firstIndex = Messages.IndexOf(leaving[0]);

        // The open message may be one of the rows leaving. Clear the reading pane before removing
        // them, exactly as delete, archive, RemoveVanishedMessages and OnMessagesRemoved all do —
        // otherwise the pane keeps rendering a message that is no longer in the list, and every
        // command gated on IsMessageOpen stays live against it.
        if (leaving.Any(m => ReferenceEquals(m, SelectedMessage)) || SelectedMessage == null)
        {
            MessageDetail = null;
            IsMessageOpen = false;
        }

        _rawMessages.RemoveAll(SameConversation);
        foreach (var m in leaving)
            Messages.Remove(m);

        // Note: deliberately no UpdateAccountCountsAfterRemoval here. Unlike the delete/archive/
        // vanish paths, these messages still exist and are still unread wherever they live — only
        // this view's membership changed.
        RebuildActiveGroupView();

        if (Messages.Count == 0)
        {
            SelectedMessage = null;
            StatusText = "No watched conversations. Press Ctrl+Shift+W on a message to watch its conversation.";
            return;
        }

        var n = Messages.Count;
        StatusText = $"{n} watched {(n == 1 ? "message" : "messages")}.";

        if (ViewMode == ViewMode.Messages)
        {
            // The row that had keyboard focus was just removed from the ListView, so focus has to be
            // asked back explicitly — same as archive.
            SelectedMessage = Messages[Math.Min(firstIndex, Messages.Count - 1)];
            MessageListFocusRequested?.Invoke();
        }
        else
        {
            // In the group trees, focus is the tree's business after RebuildActiveGroupView replaces
            // its items. Clearing SelectedMessage keeps HasSelectedMessage false so the global
            // per-message hotkeys don't act on a row the user never selected. (Same rationale as
            // archive and delete.)
            SelectedMessage = null;
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

            // #423: per-account All Mail's incremental adds go through InsertMessageSorted (not
            // SetMessages), so stamp the source folder here too — otherwise the newest rows (at the
            // top) announce no folder while the cached rows below them do. Mirrors FetchAllMailAsync.
            // The OnlineMode branch below is already covered — it flows through SetMessages.
            ApplyFolderDisplayNames(newMessages);
            // Same reason, same rows: these bypass SetMessages, so the derived watch flag has to be
            // stamped here too or the newest rows speak no watch state while the cached ones do.
            StampWatchedFlags(newMessages);

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

    /// <summary>
    /// The folder-scoped aggregates: virtual folders whose contents are the union of a specific set
    /// of real folders on every account. All Mail (union of everything non-excluded), All Flagged
    /// (a message-level predicate) and the saved-view / contact-mail sentinels are deliberately not
    /// in this family — they resolve their sources differently.
    /// </summary>
    private static bool IsFolderScopedAggregate(string? fullName) =>
        fullName != null &&
        (string.Equals(fullName, AllInboxesFolder.FullName, StringComparison.Ordinal) ||
         string.Equals(fullName, AllDraftsFolder.FullName,  StringComparison.Ordinal) ||
         string.Equals(fullName, AllSentFolder.FullName,    StringComparison.Ordinal) ||
         string.Equals(fullName, AllTrashFolder.FullName,   StringComparison.Ordinal) ||
         string.Equals(fullName, AllArchiveFolder.FullName, StringComparison.Ordinal));

    /// <summary>True if the given account id belongs to a shared mailbox (#31) — used to keep shared
    /// mail out of the global aggregates (All Mail / All Flagged / All Watched / contact-mail) while it
    /// still gets swept.</summary>
    internal bool IsSharedAccountId(Guid accountId) =>
        Accounts.FirstOrDefault(a => a.Id == accountId)?.IsShared == true;

    /// <summary>
    /// Drops shared-mailbox messages (#31) from a cross-account aggregate read from cache. The global
    /// aggregates never include shared mail; this is the cache-read counterpart to the
    /// <see cref="IsSharedAccountId"/> filter the live-arrival path applies in <c>OnFolderSynced</c>, so
    /// the fetch and the live filter can never disagree (the invariant <see cref="FolderScopedAggregateSources"/>
    /// documents). Fast-paths the common no-shared-accounts case so All Mail loads pay nothing for it.
    /// </summary>
    private List<MailMessageSummary> ExcludeSharedMail(IEnumerable<MailMessageSummary> summaries)
    {
        var sharedIds = Accounts.Where(a => a.IsShared).Select(a => a.Id).ToHashSet();
        return sharedIds.Count == 0
            ? summaries as List<MailMessageSummary> ?? summaries.ToList()
            : summaries.Where(m => !sharedIds.Contains(m.AccountId)).ToList();
    }

    /// <summary>
    /// The real (account, folder) pairs a folder-scoped aggregate spans. All Inboxes / Drafts / Sent
    /// / Trash match on <see cref="SpecialFolderKind"/>; All Archive resolves each account's archive
    /// destination through <see cref="ResolveArchiveFolder"/> so a per-account override is honored
    /// and the aggregate always shows exactly where Move to Archive puts mail. Accounts whose folder
    /// list has not been cached yet, and accounts with no archive destination, contribute nothing.
    /// Both the fetch and the live-arrival filter read this, so they can never disagree.
    ///
    /// <para><paramref name="connectedOnly"/> exists because the two callers want different sets
    /// since #516. A LIVE fetch must skip accounts that never connected: the client pool connects
    /// lazily, so including one turns opening All Inboxes into a blocking wait on its connect
    /// timeout before anything renders — the same hazard the periodic sweep already guards against.
    /// A CACHE read wants every account whose folders we know, which is exactly what the restored
    /// cache gives us and why the startup load can render All Inboxes offline at all.</para>
    /// </summary>
    internal IEnumerable<(AccountModel Account, MailFolderModel Folder)> FolderScopedAggregateSources(
        string fullName, bool connectedOnly = false)
    {
        var isArchive = string.Equals(fullName, AllArchiveFolder.FullName, StringComparison.Ordinal);
        var kind = string.Equals(fullName, AllInboxesFolder.FullName, StringComparison.Ordinal) ? SpecialFolderKind.Inbox
                 : string.Equals(fullName, AllDraftsFolder.FullName,  StringComparison.Ordinal) ? SpecialFolderKind.Drafts
                 : string.Equals(fullName, AllSentFolder.FullName,    StringComparison.Ordinal) ? SpecialFolderKind.Sent
                 : string.Equals(fullName, AllTrashFolder.FullName,   StringComparison.Ordinal) ? SpecialFolderKind.Trash
                 : SpecialFolderKind.None;
        if (!isArchive && kind == SpecialFolderKind.None) yield break;

        foreach (var account in Accounts)
        {
            if (account.IsShared) continue;   // #31: shared mailboxes are excluded from All-* aggregates (still swept)
            if (connectedOnly && !_connectedAccountIds.Contains(account.Id)) continue;
            if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;

            if (isArchive)
            {
                var dest = ResolveArchiveFolder(account.Id);
                if (dest != null) yield return (account, dest);
                continue;
            }

            foreach (var folder in folders)
                if (folder.Kind == kind)
                    yield return (account, folder);
        }
    }

    /// <summary>
    /// Canonical display name for a folder-scoped aggregate sentinel. Throws rather than falling
    /// back to a default so that a sixth aggregate added to <see cref="IsFolderScopedAggregate"/>
    /// and forgotten here fails loudly instead of silently inheriting another folder's name in its
    /// status text, its loading text, and its log tag.
    /// </summary>
    private static string FolderScopedAggregateDisplayName(string fullName) =>
        string.Equals(fullName, AllInboxesFolder.FullName, StringComparison.Ordinal) ? AllInboxesFolder.DisplayName
        : string.Equals(fullName, AllDraftsFolder.FullName,  StringComparison.Ordinal) ? AllDraftsFolder.DisplayName
        : string.Equals(fullName, AllSentFolder.FullName,    StringComparison.Ordinal) ? AllSentFolder.DisplayName
        : string.Equals(fullName, AllTrashFolder.FullName,   StringComparison.Ordinal) ? AllTrashFolder.DisplayName
        : string.Equals(fullName, AllArchiveFolder.FullName, StringComparison.Ordinal) ? AllArchiveFolder.DisplayName
        : throw new ArgumentOutOfRangeException(
            nameof(fullName), "Not a folder-scoped aggregate sentinel.");

    private async Task FetchVirtualFolderAsync(string fullName)
    {
        var displayName = FolderScopedAggregateDisplayName(fullName);
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
            // connectedOnly: this is the live-fetch path. An account that never connected would
            // otherwise be handed to GetMessageSummariesAsync, and the lazy client pool would sit on
            // its connect timeout inside Task.WhenAll before the view could render.
            var perAccountTasks = FolderScopedAggregateSources(fullName, connectedOnly: true)
                .GroupBy(s => s.Account)
                .Select(g => FetchAccountFoldersAsync(g.Key, g.Select(s => s.Folder).ToList(), ct));

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
                : $"{sorted.Count} {(sorted.Count == 1 ? "message" : "messages")} in {displayName}.";
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

    private async Task<List<MailMessageSummary>> FetchAccountFoldersAsync(
        AccountModel account, List<MailFolderModel> folders, CancellationToken ct)
    {
        var result = new List<MailMessageSummary>();

        foreach (var folder in folders)
        {
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
        // A selected group header deletes the whole group, as the group trees' own Delete key and
        // the group context menus already do (issue #566).
        if (SelectedGroupMessages() is { } group) { await DeleteMessagesAsync(group); return; }
        if (SelectedMessage == null) return;
        await DeleteMessagesAsync([SelectedMessage]);
    }

    public async Task DeleteMessagesAsync(IReadOnlyList<MailMessageSummary> toDelete)
    {
        if (toDelete.Count == 0) return;

        // Outbox rows have no server copy and no Trash to recover from; the Outbox code path asks
        // first and removes from the queue (#637). A list shows one folder, so the selection is all
        // Outbox or none of it.
        if (toDelete.All(m => m.FolderName == OutboxFolder.FullName))
        {
            await RemoveOutboxItemsAsync(toDelete);
            return;
        }

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
        if (SelectedGroupMessages() is { } group) { await ArchiveMessagesAsync(group); return; }
        if (SelectedMessage == null) return;
        if (IsOutboxRow(SelectedMessage)) { SetStatus(OutboxRowHint, AnnouncementCategory.Result); return; }
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
    /// <returns>
    /// The reload of the All Archive list when that aggregate is on screen, so a caller (or a test)
    /// can await it; an already-completed task otherwise.
    /// </returns>
    public Task SetArchiveFolderAsync(Guid accountId, string? fullName)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return Task.CompletedTask;
        account.ArchiveFolderFullName = string.IsNullOrEmpty(fullName) ? null : fullName;
        _accountService.SaveAccounts([.. Accounts]);

        // All Archive is the one aggregate whose membership a user action can change. Its list was
        // resolved against the old destination, while OnFolderSynced starts filtering on the new
        // one immediately — so without this reload a later sync would append the new archive's
        // messages alongside the old archive's, showing both at once. Reachable via the folder
        // tree's context menu, which acts on the right-clicked node rather than on the selection
        // and so leaves All Archive selected.
        return string.Equals(SelectedFolder?.FullName, AllArchiveFolder.FullName, StringComparison.Ordinal)
            ? FetchVirtualAsync(SelectedFolder!)
            : Task.CompletedTask;
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

    /// <summary>Raised when the user asks to grant organization admin consent (#607). The View opens the
    /// AdminConsentWindow in response — the VM never opens windows.</summary>
    public event EventHandler? AdminConsentRequested;

    /// <summary>Raised when the user asks for the Connection Diagnostics window.</summary>
    public event EventHandler? ConnectionDiagnosticsRequested;

    /// <summary>
    /// Whether connection diagnostics are switched on. Drives the visibility of the Help menu item;
    /// the matching command is registered and unregistered alongside it so the command palette and
    /// the menu never disagree about whether the feature exists.
    /// </summary>
    [ObservableProperty]
    private bool _isConnectionDiagnosticsEnabled;

    /// <summary>
    /// Applies the ConnectionDiagnostics setting to the journal, the command registry and the menu.
    /// Called at startup and again whenever settings are saved, so the toggle takes effect without
    /// a restart — someone switching this on is troubleshooting a live problem.
    /// </summary>
    internal void ApplyConnectionDiagnosticsSetting(bool enabled)
    {
        IsConnectionDiagnosticsEnabled = enabled;
        ConnectionJournal.Enabled = enabled;

        if (enabled)
        {
            _commandRegistry.Register(new CommandDefinition(
                id: "help.connectionDiagnostics", category: "Help", title: "Connection Diagnostics",
                execute: () => ConnectionDiagnosticsRequested?.Invoke(this, EventArgs.Empty)));
        }
        else
        {
            _commandRegistry.Unregister("help.connectionDiagnostics");
        }
    }

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
                : await LoadDetailForPropertiesAsync(msg);

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
        RetargetToGroupNewest();
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReply(detail, detail.AccountId));
    }

    [RelayCommand]
    private async Task ReplyAll()
    {
        RetargetToGroupNewest();
        var detail = await EnsureDetailAsync();
        if (detail == null) return;
        var ownAddress = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Username ?? string.Empty;
        ComposeRequested?.Invoke(ComposeViewModel.CreateReplyAll(detail, detail.AccountId, ownAddress));
    }

    [RelayCommand]
    private async Task Forward()
    {
        // No-op when a group command already set the target to this same newest message.
        RetargetToGroupNewest();
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
                    int offlineFailures = 0;
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
                                if (ConnectionFailure.IsConnectionFailure(ex, CancellationToken.None))
                                    offlineFailures++;
                                else
                                    _connectivity?.NoteAccountReachable(detail.AccountId, "attachment-download-refused");
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
                        if (offlineFailures > 0)
                            _connectivity?.NoteAccountUnreachable(detail.AccountId, "attachment-download-failed");
                        StatusText = offlineFailures == failed
                            ? $"{downloaded} of {total} attachment{(total == 1 ? "" : "s")} included — attachments are not available offline."
                            : $"{downloaded} of {total} attachment{(total == 1 ? "" : "s")} included ({failed} could not be downloaded).";
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

    /// <summary>Cached detail for the Properties dialog, repaired first so the From row shows a real
    /// address rather than the display name the summary carries (issue #636).</summary>
    private async Task<MailMessageDetail?> LoadDetailForPropertiesAsync(MailMessageSummary msg)
    {
        var detail = await _localStore.LoadDetailAsync(msg.AccountId, msg.FolderName, msg.MessageId);
        if (detail == null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await RepairMissingFromAddressAsync(detail, background: false, cts.Token);
    }

    /// <summary>Fills the sender's address back into a cache-served detail that has only a display
    /// name (issue #636). See <see cref="DetailFromAddressRepair"/>.</summary>
    private Task<MailMessageDetail> RepairMissingFromAddressAsync(
        MailMessageDetail detail, bool background, CancellationToken ct) =>
        DetailFromAddressRepair.RepairAsync(detail, _localStore, _imap, background, ct);

    // Returns MessageDetail if already loaded for the selected message,
    // otherwise fetches it (cache then IMAP) so compose can always proceed.
    // Deliberately bypasses SelectMessageCommand to avoid concurrent-execution
    // guards on that command and to avoid opening the reading pane as a side-effect.
    private async Task<MailMessageDetail?> EnsureDetailAsync()
    {
        var summary = SelectedMessage;
        if (summary == null) return null;
        if (IsOutboxRow(summary))
        {
            SetStatus(OutboxRowHint, AnnouncementCategory.Result);
            return null;
        }

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

        // Load from local cache first, fall back to IMAP — in two scopes, so a store failure
        // (--online mode) falls through to the server instead of skipping it (#637).
        MailMessageDetail? detail = null;
        try
        {
            detail = await _localStore.LoadDetailAsync(
                summary.AccountId, summary.FolderName, summary.MessageId);
        }
        catch (Exception ex)
        {
            LogService.Log("EnsureDetail: local store unavailable — falling back to the server", ex);
        }

        try
        {
            if (detail == null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    detail = await _imap.GetMessageDetailAsync(
                        summary.AccountId, summary.FolderName, summary.MessageId, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // A bound on the wait, not a verdict on the network: a large message on a thin link.
                    StatusText = "The message is taking too long to load. Try again.";
                    return null;
                }
                catch (Exception ex)
                {
                    _connectivity?.NoteOperationOutcome(summary.AccountId, ex, "message-load-failed");
                    // Callers (Reply, Forward…) do nothing on null, so the user has to hear why here.
                    StatusText = OfflineOrErrorStatus(ex, CancellationToken.None,
                        () => "This message is not available offline.",
                        () => $"Failed to load message: {ex.Message}");
                    LogService.Log("EnsureDetail", ex);
                    return null;
                }
                _connectivity?.NoteAccountReachable(summary.AccountId, "message-loaded");
                _localStore.UpsertDetailAsync(detail).LogFaults("local store: upsert detail");
            }
            else
            {
                using var repairCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                detail = await RepairMissingFromAddressAsync(detail, background: false, repairCts.Token);
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
        // On a group header the rule comes from its newest message, the same one Reply answers.
        RetargetToGroupNewest();
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

    /// <summary>True when the account has connected this session, i.e. a message detail fetch by id
    /// can succeed. Used to decide whether to open a toast's message now or defer it. Deliberately
    /// not "are its folders cached" — since #516 folders are restored from SQLite before any
    /// connect, so that question is true offline and the fetch would fail.</summary>
    public bool IsAccountReady(Guid accountId) => _connectedAccountIds.Contains(accountId);

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

        // #31: a parent's shared mailboxes have no independent existence — remove them with it, naming
        // them in the confirmation, exactly as the Account Manager does (AccountManagerViewModel uses the
        // same AccountModel.SharedChildrenOf helper). Without this, deleting a parent here left orphaned
        // shared accounts whose ParentAccountId points at nothing — unconnectable and unrepairable.
        var sharedChildren = AccountModel.SharedChildrenOf(account, Accounts);
        var prompt = sharedChildren.Count == 0
            ? $"Remove the account '{account.AccountLabel}'? This only removes it from QuickMail — your mail on the server is not affected."
            : $"Removing '{account.AccountLabel}' will also remove its shared mailbox{(sharedChildren.Count == 1 ? "" : "es")}: "
              + $"{string.Join(", ", sharedChildren.Select(c => c.AccountLabel))}. This only removes them from QuickMail — your mail on the server is not affected.";
        // Fail closed: an unwired or declined confirmation removes nothing.
        if (ConfirmationRequested?.Invoke(prompt, "Remove Account") != true) return;

        var removed = new List<AccountModel> { account };
        removed.AddRange(sharedChildren);

        foreach (var a in removed)
        {
            _credentials.DeletePassword(a.Id);
            Accounts.Remove(a);
            _cachedFolders.Remove(a.Id);
            _connectedAccountIds.Remove(a.Id);
            _connectivity?.Forget(a.Id);
        }
        // DeleteAccountDataAsync drops the account's persisted folders too, so a removed account
        // does not come back in the folder tree on the next launch (#516).
        _accountService.SaveAccounts([.. Accounts]);
        RebuildFolderListFromCache();

        if (SelectedAccount != null && removed.Any(a => a.Id == SelectedAccount.Id))
        {
            SelectedAccount  = Accounts.FirstOrDefault();
            FallBackToAllMail();
            Messages.Clear();
        }

        var config = _configService.Load();
        var configChanged = false;
        foreach (var a in removed) configChanged |= config.Accounts.Remove(a.Id);
        if (configChanged) _configService.Save(config);

        StatusText = $"Account '{account.AccountLabel}' removed. Cleaning up local data…";

        foreach (var a in removed)
        {
            try   { await _localStore.DeleteAccountDataAsync(a.Id); }
            catch (Exception ex) { LogService.Log($"DeleteAccount: failed to purge mail.db for {a.AccountLabel} — {ex.Message}"); }

            // A shared child inherits the parent's AuthType, so it enters this block too; SignOutAsync
            // matches the MSAL cache by Username (the shared address, never the parent's), so it resolves
            // to no account and is a harmless no-op — the parent's token is not touched by the child.
            if (a.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
            {
                try   { await _oauthService.SignOutAsync(a); }
                catch (Exception ex) { LogService.Log($"DeleteAccount: failed OAuth sign-out for {a.AccountLabel} — {ex.Message}"); }
            }
        }

        StatusText = sharedChildren.Count == 0
            ? $"Account '{account.AccountLabel}' removed."
            : $"Account '{account.AccountLabel}' removed, along with {sharedChildren.Count} shared mailbox{(sharedChildren.Count == 1 ? "" : "es")}.";
        SetConnectionPhase(ConnectionPhase.Idle);
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
            SetCachedFolders(accountId, folderList);
            var account = Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account != null) ApplyAccountStatus(account, folderList, "folder-refresh");
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
        // IMAP and Graph both expose per-folder server unread counts, refreshed here through the router's
        // GetFoldersAsync (IMAP STATUS / Graph unreadItemCount). #491: Graph was excluded on the claim it
        // "got counts from a different path" — there was no such path, so its folder badges froze at the
        // last full fetch (and, being part of the folder's accessible name, were spoken stale). POP3 has a
        // single maildrop with no per-folder counts and stays out. Guarding null avoids a doomed fetch.
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null || !BackendGetsLiveFolderCounts(account.BackendKind)) return;

        if (_folderCountCts.TryGetValue(accountId, out var old))
        {
            try { old.Cancel(); old.Dispose(); } catch { }
        }
        var cts = new CancellationTokenSource();
        _folderCountCts[accountId] = cts;
        _ = RefreshFolderCountsDebouncedAsync(accountId, cts.Token);
    }

    /// <summary>Which backends expose per-folder server unread counts that
    /// <see cref="ScheduleFolderCountRefresh"/> can refresh via GetFoldersAsync: IMAP (STATUS) and
    /// Microsoft Graph (unreadItemCount). POP3 has a single maildrop and no per-folder counts. (#491)</summary>
    internal static bool BackendGetsLiveFolderCounts(BackendKind kind)
        => kind is BackendKind.ImapSmtp or BackendKind.MicrosoftGraph;

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

        SetCachedFolders(accountId, folders.Where(f => !ShouldRemove(f)).ToList());

        // Keep the flat Folders collection in sync — it backs saved-view resolution, the folder
        // picker, and the next tree rebuild, so a stale entry there would resolve a deleted folder.
        for (int i = Folders.Count - 1; i >= 0; i--)
            if (Folders[i].AccountId == accountId && !Folders[i].IsHeader && ShouldRemove(Folders[i]))
                Folders.RemoveAt(i);

        // Re-sum the account's unread badge from the pruned folder list.
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account != null) ApplyAccountStatus(account, _cachedFolders[accountId], "folder-deleted-resum");
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

    /// <summary>
    /// Why folder management is unavailable on this account, or null when it is available. The single
    /// gate every folder-CRUD entry point asks before it opens a dialog or reaches a backend —
    /// <paramref name="verb"/> is the action as the user would say it ("move", "create a folder in").
    ///
    /// <para>Here rather than in <c>MainWindow</c>: which accounts can manage folders is a state
    /// decision, and code-behind is not where those live (see the MVVM rules). An account this
    /// ViewModel has never heard of is allowed through — a guard is not the place to invent a
    /// refusal, and the backend still answers for itself.</para>
    /// </summary>
    public string? FolderCrudRefusal(Guid accountId, string verb)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null || account.SupportsFolderCrud) return null;

        return $"POP3 accounts have no server folders, so there is nothing to {verb}. "
             + $"{account.AccountLabel} has only Inbox, Sent, Drafts and Trash.";
    }

    /// <summary>True when every one of these accounts can manage folders — the test a picker that
    /// offers to create one has to pass, since the button is not per-account.</summary>
    public bool AllSupportFolderCrud(IEnumerable<Guid> accountIds) =>
        accountIds.All(id => FolderCrudRefusal(id, "create a folder in") is null);

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
            SetCachedFolders(accountId, folderList);
            var account = Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account != null) ApplyAccountStatus(account, folderList, "folder-created");
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
    /// <summary>
    /// Whether <paramref name="folder"/> already sits directly under <paramref name="destination"/>.
    ///
    /// <para>Worth checking because the picker now opens pre-selected on exactly that parent, so
    /// Enter is one keystroke away, and the outcome is not harmless: IMAP's <c>RENAME</c> to the
    /// same parent and name fails with a server error, and Graph's folder <c>/copy</c> succeeds —
    /// Graph allows duplicate display names — leaving a second copy of the folder and all its mail
    /// beside the original.</para>
    /// </summary>
    internal static bool IsAlreadyUnder(MailFolderModel folder, MailFolderModel destination)
    {
        if (folder.AccountId != destination.AccountId) return false;

        // Graph references the parent by id, and its ids are case-sensitive.
        if (folder.ParentId != null)
            return string.Equals(folder.ParentId, destination.FullName, StringComparison.Ordinal);

        // IMAP encodes the hierarchy in the separator-delimited FullName, so the destination is the
        // parent when the folder's path is the destination's plus exactly one more segment. Both
        // separators MailKit reports are accepted, the same pair FolderTreeBuilder detects between.
        var full = folder.FullName;
        var dest = destination.FullName;
        if (dest.Length == 0 || full.Length <= dest.Length + 1) return false;
        if (!full.StartsWith(dest, StringComparison.OrdinalIgnoreCase)) return false;
        if (full[dest.Length] is not ('/' or '.')) return false;

        return full.IndexOfAny(['/', '.'], dest.Length + 1) < 0;
    }

    public async Task MoveFolderToAsync(FolderTreeNode node, MailFolderModel destination)
    {
        if (node.Folder == null) return;
        if (IsAlreadyUnder(node.Folder, destination))
        {
            StatusText = $"'{node.Label}' is already in {destination.DisplayName}.";
            Announce(StatusText);
            return;
        }
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
        if (IsAlreadyUnder(node.Folder, destination))
        {
            StatusText = $"'{node.Label}' is already in {destination.DisplayName}.";
            Announce(StatusText);
            return;
        }
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
                FallBackToAllMail();
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

    /// <summary>
    /// Whether every one of these messages already lives in <paramref name="destination"/>.
    ///
    /// <para>Neither backend refuses this. A same-folder copy duplicates every message where it
    /// already is, and a same-folder IMAP <c>UID MOVE</c> re-creates them under new UIDs while the
    /// code below deletes the old ids from the local store and the list — so the messages disappear
    /// from view until that folder next syncs. Both are worth a keystroke of protection now that the
    /// picker opens pre-selected on the folder the messages came from: activating "Copy to Folder…"
    /// with Enter and a repeated keypress would otherwise be enough.</para>
    /// </summary>
    private static bool AlreadyIn(IReadOnlyList<MailMessageSummary> messages, MailFolderModel destination) =>
        messages.All(m => m.AccountId == destination.AccountId &&
                          string.Equals(m.FolderName, destination.FullName, StringComparison.Ordinal));

    // ── Last message destination (#515) ───────────────────────────────────────
    //
    // Filing is repetitive: a run of messages usually goes to the same place, and reopening the
    // picker on the folder they came out of meant walking the tree to that place again every time.
    // The destination of the last successful move (and, separately, copy) is remembered per account
    // and becomes where the picker opens.

    /// <summary>
    /// Records where messages were just filed, so the next picker opens there. Called only after
    /// the server operation succeeded — a move that failed is not somewhere the user filed
    /// anything, and offering it next time would be a lie about what happened.
    /// </summary>
    private void RememberMessageDestination(MailFolderModel destination, bool copy)
    {
        // Never a virtual folder: those are not legal destinations, so one cannot get here.
        // Guarded anyway, because writing a sentinel into config.ini is the shape of bug #520's
        // saved-view corruption, and the entry point is where that is cheap to prevent. Not a
        // hand-rolled NUL test: the per-account "All Mail", saved-view and contact-mail sentinels
        // carry a REAL AccountId, so a check for Guid.Empty passes every one of them through.
        if (destination.AccountId == Guid.Empty ||
            string.IsNullOrEmpty(destination.FullName) ||
            IsVirtualFolder(destination))
            return;

        var cfg = _configService.Load();
        if (!cfg.Accounts.TryGetValue(destination.AccountId, out var ovr))
            cfg.Accounts[destination.AccountId] = ovr = new AccountOverrideConfig();

        var current = copy ? ovr.LastCopyFolder : ovr.LastMoveFolder;
        if (string.Equals(current, destination.FullName, StringComparison.Ordinal))
            return;   // already what we would write; do not rewrite the file on every move

        if (copy) ovr.LastCopyFolder = destination.FullName;
        else      ovr.LastMoveFolder = destination.FullName;

        // The write is the convenience, not the operation. This runs inside the move's try block,
        // where an escaping IO exception — an antivirus holding the temp file across the rename, a
        // full disk, a read-only profile — would be caught as "Failed to move" for a move the
        // server completed and the list has already dropped, and would skip the focus return that
        // follows it. Remembering where the user filed must never be able to misreport the filing.
        try
        {
            _configService.Save(cfg);
        }
        catch (Exception ex)
        {
            LogService.Log("RememberMessageDestination: saving the last destination", ex);
        }
    }

    /// <summary>
    /// Where a Move/Copy to Folder picker should open for <paramref name="messages"/>: the folder
    /// this account last filed to, when it is still there. Null means nothing is remembered that
    /// still resolves, and the caller falls back to the folder the messages came from (#490).
    /// </summary>
    /// <param name="copy">Ask for the copy destination rather than the move one.</param>
    public MailFolderModel? LastDestinationFor(IReadOnlyList<MailMessageSummary> messages, bool copy)
    {
        // One account only. Destinations are account-scoped — the backends move by folder name over
        // the source account's connection — so with a selection spanning accounts there is no one
        // remembered folder that is right for all of it.
        var accountIds = messages.Select(m => m.AccountId).Distinct().Take(2).ToList();
        if (accountIds.Count != 1) return null;

        var accountId = accountIds[0];
        var cfg = _configService.Load();
        if (!cfg.Accounts.TryGetValue(accountId, out var ovr)) return null;

        var remembered = copy ? ovr.LastCopyFolder : ovr.LastMoveFolder;
        if (string.IsNullOrEmpty(remembered)) return null;

        // Resolved against the folders that actually exist now. A folder the user has since
        // deleted, renamed, or moved simply is not found, and the picker opens where it used to.
        return _cachedFolders.TryGetValue(accountId, out var folders)
            ? folders.FirstOrDefault(f => string.Equals(f.FullName, remembered, StringComparison.Ordinal))
            : null;
    }

    /// <summary>Moves the given messages to a destination folder and removes them from the current view.</summary>
    public async Task MoveSelectedMessagesToFolderAsync(IReadOnlyList<MailMessageSummary> messages, MailFolderModel destination)
    {
        if (messages.Count == 0) return;

        if (AlreadyIn(messages, destination))
        {
            StatusText = messages.Count == 1
                ? $"That message is already in {destination.DisplayName}."
                : $"Those messages are already in {destination.DisplayName}.";
            Announce(StatusText);
            return;
        }

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

            RememberMessageDestination(destination, copy: false);

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

        if (AlreadyIn(messages, destination))
        {
            StatusText = messages.Count == 1
                ? $"That message is already in {destination.DisplayName}."
                : $"Those messages are already in {destination.DisplayName}.";
            Announce(StatusText);
            return;
        }

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

            RememberMessageDestination(destination, copy: true);

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

    // ── List density command (#421, View menu) ────────────────────────────────

    /// <summary>Current message-list density ("comfortable"/"compact"); drives
    /// the View menu check marks. The token publish itself lives in ThemeService.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListDensityComfortable))]
    [NotifyPropertyChangedFor(nameof(IsListDensityCompact))]
    private string _listDensity = "comfortable";

    public bool IsListDensityComfortable => ListDensity == "comfortable";
    public bool IsListDensityCompact => ListDensity == "compact";

    /// <summary>
    /// Same setting the Settings dialog persists — the View menu adjusts it in
    /// place. Density is padding-only, so ThemeService re-publishes without
    /// raising ThemeChanged; the result announcement here is the only speech.
    /// </summary>
    [RelayCommand]
    private void SetListDensity(string? density)
    {
        var normalized = string.Equals(density, "compact", StringComparison.OrdinalIgnoreCase) ? "compact" : "comfortable";
        if (normalized == ListDensity) return;
        ListDensity = normalized;

        var cfg = _configService.Load();
        cfg.AppearanceListDensity = normalized;
        _configService.Save(cfg);
        _themeService?.ApplyAppearance(cfg);

        Announce(normalized == "compact" ? "Compact density." : "Comfortable density.");
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
        var changed = !string.Equals(_activeFlagFilterId, id, StringComparison.Ordinal);
        _activeFlagFilterId = id;
        OnPropertyChanged(nameof(ActiveFlagFilterId));
        OnPropertyChanged(nameof(IsFilterAllFlagged));

        // Not an [ObservableProperty], so there is no generated handler to hook — note it here.
        // Guarded on an actual change so the no-op calls that clear an already-null id (every
        // SetFilterAsync, for one) do not detach an active view or write to disk.
        if (changed) NoteListStateChanged(ListField.Filter);
    }

    [RelayCommand]
    private Task SetFilterAsync(string? filter)
    {
        ActiveFilter = ConfigModel.ParseFilter(filter);
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
                _connectivity?.NoteOperationOutcome(MessageDetail.AccountId, ex, "attachment-download-failed");
                StatusText = OfflineOrErrorStatus(ex, CancellationToken.None,
                    () => "Attachments are not available offline.",
                    () => $"Download failed: {ex.Message}");
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
            _connectivity?.NoteOperationOutcome(MessageDetail.AccountId, ex, "attachment-download-failed");
            StatusText = OfflineOrErrorStatus(ex, CancellationToken.None,
                () => "Attachments are not available offline.",
                () => $"Save all failed: {ex.Message}");
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
                _connectivity?.NoteOperationOutcome(MessageDetail.AccountId, ex, "attachment-download-failed");
                StatusText = OfflineOrErrorStatus(ex, CancellationToken.None,
                    () => "Attachments are not available offline.",
                    () => $"Download failed: {ex.Message}");
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
        // #31: a shared mailbox has no credentials of its own — it reads through its parent's token. A
        // Graph-parent shared mailbox connects from PR 2 (the resolver borrows the parent's token); an
        // IMAP-parent shared mailbox stays deferred to PR 3, so it is excluded here and remains a
        // navigable, empty top-level node until then.
        => accounts.Where(a => (!a.IsShared || a.BackendKind == BackendKind.MicrosoftGraph)
                               && (!isBackendConnected(a.Id) || !hasCachedFolders(a.Id))).ToList();

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
        // The VM-side predicate asks "has this account connected this session", which _cachedFolders
        // stopped answering in #516 — it is restored from SQLite at launch, so a never-connected
        // account would look present here and never be reconnected.
        ReconnectOfflineAccountsAsync("account-list-refresh").LogFaults("reconnect after account-list refresh");
    }

    /// <summary>
    /// Connects every account that is not truly connected, off the UI thread, then rewires the
    /// watchers. Shared by the account-list refresh, the network-returned handler and the offline
    /// retry loop (#637). Returns how many accounts connected.
    /// </summary>
    private async Task<int> ReconnectOfflineAccountsAsync(string source)
    {
        // _cachedFolders and _connectedAccountIds are UI-thread-owned: snapshot the work list on the
        // UI thread and marshal every write back through _ui so the background loop never touches them.
        // The VM-side predicate asks "has this account connected this session AND is it not known to
        // be offline": _cachedFolders stopped answering the first half in #516 (restored from SQLite at
        // launch), and the connectivity service answers the second so a dropped account reconnects.
        List<AccountModel> accountsToConnect = [];
        _ui.Invoke(() => accountsToConnect = AccountsNeedingConnect(
            Accounts, _imap.IsConnected,
            id => _connectedAccountIds.Contains(id) && (_connectivity?.IsAccountOnline(id) ?? true)));
        if (accountsToConnect.Count == 0) return 0;

        var connected = 0;
        await Task.Run(async () =>
        {
            foreach (var account in accountsToConnect)
            {
                var result = await ConnectOneAccountAsync(account);
                _ui.Invoke(() =>
                {
                    ApplyAccountStatus(account, result.Folders, source);
                    if (result.Folders != null)
                    {
                        connected++;
                        SetCachedFolders(result.Id, result.Folders);
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
        return connected;
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
    /// <summary>
    /// Raised to update the open invite card's in-document <c>aria-live</c> status region (issue #329).
    /// Host-window announcements are unreliable while focus is inside the reading-pane WebView2, so a
    /// reading-pane RSVP's sending / success / failure feedback is delivered here, inside the document
    /// the screen reader is already reading. The View injects the text via <c>ExecuteScriptAsync</c>.
    /// </summary>
    public event Action<string>? OpenInviteCardStatus;

    /// <summary>The ICS PARTSTAT and the verb used in the confirmation, for a user's answer.</summary>
    private static (string PartStat, string ActionLabel) PartsFor(InviteResponse response) => response switch
    {
        InviteResponse.Accept    => ("ACCEPTED",  "accepted"),
        InviteResponse.Tentative => ("TENTATIVE", "tentatively accepted"),
        InviteResponse.Decline   => ("DECLINED",  "declined"),
        // No catch-all default: an unmapped value must not silently decline someone's meeting.
        _ => throw new ArgumentOutOfRangeException(nameof(response)),
    };

    [RelayCommand]
    private async Task AcceptInvite() => await RespondToInviteAsync(MessageDetail, InviteResponse.Accept);

    [RelayCommand]
    private async Task DeclineInvite() => await RespondToInviteAsync(MessageDetail, InviteResponse.Decline);

    [RelayCommand]
    private async Task TentativeInvite() => await RespondToInviteAsync(MessageDetail, InviteResponse.Tentative);

    /// <summary>
    /// Responds to the invite shown in a standalone <c>MessageWindow</c>. That window renders its own
    /// copy of the event card in its own WebView2, so the reply is driven by the message IT has open
    /// (<paramref name="detail"/>) rather than <see cref="MessageDetail"/> — in Window mode the
    /// reading pane is not showing this message, and may be showing a different one. Both feedback
    /// channels are redirected to that window for the same reason: <paramref name="cardStatus"/>
    /// writes into its card instead of the reading pane's, and <paramref name="hostAnnounce"/> raises
    /// the announcement on its automation peer instead of the main window's.
    /// </summary>
    public Task RespondToOpenInviteAsync(MailMessageDetail detail, InviteResponse response,
        Action<string> cardStatus, Action<string> hostAnnounce) =>
        RespondToInviteAsync(detail, response, cardStatus, hostAnnounce);

    /// <summary>
    /// The one RSVP path for an invite the user has open, wherever it is open. The optional sinks
    /// redirect the two feedback channels to a window other than the main one; with both null the
    /// reading pane's card and the main window's announcements are used.
    /// </summary>
    private async Task RespondToInviteAsync(MailMessageDetail? detail, InviteResponse response,
        Action<string>? cardStatus = null, Action<string>? hostAnnounce = null)
    {
        void Card(string text)
        {
            if (cardStatus != null) cardStatus(text);
            else OpenInviteCardStatus?.Invoke(text);
        }
        void Host(string text)
        {
            if (hostAnnounce != null) hostAnnounce(text);
            else Announce(text, AnnouncementCategory.Result);
        }

        var invite = detail?.CalendarInvite;
        if (invite == null)
        {
            // The card is shown but the invite data isn't available (e.g. a cache-served reopen before
            // reconstruction). Say so instead of returning silently (#329). The card is what's focused,
            // so route it through the card's live region as well as the host announce.
            const string msg = "This invitation can't be answered right now. Open it again from the message list and try once it has loaded.";
            Card(msg);
            Host(msg);
            return;
        }

        var account = Accounts.FirstOrDefault(a => a.Id == detail!.AccountId);
        if (account == null)
        {
            // Through the card as well: focus is inside the WebView2 at this point, which is the very
            // case a host announcement is dropped for — without this the button press does nothing
            // the user can perceive.
            const string msg = "Cannot send calendar response: the account for this message isn't available.";
            Card(msg);
            Host(msg);
            return;
        }

        var (partStat, actionLabel) = PartsFor(response);
        await SendIcsReplyForAsync(invite, account, partStat, actionLabel,
            detail!.MessageId, detail!.FolderName, cardStatus, hostAnnounce);
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
        string actionLabel, string sourceMessageId, string sourceFolder,
        Action<string>? cardStatus = null, Action<string>? hostAnnounce = null)
    {
        // Announcements follow the card: a reply driven from a standalone window must be announced on
        // that window's peer, not the main window's. Passing the sink per call rather than flipping a
        // shared "announce here instead" field keeps it correct across the await — a send takes
        // seconds, and unrelated announcements (sync status, a move result) must not be dragged along.
        void HostAnnounce(string text)
        {
            if (hostAnnounce != null) hostAnnounce(text);
            else Announce(text, AnnouncementCategory.Result);
        }

        // Feedback for a reading-pane RSVP goes through the card's in-document live region (#329), but
        // only while the reading pane still shows THIS invite \u2014 if the user navigated away during the
        // send, or this reply came from the calendar list, we must not write into a different card.
        // A caller-supplied sink (the standalone MessageWindow) owns a different document, so it wins
        // outright: that window's card is the one the user is reading, and the reading pane behind it
        // is not showing this message at all.
        void CardStatus(string text)
        {
            if (cardStatus != null)
            {
                cardStatus(text);
                return;
            }
            if (MessageDetail?.CalendarInvite is { } open &&
                string.Equals(open.Uid, invite.Uid, StringComparison.Ordinal))
                OpenInviteCardStatus?.Invoke(text);
        }

        CardStatus("Sending your reply\u2026");

        try
        {
            var attendeeName = account.SenderDisplayName;
            var attendeeEmail = account.Username;
            var icsContent = invite.GenerateReply(attendeeEmail, attendeeName, partStat);

            var password = _credentials.GetPassword(account.Id);
            await _smtp.SendIcsReplyAsync(icsContent, account, password, invite.Organizer ?? "");

            var eventTitle = invite.Summary ?? "calendar event";
            HostAnnounce($"Calendar response sent: {actionLabel} \u2014 {eventTitle}.");

            // Say what actually happened. The event is upserted to the calendar for every response
            // (the block below runs regardless of partStat, so the calendar reflects the reply), but
            // the decline MESSAGE omits the calendar line — telling someone who declined "it's on your
            // calendar" would read oddly. This is the reliable, in-document confirmation the reporter
            // was missing (#329).
            CardStatus(partStat == "DECLINED"
                ? $"You {actionLabel} this meeting. Your reply was sent to the organizer."
                : $"You {actionLabel} this meeting. It's been added to your calendar, and your reply was sent to the organizer.");

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
            HostAnnounce($"Failed to send calendar response: {ex.Message}");
            // A failed send would otherwise be silent in the reading pane too (host announce dropped),
            // and the buttons remain so the user can retry.
            CardStatus("Couldn't send your reply. You can try again.");
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

    private async Task RunGraphCalendarSyncAsync(
        CalendarSyncFollowUp followUp = CalendarSyncFollowUp.RefreshAndAnnounce)
    {
        if (_graphCalendarSync == null || _calendarService == null || OnlineMode) return;
        if (_graphCalendarSyncRunning) return;
        _graphCalendarSyncRunning = true;
        try
        {
            ReplaceCts(ref _graphCalSyncCts, out var ct);
            var result = await _graphCalendarSync.SyncAllAsync(ct);
            _lastCalendarPullUtc = DateTime.UtcNow;
            // Refresh the per-calendar source list and rebuild the tree so newly discovered calendars
            // appear as their own nodes (and vanished ones drop off).
            await ReloadCalendarSourcesAsync();
            BuildFolderTree();
            // Nothing eligible (no Graph accounts) or nothing pulled — leave the calendar alone.
            if (result.AccountsSynced == 0) return;
            if (followUp == CalendarSyncFollowUp.CallerHandlesIt) return;

            await _calendarService.RefreshAsync(ct);
            if (IsCalendarView)
            {
                var before = CalendarVm?.VisibleEvents.Count ?? 0;
                CalendarVm?.ApplyFiltersFromExternalUpdate();
                var after = CalendarVm?.VisibleEvents.Count ?? 0;

                if (followUp == CalendarSyncFollowUp.RefreshAndAnnounce)
                {
                    var n = result.EventsFetched;
                    Announce($"Calendar sync complete. {n} event{(n == 1 ? "" : "s")}.",
                             AnnouncementCategory.Status);
                }
                else if (after != before)
                {
                    // Opening the calendar: only when the list the user was just given a count for
                    // has actually changed under them.
                    Announce($"Calendar updated. {after} event{(after == 1 ? "" : "s")}.",
                             AnnouncementCategory.Status);
                }
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

    // The published user guide splits on top-level headings, one page per "## " section, with a
    // pandoc-generated id on every heading beneath it. "Moving to the ARM version on a Snapdragon
    // PC" is a "### " under "Installing and Updating QuickMail", hence page + fragment rather than
    // a page of its own. Both halves are asserted against docs/USER-GUIDE.md by NativeArmNoticeTests.
    internal const string ArmSwitchGuideAnchor = "moving-to-the-arm-version-on-a-snapdragon-pc";
    internal const string ArmSwitchGuideUrl =
        $"https://kellylford.github.io/QuickMail/installing-and-updating-quickmail.html#{ArmSwitchGuideAnchor}";

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

    /// <summary>
    /// On an ARM64 device running the x64 build, mentions once per version that a native ARM
    /// build exists. Called at the same startup moment as the update-installed notice.
    ///
    /// Deliberately an announcement and a Help menu entry rather than a dialog: nothing here
    /// is urgent, and a modal at launch would interrupt the first thing a screen reader user
    /// hears. Category is Result, not Status, for the same reason the update-available notice
    /// is — a one-time discovery outcome, which users who silence background progress chatter
    /// must still hear. The Help entry stays visible afterwards, so this never repeats to
    /// remain findable.
    /// </summary>
    public void MaybeAnnounceNativeArmAvailable()
    {
        if (!IsNativeArmAvailable) return;

        var cfg = _configService.Load();
        if (cfg.NativeArmNoticeVersion == CurrentVersion) return;

        cfg.NativeArmNoticeVersion = CurrentVersion;
        _configService.Save(cfg);

        // States the benefit and the action, and does not imply the running build is broken —
        // it works, it is simply emulated.
        //
        // The uninstall-first instruction is load-bearing, not politeness. Both architectures
        // pack from one packId, so their installers share an MSI UpgradeCode but get a fresh
        // random ProductCode per pack, and the Upgrade row's VersionMax is exclusive. Installing
        // the ARM build over an x64 build *of the same version* therefore matches neither the
        // upgrade nor the downgrade row: Windows Installer registers it as a second, unrelated
        // product beside the first, and Velopack — seeing that version already staged — leaves
        // the existing x64 binary in current\. The user is still emulated, and because
        // NativeArmNoticeVersion was stamped above, this notice never says so again. Uninstalling
        // first is what keeps the switch from failing silently.
        Announce("This PC has an ARM processor, and QuickMail has an ARM version that runs faster on it. " +
                 "Switching means uninstalling QuickMail first, then installing the ARM version. " +
                 "See Get the ARM Version in the Help menu.", AnnouncementCategory.Result);
    }

    /// <summary>
    /// Opens the user guide's ARM section rather than the releases page. Switching is a manual
    /// uninstall and reinstall — no update path crosses architectures — and doing it in the wrong
    /// order fails silently, so the instruction has to travel with the download link.
    ///
    /// The releases page carried that warning only while v0.8.37 was newest; every release after
    /// it puts notes with no ARM section on top, and this entry would then hand an ARM user the
    /// installer with nothing telling them to uninstall first (#477). The guide section is
    /// versionless, holds the numbered steps and the recovery, and links on to the releases page
    /// for the download itself. <see cref="ArmSwitchGuideAnchor"/> is pinned to the real heading
    /// by <c>NativeArmNoticeTests</c>, since a reworded heading would break the anchor silently.
    /// </summary>
#pragma warning disable CA1822 // [RelayCommand] target must be an instance method for the MVVM Toolkit source generator
    [RelayCommand]
    private void OpenArmDownloadPage() =>
        Helpers.ExternalUriPolicy.TryOpenExternal(ArmSwitchGuideUrl);
#pragma warning restore CA1822

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
