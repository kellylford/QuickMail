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

    [Fact]
    public void MissingTitle_FailsValidation()
    {
        var vm = new EventEditorViewModel(DateTime.Now) { Title = "   " };
        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Contains("Title", error);
    }

    [Fact]
    public void EndBeforeStart_FailsValidation()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 10, 0, 0))
        {
            Title = "Backwards",
            StartDate = new DateTime(2026, 7, 16),
            StartTime = "10:00 AM",
            EndDate = new DateTime(2026, 7, 16),
            EndTime = "9:00 AM",
        };
        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Contains("before", error);
    }

    [Fact]
    public void InvalidStartTime_FailsWithFriendlyMessage()
    {
        var vm = new EventEditorViewModel(DateTime.Now)
        {
            Title = "Bad time",
            StartDate = DateTime.Today,
            StartTime = "not a time",
        };
        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Contains("valid time", error);
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

        vm.Title = "Standup (edited)";
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal("local-abc", evt.Uid); // same Uid => upsert updates in place
        Assert.Equal("Standup (edited)", evt.Summary);
    }

    [Fact]
    public void AllDay_SpansWholeDay_AndIgnoresTimeText()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 17, 9, 0, 0))
        {
            Title = "Conference",
            IsAllDay = true,
            StartDate = new DateTime(2026, 7, 17),
            EndDate = new DateTime(2026, 7, 17),
            StartTime = "garbage", // ignored when all-day
        };

        Assert.False(vm.HasTimes);
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.True(evt.IsAllDay);
        var start = new DateTime(evt.StartTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        var end = new DateTime(evt.EndTimeTicks!.Value, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(new DateTime(2026, 7, 17, 0, 0, 0), start);
        Assert.Equal(start.Date, end.Date);          // single-day all-day stays within the day
        Assert.Contains("all day", evt.DisplayLine);
    }

    [Fact]
    public void AllDay_EndDateBeforeStart_FailsValidation()
    {
        var vm = new EventEditorViewModel(DateTime.Now)
        {
            Title = "Bad range",
            IsAllDay = true,
            StartDate = new DateTime(2026, 7, 17),
            EndDate = new DateTime(2026, 7, 16),
        };
        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Contains("before", error);
    }

    [Fact]
    public void Repeat_None_ProducesNoRecurrence()
    {
        var vm = new EventEditorViewModel(DateTime.Now) { Title = "One-off", RepeatIndex = 0 };
        Assert.False(vm.HasRepeat);
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Null(evt.RecurrenceRule);
        Assert.False(evt.IsRecurring);
    }

    [Fact]
    public void Repeat_WeeklyEveryTwo_BuildsRRule()
    {
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 14, 9, 0, 0))
        {
            Title = "Biweekly sync",
            StartDate = new DateTime(2026, 7, 14),
            StartTime = "9:00 AM",
            EndTime = "9:30 AM",
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
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 13, 9, 0, 0))
        {
            Title = "MWF workout",
            StartDate = new DateTime(2026, 7, 13),
            StartTime = "6:00 AM",
            EndTime = "7:00 AM",
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
        var vm = new EventEditorViewModel(DateTime.Now)
        {
            Title = "Simple weekly",
            StartDate = DateTime.Today,
            StartTime = "9:00 AM",
            EndTime = "9:30 AM",
            RepeatIndex = 2,
        };
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.DoesNotContain("BYDAY", evt.RecurrenceRule); // engine falls back to start weekday
    }

    [Fact]
    public void Repeat_DayCheckboxesIgnored_WhenNotWeekly()
    {
        var vm = new EventEditorViewModel(DateTime.Now)
        {
            Title = "Daily thing",
            StartDate = DateTime.Today,
            StartTime = "9:00 AM",
            EndTime = "9:30 AM",
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
        var vm = new EventEditorViewModel(DateTime.Now)
        {
            Title = "Bad repeat",
            StartDate = new DateTime(2026, 7, 14),
            StartTime = "9:00 AM",
            RepeatIndex = 1,
            RepeatUntil = new DateTime(2026, 7, 10),
        };
        Assert.False(vm.TryBuildEvent(out _, out var error));
        Assert.Contains("before the start", error);
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
        var vm = new EventEditorViewModel(DateTime.Now) { Title = "Lunch" };
        CalendarEvent? captured = null;
        vm.Saved += e => captured = e;

        vm.SaveCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal("Lunch", captured!.Summary);
    }

    [Fact]
    public void Save_WithInvalidData_AnnouncesInsteadOfRaisingSaved()
    {
        var vm = new EventEditorViewModel(DateTime.Now) { Title = "" };
        var saved = false;
        string? announced = null;
        vm.Saved += _ => saved = true;
        vm.AnnouncementRequested += (t, _) => announced = t;

        vm.SaveCommand.Execute(null);

        Assert.False(saved);
        Assert.NotNull(announced);
        Assert.Contains("Title", announced);
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
        Assert.Equal("My Appointments (this computer)", vm.SaveTargetLabels[0]);
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
        Assert.Equal("Repeating appointments can only be saved to My Appointments for now.", error);

        // Back on the local target the same repeat is fine.
        vm.SelectedTargetIndex = 0;
        Assert.True(vm.TryBuildEvent(out var evt, out _));
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
        Assert.True(evt.IsRecurring);
    }

    [Fact]
    public void SaveTargets_NoAccounts_PickerHidden()
    {
        var vm = new EventEditorViewModel(DateTime.Now);
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
