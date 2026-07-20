using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// ViewModel for the calendar pane. Owns the event list, today filter, refresh,
/// and open-source-message command. Announcements are surfaced via
/// <see cref="AnnouncementRequested"/>; the View subscribes and calls
/// <c>AccessibilityHelper.Announce</c>.
/// </summary>
public partial class CalendarViewModel : ObservableObject
{
    private readonly ICalendarService _calendarService;
    private readonly bool _onlineMode;
    private readonly bool _showDeclinedEvents;
    private bool _showFieldLabels;

    // Server calendar push (save-target picker + edit/delete write-back). Both optional: null in
    // tests and when the app has no server-backed (Microsoft or Google) accounts — the editor then
    // offers only the local calendar. The provider is a deferred lookup because the account list
    // loads after this VM is constructed.
    private readonly IGraphCalendarSyncService? _graphSync;
    private readonly Func<IReadOnlyList<AccountModel>>? _graphAccountsProvider;

    /// <summary>
    /// Raised when a screen reader announcement is needed.
    /// The View subscribes and calls <c>AccessibilityHelper.Announce(text, category)</c>.
    /// </summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>
    /// Raised when the user activates an event (Enter) to open the source invite.
    /// The View subscribes, constructs a <see cref="MailMessageSummary"/> from the args,
    /// and routes through the existing <c>SelectMessageCommand</c> so
    /// <see cref="MessageOpenMode"/> is honored.
    /// </summary>
    public event Action<Guid, string, string>? OpenSourceMessageRequested;

    /// <summary>
    /// Raised when the user opens the appointment editor (New or Edit). The View opens a
    /// modeless <c>EventEditorWindow</c> for the supplied editor VM and restores focus on close.
    /// </summary>
    public event Action<EventEditorViewModel>? EditorRequested;

    /// <summary>
    /// Raised to confirm deleting a locally-created appointment. The View shows a confirmation
    /// and invokes the supplied callback only if the user confirms.
    /// </summary>
    public event Action<CalendarEvent, Action>? DeleteConfirmRequested;

    /// <summary>
    /// Raised to confirm deleting from a repeating series: the View offers "just this occurrence"
    /// (first callback), "the whole series" (second), or cancel.
    /// </summary>
    public event Action<CalendarEvent, Action, Action>? RecurringDeleteConfirmRequested;

    /// <summary>
    /// Raised after a create/edit/delete has persisted and the list rebuilt, so the View can
    /// place focus on the correct (new/neighbouring) row. Fires after the async save completes,
    /// which the editor window's synchronous Closed handler cannot wait for.
    /// </summary>
    public event Action? ListFocusRequested;

    /// <summary>
    /// Raised to save an event as a .ics file. The View shows a Save dialog for the suggested
    /// file name and writes the supplied file body.
    /// </summary>
    public event Action<string, string>? ExportRequested;

    [ObservableProperty]
    private BatchObservableCollection<CalendarEvent> _events = [];

    /// <summary>All events after filtering (today filter + declined filter), sorted by start time.</summary>
    public IReadOnlyList<CalendarEvent> VisibleEvents => _filteredEvents;
    private List<CalendarEvent> _filteredEvents = [];

    [ObservableProperty]
    private bool _isTodayFilter;

    /// <summary>Which slice of the calendar the list shows (Agenda / Day / Week).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel))]
    [NotifyPropertyChangedFor(nameof(IsAgendaView))]
    [NotifyPropertyChangedFor(nameof(IsDayView))]
    [NotifyPropertyChangedFor(nameof(IsWeekView))]
    [NotifyPropertyChangedFor(nameof(IsMonthView))]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    private CalendarViewMode _viewMode = CalendarViewMode.Agenda;

    /// <summary>Check-state helpers for the View Mode menu.</summary>
    public bool IsAgendaView => ViewMode == CalendarViewMode.Agenda;
    public bool IsDayView => ViewMode == CalendarViewMode.Day;
    public bool IsWeekView => ViewMode == CalendarViewMode.Week;
    public bool IsMonthView => ViewMode == CalendarViewMode.Month;

    /// <summary>True for the list-based views (Agenda/Day/Week); false when the Month grid shows.</summary>
    public bool IsListView => ViewMode != CalendarViewMode.Month;

    /// <summary>The date Day/Week views are centred on. Ignored in Agenda.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel))]
    private DateTime _referenceDate = DateTime.Today;

    /// <summary>Human-readable label for the current view + period, shown above the list.</summary>
    public string PeriodLabel => ViewMode switch
    {
        CalendarViewMode.Day   => "Day: " + ReferenceDate.ToString("dddd, MMMM d, yyyy"),
        CalendarViewMode.Week  => "Week of " + WeekStart(ReferenceDate).ToString("MMMM d")
                                  + " to " + WeekStart(ReferenceDate).AddDays(6).ToString("MMMM d"),
        CalendarViewMode.Month => ReferenceDate.ToString("MMMM yyyy"),
        _                      => IsTodayFilter ? "Agenda: today" : "Agenda: all appointments",
    };

