using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// One entry in the appointment editor's "Calendar" save-target picker: the local calendar
/// (<see cref="CalendarEvent.LocalAccountId"/>) or a server-backed (Microsoft or Google)
/// account's calendar.
/// </summary>
public sealed record CalendarSaveTarget(string Label, Guid AccountId);

/// <summary>
/// Authoring ViewModel for a single locally-created calendar appointment. Holds the editable
/// fields (title, start/end date+time, location, notes), validates them, and produces a
/// <see cref="CalendarEvent"/> on save. Pure VM: no View types, no window references. The View
/// subscribes to <see cref="Saved"/> / <see cref="Cancelled"/> to close and persist, and to
/// <see cref="AnnouncementRequested"/> for validation feedback.
///
/// v1 scope: timed events only. All-day support needs a persisted model flag and is deferred.
/// </summary>
public partial class EventEditorViewModel : ObservableObject
{
    private readonly string _uid;
    private readonly List<CalendarSaveTarget> _saveTargets;

    /// <summary>True when editing an existing event; false when creating a new one.</summary>
    public bool IsEdit { get; }

    /// <summary>
    /// Labels for the "Calendar" save-target picker. Index 0 is always the local calendar;
    /// the rest are server-backed (Microsoft or Google) accounts. Plain strings so the ComboBox
    /// items announce correctly (Selector accessibility rule).
    /// </summary>
    public IReadOnlyList<string> SaveTargetLabels { get; }

    /// <summary>Selected save target. Defaults to 0 (the local calendar).</summary>
    [ObservableProperty] private int _selectedTargetIndex;

    /// <summary>The account id the appointment will save to (resolved from the picker).</summary>
    public Guid SelectedTargetAccountId =>
        SelectedTargetIndex >= 0 && SelectedTargetIndex < _saveTargets.Count
            ? _saveTargets[SelectedTargetIndex].AccountId
            : CalendarEvent.LocalAccountId;

    /// <summary>
    /// True when the View should show the save-target picker: only for NEW appointments (an
    /// appointment cannot move calendars in v1) and only when there is a real choice to make.
    /// </summary>
    public bool ShowSaveTarget => !IsEdit && _saveTargets.Count > 1;

    /// <summary>Window title text ("New appointment" / "Edit appointment").</summary>
    public string WindowTitle => IsEdit ? "Edit appointment" : "New appointment";

