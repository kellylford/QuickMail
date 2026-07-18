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
    public async Task RequestGoToDate_RaisesRequest_SeededWithReferenceDate()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(9)) });
        await vm.LoadAsync();
        vm.ShowDayCommand.Execute(null);
        vm.NextPeriodCommand.Execute(null);          // reference = tomorrow

        DateTime? seed = null;
        vm.GoToDateRequested += d => seed = d;
        vm.RequestGoToDateCommand.Execute(null);

        Assert.Equal(DateTime.Today.AddDays(1), seed);
    }

    [Fact]
    public async Task RequestGoToDate_InAgenda_SeedsToday()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(9)) });
        await vm.LoadAsync();                          // Agenda ignores ReferenceDate

        DateTime? seed = null;
        vm.GoToDateRequested += d => seed = d;
        vm.RequestGoToDateCommand.Execute(null);

        Assert.Equal(DateTime.Today, seed);
    }

    [Fact]
    public void RequestGoToDate_OnlineMode_DoesNotRaise()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1") }, onlineMode: true);

        bool raised = false;
        vm.GoToDateRequested += _ => raised = true;
        vm.RequestGoToDateCommand.Execute(null);

        Assert.False(raised);
    }

    [Fact]
    public async Task GoToDate_InDayView_MovesReferenceAndFiltersToThatDay()
    {
        var target = DateTime.Today.AddDays(5);
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("today", DateTime.Today.AddHours(9)),
            MakeEvent("target", target.AddHours(9)),
        });
        await vm.LoadAsync();
        vm.ShowDayCommand.Execute(null);
        Assert.Equal("today", vm.VisibleEvents[0].Uid);

        vm.GoToDate(target);

        Assert.True(vm.IsDayView);
        Assert.Equal(target, vm.ReferenceDate.Date);
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("target", vm.VisibleEvents[0].Uid);
    }

    [Fact]
    public async Task GoToDate_FromAgenda_SwitchesToDayView()
    {
        var target = DateTime.Today.AddDays(3);
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("today", DateTime.Today.AddHours(9)),
            MakeEvent("target", target.AddHours(9)),
        });
        await vm.LoadAsync();                          // Agenda

        vm.GoToDate(target);

        Assert.True(vm.IsDayView);                     // Agenda ignores ReferenceDate → show the day
        Assert.Equal(target, vm.ReferenceDate.Date);
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("target", vm.VisibleEvents[0].Uid);
    }

    [Fact]
    public async Task GoToDate_InMonthView_KeepsMonthViewAndMovesMonth()
    {
        var target = DateTime.Today.AddMonths(2);
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1", DateTime.Today.AddHours(9)) });
        await vm.LoadAsync();
        vm.ShowMonthCommand.Execute(null);

        vm.GoToDate(target);

        Assert.True(vm.IsMonthView);                   // view mode preserved
        Assert.Equal(target.Date, vm.ReferenceDate.Date);
        Assert.Contains(target.ToString("MMMM yyyy"), vm.PeriodLabel);
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

    [Fact]
    public async Task AccountFilter_ShowsOnlyThatSource()
    {
        var acctA = Guid.NewGuid();
        var local = new CalendarEvent
        {
            Uid = "loc-1", AccountId = CalendarEvent.LocalAccountId, Summary = "Mine",
            StartTimeTicks = DateTime.Today.AddHours(9).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var fromA = MakeEvent("a-1", DateTime.Today.AddHours(10)); fromA.AccountId = acctA;
        var fromB = MakeEvent("b-1", DateTime.Today.AddHours(11)); // random other account

        var vm = MakeVm(new List<CalendarEvent> { local, fromA, fromB });
        await vm.LoadAsync();
        Assert.Equal(3, vm.VisibleEvents.Count);          // null filter = all

        vm.AccountFilter = acctA;                          // one account's calendar
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("a-1", vm.VisibleEvents[0].Uid);

        vm.AccountFilter = Guid.Empty;                     // local appointments only
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("loc-1", vm.VisibleEvents[0].Uid);

        vm.AccountFilter = null;                           // back to all
        Assert.Equal(3, vm.VisibleEvents.Count);
    }

    [Fact]
    public void CalendarFilterFor_MapsFolderNamesCorrectly()
    {
        var id = Guid.NewGuid();
        Assert.Null(MainViewModel.CalendarFilterFor(MainViewModel.CalendarFolder.FullName));
        Assert.Null(MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + "all"));
        Assert.Equal(Guid.Empty, MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + "local"));
        Assert.Equal(id, MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + id.ToString("D")));
        Assert.True(MainViewModel.IsCalendarFolderName(MainViewModel.CalendarSourcePrefix + "local"));
        Assert.True(MainViewModel.IsCalendarFolderName(MainViewModel.CalendarFolder.FullName));
        Assert.False(MainViewModel.IsCalendarFolderName("INBOX"));
    }

    [Fact]
    public async Task MonthView_Builds42Cells_WithCountsAndSelection()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("m1", today.AddHours(9)),
            MakeEvent("m2", today.AddHours(14)),
        });
        await vm.LoadAsync();

        vm.ShowMonthCommand.Execute(null);

        Assert.Equal(42, vm.MonthCells.Count);
        Assert.NotNull(vm.SelectedMonthCell);
        Assert.Equal(today, vm.SelectedMonthCell!.Date);
        Assert.Equal(2, vm.SelectedMonthCell.EventCount);
        Assert.Contains("2 events", vm.SelectedMonthCell.AccessibleName);
        Assert.Contains("today", vm.SelectedMonthCell.AccessibleName);
        Assert.Contains(today.ToString("MMMM yyyy"), vm.PeriodLabel);
        // Details pane shows the selected day's events.
        Assert.Contains("Event m1", vm.SelectedEventDetail);
    }

    [Fact]
    public async Task MonthView_DrillIntoDay_SwitchesToDayView()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("m3", today.AddHours(9)) });
        await vm.LoadAsync();
        vm.ShowMonthCommand.Execute(null);

        vm.DrillIntoDayCommand.Execute(null);

        Assert.True(vm.IsDayView);
        Assert.Equal(today, vm.ReferenceDate.Date);
        Assert.Single(vm.VisibleEvents);
    }

    [Fact]
    public async Task MonthView_NextPeriod_MovesOneMonth()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("m4", DateTime.Today.AddHours(9)) });
        await vm.LoadAsync();
        vm.ShowMonthCommand.Execute(null);
        var before = vm.ReferenceDate;

        vm.NextPeriodCommand.Execute(null);

        Assert.Equal(before.AddMonths(1).Month, vm.ReferenceDate.Month);
        Assert.Equal(42, vm.MonthCells.Count);
    }

    [Fact]
    public async Task SearchText_FiltersAcrossSummaryLocationAndNotes()
    {
        var events = new List<CalendarEvent>
        {
            MakeEvent("e1", DateTime.Today.AddHours(9)),
            MakeEvent("e2", DateTime.Today.AddHours(10)),
            MakeEvent("e3", DateTime.Today.AddHours(11)),
        };
        events[0].Summary = "Dentist visit";
        events[1].Location = "Dentist office downtown";
        events[2].Description = "Ask the dentist about crowns";
        events.Add(MakeEvent("e4", DateTime.Today.AddHours(12))); // no match

        var vm = MakeVm(events);
        await vm.LoadAsync();
        Assert.Equal(4, vm.VisibleEvents.Count);

        vm.SearchText = "dentist";
        Assert.Equal(3, vm.VisibleEvents.Count);

        vm.ClearSearch();
        Assert.Equal(4, vm.VisibleEvents.Count);
        Assert.False(vm.IsSearchActive);
        Assert.Equal(string.Empty, vm.SearchText);
    }

    [Fact]
    public async Task SearchText_CombinesWithDayView()
    {
        var today = DateTime.Today;
        var e1 = MakeEvent("t1", today.AddHours(9));  e1.Summary = "Budget review";
        var e2 = MakeEvent("t2", today.AddHours(10)); e2.Summary = "Lunch";
        var e3 = MakeEvent("m1", today.AddDays(1).AddHours(9)); e3.Summary = "Budget kickoff"; // tomorrow

        var vm = MakeVm(new List<CalendarEvent> { e1, e2, e3 });
        await vm.LoadAsync();
        vm.ShowDayCommand.Execute(null);   // today only
        vm.SearchText = "budget";

        Assert.Single(vm.VisibleEvents);   // tomorrow's budget event is outside the day window
        Assert.Equal("t1", vm.VisibleEvents[0].Uid);
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
    public async Task ExDate_SkipsThatOccurrenceOnly()
    {
        var start = DateTime.Today.AddHours(9);
        var master = MakeRecurring("x1", start, "FREQ=DAILY;COUNT=5");
        master.AddExDate(start.AddDays(2)); // exclude the third day
        var vm = MakeVm(new List<CalendarEvent> { master });
        await vm.LoadAsync();

        Assert.Equal(4, vm.VisibleEvents.Count);
        Assert.DoesNotContain(vm.VisibleEvents, e => e.StartTime == start.AddDays(2));
    }

    [Fact]
    public async Task DeleteOccurrence_ExDatesMasterAndKeepsSeries()
    {
        var start = DateTime.Today.AddHours(9);
        var svc = new StubCalendarService { StoredEvents = [MakeRecurring("d1", start, "FREQ=DAILY;COUNT=4")] };
        var vm = new CalendarViewModel(svc, onlineMode: false, showDeclinedEvents: false);
        await vm.LoadAsync();
        Assert.Equal(4, vm.VisibleEvents.Count);

        var second = vm.VisibleEvents[1];             // tomorrow's occurrence
        Action? deleteOne = null;
        vm.RecurringDeleteConfirmRequested += (_, one, _) => deleteOne = one;
        vm.DeleteEventCommand.Execute(second);
        Assert.NotNull(deleteOne);
        deleteOne!();

        // Master survives with an EXDATE; only 3 occurrences remain.
        Assert.Single(svc.StoredEvents);
        Assert.Contains(svc.StoredEvents[0].GetExDates(), d => d == start.AddDays(1));
        Assert.Equal(3, vm.VisibleEvents.Count);
    }

    [Fact]
    public async Task EditThisEventOnly_DetachesOccurrence()
    {
        var start = DateTime.Today.AddHours(9);
        var svc = new StubCalendarService { StoredEvents = [MakeRecurring("m1", start, "FREQ=DAILY;COUNT=3")] };
        var vm = new CalendarViewModel(svc, onlineMode: false, showDeclinedEvents: false);
        await vm.LoadAsync();

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.EditEventCommand.Execute(vm.VisibleEvents[1]); // tomorrow's occurrence

        Assert.NotNull(editor);
        Assert.True(editor!.IsRecurringEdit);
        Assert.True(editor.EditThisEventOnly);            // default scope
        editor.Title = "Moved just this one";
        editor.SaveCommand.Execute(null);

        // Master got an EXDATE; a standalone copy exists with a new uid.
        Assert.Equal(2, svc.StoredEvents.Count);
        var master = svc.StoredEvents.First(e => e.Uid == "m1");
        var detached = svc.StoredEvents.First(e => e.Uid != "m1");
        Assert.Single(master.GetExDates());
        Assert.Equal("Moved just this one", detached.Summary);
        Assert.False(detached.IsRecurring);

        // Still 3 visible: 2 remaining series occurrences + the detached copy.
        Assert.Equal(3, vm.VisibleEvents.Count);
    }

    [Fact]
    public async Task EditAllEvents_UpdatesSeriesMaster()
    {
        var start = DateTime.Today.AddHours(9);
        var svc = new StubCalendarService { StoredEvents = [MakeRecurring("m2", start, "FREQ=DAILY;COUNT=3")] };
        var vm = new CalendarViewModel(svc, onlineMode: false, showDeclinedEvents: false);
        await vm.LoadAsync();

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.EditEventCommand.Execute(vm.VisibleEvents[1]);

        editor!.EditThisEventOnly = false;   // All events
        editor.Title = "Whole series renamed";
        editor.SaveCommand.Execute(null);

        Assert.Single(svc.StoredEvents);     // still one master, no detached copy
        Assert.Equal("Whole series renamed", svc.StoredEvents[0].Summary);
        Assert.True(svc.StoredEvents[0].IsRecurring);
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

    // ── Save-target push (new appointments) ──────────────────────────────────────

    private static (CalendarViewModel Vm, StubCalendarService Store, StubGraphCalendarSyncService Sync, AccountModel Account)
        MakePushVm()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "work@example.com",
        };
        var store = new StubCalendarService();
        var sync = new StubGraphCalendarSyncService();
        var vm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false,
                                       showFieldLabels: false, graphSync: sync,
                                       graphAccountsProvider: () => new[] { account });
        return (vm, store, sync, account);
    }

    [Fact]
    public void IsCalendarPushAccount_CoversGraphAndGoogle()
    {
        Assert.True(MainViewModel.IsCalendarPushAccount(new AccountModel { BackendKind = BackendKind.MicrosoftGraph }));
        Assert.True(MainViewModel.IsCalendarPushAccount(new AccountModel { AuthType = AuthType.OAuth2Google }));
        Assert.False(MainViewModel.IsCalendarPushAccount(new AccountModel { AuthType = AuthType.Password }));
    }

    [Fact]
    public async Task GoogleAccount_EditRoutesThroughServerPush()
    {
        // A Google account in the push-accounts list: routing must treat it like Microsoft.
        var account = new AccountModel { Id = Guid.NewGuid(), AuthType = AuthType.OAuth2Google, Username = "k@gmail.com" };
        var store = new StubCalendarService();
        var sync = new StubGraphCalendarSyncService();
        var vm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false,
                                       showFieldLabels: false, graphSync: sync,
                                       graphAccountsProvider: () => new[] { account });
        var row = MakeGraphRow(account.Id, "goog-rw-1");
        store.StoredEvents.Add(row);
        await vm.LoadAsync();

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.EditEventCommand.Execute(row);

        Assert.NotNull(editor);                       // editable, not read-only
        editor!.Title = "Moved on Google";
        editor.SaveCommand.Execute(null);
        Assert.Single(sync.UpdatedEvents);
        Assert.Equal(account.Id, sync.UpdatedEvents[0].AccountId);
    }

    private static CalendarEvent MakeGraphRow(Guid accountId, string uid = "srv-1") => new()
    {
        Uid = uid, AccountId = accountId, IsGraph = true, Summary = "Server event",
        StartTimeTicks = DateTime.Today.AddHours(9).ToUniversalTime().Ticks,
        EndTimeTicks = DateTime.Today.AddHours(10).ToUniversalTime().Ticks,
        ResponseStatus = CalendarResponseStatus.Accepted,
    };

    [Fact]
    public async Task EditGraphEvent_PushesUpdateToServer()
    {
        var (vm, store, sync, account) = MakePushVm();
        var row = MakeGraphRow(account.Id);
        store.StoredEvents.Add(row);
        await vm.LoadAsync();

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.EditEventCommand.Execute(row);

        Assert.NotNull(editor);            // server-editable, editor opened
        editor!.Title = "Server event (moved)";
        editor.SaveCommand.Execute(null);

        Assert.Single(sync.UpdatedEvents);
        Assert.Equal(row.Uid, sync.UpdatedEvents[0].Uid);          // identity preserved
        Assert.Equal(account.Id, sync.UpdatedEvents[0].AccountId);
    }

    [Fact]
    public async Task EditGraphEvent_PushFailure_AnnouncesAndChangesNothing()
    {
        var (vm, store, sync, account) = MakePushVm();
        store.StoredEvents.Add(MakeGraphRow(account.Id));
        await vm.LoadAsync();
        sync.WriteFailure = new InvalidOperationException("boom");

        EventEditorViewModel? editor = null;
        string? announced = null;
        vm.EditorRequested += e => editor = e;
        vm.AnnouncementRequested += (t, _) => announced = t;

        vm.EditEventCommand.Execute(vm.VisibleEvents[0]);
        editor!.Title = "won't stick";
        editor.SaveCommand.Execute(null);

        Assert.Contains("Could not update", announced);
        Assert.Equal("Server event", store.StoredEvents[0].Summary); // untouched
    }

    [Fact]
    public async Task DeleteGraphEvent_ConfirmsThenDeletesOnServer()
    {
        var (vm, store, sync, account) = MakePushVm();
        var row = MakeGraphRow(account.Id);
        store.StoredEvents.Add(row);
        await vm.LoadAsync();

        Action? confirm = null;
        vm.DeleteConfirmRequested += (_, cb) => confirm = cb;
        vm.DeleteEventCommand.Execute(row);
        Assert.NotNull(confirm);
        confirm!();

        Assert.Single(sync.DeletedEvents);
        Assert.Equal(row.Uid, sync.DeletedEvents[0].Uid);
    }

    [Fact]
    public async Task GoogleRow_StaysReadOnly()
    {
        var (vm, store, _, _) = MakePushVm();
        // A server row whose account is NOT in the Graph-accounts list (i.e. a Google account).
        var googleRow = MakeGraphRow(Guid.NewGuid(), "goog-1");
        store.StoredEvents.Add(googleRow);
        await vm.LoadAsync();

        var editorOpened = false;
        string? announced = null;
        vm.EditorRequested += _ => editorOpened = true;
        vm.AnnouncementRequested += (t, _) => announced = t;

        vm.EditEventCommand.Execute(googleRow);
        Assert.False(editorOpened);
        Assert.Contains("can't be edited here", announced);

        vm.DeleteEventCommand.Execute(googleRow);
        Assert.Contains("can't be deleted here", announced);
    }

    [Fact]
    public async Task SaveNewEvent_AccountTarget_PushesToGraph()
    {
        var (vm, store, sync, account) = MakePushVm();
        var evt = new CalendarEvent
        {
            Uid = "local-tmp", AccountId = account.Id, Summary = "Pushed",
            StartTimeTicks = DateTime.UtcNow.AddHours(1).Ticks,
        };

        await vm.SaveNewEventAsync(evt);

        var created = Assert.Single(sync.CreatedEvents);
        Assert.Equal(account.Id, created.AccountId);
        Assert.True(created.IsGraph);
        // The push path persists via the sync service (server copy), not a local upsert.
        Assert.DoesNotContain(store.StoredEvents, e => e.Uid == "local-tmp");
    }

    [Fact]
    public async Task SaveNewEvent_PushFails_FallsBackToLocal_AndAnnounces()
    {
        var (vm, store, sync, account) = MakePushVm();
        sync.CreateFailure = new InvalidOperationException("network down");
        var announcements = new List<string>();
        vm.AnnouncementRequested += (text, _) => announcements.Add(text);
        var evt = new CalendarEvent
        {
            Uid = "local-fallback", AccountId = account.Id, Summary = "Keep me",
            StartTimeTicks = DateTime.UtcNow.AddHours(1).Ticks,
        };

        await vm.SaveNewEventAsync(evt);

        // Saved locally so the user's data is never lost.
        var saved = Assert.Single(store.StoredEvents);
        Assert.Equal("local-fallback", saved.Uid);
        Assert.Equal(CalendarEvent.LocalAccountId, saved.AccountId);
        Assert.True(saved.IsUserCreated);
        Assert.Contains(announcements, a =>
            a.Contains("Could not save to") && a.Contains("Saved to Local Calendar instead."));
    }

    [Fact]
    public async Task SaveNewEvent_LocalTarget_SavesLocallyWithoutPush()
    {
        var (vm, store, sync, _) = MakePushVm();
        var evt = new CalendarEvent
        {
            Uid = "local-only", AccountId = CalendarEvent.LocalAccountId, Summary = "Local",
            StartTimeTicks = DateTime.UtcNow.AddHours(1).Ticks,
        };

        await vm.SaveNewEventAsync(evt);

        Assert.Empty(sync.CreatedEvents);
        Assert.Equal("local-only", Assert.Single(store.StoredEvents).Uid);
    }
}