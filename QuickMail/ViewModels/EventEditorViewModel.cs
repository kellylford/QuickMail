using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// One entry in the appointment editor's "Calendar" save-target picker: the local calendar
/// (<see cref="CalendarEvent.LocalAccountId"/>) or a server-backed account's calendar.
/// <paramref name="CalendarId"/> is the CalDAV collection URL for an iCloud target (which offers
/// one entry per calendar, e.g. Home / Family); it is null for Local, Microsoft, and Google
/// targets, which save to the account's default/primary calendar.
/// </summary>
public sealed record CalendarSaveTarget(string Label, Guid AccountId,
                                        string? CalendarId = null, string? CalendarName = null);

/// <summary>Which field a refused save blames, so the View can move focus to it.</summary>
public enum EditorField
{
    None,
    Title,
    Start,
    End,
    Repeat,
    RepeatInterval,
    RepeatUntil,
    SaveTarget,
}

/// <summary>
/// Authoring ViewModel for a single calendar appointment. Holds the editable fields (title,
/// start and end instants, location, notes, recurrence), validates them, and produces a
/// <see cref="CalendarEvent"/> on save. Pure VM: no View types, no window references. The View
/// subscribes to <see cref="Saved"/> / <see cref="Cancelled"/> to close and persist, to
/// <see cref="FieldFocusRequested"/> to put focus on a rejected field, and to
/// <see cref="AnnouncementRequested"/> for spoken feedback.
///
/// The start and the end are each a single <see cref="DateTime"/> rather than a date part plus a
/// time part. The editor's date field and time field bind to the SAME property and differ only in
/// how they format it and how far they step it. That is what lets a time stepped up from 23:50
/// land on 00:05 tomorrow instead of wrapping back to the same morning, and it is why there is no
/// nullable "no date yet" state to validate against.
///
/// The two are linked by <see cref="_duration"/>: moving the start moves the end with it, so the
/// appointment keeps its length and the user is not sent back to fix a field they never touched.
/// </summary>
public partial class EventEditorViewModel : ObservableObject
{
    private readonly string _uid;
    private readonly List<CalendarSaveTarget> _saveTargets;
    private readonly int? _masterRepeatCount;
    private readonly Guid _editAccountId = CalendarEvent.LocalAccountId;

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
    public Guid SelectedTargetAccountId => SelectedTarget?.AccountId ?? CalendarEvent.LocalAccountId;

    /// <summary>
    /// The chosen target's calendar (CalDAV collection URL) and display name — set only for an
    /// iCloud target, which the save uses to tag and route the event. Null for Local / Microsoft /
    /// Google (their default calendar).
    /// </summary>
    public string? SelectedTargetCalendarId => SelectedTarget?.CalendarId;
    public string? SelectedTargetCalendarName => SelectedTarget?.CalendarName;

    private CalendarSaveTarget? SelectedTarget =>
        SelectedTargetIndex >= 0 && SelectedTargetIndex < _saveTargets.Count
            ? _saveTargets[SelectedTargetIndex]
            : null;

    /// <summary>
    /// True when the View should show the save-target picker: only for NEW appointments (an
    /// appointment cannot move calendars in v1) and only when there is a real choice to make.
    /// </summary>
    public bool ShowSaveTarget => !IsEdit && _saveTargets.Count > 1;

    /// <summary>
    /// Which calendar the appointment being edited lives on ("Apple: Family", "Local", …), shown
    /// read-only in the editor (editing can't move an appointment between calendars in v1). Empty
    /// for a new appointment (which uses the save-target picker instead).
    /// </summary>
    public string EditCalendarLabel { get; } = string.Empty;

    /// <summary>True when the read-only "Calendar" line should show — i.e. editing a tagged event.</summary>
    public bool ShowEditCalendar => IsEdit && !string.IsNullOrEmpty(EditCalendarLabel);

    /// <summary>Window title text ("New appointment" / "Edit appointment").</summary>
    public string WindowTitle => IsEdit ? "Edit appointment" : "New appointment";

