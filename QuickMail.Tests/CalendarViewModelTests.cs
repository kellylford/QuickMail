using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="CalendarViewModel"/> using a <see cref="StubCalendarService"/>.
/// No UI, no STA thread — pure VM logic.
/// </summary>
public class CalendarViewModelTests
{
    private static CalendarEvent MakeEvent(string uid, DateTime? start = null, CalendarResponseStatus status = CalendarResponseStatus.Accepted)
        => new()
        {
            Uid = uid,
            AccountId = Guid.NewGuid(),
            Summary = $"Event {uid}",
            StartTimeTicks = start?.ToUniversalTime().Ticks,
            ResponseStatus = status,
            SourceMessageId = $"msg-{uid}",
            SourceFolder = "INBOX",
        };

    private static CalendarViewModel MakeVm(List<CalendarEvent> events, bool onlineMode = false,
                                            bool showDeclined = false, bool showFieldLabels = false)
    {
        var svc = new StubCalendarService { StoredEvents = events };
        return new CalendarViewModel(svc, onlineMode, showDeclined, showFieldLabels);
    }

    [Fact]
    public async Task LoadAsync_PopulatesEventsAndAnnouncesHint()
    {
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("e1", DateTime.Today.AddHours(10)),
            MakeEvent("e2", DateTime.Today.AddHours(14)),
        });

        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.AnnouncementRequested += (text, category) => { announced = text; cat = category; };

        await vm.LoadAsync();

        Assert.Equal(2, vm.VisibleEvents.Count);
        Assert.Equal(AnnouncementCategory.Hint, cat);
        Assert.Contains("2 upcoming events", announced);
    }

    [Fact]
    public async Task LoadAsync_WithNoEvents_AnnouncesCreateHint()
    {
        var vm = MakeVm(new List<CalendarEvent>());

        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.AnnouncementRequested += (text, category) => { announced = text; cat = category; };

        await vm.LoadAsync();

        Assert.Empty(vm.VisibleEvents);
        Assert.Equal(AnnouncementCategory.Hint, cat);
        Assert.Contains("No events", announced);
        Assert.Contains("Press N", announced);
    }

    [Fact]
    public async Task FieldLabels_Off_StampsDataOnlyAccessibleName()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(10)) },
                        showFieldLabels: false);
        await vm.LoadAsync();

        var name = vm.VisibleEvents[0].AccessibleName;
        Assert.Equal(vm.VisibleEvents[0].DisplayLine, name);
        Assert.DoesNotContain("Subject ", name);
    }

    [Fact]
    public async Task FieldLabels_On_StampsLabeledAccessibleName()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(10)) },
                        showFieldLabels: true);
        await vm.LoadAsync();

        var name = vm.VisibleEvents[0].AccessibleName;
        Assert.StartsWith("Subject ", name);
        Assert.Contains(", when ", name);
    }

    [Fact]
    public async Task ShowFieldLabels_ToggledLive_RestampsRows()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(10)) },
                        showFieldLabels: false);
        await vm.LoadAsync();
        Assert.DoesNotContain("Subject ", vm.VisibleEvents[0].AccessibleName);

        vm.ShowFieldLabels = true;   // e.g. from ApplySettings
        Assert.StartsWith("Subject ", vm.VisibleEvents[0].AccessibleName);
    }

    [Fact]
    public void NewEvent_RaisesEditorRequested_AndSavePersistsToService()
    {
        var svc = new StubCalendarService { StoredEvents = [] };
        var vm = new CalendarViewModel(svc, onlineMode: false, showDeclinedEvents: false);

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.NewEventCommand.Execute(null);
        Assert.NotNull(editor);

        editor!.Title = "Coffee";
        editor.SaveCommand.Execute(null);

        Assert.Single(svc.StoredEvents);
        Assert.Equal("Coffee", svc.StoredEvents[0].Summary);
        Assert.True(svc.StoredEvents[0].IsUserCreated);
    }

    [Fact]
    public void EditEvent_OnInviteSourcedEvent_AnnouncesAndDoesNotOpenEditor()
    {
        var invite = MakeEvent("inv1", DateTime.Today.AddHours(9)); // has a real AccountId
        var vm = MakeVm(new List<CalendarEvent> { invite });

        var editorOpened = false;
        string? announced = null;
        vm.EditorRequested += _ => editorOpened = true;
        vm.AnnouncementRequested += (t, _) => announced = t;

        vm.EditEventCommand.Execute(invite);

        Assert.False(editorOpened);
        Assert.Contains("created can be edited", announced);
    }

    [Fact]
    public async Task DayView_FiltersToReferenceDate()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("today-1", today.AddHours(9)),
            MakeEvent("today-2", today.AddHours(15)),
            MakeEvent("tomorrow", today.AddDays(1).AddHours(9)),
        });
        await vm.LoadAsync();
        Assert.Equal(3, vm.VisibleEvents.Count); // Agenda shows all

        vm.ShowDayCommand.Execute(null);         // Day view, reference = today
        Assert.Equal(2, vm.VisibleEvents.Count);
        Assert.All(vm.VisibleEvents, e => Assert.Equal(today, e.StartTime!.Value.Date));
        Assert.Contains("Day:", vm.PeriodLabel);
    }

    [Fact]
    public async Task DayView_NextPeriod_MovesToNextDay()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("today", today.AddHours(9)),
            MakeEvent("tomorrow", today.AddDays(1).AddHours(9)),
        });
        await vm.LoadAsync();
        vm.ShowDayCommand.Execute(null);
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("today", vm.VisibleEvents[0].Uid);

        vm.NextPeriodCommand.Execute(null);      // advance one day
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("tomorrow", vm.VisibleEvents[0].Uid);
    }

    [Fact]
    public async Task WeekView_IncludesWholeWeek_ExcludesNextWeek()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("in-week", today.AddHours(9)),
            MakeEvent("plus-3", today.AddDays(3).AddHours(9)),
            MakeEvent("plus-9", today.AddDays(9).AddHours(9)),
        });
        await vm.LoadAsync();
        vm.ShowWeekCommand.Execute(null);

        // The event 9 days out is definitely in a later week regardless of week-start day.
        Assert.DoesNotContain(vm.VisibleEvents, e => e.Uid == "plus-9");
        Assert.Contains(vm.VisibleEvents, e => e.Uid == "in-week");
        Assert.Contains("Week of", vm.PeriodLabel);
    }

    private static CalendarEvent MakeRecurring(string uid, DateTime start, string rrule)
        => new()
        {
            Uid = uid,
            AccountId = CalendarEvent.LocalAccountId,
            Summary = $"Recurring {uid}",
            StartTimeTicks = start.ToUniversalTime().Ticks,
            EndTimeTicks = start.AddMinutes(30).ToUniversalTime().Ticks,
            RecurrenceRule = rrule,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };

    [Fact]
    public async Task WeeklyRecurring_ExpandsAcrossWeekView()
    {
        // Weekly on the reference week's Monday, starting 4 weeks earlier.
        var start = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek + 1).AddDays(-28).AddHours(9);
        var vm = MakeVm(new List<CalendarEvent> { MakeRecurring("w1", start, "FREQ=WEEKLY") });
        await vm.LoadAsync();

        vm.ShowWeekCommand.Execute(null); // ReferenceDate defaults to today

        // Exactly one occurrence falls in the current week (same weekday as start).
        Assert.Single(vm.VisibleEvents);
        Assert.True(vm.VisibleEvents[0].IsRecurring);
        Assert.Equal(start.DayOfWeek, vm.VisibleEvents[0].StartTime!.Value.DayOfWeek);
    }

    [Fact]
    public async Task DailyRecurring_ExpandsAcrossDayAndAgenda()
    {
        var start = DateTime.Today.AddDays(-3).AddHours(8);
        var vm = MakeVm(new List<CalendarEvent> { MakeRecurring("d1", start, "FREQ=DAILY") });
        await vm.LoadAsync();

        // Day view for today → exactly one occurrence today.
        vm.ShowDayCommand.Execute(null);
        Assert.Single(vm.VisibleEvents);
        Assert.Equal(DateTime.Today, vm.VisibleEvents[0].StartTime!.Value.Date);

        // Agenda (all) → many occurrences across the look-ahead window.
        vm.ShowAgendaCommand.Execute(null);
        Assert.True(vm.VisibleEvents.Count > 10);
    }

    [Fact]
    public async Task RecurringWithCount_StopsAfterN()
    {
        var start = DateTime.Today.AddHours(9);
        var vm = MakeVm(new List<CalendarEvent> { MakeRecurring("c1", start, "FREQ=DAILY;COUNT=3") });
        await vm.LoadAsync(); // Agenda, all

        Assert.Equal(3, vm.VisibleEvents.Count);
    }

    [Fact]
    public void DeleteEvent_LocalEvent_ConfirmsThenDeletes()
    {
        var local = new CalendarEvent
        {
            Uid = "local-1",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Gym",
            StartTimeTicks = DateTime.Today.AddHours(18).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var svc = new StubCalendarService { StoredEvents = [local] };
        var vm = new CalendarViewModel(svc, onlineMode: false, showDeclinedEvents: false);

        Action? confirm = null;
        vm.DeleteConfirmRequested += (_, cb) => confirm = cb;

        vm.DeleteEventCommand.Execute(local);
        Assert.NotNull(confirm);      // confirmation requested, not yet deleted
        Assert.Single(svc.StoredEvents);

        confirm!();                    // user confirms
        Assert.Empty(svc.StoredEvents);
    }

    [Fact]
    public async Task LoadAsync_OnlineMode_SetsUnavailableAndAnnouncesHint()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1") }, onlineMode: true);

        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.AnnouncementRequested += (text, category) => { announced = text; cat = category; };

        await vm.LoadAsync();

        Assert.True(vm.IsUnavailable);
        Assert.Empty(vm.VisibleEvents); // no events loaded in online mode
        Assert.Equal(AnnouncementCategory.Hint, cat);
        Assert.Contains("unavailable in online mode", announced);
    }

    [Fact]
    public async Task ToggleTodayFilter_FiltersToToday()
    {
        var today = DateTime.Today.AddHours(10);
        var tomorrow = DateTime.Today.AddDays(1).AddHours(14);
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("today", today),
            MakeEvent("tomorrow", tomorrow),
        });
        await vm.LoadAsync();

        Assert.False(vm.IsTodayFilter);
        Assert.Equal(2, vm.VisibleEvents.Count);

        vm.ToggleTodayFilterCommand.Execute(null);

        Assert.True(vm.IsTodayFilter);
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("today", vm.VisibleEvents[0].Uid);
    }

    [Fact]
    public async Task ToggleTodayFilter_Twice_ClearsFilter()
    {
        var today = DateTime.Today.AddHours(10);
        var tomorrow = DateTime.Today.AddDays(1).AddHours(14);
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("today", today),
            MakeEvent("tomorrow", tomorrow),
        });
        await vm.LoadAsync();

        vm.ToggleTodayFilterCommand.Execute(null);
        Assert.Single(vm.VisibleEvents);

        vm.ToggleTodayFilterCommand.Execute(null);
        Assert.False(vm.IsTodayFilter);
        Assert.Equal(2, vm.VisibleEvents.Count);
    }

    [Fact]
    public async Task LoadAsync_HidesDeclinedEventsByDefault()
    {
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("accepted", DateTime.Today.AddHours(10), CalendarResponseStatus.Accepted),
            MakeEvent("declined", DateTime.Today.AddHours(14), CalendarResponseStatus.Declined),
        }, showDeclined: false);

        await vm.LoadAsync();

        Assert.Single(vm.VisibleEvents);
        Assert.Equal("accepted", vm.VisibleEvents[0].Uid);
    }

    [Fact]
    public async Task LoadAsync_ShowsDeclinedEventsWhenSettingOn()
    {
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("accepted", DateTime.Today.AddHours(10), CalendarResponseStatus.Accepted),
            MakeEvent("declined", DateTime.Today.AddHours(14), CalendarResponseStatus.Declined),
        }, showDeclined: true);

        await vm.LoadAsync();

        Assert.Equal(2, vm.VisibleEvents.Count);
    }

    [Fact]
    public async Task LoadAsync_HidesCancelledEvents()
    {
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("accepted", DateTime.Today.AddHours(10), CalendarResponseStatus.Accepted),
            MakeEvent("cancelled", DateTime.Today.AddHours(14), CalendarResponseStatus.Cancelled),
        });

        await vm.LoadAsync();

        Assert.Single(vm.VisibleEvents);
        Assert.Equal("accepted", vm.VisibleEvents[0].Uid);
    }

    [Fact]
    public async Task OpenSourceMessage_RaisesEventWithCorrectArgs()
    {
        var evt = MakeEvent("e1", DateTime.Today.AddHours(10));
        var vm = MakeVm(new List<CalendarEvent> { evt });
        await vm.LoadAsync();

        Guid? raisedAccountId = null;
        string? raisedFolder = null;
        string? raisedMsgId = null;
        vm.OpenSourceMessageRequested += (accountId, folder, msgId) =>
        {
            raisedAccountId = accountId;
            raisedFolder = folder;
            raisedMsgId = msgId;
        };

        vm.OpenSourceMessageCommand.Execute(evt);

        Assert.Equal(evt.AccountId, raisedAccountId);
        Assert.Equal("INBOX", raisedFolder);
        Assert.Equal("msg-e1", raisedMsgId);
    }

    [Fact]
    public async Task OpenSourceMessage_WithNullEvent_DoesNotRaise()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1") });
        await vm.LoadAsync();

        bool raised = false;
        vm.OpenSourceMessageRequested += (_, _, _) => raised = true;

        vm.OpenSourceMessageCommand.Execute(null);
        Assert.False(raised);
    }

    [Fact]
    public async Task OpenSourceMessage_WithEmptySourceMessageId_AnnouncesUnavailable()
    {
        // Simulates an event whose source was purged from the local cache and had
        // its SourceMessageId cleared by ClearOrphanedCalendarSourceLinksAsync.
        var evt = new CalendarEvent
        {
            Uid = "orphan",
            AccountId = Guid.NewGuid(),
            Summary = "Orphaned meeting",
            SourceMessageId = string.Empty,
            SourceFolder = string.Empty,
            StartTimeTicks = DateTime.Today.AddHours(10).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var vm = MakeVm(new List<CalendarEvent> { evt });
        await vm.LoadAsync();

        bool raised = false;
        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.OpenSourceMessageRequested += (_, _, _) => raised = true;
        vm.AnnouncementRequested += (text, c) => { announced = text; cat = c; };

        vm.OpenSourceMessageCommand.Execute(evt);

        Assert.False(raised);
        Assert.NotNull(announced);
        Assert.Contains("no longer in your local message cache", announced);
        Assert.Equal(AnnouncementCategory.Result, cat);
    }

    [Fact]
    public async Task RefreshCommand_AnnouncesStatusThenResult()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(10)) });
        await vm.LoadAsync();

        var announcements = new List<(string text, AnnouncementCategory cat)>();
        vm.AnnouncementRequested += (text, cat) => announcements.Add((text, cat));

        vm.RefreshCommand.Execute(null);

        // Wait for the async refresh to complete.
        await Task.Delay(100);

        Assert.Contains(announcements, a => a.text == "Refreshing calendar." && a.cat == AnnouncementCategory.Status);
        Assert.Contains(announcements, a => a.text.Contains("Calendar updated") && a.cat == AnnouncementCategory.Result);
    }

    [Fact]
    public async Task UpdateResponseStatusAsync_UpdatesEventAndRefilters()
    {
        var evt = MakeEvent("e1", DateTime.Today.AddHours(10), CalendarResponseStatus.Pending);
        var vm = MakeVm(new List<CalendarEvent> { evt });
        await vm.LoadAsync();

        await vm.UpdateResponseStatusAsync("e1", evt.AccountId, CalendarResponseStatus.Declined);

        // Declined events are hidden by default.
        Assert.Empty(vm.VisibleEvents);
    }

    [Fact]
    public async Task VisibleEvents_SortedByStartTimeAscending()
    {
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("late", DateTime.Today.AddHours(14)),
            MakeEvent("early", DateTime.Today.AddHours(9)),
            MakeEvent("noon", DateTime.Today.AddHours(12)),
        });
        await vm.LoadAsync();

        Assert.Equal(3, vm.VisibleEvents.Count);
        Assert.Equal("early", vm.VisibleEvents[0].Uid);
        Assert.Equal("noon", vm.VisibleEvents[1].Uid);
        Assert.Equal("late", vm.VisibleEvents[2].Uid);
    }
}