    [ObservableProperty] private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimes))]
    private bool _isAllDay;

    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private string _startTime = string.Empty;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private string _endTime = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    // Recurrence — 0 = Does not repeat, 1 = Daily, 2 = Weekly, 3 = Monthly, 4 = Yearly.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepeat))]
    [NotifyPropertyChangedFor(nameof(RepeatUnitLabel))]
    [NotifyPropertyChangedFor(nameof(IsWeekly))]
    private int _repeatIndex;

    [ObservableProperty] private int _repeatInterval = 1;
    [ObservableProperty] private DateTime? _repeatUntil;

    // Weekly only: which days the appointment repeats on. All unchecked = the start date's weekday.
    [ObservableProperty] private bool _repeatOnSunday;
    [ObservableProperty] private bool _repeatOnMonday;
    [ObservableProperty] private bool _repeatOnTuesday;
    [ObservableProperty] private bool _repeatOnWednesday;
    [ObservableProperty] private bool _repeatOnThursday;
    [ObservableProperty] private bool _repeatOnFriday;
    [ObservableProperty] private bool _repeatOnSaturday;

    /// <summary>True when Weekly is selected — the View shows the day-of-week checkboxes.</summary>
    public bool IsWeekly => RepeatIndex == 2;

    /// <summary>
    /// True when editing one occurrence of a repeating series — the View shows the edit-scope
    /// radio group (This event / All events).
    /// </summary>
    public bool IsRecurringEdit { get; }

    /// <summary>The occurrence's original start (local), for excluding it when detaching.</summary>
    public DateTime? OccurrenceStart { get; }

    /// <summary>Edit scope: true = only this occurrence (default), false = the whole series.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditWholeSeries))]
    [NotifyPropertyChangedFor(nameof(CanEditRepeat))]
    private bool _editThisEventOnly = true;

    /// <summary>Inverse radio binding for the "All events" option.</summary>
    public bool EditWholeSeries
    {
        get => !EditThisEventOnly;
        set => EditThisEventOnly = !value;
    }

    /// <summary>The Repeat controls only make sense when the edit applies to the series.</summary>
    public bool CanEditRepeat => !(IsRecurringEdit && EditThisEventOnly);

    /// <summary>
    /// True when the save should detach this occurrence: caller must EXDATE the master at
    /// <see cref="OccurrenceStart"/> and insert the returned standalone event.
    /// </summary>
    public bool IsDetachSave => IsRecurringEdit && EditThisEventOnly;

    /// <summary>False when the appointment is all-day — the View disables the time fields.</summary>
    public bool HasTimes => !IsAllDay;

    /// <summary>True when a repeat frequency is selected — the View shows interval/until controls.</summary>
    public bool HasRepeat => RepeatIndex > 0;

    /// <summary>Unit word for the "every N ___" interval control.</summary>
    public string RepeatUnitLabel => RepeatIndex switch
    {
        1 => "days", 2 => "weeks", 3 => "months", 4 => "years", _ => "",
    };

    /// <summary>Raised with the built event when the user saves and validation passes.</summary>
    public event Action<CalendarEvent>? Saved;

    /// <summary>Raised when the user cancels.</summary>
    public event Action? Cancelled;

    /// <summary>Raised for screen-reader feedback (validation errors). View calls AccessibilityHelper.Announce.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>
    /// Creates an editor for a new appointment defaulting to the given start (usually now,
    /// rounded). <paramref name="accountTargets"/> lists the server-backed accounts the
    /// appointment may alternatively be saved to; the local calendar is always offered first
    /// and is the default.
    /// </summary>
    public EventEditorViewModel(DateTime defaultStart, IReadOnlyList<CalendarSaveTarget>? accountTargets = null)
    {
        _uid = "local-" + Guid.NewGuid().ToString("N");
        IsEdit = false;
        _saveTargets = BuildSaveTargets(accountTargets);
        SaveTargetLabels = _saveTargets.ConvertAll(t => t.Label);
        var start = RoundUpToQuarterHour(defaultStart);
        StartDate = start.Date;
        StartTime = start.ToString("t");
        EndDate = start.Date;
        EndTime = start.AddMinutes(30).ToString("t");
    }

    private static List<CalendarSaveTarget> BuildSaveTargets(IReadOnlyList<CalendarSaveTarget>? accountTargets)
    {
        var targets = new List<CalendarSaveTarget>
        {
            new("My Appointments (this computer)", CalendarEvent.LocalAccountId),
        };
        if (accountTargets != null)
            foreach (var t in accountTargets)
                if (t.AccountId != CalendarEvent.LocalAccountId)
                    targets.Add(t);
        return targets;
    }

    /// <summary>
    /// Creates an editor populated from an existing locally-created event. For an expanded
    /// occurrence of a repeating series (OccurrenceStart set), the editor opens on the
    /// occurrence's own date/time and offers the This event / All events scope choice.
    /// </summary>
    public EventEditorViewModel(CalendarEvent existing)
    {
        _uid = existing.Uid;
        IsEdit = true;
        // Editing never moves an appointment between calendars (v1) — no picker.
        _saveTargets = BuildSaveTargets(null);
        SaveTargetLabels = _saveTargets.ConvertAll(t => t.Label);
        IsRecurringEdit = existing.IsRecurring && existing.OccurrenceStart.HasValue;
        OccurrenceStart = existing.OccurrenceStart;
        Title = existing.Summary;
        Location = existing.Location;
        Notes = existing.Description;
        IsAllDay = existing.IsAllDay;

        var start = existing.StartTime ?? DateTime.Now;
        StartDate = start.Date;
        StartTime = start.ToString("t");
        var end = existing.EndTime ?? start.AddMinutes(30);
        EndDate = end.Date;
        EndTime = end.ToString("t");

        var rule = Models.RecurrenceRule.Parse(existing.RecurrenceRule);
        if (rule != null)
        {
            RepeatIndex = rule.Frequency switch
            {
                RecurrenceFrequency.Daily => 1,
                RecurrenceFrequency.Weekly => 2,
                RecurrenceFrequency.Monthly => 3,
                RecurrenceFrequency.Yearly => 4,
                _ => 0,
            };
            RepeatInterval = rule.Interval;
            RepeatUntil = rule.Until;
            foreach (var day in rule.ByDay)
                SetRepeatDay(day, true);
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryBuildEvent(out var evt, out var error))
        {
            AnnouncementRequested?.Invoke(error, AnnouncementCategory.Result);
            return;
        }
        Saved?.Invoke(evt);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    /// <summary>
    /// Validates the fields and, on success, produces the persisted <see cref="CalendarEvent"/>.
    /// Returns false with a spoken-friendly <paramref name="error"/> message on failure.
    /// </summary>
    public bool TryBuildEvent(out CalendarEvent evt, out string error)
    {
        evt = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(Title))
        {
            error = "Title is required.";
            return false;
        }
        if (StartDate is null)
        {
            error = "Start date is required.";
            return false;
        }

        DateTime start, end;

        if (IsAllDay)
        {
            // All-day: span whole day(s). Single day => start 00:00, end 23:59:59 same date.
            start = StartDate.Value.Date;
            var endDay = (EndDate ?? StartDate.Value).Date;
            if (endDay < start.Date)
            {
                error = "The end date is before the start date.";
                return false;
            }
            end = endDay.AddDays(1).AddSeconds(-1);
        }
        else
        {
            start = CombineDateAndTime(StartDate.Value, StartTime, out var startOk);
            if (!startOk)
            {
                error = "Start time is not a valid time. Try a format like 9:00 AM.";
                return false;
            }

            var endBaseDate = EndDate ?? StartDate.Value;
            if (string.IsNullOrWhiteSpace(EndTime))
            {
                end = start.AddMinutes(30);
            }
            else
            {
                end = CombineDateAndTime(endBaseDate, EndTime, out var endOk);
                if (!endOk)
                {
                    error = "End time is not a valid time. Try a format like 9:30 AM.";
                    return false;
                }
            }

            if (end < start)
            {
                error = "The end time is before the start time.";
                return false;
            }
        }

        string? rrule = null;
        if (IsDetachSave)
        {
            // Detached occurrence becomes an independent one-off appointment.
        }
        else if (RepeatIndex > 0)
        {
            if (RepeatInterval < 1)
            {
                error = "Repeat interval must be at least 1.";
                return false;
            }
            if (RepeatUntil is DateTime u && u.Date < start.Date)
            {
                error = "The repeat end date is before the start date.";
                return false;
            }
            var rule = new RecurrenceRule
            {
                Frequency = RepeatIndex switch
                {
                    1 => RecurrenceFrequency.Daily,
                    2 => RecurrenceFrequency.Weekly,
                    3 => RecurrenceFrequency.Monthly,
                    _ => RecurrenceFrequency.Yearly,
                },
                Interval = RepeatInterval,
                Until = RepeatUntil,
            };
            if (RepeatIndex == 2)
                rule.ByDay.AddRange(CheckedRepeatDays());
            rrule = rule.ToRRule();
        }

        // v1 calendar push handles single events only — a repeating appointment must stay local.
        var targetAccountId = IsEdit ? CalendarEvent.LocalAccountId : SelectedTargetAccountId;
        if (targetAccountId != CalendarEvent.LocalAccountId && rrule != null)
        {
            error = "Repeating appointments can only be saved to My Appointments for now.";
            return false;
        }

        evt = new CalendarEvent
        {
            Uid            = IsDetachSave ? "local-" + Guid.NewGuid().ToString("N") : _uid,
            AccountId      = targetAccountId,
            Summary        = Title.Trim(),
            Location       = Location.Trim(),
            Description    = Notes.Trim(),
            StartTimeTicks = start.ToUniversalTime().Ticks,
            EndTimeTicks   = end.ToUniversalTime().Ticks,
            IsAllDay       = IsAllDay,
            RecurrenceRule = rrule,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        return true;
    }

    /// <summary>Combines a date with a free-typed time string ("9", "9:00", "9:00 AM", "14:30").</summary>
    private static DateTime CombineDateAndTime(DateTime date, string timeText, out bool ok)
    {
        if (string.IsNullOrWhiteSpace(timeText))
        {
            ok = true;
            return date.Date; // midnight
        }
        if (DateTime.TryParse(timeText.Trim(), out var parsed))
        {
            ok = true;
            return date.Date + parsed.TimeOfDay;
        }
        ok = false;
        return date.Date;
    }

    private void SetRepeatDay(DayOfWeek day, bool value)
    {
        switch (day)
        {
            case DayOfWeek.Sunday: RepeatOnSunday = value; break;
            case DayOfWeek.Monday: RepeatOnMonday = value; break;
            case DayOfWeek.Tuesday: RepeatOnTuesday = value; break;
            case DayOfWeek.Wednesday: RepeatOnWednesday = value; break;
            case DayOfWeek.Thursday: RepeatOnThursday = value; break;
            case DayOfWeek.Friday: RepeatOnFriday = value; break;
            case DayOfWeek.Saturday: RepeatOnSaturday = value; break;
        }
    }

    private IEnumerable<DayOfWeek> CheckedRepeatDays()
    {
        if (RepeatOnSunday) yield return DayOfWeek.Sunday;
        if (RepeatOnMonday) yield return DayOfWeek.Monday;
        if (RepeatOnTuesday) yield return DayOfWeek.Tuesday;
        if (RepeatOnWednesday) yield return DayOfWeek.Wednesday;
        if (RepeatOnThursday) yield return DayOfWeek.Thursday;
        if (RepeatOnFriday) yield return DayOfWeek.Friday;
        if (RepeatOnSaturday) yield return DayOfWeek.Saturday;
    }

    private static DateTime RoundUpToQuarterHour(DateTime dt)
    {
        var minutes = (dt.Minute / 15 + 1) * 15;
        return dt.Date.AddHours(dt.Hour).AddMinutes(minutes);
    }
}
