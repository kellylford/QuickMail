using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="EventEditorViewModel"/> — validation and event construction.
/// Pure VM logic, no UI.
/// </summary>
public class EventEditorViewModelTests
{
    /// <summary>
    /// Fixed seed for tests that only need "some valid start time". Seeding these from
    /// <c>DateTime.Now</c> made them fail for anyone running the suite late in the evening — the
    /// default end rolls past midnight — so the time of day is pinned here instead. Tests that are
    /// specifically about a time of day pass their own value. (#378)
    /// </summary>
    private static readonly DateTime FixedStart = new(2026, 7, 16, 9, 3, 0);

    [Fact]
    public void NewEditor_DefaultsToLocalAccountAndHalfHourSlot()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 3, 0));
        vm.Title = "Dentist";

        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
        Assert.True(evt.IsUserCreated);
        Assert.StartsWith("local-", evt.Uid);
        Assert.Equal("Dentist", evt.Summary);
        // 9:03 rounds up to 9:15; end defaults 30 min later => 30 min duration.
        var start = new DateTime(evt.StartTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        var end = new DateTime(evt.EndTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(TimeSpan.FromMinutes(30), end - start);
    }

    [Theory]
    [InlineData(23, 38)]   // rounds to 23:45; end 00:15 the next day
    [InlineData(23, 50)]   // start itself rounds over midnight to 00:00
    [InlineData(23, 31)]
    [InlineData(0, 0)]     // start of day, for contrast
    public void NewEditor_LateInTheDay_DefaultsToAValidRange(int hour, int minute)
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, hour, minute, 0)) { Title = "Late" };

        Assert.True(vm.TryBuildEvent(out var evt, out var error), error);
        var start = new DateTime(evt.StartTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        var end = new DateTime(evt.EndTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(TimeSpan.FromMinutes(30), end - start);
    }

    [Fact]
    public void NewEditor_WhenDefaultEndRollsPastMidnight_EndCarriesToTheNextDay()
    {
        // 23:38 rounds up to 23:45; the default half-hour lands at 00:15 the following day. The
        // old editor pinned the end to the start's DATE and so produced an end before the start,
        // making the untouched defaults unsaveable late in the evening. (#378)
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 23, 38, 0));

        Assert.Equal(new DateTime(2026, 7, 16, 23, 45, 0), vm.Start);
        Assert.Equal(new DateTime(2026, 7, 17, 0, 15, 0), vm.End);
    }

    [Fact]
    public void MissingTitle_FailsValidation()
    {
        var vm = new EventEditorViewModel(FixedStart) { Title = "   " };
        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Contains("Title", error);
    }

    [Fact]
    public void EndBeforeStart_FailsValidation()
    {
        // Only reachable by dragging the END backwards on purpose — moving the start takes the end
        // with it. The branch survives for exactly this case.
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 10, 0, 0)) { Title = "Backwards" };
        vm.End = vm.Start.AddHours(-1);

        Assert.False(vm.TryBuildEvent(out _, out var error, out var field));
        Assert.Contains("before", error);
        Assert.Equal(EditorField.End, field);
    }

    [Fact]
    public void EditExisting_PreservesUidAndRoundTripsFields()
    {
        var original = new CalendarEvent
        {
            Uid = "local-abc",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Standup",
            Location = "Room 1",
            Description = "Daily sync",
            StartTimeTicks = new DateTime(2026, 7, 16, 9, 0, 0).ToUniversalTime().Ticks,
            EndTimeTicks = new DateTime(2026, 7, 16, 9, 30, 0).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };

        var vm = new EventEditorViewModel(original);
        Assert.True(vm.IsEdit);
        Assert.Equal("Standup", vm.Title);
        Assert.Equal("Room 1", vm.Location);
        Assert.Equal("Daily sync", vm.Notes);

        Assert.Equal(new DateTime(2026, 7, 16, 9, 0, 0), vm.Start);
        Assert.Equal(new DateTime(2026, 7, 16, 9, 30, 0), vm.End);

        vm.Title = "Standup (edited)";
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal("local-abc", evt.Uid); // same Uid => upsert updates in place
        Assert.Equal("Standup (edited)", evt.Summary);
    }

    [Fact]
    public void AllDay_SpansWholeDay_AndZeroesTheTimeOfDay()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 17, 9, 0, 0))
        {
            Title = "Conference",
            IsAllDay = true,
        };

        Assert.False(vm.HasTimes);
        Assert.Equal(new DateTime(2026, 7, 17), vm.Start);
        Assert.Equal(new DateTime(2026, 7, 17), vm.End);   // inclusive last day, held at midnight

        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.True(evt.IsAllDay);
        var start = new DateTime(evt.StartTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        var end = new DateTime(evt.EndTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(new DateTime(2026, 7, 17, 0, 0, 0), start);
        Assert.Equal(start.Date, end.Date);          // single-day all-day stays within the day
        Assert.Contains("all day", evt.DisplayLine);
    }

    [Fact]
    public void AllDay_RoundTrip_RestoresTheTimeAndLength()
    {
        // Seeded at 9:05 because the constructor rounds up to the next quarter hour, giving 9:15.
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 17, 9, 5, 0)) { Title = "Review" };
        vm.End = vm.Start.AddMinutes(90);

        vm.IsAllDay = true;
        vm.IsAllDay = false;

        // Turning All day off must give back the appointment the user had, not strand them at
        // midnight with a fresh half-hour default.
        Assert.Equal(new DateTime(2026, 7, 17, 9, 15, 0), vm.Start);
        Assert.Equal(TimeSpan.FromMinutes(90), vm.End - vm.Start);
    }

    [Fact]
    public void AllDay_WhenTheAppointmentSpansMidnight_IsOneDayNotTwo()
    {
        // 23:45 to 00:15 touches two calendar dates but is a single day's event.
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 23, 38, 0)) { Title = "Late" };
        Assert.Equal(new DateTime(2026, 7, 17, 0, 15, 0), vm.End);

        vm.IsAllDay = true;

        Assert.Equal(new DateTime(2026, 7, 16), vm.Start);
        Assert.Equal(new DateTime(2026, 7, 17), vm.End);
    }

    [Fact]
    public void AllDay_MultiDaySpanSurvivesTheToggle()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 17, 9, 0, 0)) { Title = "Conference" };
        vm.IsAllDay = true;
        vm.End = new DateTime(2026, 7, 19);

        Assert.True(vm.TryBuildEvent(out var evt, out var error), error);
        var end = new DateTime(evt.EndTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(new DateTime(2026, 7, 19, 23, 59, 59), end);
    }

    [Fact]
    public void AllDay_EndDateBeforeStart_FailsValidation()
    {
        var vm = new EventEditorViewModel(FixedStart) { Title = "Bad range", IsAllDay = true };
        vm.End = vm.Start.AddDays(-1);

        Assert.False(vm.TryBuildEvent(out _, out var error, out var field));
        Assert.Contains("before", error);
        Assert.Equal(EditorField.End, field);
    }

    [Fact]
    public void Repeat_None_ProducesNoRecurrence()
    {
        var vm = new EventEditorViewModel(FixedStart) { Title = "One-off", RepeatIndex = 0 };
        Assert.False(vm.HasRepeat);
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Null(evt.RecurrenceRule);
        Assert.False(evt.IsRecurring);
    }

    [Fact]
    public void Repeat_WeeklyEveryTwo_BuildsRRule()
    {
        // The constructor already produces 9:00 to 9:30 from this seed, so the range needs no setup.
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 14, 9, 0, 0))
        {
            Title = "Biweekly sync",
            RepeatIndex = 2,        // Weekly
            RepeatInterval = 2,
        };
        Assert.True(vm.HasRepeat);
        Assert.Equal("weeks", vm.RepeatUnitLabel);
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2", evt.RecurrenceRule);
        Assert.True(evt.IsRecurring);
    }

    [Fact]
    public void Repeat_WeeklyWithDayPicker_BuildsByDay()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 13, 6, 0, 0))
        {
            Title = "MWF workout",
            RepeatIndex = 2,          // Weekly
            RepeatOnMonday = true,
            RepeatOnWednesday = true,
            RepeatOnFriday = true,
        };
        Assert.True(vm.IsWeekly);
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Contains("BYDAY=MO,WE,FR", evt.RecurrenceRule);
    }

    [Fact]
    public void Repeat_WeeklyNoDaysChecked_OmitsByDay()
    {
        var vm = new EventEditorViewModel(FixedStart)
        {
            Title = "Simple weekly",
            RepeatIndex = 2,
        };
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.DoesNotContain("BYDAY", evt.RecurrenceRule); // engine falls back to start weekday
    }

    [Fact]
    public void Repeat_DayCheckboxesIgnored_WhenNotWeekly()
    {
        var vm = new EventEditorViewModel(FixedStart)
        {
            Title = "Daily thing",
            RepeatIndex = 1,          // Daily
            RepeatOnMonday = true,    // leftover check must not leak into the rule
        };
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.DoesNotContain("BYDAY", evt.RecurrenceRule);
    }

    [Fact]
    public void EditWeeklyWithByDay_PopulatesDayCheckboxes()
    {
        var master = new CalendarEvent
        {
            Uid = "local-mwf",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Workout",
            StartTimeTicks = new DateTime(2026, 7, 13, 6, 0, 0).ToUniversalTime().Ticks,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=MO,WE,FR",
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var vm = new EventEditorViewModel(master);
        Assert.True(vm.RepeatOnMonday);
        Assert.True(vm.RepeatOnWednesday);
        Assert.True(vm.RepeatOnFriday);
        Assert.False(vm.RepeatOnTuesday);
        Assert.False(vm.RepeatOnSunday);
    }

    [Fact]
    public void Repeat_UntilBeforeStart_FailsValidation()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 14, 9, 0, 0))
        {
            Title = "Bad repeat",
            RepeatIndex = 1,
            RepeatUntil = new DateTime(2026, 7, 10),
        };
        Assert.False(vm.TryBuildEvent(out _, out var error, out var field));
        Assert.Contains("before the start", error);
        Assert.Equal(EditorField.RepeatUntil, field);
    }

    [Fact]
    public void RepeatUntilSeed_IsAMonthPastTheStart()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 14, 9, 0, 0));

        Assert.Null(vm.RepeatUntil);
        Assert.Equal(new DateTime(2026, 8, 14), vm.RepeatUntilSeed);
    }

    [Fact]
    public void EditRecurring_PopulatesRepeatFields()
    {
        var master = new CalendarEvent
        {
            Uid = "local-rec",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Standup",
            StartTimeTicks = new DateTime(2026, 7, 14, 9, 0, 0).ToUniversalTime().Ticks,
            EndTimeTicks = new DateTime(2026, 7, 14, 9, 15, 0).ToUniversalTime().Ticks,
            RecurrenceRule = "FREQ=MONTHLY;INTERVAL=3",
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var vm = new EventEditorViewModel(master);
        Assert.Equal(3, vm.RepeatIndex);   // Monthly
        Assert.Equal(3, vm.RepeatInterval);
        Assert.True(vm.HasRepeat);
    }

    [Fact]
    public void Save_RaisesSavedWithBuiltEvent()
    {
        var vm = new EventEditorViewModel(FixedStart) { Title = "Lunch" };
        CalendarEvent? captured = null;
        vm.Saved += e => captured = e;

        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal("Lunch", captured!.Summary);
    }

    [Fact]
    public void Save_WithInvalidData_ShowsTheErrorAndAsksForFocus()
    {
        var vm = new EventEditorViewModel(FixedStart) { Title = "" };
        var saved = false;
        string? announced = null;
        EditorField? focused = null;
        vm.Saved += _ => saved = true;
        vm.AnnouncementRequested += (t, _) => announced = t;
        vm.FieldFocusRequested += f => focused = f;

        vm.SaveCommand.Execute(null);

        Assert.False(saved);
        Assert.True(vm.HasError);
        Assert.Contains("Title", vm.ErrorText);
        Assert.Equal(EditorField.Title, focused);
        // The announcement still fires for users who have result announcements on, but it is no
        // longer the only feedback — with them off, the error line and the focus move remain.
        Assert.NotNull(announced);
        Assert.Contains("Title", announced);
    }

    [Fact]
    public void Save_TwiceOnTheSameUnfixedError_NotifiesBothTimes()
    {
        // ErrorText is an [ObservableProperty] and suppresses an equal assignment, so re-setting
        // the same message would change nothing on the second press — Save would look dead. The
        // clear-then-set in the command is what prevents that.
        var vm = new EventEditorViewModel(FixedStart) { Title = "" };
        var notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventEditorViewModel.ErrorText) && vm.HasError) notifications++;
        };

        vm.SaveCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(2, notifications);
    }

    [Fact]
    public void ErrorClearsAsSoonAsTheFieldIsFixed()
    {
        var vm = new EventEditorViewModel(FixedStart) { Title = "" };
        vm.SaveCommand.Execute(null);
        Assert.True(vm.HasError);

        vm.Title = "Now valid";

        Assert.False(vm.HasError);
    }

    [Fact]
    public void ErrorStaysQuietUntilTheFirstSaveAttempt()
    {
        // Flagging an appointment as incomplete while the user is still filling it in is nagging.
        var vm = new EventEditorViewModel(FixedStart) { Title = "" };

        vm.Start = vm.Start.AddDays(1);

        Assert.False(vm.HasError);
    }

    // ── Start and end stay linked by duration ───────────────────────────────────

    [Fact]
    public void MovingTheStart_ShiftsTheEndByTheSameAmount()
    {
        // 9:00 rounds up to 9:15, so the appointment runs 9:15 to 10:45.
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 0, 0)) { Title = "Review" };
        vm.End = vm.Start.AddMinutes(90);

        vm.Start = vm.Start.AddDays(3);

        // This is the whole point: the user who moves an appointment to another day must not then
        // be sent back to fix an end date they never touched.
        Assert.Equal(new DateTime(2026, 7, 19, 9, 15, 0), vm.Start);
        Assert.Equal(new DateTime(2026, 7, 19, 10, 45, 0), vm.End);
        Assert.True(vm.TryBuildEvent(out _, out var error), error);
    }

    [Fact]
    public void MovingTheStartAcrossMidnight_CarriesTheEndWithIt()
    {
        // 23:38 rounds up to 23:45, so the appointment already ends at 00:15 the next day.
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 23, 38, 0)) { Title = "Late" };

        vm.Start = vm.Start.AddMinutes(30);

        Assert.Equal(new DateTime(2026, 7, 17, 0, 15, 0), vm.Start);
        Assert.Equal(new DateTime(2026, 7, 17, 0, 45, 0), vm.End);
    }

    [Fact]
    public void EditingTheEnd_RedefinesTheDurationForLaterStartMoves()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 0, 0)) { Title = "Review" };

        vm.End = vm.Start.AddHours(2);      // now a two-hour appointment
        vm.Start = vm.Start.AddDays(1);     // which must stay two hours long

        Assert.Equal(TimeSpan.FromHours(2), vm.End - vm.Start);
    }

    [Fact]
    public void EndDraggedBeforeStart_DoesNotStoreANegativeDuration()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 0, 0)) { Title = "Review" };

        vm.End = vm.Start.AddHours(-1);     // invalid, and left standing for the user to see
        vm.Start = vm.Start.AddDays(1);     // a later start edit must heal it, not compound it

        Assert.Equal(vm.Start, vm.End);
        Assert.True(vm.TryBuildEvent(out _, out var error), error);
    }

    [Fact]
    public void StartToEndPropagation_DoesNotRipple()
    {
        // One write to Start must produce exactly one write to End. A missing re-entrancy guard
        // would show up here as a second notification.
        var vm = new EventEditorViewModel(FixedStart) { Title = "Review" };
        var endChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventEditorViewModel.End)) endChanges++;
        };

        vm.Start = vm.Start.AddDays(1);

        Assert.Equal(1, endChanges);
    }

    // ── Save-target (Calendar) picker ────────────────────────────────────────────

    private static readonly Guid AccountA = Guid.NewGuid();

    private static EventEditorViewModel NewEditorWithAccount() =>
        new(new DateTime(2026, 7, 20, 9, 0, 0),
            new[] { new CalendarSaveTarget("Work (Microsoft)", AccountA) });

    [Fact]
    public void SaveTargets_DefaultToLocal_WithAccountListed()
    {
        var vm = NewEditorWithAccount();

        Assert.True(vm.ShowSaveTarget);
        Assert.Equal(2, vm.SaveTargetLabels.Count);
        Assert.Equal("Local Calendar (this computer)", vm.SaveTargetLabels[0]);
        Assert.Equal("Work (Microsoft)", vm.SaveTargetLabels[1]);
        Assert.Equal(0, vm.SelectedTargetIndex);
        Assert.Equal(CalendarEvent.LocalAccountId, vm.SelectedTargetAccountId);

        vm.Title = "Local by default";
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
    }

    [Fact]
    public void SaveTargets_AccountSelected_SetsEventAccountId()
    {
        var vm = NewEditorWithAccount();
        vm.Title = "On the work calendar";
        vm.SelectedTargetIndex = 1;

        Assert.Equal(AccountA, vm.SelectedTargetAccountId);
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal(AccountA, evt.AccountId);
        Assert.False(evt.IsUserCreated);
    }

    [Fact]
    public void SaveTargets_RecurringToAccount_FailsValidation()
    {
        var vm = NewEditorWithAccount();
        vm.Title = "Weekly on the account";
        vm.SelectedTargetIndex = 1;
        vm.RepeatIndex = 2; // Weekly

        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Equal("Repeating appointments can only be saved to Local Calendar for now.", error);

        // Back on the local target the same repeat is fine.
        vm.SelectedTargetIndex = 0;
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
        Assert.True(evt.IsRecurring);
    }

    [Fact]
    public void SaveTargets_NoAccounts_PickerHidden()
    {
        var vm = new EventEditorViewModel(FixedStart);
        Assert.False(vm.ShowSaveTarget);
        Assert.Single(vm.SaveTargetLabels);
    }

    [Fact]
    public void SaveTargets_EditMode_PickerHidden()
    {
        var vm = new EventEditorViewModel(new CalendarEvent
        {
            Uid = "local-x", AccountId = CalendarEvent.LocalAccountId, Summary = "Existing",
            StartTimeTicks = DateTime.UtcNow.Ticks,
        });
        Assert.False(vm.ShowSaveTarget);

        // Editing keeps the appointment on the local calendar (cannot move calendars in v1).
        vm.Title = "Existing";
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
    }
}