    /// <summary>Day cells for the Month grid (leading/trailing days pad to full weeks).</summary>
    public BatchObservableCollection<MonthCell> MonthCells { get; } = [];

    [ObservableProperty]
    private MonthCell? _selectedMonthCell;

    partial void OnSelectedMonthCellChanged(MonthCell? value)
        => OnPropertyChanged(nameof(SelectedEventDetail));

    private static DateTime WeekStart(DateTime date)
    {
        var first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var diff = (7 + (date.DayOfWeek - first)) % 7;
        return date.Date.AddDays(-diff);
    }

    [ObservableProperty]
    private CalendarEvent? _selectedEvent;

    /// <summary>
    /// Multi-line details for the Details pane: the selected event's fields, or in Month view
    /// the selected day's event list.
    /// </summary>
    public string SelectedEventDetail => ViewMode == CalendarViewMode.Month
        ? SelectedMonthCell?.DayDetail ?? string.Empty
        : SelectedEvent?.DetailText ?? string.Empty;

    /// <summary>True when the selected event is a locally-created appointment (editable/deletable).</summary>
    public bool CanEditSelected => SelectedEvent?.IsUserCreated == true;

    partial void OnSelectedEventChanged(CalendarEvent? value)
    {
        OnPropertyChanged(nameof(SelectedEventDetail));
        OnPropertyChanged(nameof(CanEditSelected));
    }

    /// <summary>True when the calendar pane is unavailable (--online mode).</summary>
    [ObservableProperty]
    private bool _isUnavailable;

    /// <summary>True while the calendar search box is shown. Bound to its visibility.</summary>
    [ObservableProperty]
    private bool _isSearchActive;