    [ObservableProperty] private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimes))]
    private bool _isAllDay;

    /// <summary>
    /// When the appointment starts. The Start date field and the Start time field both bind here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatUntilSeed))]
    private DateTime _start;

    /// <summary>
    /// When the appointment ends. For an all-day appointment this is the INCLUSIVE last day at
    /// midnight; <see cref="TryBuildEvent"/> converts it to 23:59:59 on that day when saving.
    /// </summary>
    [ObservableProperty] private DateTime _end;

    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    /// <summary>
    /// How long the appointment lasts. Preserved when the start moves; re-derived whenever the end
    /// is edited directly. Never stored negative — an end briefly dragged behind the start would
    /// otherwise make the next start edit push the end even further into the past.
    /// </summary>
    private TimeSpan _duration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Set while one changed-handler is writing its sibling, so the sibling treats that write as
    /// propagation rather than as a fresh edit. A single bool is enough: propagation runs exactly
    /// one level deep (Start to End, never the reverse) and a VM is only ever touched on the UI
    /// thread.
    /// </summary>
    private bool _syncing;

    /// <summary>Time of day and length remembered across an All day round trip.</summary>
    private TimeSpan _savedTimeOfDay = TimeSpan.FromHours(9);
    private TimeSpan _savedDuration = TimeSpan.FromMinutes(30);

    partial void OnStartChanged(DateTime value)
    {
        if (_syncing) { Revalidate(); return; }

        _syncing = true;
        try { End = value + _duration; }
        finally { _syncing = false; }
        Revalidate();
    }

    partial void OnEndChanged(DateTime value)
    {
        // A direct edit of the end redefines the length. When it came from OnStartChanged the
        // length is already correct and re-deriving it here would just recompute what produced it.
        if (!_syncing)
        {
            var length = value - Start;
            _duration = length > TimeSpan.Zero ? length : TimeSpan.Zero;
        }
        Revalidate();
    }

    partial void OnTitleChanged(string value) => Revalidate();

    partial void OnRepeatUntilChanged(DateTime? value) => Revalidate();

    partial void OnRepeatIntervalChanged(int value) => Revalidate();

    partial void OnIsAllDayChanged(bool value)
    {
        _syncing = true;   // both endpoints are rewritten below; neither write is a user edit
        try
        {
            if (value)
            {
                _savedTimeOfDay = Start.TimeOfDay;
                _savedDuration = _duration;

                // Day count comes from the DATES, not from the length: an appointment running
                // 23:45 to 00:15 touches two calendar dates but is one all-day event. Math.Max
                // absorbs the transient end-before-start state described on _duration.
                var days = Math.Max(0, (End.Date - Start.Date).Days);
                Start = Start.Date;
                End = Start.Date.AddDays(days);
                _duration = End - Start;
            }
            else
            {
                Start = Start.Date + _savedTimeOfDay;
                _duration = _savedDuration > TimeSpan.Zero ? _savedDuration : TimeSpan.FromMinutes(30);
                End = Start + _duration;
            }
        }
        finally { _syncing = false; }
        Revalidate();
    }

    // Recurrence — 0 = Does not repeat, 1 = Daily, 2 = Weekly, 3 = Monthly, 4 = Yearly.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepeat))]
    [NotifyPropertyChangedFor(nameof(RepeatUnitLabel))]
    [NotifyPropertyChangedFor(nameof(IsWeekly))]
    private int _repeatIndex;

    [ObservableProperty] private int _repeatInterval = 1;

    /// <summary>
    /// When the series stops, or null for "repeat forever". Unlike the start and the end this one
    /// keeps its nullable form, because an empty repeat-until is a real and common answer rather
    /// than a field the user has not reached yet.
    /// </summary>
    [ObservableProperty] private DateTime? _repeatUntil;

    /// <summary>
    /// What the empty repeat-until field should adopt the first time it is stepped, so the control
    /// does not have to invent a date of its own.
    /// </summary>
    public DateTime RepeatUntilSeed => Start.Date.AddMonths(1);

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

    /// <summary>
    /// Why the last save was refused, or empty when the fields are valid. Shown on a permanent
    /// error line in the editor, so the reason survives every announcement setting being switched
    /// off — the old code announced the message and nothing else, which made Save look dead to
    /// anyone who had turned result announcements off.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>
    /// Raised after a refused save so the View can move focus to the field at fault. Focus
    /// movement is the other half of the feedback: a screen reader announces it as ordinary
    /// navigation, with no programmatic notification to be filtered out by a config setting.
    /// </summary>
    public event Action<EditorField>? FieldFocusRequested;

    /// <summary>
    /// Whether Save has been pressed yet. Validation stays silent until it has: flagging an
    /// incomplete appointment while the user is still filling it in is nagging, but going quiet
    /// after the first refusal — so the user has to press Save again to learn whether their fix
    /// worked — is worse.
    /// </summary>
    private bool _saveAttempted;

    private void Revalidate()
    {
        if (!_saveAttempted) return;
        ErrorText = TryBuildEvent(out _, out var error, out _) ? string.Empty : error;
    }

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
        // Backing fields, not the properties: the changed-handlers would run duration propagation
        // and All day bookkeeping before anything is subscribed, and there is nothing for them to
        // do here anyway.
        //
        // The end is derived from the start instant plus the length, so 23:45 + 30 minutes is
        // 00:15 the next day. The old code pinned the end to the start's DATE, which put the end
        // before the start and made the editor reject its own untouched defaults late in the
        // evening (#378). With one instant per endpoint that failure mode no longer exists.
        _start = RoundUpToQuarterHour(defaultStart);
        _duration = TimeSpan.FromMinutes(30);
        _end = _start + _duration;
    }

    /// <summary>
    /// Preselects the save target matching the user's default calendar (issue #497), and reports
    /// whether one was found. Prefers the exact calendar; falls back to any target on the same
    /// account, because the tree offers a node per discovered calendar while a Microsoft or Google
    /// account contributes a single target (its default calendar) — "that account" is still the
    /// right answer there. An unmatched default (the account was removed, or the calendar has not
    /// synced yet) leaves the picker on the local calendar rather than guessing.
    /// </summary>
    public bool SelectTarget(Guid accountId, string? calendarId)
    {
        var index = _saveTargets.FindIndex(t => t.AccountId == accountId
            && string.Equals(t.CalendarId ?? string.Empty, calendarId ?? string.Empty, StringComparison.Ordinal));
        if (index < 0) index = _saveTargets.FindIndex(t => t.AccountId == accountId);
        if (index < 0) return false;
        SelectedTargetIndex = index;
        return true;
    }

    private static List<CalendarSaveTarget> BuildSaveTargets(IReadOnlyList<CalendarSaveTarget>? accountTargets)
    {
        var targets = new List<CalendarSaveTarget>
        {
            new("Local Calendar (this computer)", CalendarEvent.LocalAccountId),
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
        _editAccountId = existing.AccountId;   // server rows keep their account; the
                                               // recurring-stays-local rule can then fire on edits too
        // Show which calendar this appointment lives on (read-only). CalendarSourceLabel is stamped
        // by CalendarViewModel for every list row ("Apple: Family" / "Local"); fall back to "Local"
        // for a locally-authored row that wasn't stamped.
        EditCalendarLabel = !string.IsNullOrEmpty(existing.CalendarSourceLabel)
            ? existing.CalendarSourceLabel
            : existing.AccountId == CalendarEvent.LocalAccountId ? "Local" : string.Empty;
        // Editing never moves an appointment between calendars (v1) — no picker.
        _saveTargets = BuildSaveTargets(null);
        SaveTargetLabels = _saveTargets.ConvertAll(t => t.Label);
        IsRecurringEdit = existing.IsRecurring && existing.OccurrenceStart.HasValue;
        OccurrenceStart = existing.OccurrenceStart;
        Title = existing.Summary;
        Location = existing.Location;
        Notes = existing.Description;

        // Backing fields throughout, so setting All day does not run the collapse-to-dates
        // bookkeeping over values that were already stored in all-day form.
        _isAllDay = existing.IsAllDay;
        _start = existing.StartTime ?? DateTime.Now;
        var end = existing.EndTime ?? _start.AddMinutes(30);
        // A stored all-day event ends at 23:59:59 on its last day; the editor holds that day at
        // midnight, which is the inclusive form TryBuildEvent converts back on save.
        _end = _isAllDay ? end.Date : end;
        _duration = _end - _start;
        if (_duration < TimeSpan.Zero) _duration = TimeSpan.Zero;

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
            _masterRepeatCount = rule.Count;   // preserved through a whole-series save (no UI yet)
            foreach (var day in rule.ByDay)
                SetRepeatDay(day, true);
        }
    }

    [RelayCommand]
    private void Save()
    {
        _saveAttempted = true;
        if (!TryBuildEvent(out var evt, out var error, out var field))
        {
            // Cleared before it is set so an identical message notifies again. ErrorText is an
            // [ObservableProperty], which suppresses an equal assignment — without this, pressing
            // Save a second time on a field the user has not fixed would change nothing anywhere,
            // which is the "the button does nothing" symptom this whole surface exists to remove.
            ErrorText = string.Empty;
            ErrorText = error;
            FieldFocusRequested?.Invoke(field);
            AnnouncementRequested?.Invoke(error, AnnouncementCategory.Result);
            return;
        }
        ErrorText = string.Empty;
        Saved?.Invoke(evt);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    /// <summary>
    /// Validates the fields and, on success, produces the persisted <see cref="CalendarEvent"/>.
    /// Returns false with a spoken-friendly <paramref name="error"/> message on failure.
    /// </summary>
    public bool TryBuildEvent(out CalendarEvent evt, out string error)
        => TryBuildEvent(out evt, out error, out _);

    /// <summary>
    /// As above, and additionally reports which <paramref name="field"/> was rejected so the
    /// caller can put focus there.
    /// </summary>
    public bool TryBuildEvent(out CalendarEvent evt, out string error, out EditorField field)
    {
        evt = null!;
        error = string.Empty;
        field = EditorField.None;

        if (string.IsNullOrWhiteSpace(Title))
        {
            error = "Title is required.";
            field = EditorField.Title;
            return false;
        }

        DateTime start, end;

        if (IsAllDay)
        {
            // All-day spans whole days: start at 00:00 and end at 23:59:59 on the last day.
            start = Start.Date;
            if (End.Date < start)
            {
                error = "The end date is before the start date.";
                field = EditorField.End;
                return false;
            }
            end = End.Date.AddDays(1).AddSeconds(-1);
        }
        else
        {
            // Both endpoints are real instants by construction — the fields cannot hold text that
            // failed to parse, and duration linking keeps the end ahead of the start whenever the
            // start moves. This branch is now only reachable by editing the END backwards on
            // purpose, which is why it stays.
            start = Start;
            end = End;
            if (end < start)
            {
                error = "The end time is before the start time.";
                field = EditorField.End;
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
                field = EditorField.RepeatInterval;
                return false;
            }
            if (RepeatUntil is DateTime u && u.Date < start.Date)
            {
                error = "The repeat end date is before the start date.";
                field = EditorField.RepeatUntil;
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
                // A COUNT-limited series must stay COUNT-limited across an edit even though the
                // editor has no count field yet — dropping it made the series infinite.
                Count = RepeatUntil is null ? _masterRepeatCount : null,
            };
            if (RepeatIndex == 2)
                rule.ByDay.AddRange(CheckedRepeatDays());
            rrule = rule.ToRRule();
        }

        // v1 calendar push handles single events only — a repeating appointment must stay local.
        var targetAccountId = IsDetachSave ? CalendarEvent.LocalAccountId
            : IsEdit ? _editAccountId
            : SelectedTargetAccountId;
        if (targetAccountId != CalendarEvent.LocalAccountId && rrule != null)
        {
            // Fires for new appointments targeting an account AND for edits of server-synced
            // events that try to add a repeat — previously the edit path skipped this and the
            // whole edit was discarded post-close by the push's NotSupportedException.
            error = "Repeating appointments can only be saved to Local Calendar for now.";
            field = IsEdit ? EditorField.Repeat : EditorField.SaveTarget;
            return false;
        }

        evt = new CalendarEvent
        {
            Uid            = IsDetachSave ? "local-" + Guid.NewGuid().ToString("N") : _uid,
            AccountId      = targetAccountId,
            // Tag with the chosen calendar for a new iCloud target (empty for Local / Microsoft /
            // Google / detach). An edit keeps its stored calendar via the caller (ServerUpdate),
            // and the edit editor offers only the local target, so this resolves to empty there.
            CalendarId     = SelectedTargetCalendarId ?? string.Empty,
            CalendarName   = SelectedTargetCalendarName ?? string.Empty,
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
