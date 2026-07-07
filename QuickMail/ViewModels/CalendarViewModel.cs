using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Resources;
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

    [ObservableProperty]
    private BatchObservableCollection<CalendarEvent> _events = [];

    /// <summary>All events after filtering (today filter + declined filter), sorted by start time.</summary>
    public IReadOnlyList<CalendarEvent> VisibleEvents => _filteredEvents;
    private List<CalendarEvent> _filteredEvents = [];

    [ObservableProperty]
    private bool _isTodayFilter;

    [ObservableProperty]
    private CalendarEvent? _selectedEvent;

    /// <summary>True when the calendar pane is unavailable (--online mode).</summary>
    [ObservableProperty]
    private bool _isUnavailable;

    public CalendarViewModel(ICalendarService calendarService, bool onlineMode, bool showDeclinedEvents)
    {
        _calendarService = calendarService;
        _onlineMode = onlineMode;
        _showDeclinedEvents = showDeclinedEvents;
    }

    /// <summary>Loads events from the service and applies filters. Call on pane open.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_onlineMode)
        {
            IsUnavailable = true;
            Announce(Strings.Calendar_Announce_UnavailableOnline,
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
        Announce(Strings.Calendar_Announce_Refreshing, AnnouncementCategory.Status);
        await _calendarService.RefreshAsync();
        ApplyFilters();
        Announce(StringsHelper.Count("Calendar_Announce_Updated", VisibleEvents.Count),
                 AnnouncementCategory.Result);
    }

    /// <summary>Toggles the Today filter. Bound to T.</summary>
    [RelayCommand]
    private void ToggleTodayFilter()
    {
        IsTodayFilter = !IsTodayFilter;
        ApplyFilters();
        if (IsTodayFilter)
            Announce(StringsHelper.Count("Calendar_Announce_TodayFiltered", VisibleEvents.Count),
                     AnnouncementCategory.Result);
        else
            Announce(StringsHelper.Count("Calendar_Announce_AllEvents", VisibleEvents.Count),
                     AnnouncementCategory.Result);
    }

    /// <summary>Opens the source invite message. Bound to Enter.</summary>
    [RelayCommand]
    private void OpenSourceMessage(CalendarEvent? evt)
    {
        if (evt == null) return;
        if (string.IsNullOrEmpty(evt.SourceMessageId))
        {
            Announce(Strings.Calendar_Announce_SourceMessageMissing,
                     AnnouncementCategory.Result);
            return;
        }
        Announce(string.Format(Strings.Calendar_Announce_OpeningMessage, evt.Summary), AnnouncementCategory.Status);
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

    /// <summary>Applies the today and declined filters, rebuilding the visible list.</summary>
    private void ApplyFilters()
    {
        var all = _calendarService.Events;

        IEnumerable<CalendarEvent> filtered = all;

        // Hide declined unless the setting is on.
        if (!_showDeclinedEvents)
            filtered = filtered.Where(e => e.ResponseStatus != CalendarResponseStatus.Declined);

        // Always hide cancelled events — the organizer cancelled the meeting,
        // so it should not clutter the calendar list.
        filtered = filtered.Where(e => e.ResponseStatus != CalendarResponseStatus.Cancelled);

        // Today filter: events starting today (local date).
        if (IsTodayFilter)
        {
            var today = DateTime.Today;
            filtered = filtered.Where(e => e.StartTime.HasValue && e.StartTime.Value.Date == today);
        }

        _filteredEvents = filtered.OrderBy(e => e.StartTimeTicks ?? long.MaxValue).ToList();

        using (Events.BeginBatchScope())
        {
            Events.Clear();
            foreach (var evt in _filteredEvents)
                Events.Add(evt);
        }
    }

    private void AnnounceOpenHint()
    {
        var count = VisibleEvents.Count;
        if (count == 0)
        {
            Announce(Strings.Calendar_Announce_NoEvents, AnnouncementCategory.Status);
        }
        else
        {
            Announce(StringsHelper.Count("Calendar_Announce_OpenHint", count),
                     AnnouncementCategory.Hint);
        }
    }

    private void Announce(string text, AnnouncementCategory category)
        => AnnouncementRequested?.Invoke(text, category);
}