    /// <summary>
    /// Live filter text: matches summary, location, and notes (case-insensitive).
    /// Empty = no filtering. Reapplies the list on every change.
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>
    /// Which calendar source(s) the views show: null = all sources merged; otherwise the
    /// <see cref="MainViewModel.CalendarFilter"/> chosen from the selected tree node (an account, one
    /// of its calendars, or the local calendar). Set by MainViewModel before LoadAsync.
    /// </summary>
    private MainViewModel.CalendarFilter? _sourceFilter;
    public MainViewModel.CalendarFilter? SourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (_sourceFilter == value) return;
            _sourceFilter = value;
            ApplyFilters();
        }
    }

    /// <summary>Clears and hides search, restoring the unfiltered view.</summary>
    public void ClearSearch()
    {
        SearchText = string.Empty;   // triggers ApplyFilters
        IsSearchActive = false;
    }

    // Resolves EVERY account's label for the "Calendar" column (the graph-accounts provider above is
    // push-capable accounts only, so it omits iCloud). Null in tests.
    private readonly Func<IReadOnlyList<AccountModel>>? _allAccountsProvider;

    // Discovered calendar sources (AccountId, CalendarId, CalendarName) — used to offer one save
    // target per iCloud calendar (Home / Family). Null in tests and until sync has discovered any.
    private readonly Func<IReadOnlyList<(Guid AccountId, string CalendarId, string CalendarName)>>? _calendarSourcesProvider;

    public CalendarViewModel(ICalendarService calendarService, bool onlineMode, bool showDeclinedEvents,
                             bool showFieldLabels = false,
                             IGraphCalendarSyncService? graphSync = null,
                             Func<IReadOnlyList<AccountModel>>? graphAccountsProvider = null,
                             Func<IReadOnlyList<AccountModel>>? allAccountsProvider = null,
                             Func<IReadOnlyList<(Guid AccountId, string CalendarId, string CalendarName)>>? calendarSourcesProvider = null)
    {
        _calendarService = calendarService;
        _onlineMode = onlineMode;
        _showDeclinedEvents = showDeclinedEvents;
        _showFieldLabels = showFieldLabels;
        _graphSync = graphSync;
        _graphAccountsProvider = graphAccountsProvider;
        _allAccountsProvider = allAccountsProvider;
        _calendarSourcesProvider = calendarSourcesProvider;
    }

    /// <summary>
    /// iCloud accounts save per-calendar: each discovered CalDAV collection is its own save target
    /// (Home / Family), unlike Microsoft/Google which save to their default calendar. Detected by
    /// IMAP host, matching the sync service's eligibility.
    /// </summary>
    private static bool IsICloudAccount(AccountModel a)
        => a.ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the "Calendar" source label for a row: "Local" for locally-authored appointments,
    /// "AccountLabel: CalendarName" (e.g. "Apple: Family") for a tagged server calendar, or the
    /// account label alone when the row has no specific calendar (e.g. a harvested invitation).
    /// </summary>
    private string SourceLabelFor(CalendarEvent e, IReadOnlyDictionary<Guid, string> labels)
    {
        if (e.AccountId == CalendarEvent.LocalAccountId) return "Local";
        var acct = labels.TryGetValue(e.AccountId, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label : "Account";
        return string.IsNullOrWhiteSpace(e.CalendarName) ? acct : $"{acct}: {e.CalendarName.Trim()}";
    }

    /// <summary>
    /// When true, each event row's accessible name uses field labels ("Subject …, when …"); when
    /// false, concise data only. Mirrors the address book's ContactListShowFieldLabels. Updated live
    /// from <c>MainViewModel.ApplySettings</c> when the setting changes.
    /// </summary>
    public bool ShowFieldLabels
    {
        get => _showFieldLabels;
        set
        {
            if (_showFieldLabels == value) return;
            _showFieldLabels = value;
            ApplyFilters();
        }
    }

    /// <summary>Loads events from the service and applies filters. Call on pane open.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_onlineMode)
        {
            IsUnavailable = true;
            Announce("Calendar is unavailable in online mode. It requires the local message cache.",
                     AnnouncementCategory.Hint);
            return;
        }

        IsUnavailable = false;
        await _calendarService.RefreshAsync(ct);
        ApplyFilters();
        AnnounceOpenHint();
    }

    /// <summary>Re-harvests from the cache and reloads. Bound to F5.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_onlineMode) return;
        Announce("Refreshing calendar.", AnnouncementCategory.Status);
        await _calendarService.RefreshAsync();
        ApplyFilters();
        Announce($"Calendar updated. {VisibleEvents.Count} event{(VisibleEvents.Count == 1 ? "" : "s")}.",
                 AnnouncementCategory.Result);
    }

    /// <summary>
    /// Bound to T. In Agenda, toggles the Today filter. In Day/Week, jumps the reference date to
    /// today (the natural "go to today" action for those views).
    /// </summary>
    [RelayCommand]
    private void ToggleTodayFilter()
    {
        if (ViewMode == CalendarViewMode.Agenda)
        {
            IsTodayFilter = !IsTodayFilter;
            OnPropertyChanged(nameof(PeriodLabel));
            ApplyFilters();
            Announce(IsTodayFilter
                    ? $"Today. {VisibleEvents.Count} event{(VisibleEvents.Count == 1 ? "" : "s")}."
                    : $"All events. {VisibleEvents.Count} event{(VisibleEvents.Count == 1 ? "" : "s")}.",
                AnnouncementCategory.Result);
        }
        else
        {
            ReferenceDate = DateTime.Today;
            ApplyFilters();
            AnnouncePeriod();
        }
    }

    /// <summary>Switches to Agenda view. Bound to A.</summary>
    [RelayCommand] private void ShowAgenda() => SwitchView(CalendarViewMode.Agenda);

    /// <summary>Switches to Day view. Bound to D.</summary>
    [RelayCommand] private void ShowDay() => SwitchView(CalendarViewMode.Day);

    /// <summary>Switches to Week view. Bound to W.</summary>
    [RelayCommand] private void ShowWeek() => SwitchView(CalendarViewMode.Week);

    /// <summary>Switches to Month view. Bound to M.</summary>
    [RelayCommand] private void ShowMonth() => SwitchView(CalendarViewMode.Month);

    /// <summary>Enter on a month cell: drill into that day's Day view.</summary>
    [RelayCommand]
    private void DrillIntoDay(MonthCell? cell)
    {
        cell ??= SelectedMonthCell;
        if (cell == null) return;
        ReferenceDate = cell.Date;
        SwitchView(CalendarViewMode.Day);
    }

    private void SwitchView(CalendarViewMode mode)
    {
        ViewMode = mode;
        ApplyFilters();
        SelectedEvent = Events.Count > 0 ? Events[0] : null;
        AnnouncePeriod();
        // Move keyboard focus to the control the new view shows (the Month grid, or the event list).
        // Switching views swaps which control is visible; without this, focus is stranded on the
        // now-hidden control and arrow keys do nothing until the user tabs away and back. The View's
        // FocusCalendarList routes focus to the grid in Month view and the list otherwise.
        ListFocusRequested?.Invoke();
    }

    /// <summary>Moves to the previous day/week. Bound to Ctrl+Left. No-op in Agenda.</summary>
    [RelayCommand] private void PreviousPeriod() => Page(-1);

    /// <summary>Moves to the next day/week. Bound to Ctrl+Right. No-op in Agenda.</summary>
    [RelayCommand] private void NextPeriod() => Page(+1);

    private void Page(int direction)
    {
        switch (ViewMode)
        {
            case CalendarViewMode.Day:   ReferenceDate = ReferenceDate.AddDays(direction); break;
            case CalendarViewMode.Week:  ReferenceDate = ReferenceDate.AddDays(7 * direction); break;
            case CalendarViewMode.Month: ReferenceDate = ReferenceDate.AddMonths(direction); break;
            default: return; // Agenda doesn't page
        }
        ApplyFilters();
        SelectedEvent = Events.Count > 0 ? Events[0] : null;
        AnnouncePeriod();
    }

    private void AnnouncePeriod()
        => Announce($"{PeriodLabel}. {VisibleEvents.Count} event{(VisibleEvents.Count == 1 ? "" : "s")}.",
                    AnnouncementCategory.Status);

    /// <summary>Raised to open the Go To Date picker, seeded with the current reference date.</summary>
    public event Action<DateTime>? GoToDateRequested;

    /// <summary>
    /// Bound to Ctrl+G (calendar.goToDate). Opens the Go To Date picker so the user can jump the
    /// calendar to any date. Seeds the picker with the current reference date (today in Agenda,
    /// which ignores it).
    /// </summary>
    [RelayCommand]
    private void RequestGoToDate()
    {
        if (_onlineMode)
        {
            Announce("Calendar is unavailable in online mode.", AnnouncementCategory.Result);
            return;
        }
        var seed = ViewMode == CalendarViewMode.Agenda ? DateTime.Today : ReferenceDate;
        GoToDateRequested?.Invoke(seed);
    }

    /// <summary>
    /// Jumps the calendar to <paramref name="date"/>. Day/Week/Month keep their view mode and
    /// recentre on the date; Agenda (which ignores ReferenceDate) switches to Day view so the
    /// chosen date is actually shown. Called by the View when the Go To Date picker confirms.
    /// </summary>
    public void GoToDate(DateTime date)
    {
        ReferenceDate = date.Date;
        if (ViewMode == CalendarViewMode.Agenda)
        {
            SwitchView(CalendarViewMode.Day); // applies filters, reselects, announces the day
            return;
        }
        ApplyFilters();
        SelectedEvent = Events.Count > 0 ? Events[0] : null;
        AnnouncePeriod();
    }

    /// <summary>Opens the appointment editor to create a new local event. Bound to N / Ctrl+Shift+N.</summary>
    [RelayCommand]
    private void NewEvent()
    {
        if (_onlineMode) { Announce("Calendar is unavailable in online mode.", AnnouncementCategory.Result); return; }

        // Save targets: the local calendar (always, default; added by the editor) plus each
        // server-backed account. Microsoft/Google contribute one target (their default calendar);
        // an iCloud account contributes one target PER discovered calendar so the user picks Home
        // vs. Family.
        var sources = _calendarSourcesProvider?.Invoke() ?? [];
        var accountTargets = new List<CalendarSaveTarget>();
        foreach (var a in _graphAccountsProvider?.Invoke() ?? [])
        {
            if (IsICloudAccount(a))
            {
                foreach (var (_, calId, calName) in sources.Where(s => s.AccountId == a.Id))
                    accountTargets.Add(new CalendarSaveTarget($"{a.AccountLabel}: {calName}", a.Id, calId, calName));
                // No discovered calendars yet (never synced) → offer nothing rather than a target
                // that can't resolve a collection to PUT to.
            }
            else
            {
                accountTargets.Add(new CalendarSaveTarget(a.AccountLabel, a.Id));
            }
        }
        var editor = new EventEditorViewModel(DateTime.Now, accountTargets);
        editor.Saved += evt => _ = SaveNewEventAsync(evt);
        EditorRequested?.Invoke(editor);
    }

    /// <summary>
    /// Persists a NEW appointment to its chosen calendar: local save, or a server create push
    /// (Microsoft or Google) for an account target. A failed push falls back to a local save so
    /// the user's data is never lost, announced as such.
    /// </summary>
    internal async Task SaveNewEventAsync(CalendarEvent evt)
    {
        var account = evt.AccountId == CalendarEvent.LocalAccountId
            ? null
            : _graphAccountsProvider?.Invoke().FirstOrDefault(a => a.Id == evt.AccountId);

        if (account is null || _graphSync is null)
        {
            // Local target — or an account target we can no longer resolve (account removed
            // between opening the editor and saving): keep the data rather than fail. Drop any
            // server calendar tag so the row reads as a clean local appointment.
            evt.AccountId = CalendarEvent.LocalAccountId;
            evt.CalendarId = string.Empty;
            evt.CalendarName = string.Empty;
            await SaveEditedEventAsync(evt, isNew: true);
            return;
        }

        try
        {
            var created = await _graphSync.CreateEventAsync(account, evt);
            await _calendarService.RefreshAsync(); // reload the in-memory list from the store
            ApplyFilters();
            SelectedEvent = Events.FirstOrDefault(e => e.Uid == created.Uid && e.AccountId == created.AccountId);
            var when = created.StartTime?.ToString("ddd, MMM d 'at' t") ?? "no date";
            Announce($"Appointment created on {account.AccountLabel}. {created.Summary}, {when}.",
                     AnnouncementCategory.Result);
            ListFocusRequested?.Invoke();
        }
        catch (Exception ex)
        {
            LogService.Log($"Graph calendar create failed for {account.AccountLabel}", ex);
            evt.AccountId = CalendarEvent.LocalAccountId;
            evt.CalendarId = string.Empty;
            evt.CalendarName = string.Empty;
            await _calendarService.UpsertEventAsync(evt);
            ApplyFilters();
            SelectedEvent = Events.FirstOrDefault(e => e.Uid == evt.Uid && e.AccountId == evt.AccountId);
            Announce($"Could not save to {account.AccountLabel}. Saved to Local Calendar instead.",
                     AnnouncementCategory.Result);
            ListFocusRequested?.Invoke();
        }
    }

    /// <summary>Opens the appointment editor to edit the selected local event. Bound to E / Enter on a local event.</summary>
    [RelayCommand]
    private void EditEvent(CalendarEvent? evt)
    {
        evt ??= SelectedEvent;
        if (evt == null) return;
        if (!evt.IsUserCreated)
        {
            // Server-synced single events on a Microsoft or Google account are editable
            // (write-back); everything else server-side (CalDAV rows, recurring server
            // occurrences, harvested invites) stays read-only.
            var pushAccount = ServerAccountFor(evt);
            if (pushAccount != null)
            {
                if (evt.IsRecurring)
                {
                    Announce("Repeating events from your online calendar can't be edited yet.",
                             AnnouncementCategory.Result);
                    return;
                }
                var serverEditor = new EventEditorViewModel(evt);
                serverEditor.Saved += updated => _ = ServerUpdateAsync(pushAccount, evt, updated);
                EditorRequested?.Invoke(serverEditor);
                return;
            }
            Announce(evt.IsGraph
                    ? "This event syncs from your online calendar and can't be edited here."
                    : "Only appointments you created can be edited.",
                AnnouncementCategory.Result);
            return;
        }

        // A recurring occurrence opens on its own date/time with the This event / All events
        // scope choice; anything else opens the stored master directly.
        var source = evt.OccurrenceStart.HasValue
            ? evt
            : _calendarService.Events.FirstOrDefault(x => x.Uid == evt.Uid && x.AccountId == evt.AccountId) ?? evt;
        var editor = new EventEditorViewModel(source);
        editor.Saved += updated => _ = editor.IsDetachSave && editor.OccurrenceStart.HasValue
            ? DetachOccurrenceAsync(source, editor.OccurrenceStart.Value, updated)
            : source.OccurrenceStart.HasValue
                ? SaveWholeSeriesAsync(source, updated)
                : SaveEditedEventAsync(updated, isNew: false);
        EditorRequested?.Invoke(editor);
    }

    /// <summary>Exports the selected event as a .ics file (any event, local or invite-sourced).</summary>
    [RelayCommand]
    private void ExportEvent(CalendarEvent? evt)
    {
        evt ??= SelectedEvent;
        if (evt == null) return;

        // For a recurring occurrence, export the series master so the RRULE and true start go out.
        var master = _calendarService.Events.FirstOrDefault(x => x.Uid == evt.Uid && x.AccountId == evt.AccountId) ?? evt;
        var ics = IcsModel.ExportEvent(master);

        var baseName = string.IsNullOrWhiteSpace(master.Summary) ? "appointment" : master.Summary;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        ExportRequested?.Invoke(baseName + ".ics", ics);
    }

    /// <summary>Deletes the selected local event (with confirmation). Bound to Delete.</summary>
    [RelayCommand]
    private void DeleteEvent(CalendarEvent? evt)
    {
        evt ??= SelectedEvent;
        if (evt == null) return;
        if (!evt.IsUserCreated)
        {
            var pushAccount = ServerAccountFor(evt);
            if (pushAccount != null && !evt.IsRecurring)
            {
                var serverTarget = evt;
                DeleteConfirmRequested?.Invoke(serverTarget, () => _ = ServerDeleteAsync(pushAccount, serverTarget));
                return;
            }
            Announce(evt.IsGraph
                    ? "This event syncs from your online calendar and can't be deleted here."
                    : "Only appointments you created can be deleted.",
                AnnouncementCategory.Result);
            return;
        }

        var target = evt;
        if (target.IsRecurring && target.OccurrenceStart.HasValue)
        {
            RecurringDeleteConfirmRequested?.Invoke(target,
                () => _ = DeleteOccurrenceAsync(target),
                () => _ = ConfirmDeleteAsync(target));
        }
        else
        {
            DeleteConfirmRequested?.Invoke(target, () => _ = ConfirmDeleteAsync(target));
        }
    }

    /// <summary>
    /// The server-backed account (Microsoft or Google) a synced row can write back to, or null
    /// when the row is not writable from here (local rows, CalDAV rows, no sync service wired).
    /// </summary>
    private AccountModel? ServerAccountFor(CalendarEvent evt)
        => evt.IsGraph && _graphSync != null
            ? _graphAccountsProvider?.Invoke().FirstOrDefault(a => a.Id == evt.AccountId)
            : null;

    /// <summary>Pushes an edit of a server-synced event to its calendar; local state only changes on success.</summary>
    private async Task ServerUpdateAsync(AccountModel account, CalendarEvent original, CalendarEvent edited)
    {
        try
        {
            // Keep the server identity: same Uid and account, is_graph flag preserved by the
            // sync service when it stores the server's returned copy. Carry the original's calendar
            // tag too — an iCloud update PUTs back to the collection the event lives on (Microsoft
            // and Google ignore it).
            edited.Uid = original.Uid;
            edited.AccountId = account.Id;
            edited.CalendarId = original.CalendarId;
            edited.CalendarName = original.CalendarName;
            var updated = await _graphSync!.UpdateEventAsync(account, edited);
            await _calendarService.RefreshAsync();
            ApplyFilters();
            SelectedEvent = Events.FirstOrDefault(e => e.Uid == updated.Uid && e.AccountId == account.Id);
            Announce($"Updated on your online calendar. {updated.Summary}.", AnnouncementCategory.Result);
        }
        catch (Exception ex)
        {
            LogService.Log("Calendar server update", ex);
            Announce("Could not update your online calendar.", AnnouncementCategory.Result);
        }
        ListFocusRequested?.Invoke();
    }

    /// <summary>Deletes a server-synced event on its calendar; local state only changes on success.</summary>
    private async Task ServerDeleteAsync(AccountModel account, CalendarEvent evt)
    {
        try
        {
            var index = _filteredEvents.FindIndex(e => e.Uid == evt.Uid && e.StartTimeTicks == evt.StartTimeTicks);
            await _graphSync!.DeleteEventAsync(account, evt);
            await _calendarService.RefreshAsync();
            ApplyFilters();
            SelectedEvent = VisibleEvents.Count > 0
                ? Events[Math.Min(Math.Max(index, 0), VisibleEvents.Count - 1)]
                : null;
            Announce($"Deleted from your online calendar. {evt.Summary}.", AnnouncementCategory.Result);
        }
        catch (Exception ex)
        {
            LogService.Log("Calendar server delete", ex);
            Announce("Could not update your online calendar.", AnnouncementCategory.Result);
        }
        ListFocusRequested?.Invoke();
    }

    /// <summary>
    /// "All events" save on a recurring occurrence: applies the edited fields to the series
    /// master while preserving what the editor doesn't show — the excluded occurrences and the
    /// series' original start DATE (the edited time-of-day and duration are applied to it).
    /// Without this, saving a series edit resurrected deleted occurrences and re-anchored the
    /// series on the edited occurrence's date.
    /// </summary>
    private async Task SaveWholeSeriesAsync(CalendarEvent occurrence, CalendarEvent updated)
    {
        var master = _calendarService.Events.FirstOrDefault(
            x => x.Uid == occurrence.Uid && x.AccountId == occurrence.AccountId);
        if (master != null)
        {
            updated.ExDates = master.ExDates;
            if (master.StartTime.HasValue && updated.StartTime.HasValue && updated.EndTime.HasValue)
            {
                var duration = updated.EndTime.Value - updated.StartTime.Value;
                var newStart = master.StartTime.Value.Date + updated.StartTime.Value.TimeOfDay;
                updated.StartTimeTicks = newStart.ToUniversalTime().Ticks;
                updated.EndTimeTicks   = (newStart + duration).ToUniversalTime().Ticks;
            }
        }
        await SaveEditedEventAsync(updated, isNew: false);
    }

    /// <summary>
    /// "This event only" save on a recurring occurrence: excludes the occurrence from the series
    /// (EXDATE on the master) and inserts the edited copy as an independent appointment.
    /// </summary>
    private async Task DetachOccurrenceAsync(CalendarEvent occurrence, DateTime occurrenceStart, CalendarEvent standalone)
    {
        var master = _calendarService.Events.FirstOrDefault(
            x => x.Uid == occurrence.Uid && x.AccountId == occurrence.AccountId);
        if (master == null) return;

        master.AddExDate(occurrenceStart);
        await _calendarService.UpsertEventAsync(master);
        await _calendarService.UpsertEventAsync(standalone);
        ApplyFilters();
        SelectedEvent = Events.FirstOrDefault(e => e.Uid == standalone.Uid);
        Announce($"This occurrence updated. {standalone.Summary}, " +
                 $"{standalone.WhenText}. " +
                 "The rest of the series is unchanged.", AnnouncementCategory.Result);
        ListFocusRequested?.Invoke();
    }

    /// <summary>Deletes a single occurrence of a repeating series (EXDATE on the master).</summary>
    private async Task DeleteOccurrenceAsync(CalendarEvent occurrence)
    {
        var master = _calendarService.Events.FirstOrDefault(
            x => x.Uid == occurrence.Uid && x.AccountId == occurrence.AccountId);
        if (master == null || !occurrence.OccurrenceStart.HasValue) return;

        var index = _filteredEvents.FindIndex(e => ReferenceEquals(e, occurrence));
        master.AddExDate(occurrence.OccurrenceStart.Value);
        await _calendarService.UpsertEventAsync(master);
        ApplyFilters();
        if (VisibleEvents.Count > 0)
        {
            var next = Math.Min(Math.Max(index, 0), VisibleEvents.Count - 1);
            SelectedEvent = Events[next];
        }
        else
        {
            SelectedEvent = null;
        }
        Announce($"Occurrence deleted. {occurrence.Summary}. The rest of the series is unchanged.",
                 AnnouncementCategory.Result);
        ListFocusRequested?.Invoke();
    }

    /// <summary>Persists a created/edited event, reloads, reselects it, and announces the result.</summary>
    private async Task SaveEditedEventAsync(CalendarEvent evt, bool isNew)
    {
        await _calendarService.UpsertEventAsync(evt);
        ApplyFilters();
        SelectedEvent = Events.FirstOrDefault(e => e.Uid == evt.Uid && e.AccountId == evt.AccountId);
        var when = evt.WhenText;
        Announce($"Appointment {(isNew ? "created" : "updated")}. {evt.Summary}, {when}.",
                 AnnouncementCategory.Result);
        ListFocusRequested?.Invoke();
    }

    private async Task ConfirmDeleteAsync(CalendarEvent evt)
    {
        var index = _filteredEvents.FindIndex(e => e.Uid == evt.Uid && e.AccountId == evt.AccountId);
        await _calendarService.DeleteEventAsync(evt.Uid, evt.AccountId);
        ApplyFilters();
        // Move selection to the neighbouring event so focus is never stranded.
        if (VisibleEvents.Count > 0)
        {
            var next = Math.Min(index, VisibleEvents.Count - 1);
            SelectedEvent = next >= 0 ? Events[next] : null;
        }
        else
        {
            SelectedEvent = null;
        }
        Announce($"Appointment deleted. {evt.Summary}.", AnnouncementCategory.Result);
        ListFocusRequested?.Invoke();
    }

    /// <summary>Opens the source invite message. Bound to Enter.</summary>
    [RelayCommand]
    private void OpenSourceMessage(CalendarEvent? evt)
    {
        if (evt == null) return;
        if (evt.IsGraph)
        {
            // Server-synced rows (Microsoft or Google) never had a source invite email; the
            // stale-cache message would be wrong and confusing for them.
            Announce("This event syncs from your online calendar.", AnnouncementCategory.Result);
            return;
        }
        if (string.IsNullOrEmpty(evt.SourceMessageId))
        {
            Announce("The original invitation email is no longer in your local message cache.",
                     AnnouncementCategory.Result);
            return;
        }
        Announce($"Opening message. {evt.Summary}.", AnnouncementCategory.Status);
        OpenSourceMessageRequested?.Invoke(evt.AccountId, evt.SourceFolder, evt.SourceMessageId);
    }

    /// <summary>Updates the response status for an event (called after accept/decline/tentative).</summary>
    public async Task UpdateResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status)
    {
        await _calendarService.SetResponseStatusAsync(uid, accountId, status);
        ApplyFilters();
    }

    /// <summary>Reapplies filters without re-fetching from the service. Called after external status updates.</summary>
    public void ApplyFiltersFromExternalUpdate() => ApplyFilters();

    /// <summary>
    /// Rebuilds the visible list for the current view: applies declined/cancelled filters, windows
    /// one-off events to the view's date range, and expands recurring masters into per-date
    /// occurrences within that window.
    /// </summary>
    private void ApplyFilters()
    {
        var baseEvents = _calendarService.Events
            .Where(e => (_sourceFilter?.Account is not Guid a || e.AccountId == a)
                        && (_sourceFilter?.CalendarId is not string c || e.CalendarId == c))
            .Where(e => _showDeclinedEvents || e.ResponseStatus != CalendarResponseStatus.Declined)
            .Where(e => e.ResponseStatus != CalendarResponseStatus.Cancelled)
            .ToList();

        // The date window for the current view. Null = Agenda "all" (one-offs are not date-bounded).
        var monthGridStart = WeekStart(new DateTime(ReferenceDate.Year, ReferenceDate.Month, 1));
        (DateTime Start, DateTime End)? window = ViewMode switch
        {
            CalendarViewMode.Day   => (ReferenceDate.Date, ReferenceDate.Date.AddDays(1)),
            CalendarViewMode.Week  => (WeekStart(ReferenceDate), WeekStart(ReferenceDate).AddDays(7)),
            CalendarViewMode.Month => (monthGridStart, monthGridStart.AddDays(42)),
            _ => IsTodayFilter
                    ? (DateTime.Today, DateTime.Today.AddDays(1))
                    : ((DateTime Start, DateTime End)?)null,
        };

        // Recurring series are unbounded, so they always expand within a bounded window: the view's
        // window, or (Agenda-all) a look-ahead from a week ago through 12 months out.
        var recStart = window?.Start ?? DateTime.Today.AddDays(-7);
        var recEnd   = window?.End   ?? DateTime.Today.AddMonths(12);

        var result = new List<CalendarEvent>();
        foreach (var e in baseEvents)
        {
            var rule = e.IsRecurring ? RecurrenceRule.Parse(e.RecurrenceRule) : null;
            if (rule != null && e.StartTime.HasValue)
            {
                var excluded = new HashSet<DateTime>(e.GetExDates());
                foreach (var occStart in RecurrenceExpander.Expand(e.StartTime.Value, rule, recStart, recEnd))
                    if (!excluded.Contains(occStart))
                        result.Add(CloneOccurrence(e, occStart));
            }
            else if (window is { } w)
            {
                // Overlap, not start-in-window: a multi-day or in-progress event must appear on
                // every day it spans, not just its first.
                if (e.StartTime.HasValue
                    && e.StartTime.Value < w.End
                    && (e.EndTime ?? e.StartTime.Value) >= w.Start)
                    result.Add(e);
            }
            else
            {
                result.Add(e); // Agenda, no today filter: every one-off event
            }
        }

        // Search filter — matches summary, location, or notes, case-insensitive.
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            result = result.Where(e =>
                    e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || e.Location.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || e.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _filteredEvents = result.OrderBy(e => e.StartTimeTicks ?? long.MaxValue).ToList();

        // Stamp each row's accessible name (per the field-labels setting, mirroring the address book)
        // plus the "Calendar" source column, which also gets spoken so screen-reader users hear which
        // calendar a row belongs to (#1). Build the account-label map once.
        var accountLabels = (_allAccountsProvider?.Invoke() ?? [])
            .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().AccountLabel);
        foreach (var evt in _filteredEvents)
        {
            var source = SourceLabelFor(evt, accountLabels);
            evt.CalendarSourceLabel = source;
            var baseName = _showFieldLabels ? evt.LabeledLine : evt.DisplayLine;
            evt.AccessibleName = string.IsNullOrEmpty(source) ? baseName : $"{baseName}, calendar {source}";
        }

        using (Events.BeginBatchScope())
        {
            Events.Clear();
            foreach (var evt in _filteredEvents)
                Events.Add(evt);
        }

        if (ViewMode == CalendarViewMode.Month)
            RebuildMonthCells(monthGridStart);
    }

    /// <summary>Builds the 42 day cells (6 full weeks) for the Month grid.</summary>
    private void RebuildMonthCells(DateTime gridStart)
    {
        var byDate = _filteredEvents
            .Where(e => e.StartTime.HasValue)
            .GroupBy(e => e.StartTime!.Value.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        using (MonthCells.BeginBatchScope())
        {
            MonthCells.Clear();
            for (var i = 0; i < 42; i++)
            {
                var date = gridStart.AddDays(i);
                byDate.TryGetValue(date, out var dayEvents);
                MonthCells.Add(new MonthCell(date, ReferenceDate.Month, dayEvents ?? []));
            }
        }
        SelectedMonthCell = MonthCells.FirstOrDefault(c => c.Date == ReferenceDate.Date)
                            ?? MonthCells.FirstOrDefault(c => c.IsInMonth);
    }

    /// <summary>Builds a display-only occurrence of a recurring master at the given local start.</summary>
    private static CalendarEvent CloneOccurrence(CalendarEvent master, DateTime occStart)
    {
        var durTicks = master.StartTimeTicks.HasValue && master.EndTimeTicks.HasValue
            ? master.EndTimeTicks.Value - master.StartTimeTicks.Value
            : TimeSpan.FromMinutes(30).Ticks;
        var startUtcTicks = occStart.ToUniversalTime().Ticks;
        return new CalendarEvent
        {
            Uid             = master.Uid,          // shared Uid → edit/delete act on the series
            AccountId       = master.AccountId,
            Summary         = master.Summary,
            Description     = master.Description,
            Location        = master.Location,
            Organizer       = master.Organizer,
            OrganizerName   = master.OrganizerName,
            StartTimeTicks  = startUtcTicks,
            EndTimeTicks    = startUtcTicks + durTicks,
            Sequence        = master.Sequence,
            Method          = master.Method,
            SourceMessageId = master.SourceMessageId,
            SourceFolder    = master.SourceFolder,
            ResponseStatus  = master.ResponseStatus,
            IsAllDay        = master.IsAllDay,
            IsGraph         = master.IsGraph,
            // Carry the calendar tag so an expanded occurrence of a recurring server calendar
            // (e.g. a CalDAV series) still filters under its own calendar node.
            CalendarId      = master.CalendarId,
            CalendarName    = master.CalendarName,
            RecurrenceRule  = master.RecurrenceRule,
            ExDates         = master.ExDates,
            OccurrenceStart = occStart,
        };
    }

    private void AnnounceOpenHint()
    {
        var count = VisibleEvents.Count;
        if (count == 0)
        {
            Announce("Calendar. No events. Press N to create an appointment.",
                     AnnouncementCategory.Hint);
        }
        else
        {
            Announce($"Calendar. {count} upcoming event{(count == 1 ? "" : "s")}. " +
                     "Use Up and Down arrows to browse. Press Enter to open. Press N for a new appointment, " +
                     "E to edit, Delete to remove. Press A for agenda, D for day, W for week; " +
                     "Control plus Left or Right to move between days or weeks. Press Escape to return to the folder list.",
                     AnnouncementCategory.Hint);
        }
    }

    private void Announce(string text, AnnouncementCategory category)
        => AnnouncementRequested?.Invoke(text, category);
}