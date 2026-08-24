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
    public async Task FieldLabels_Off_StampsDataOnlyAccessibleName_NoFieldLabelWords()
    {
        var evt = MakeEvent("e1", DateTime.Today.AddHours(10));
        evt.Location = "Erin's Snug Irish Pub";
        var vm = MakeVm(new List<CalendarEvent> { evt }, showFieldLabels: false);
        await vm.LoadAsync();

        var name = vm.VisibleEvents[0].AccessibleName;
        // Concise mode carries NO field-label words — not "Subject", "Location:", or "calendar".
        Assert.StartsWith(vm.VisibleEvents[0].DisplayLine, name);
        Assert.DoesNotContain("Subject ", name);
        Assert.DoesNotContain("Location:", name);
        Assert.DoesNotContain("calendar ", name);
        Assert.Contains("Erin's Snug Irish Pub", name);   // location value present, just no label
        Assert.EndsWith(", Account", name);               // calendar source appended as a bare value
    }

    [Fact]
    public async Task CalendarSourceLabel_LocalAndTaggedCalendar()
    {
        var acctId = Guid.NewGuid();
        var local = new CalendarEvent
        {
            Uid = "loc", AccountId = CalendarEvent.LocalAccountId, Summary = "Mine",
            StartTimeTicks = DateTime.Today.AddHours(9).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var tagged = new CalendarEvent
        {
            Uid = "fam", AccountId = acctId, IsGraph = true, Summary = "Reunion",
            CalendarName = "Family",
            StartTimeTicks = DateTime.Today.AddHours(10).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var svc = new StubCalendarService { StoredEvents = [local, tagged] };
        var vm = new CalendarViewModel(svc, onlineMode: false, showDeclinedEvents: false,
            allAccountsProvider: () => new[] { new AccountModel { Id = acctId, AccountName = "Apple" } });
        await vm.LoadAsync();

        Assert.Equal("Local", vm.VisibleEvents.First(e => e.Uid == "loc").CalendarSourceLabel);
        Assert.Equal("Apple: Family", vm.VisibleEvents.First(e => e.Uid == "fam").CalendarSourceLabel);
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
    public void RequestGoToDate_OnlineMode_DoesNotRaise_ButAnnounces()
    {
        var vm = MakeVm(new List<CalendarEvent> { MakeEvent("e1") }, onlineMode: true);

        bool raised = false;
        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.GoToDateRequested += _ => raised = true;
        vm.AnnouncementRequested += (t, c) => { announced = t; cat = c; };

        vm.RequestGoToDateCommand.Execute(null);

        Assert.False(raised);
        Assert.Contains("unavailable in online mode", announced);
        Assert.Equal(AnnouncementCategory.Result, cat);
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

    // ---- Opening selection (issue #567): the agenda opens on today, not on the oldest event ----

    [Fact]
    public async Task LoadAsync_InAgenda_SelectsFirstEventOnOrAfterToday()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("past-1",  today.AddDays(-25).AddHours(9)),
            MakeEvent("past-2",  today.AddDays(-3).AddHours(9)),
            MakeEvent("today-1", today.AddHours(14)),
            MakeEvent("future",  today.AddDays(6).AddHours(9)),
        });

        await vm.LoadAsync();

        Assert.Equal(4, vm.VisibleEvents.Count);            // past events stay in the list
        Assert.Equal("today-1", vm.SelectedEvent?.Uid);     // but selection opens on today
    }

    [Fact]
    public async Task LoadAsync_InAgenda_WithNoEventsToday_SelectsNextUpcoming()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("past",   today.AddDays(-10).AddHours(9)),
            MakeEvent("soon",   today.AddDays(2).AddHours(9)),
            MakeEvent("later",  today.AddDays(20).AddHours(9)),
        });

        await vm.LoadAsync();

        Assert.Equal("soon", vm.SelectedEvent?.Uid);
    }

    [Fact]
    public async Task LoadAsync_InAgenda_WithOnlyPastEvents_SelectsMostRecent()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("oldest", today.AddDays(-30).AddHours(9)),
            MakeEvent("newest", today.AddDays(-1).AddHours(9)),
        });

        await vm.LoadAsync();

        Assert.Equal("newest", vm.SelectedEvent?.Uid);
    }

    [Fact]
    public async Task LoadAsync_WithNoEvents_SelectsNothing()
    {
        var vm = MakeVm(new List<CalendarEvent>());

        await vm.LoadAsync();

        Assert.Null(vm.SelectedEvent);
    }

    [Fact]
    public async Task ShowAgenda_AfterDayView_SelectsFirstEventOnOrAfterToday()
    {
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("past",    today.AddDays(-14).AddHours(9)),
            MakeEvent("today-1", today.AddHours(11)),
        });
        await vm.LoadAsync();
        vm.ShowDayCommand.Execute(null);

        vm.ShowAgendaCommand.Execute(null);

        Assert.True(vm.IsAgendaView);
        Assert.Equal(2, vm.VisibleEvents.Count);
        Assert.Equal("today-1", vm.SelectedEvent?.Uid);
    }

    [Fact]
    public async Task ShowWeek_SelectsFirstRowOfTheWindow()
    {
        // Day/Week/Month are already windowed on ReferenceDate, so they keep opening on row 0 —
        // including a row earlier in the same week than today.
        var today = DateTime.Today;
        var vm = MakeVm(new List<CalendarEvent>
        {
            MakeEvent("past", today.AddDays(-40).AddHours(9)),
            MakeEvent("now",  today.AddHours(9)),
        });
        await vm.LoadAsync();

        vm.ShowWeekCommand.Execute(null);

        Assert.Same(vm.VisibleEvents[0], vm.SelectedEvent);
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
    public async Task SourceFilter_ShowsOnlyThatSource()
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
        Assert.Equal(3, vm.VisibleEvents.Count);                              // null filter = all

        vm.SourceFilter = new MainViewModel.CalendarFilter(acctA, null);      // one account's calendars
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("a-1", vm.VisibleEvents[0].Uid);

        vm.SourceFilter = new MainViewModel.CalendarFilter(Guid.Empty, null); // local appointments only
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("loc-1", vm.VisibleEvents[0].Uid);

        vm.SourceFilter = null;                                              // back to all
        Assert.Equal(3, vm.VisibleEvents.Count);
    }

    [Fact]
    public async Task SourceFilter_SingleCalendar_ShowsOnlyThatCalendarsEvents()
    {
        var acct = Guid.NewGuid();
        var home = MakeEvent("home-1", DateTime.Today.AddHours(9)); home.AccountId = acct; home.CalendarId = "cal-home";
        var work = MakeEvent("work-1", DateTime.Today.AddHours(10)); work.AccountId = acct; work.CalendarId = "cal-work";

        var vm = MakeVm(new List<CalendarEvent> { home, work });
        await vm.LoadAsync();

        vm.SourceFilter = new MainViewModel.CalendarFilter(acct, "cal-home"); // one specific calendar
        Assert.Single(vm.VisibleEvents);
        Assert.Equal("home-1", vm.VisibleEvents[0].Uid);

        vm.SourceFilter = new MainViewModel.CalendarFilter(acct, null);       // all of the account's calendars
        Assert.Equal(2, vm.VisibleEvents.Count);
    }

    [Fact]
    public void CalendarFilterFor_MapsFolderNamesCorrectly()
    {
        var id = Guid.NewGuid();
        Assert.Null(MainViewModel.CalendarFilterFor(MainViewModel.CalendarFolder.FullName));
        Assert.Equal(new MainViewModel.CalendarFilter(null, null),
            MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + "all"));
        Assert.Equal(new MainViewModel.CalendarFilter(Guid.Empty, null),
            MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + "local"));
        Assert.Equal(new MainViewModel.CalendarFilter(id, null),
            MainViewModel.CalendarFilterFor(MainViewModel.CalendarSourcePrefix + id.ToString("D")));
        // A single-calendar tail: "{guid}|{escapedCalId}" round-trips the (possibly slash-bearing) id.
        var calId = "https://p42-caldav.icloud.com/123/calendars/home/";
        Assert.Equal(new MainViewModel.CalendarFilter(id, calId),
            MainViewModel.CalendarFilterFor(
                MainViewModel.CalendarSourcePrefix + id.ToString("D") + "|" + Uri.EscapeDataString(calId)));
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
        var sync = new StubGraphCalendarSyncService { CalendarStore = store };
        var vm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false,
                                       showFieldLabels: false, graphSync: sync,
                                       graphAccountsProvider: () => new[] { account });
        return (vm, store, sync, account);
    }

    [Fact]
    public void IsCalendarPushAccount_CoversGraphGoogleAndICloud_WhenOptedIn()
    {
        // Push targets require BOTH a supported provider AND the per-account calendar opt-in (#282).
        Assert.True(MainViewModel.IsCalendarPushAccount(new AccountModel { BackendKind = BackendKind.MicrosoftGraph, SyncCalendar = true }));
        Assert.True(MainViewModel.IsCalendarPushAccount(new AccountModel { AuthType = AuthType.OAuth2Google, SyncCalendar = true }));
        // iCloud is a CalDAV push target, detected by IMAP host.
        Assert.True(MainViewModel.IsCalendarPushAccount(new AccountModel { ImapHost = "imap.mail.me.com", SyncCalendar = true }));
        Assert.False(MainViewModel.IsCalendarPushAccount(new AccountModel { AuthType = AuthType.Password, SyncCalendar = true }));
        // Not opted in → not a push target even for a supported provider.
        Assert.False(MainViewModel.IsCalendarPushAccount(new AccountModel { BackendKind = BackendKind.MicrosoftGraph, SyncCalendar = false }));
        Assert.False(MainViewModel.IsCalendarPushAccount(new AccountModel { AuthType = AuthType.OAuth2Google, SyncCalendar = false }));
        Assert.False(MainViewModel.IsCalendarPushAccount(new AccountModel { ImapHost = "imap.mail.me.com", SyncCalendar = false }));
    }

    [Fact]
    public void NewEvent_ICloudAccount_OffersOneTargetPerCalendar_PlusLocal()
    {
        var apple = new AccountModel
        {
            Id = Guid.NewGuid(), ImapHost = "imap.mail.me.com", AccountName = "Apple", SyncCalendar = true,
        };
        var store = new StubCalendarService();
        var vm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false,
            showFieldLabels: false, graphSync: new StubGraphCalendarSyncService(),
            graphAccountsProvider: () => new[] { apple },
            calendarSourcesProvider: () => new[]
            {
                new CalendarSourceInfo(apple.Id, "https://p42-caldav.icloud.com/1/calendars/home/", "Home", CanWrite: true),
                new CalendarSourceInfo(apple.Id, "https://p42-caldav.icloud.com/1/calendars/family/", "Family", CanWrite: true),
            });

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.NewEventCommand.Execute(null);

        Assert.NotNull(editor);
        Assert.True(editor!.ShowSaveTarget);
        // Local first, then one target per discovered iCloud calendar.
        Assert.Equal(3, editor.SaveTargetLabels.Count);
        Assert.Contains("Apple: Home", editor.SaveTargetLabels);
        Assert.Contains("Apple: Family", editor.SaveTargetLabels);

        // Selecting the second iCloud calendar stamps the built event with that collection URL.
        editor.SelectedTargetIndex = 2; // Apple: Family
        editor.Title = "Reunion";
        Assert.True(editor.TryBuildEvent(out var evt, out _));
        Assert.Equal(apple.Id, evt.AccountId);
        Assert.Equal("https://p42-caldav.icloud.com/1/calendars/family/", evt.CalendarId);
        Assert.Equal("Family", evt.CalendarName);
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

    /// <summary>
    /// A new appointment is in the list the moment it is saved, without the user pressing F5
    /// (issue #519). Both save targets are covered because they persist by different routes: the
    /// local target upserts through the calendar service, while an account target hands the event
    /// to the sync service, which stores the server's copy — so only a reload-and-refilter after
    /// the push puts it on screen.
    /// </summary>
    [Fact]
    public async Task SaveNewEvent_AccountTarget_ShowsInTheListWithoutARefresh()
    {
        var (vm, _, _, account) = MakePushVm();
        await vm.LoadAsync();
        Assert.Empty(vm.VisibleEvents);

        await vm.SaveNewEventAsync(new CalendarEvent
        {
            Uid = "local-tmp", AccountId = account.Id, Summary = "Pushed",
            StartTimeTicks = DateTime.UtcNow.AddDays(3).Ticks,
        });

        var row = Assert.Single(vm.VisibleEvents);
        Assert.Equal("Pushed", row.Summary);
        // And selection lands on it, so the focus handoff to the list opens on the new appointment.
        Assert.Equal(row.Uid, vm.SelectedEvent?.Uid);
    }

    [Fact]
    public async Task SaveNewEvent_LocalTarget_ShowsInTheListWithoutARefresh()
    {
        var (vm, _, _, _) = MakePushVm();
        await vm.LoadAsync();

        await vm.SaveNewEventAsync(new CalendarEvent
        {
            Uid = "local-only", AccountId = CalendarEvent.LocalAccountId, Summary = "Local",
            StartTimeTicks = DateTime.UtcNow.AddDays(3).Ticks,
        });

        var row = Assert.Single(vm.VisibleEvents);
        Assert.Equal("Local", row.Summary);
        Assert.Equal(row.Uid, vm.SelectedEvent?.Uid);
    }

    /// <summary>
    /// The symptom #569 was reported against, end to end: with ONE of an account's calendars
    /// selected in the folder tree, a new appointment saved to that account is in the list at once,
    /// without an F5.
    ///
    /// <para>
    /// This is the configuration the bug needed. The other save tests run with no
    /// <c>SourceFilter</c>, and with no calendar-id filter <c>ApplyFilters</c> short-circuits, so
    /// they pass whatever the row is tagged with. Here the filter is live, so an untagged row — what
    /// the service stored before #569 — fails it and the appointment is missing from the very
    /// calendar it was just filed on, until the next sync restamps it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveNewEvent_WhileOneCalendarIsSelected_ShowsUnderThatCalendar()
    {
        var (vm, _, sync, account) = MakePushVm();
        sync.DefaultCalendarId = "cal-default";
        sync.DefaultCalendarName = "Calendar";
        vm.SourceFilter = new MainViewModel.CalendarFilter(account.Id, "cal-default");
        await vm.LoadAsync();

        await vm.SaveNewEventAsync(new CalendarEvent
        {
            Uid = "local-tmp", AccountId = account.Id, Summary = "Dentist",
            StartTimeTicks = DateTime.UtcNow.AddDays(3).Ticks,
        });

        var row = Assert.Single(vm.VisibleEvents);
        Assert.Equal("Dentist", row.Summary);
        Assert.Equal("cal-default", row.CalendarId);
        Assert.Equal(row.Uid, vm.SelectedEvent?.Uid);
    }

    // ── The Calendar picker offers every writable calendar (#569 follow-up) ───────

    private static (CalendarViewModel Vm, AccountModel Account) PickerVm(params CalendarSourceInfo[] sources)
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph,
            AccountName = "Work", Username = "kelly@work.example", SyncCalendar = true,
        };
        var stamped = sources.Select(s => s with { AccountId = account.Id }).ToList();
        var vm = new CalendarViewModel(new StubCalendarService(), onlineMode: false,
            showDeclinedEvents: false, showFieldLabels: false,
            graphSync: new StubGraphCalendarSyncService(),
            graphAccountsProvider: () => new[] { account },
            calendarSourcesProvider: () => stamped);
        return (vm, account);
    }

    private static EventEditorViewModel OpenEditor(CalendarViewModel vm)
    {
        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.NewEventCommand.Execute(null);
        Assert.NotNull(editor);
        return editor!;
    }

    /// <summary>
    /// A Microsoft or Google account offers one target per calendar, the way iCloud already did.
    /// Until now it offered a single account-level entry, so an appointment could only ever be
    /// filed onto the account's default calendar.
    /// </summary>
    [Fact]
    public void NewEvent_ServerAccount_OffersOneTargetPerWritableCalendar()
    {
        var (vm, account) = PickerVm(
            new CalendarSourceInfo(Guid.Empty, "cal-default", "Calendar", CanWrite: true),
            new CalendarSourceInfo(Guid.Empty, "cal-team", "Team", CanWrite: true));

        var editor = OpenEditor(vm);

        Assert.Equal(3, editor.SaveTargetLabels.Count);   // Local, then the two calendars
        Assert.Contains("Work: Calendar", editor.SaveTargetLabels);
        Assert.Contains("Work: Team", editor.SaveTargetLabels);

        // And picking one stamps the appointment with it, so it saves where the user said.
        editor.SelectedTargetIndex = editor.SaveTargetLabels.ToList().IndexOf("Work: Team");
        editor.Title = "Standup";
        Assert.True(editor.TryBuildEvent(out var evt, out var error), error);
        Assert.Equal(account.Id, evt.AccountId);
        Assert.Equal("cal-team", evt.CalendarId);
        Assert.Equal("Team", evt.CalendarName);
    }

    /// <summary>
    /// A calendar the user only subscribes to — a holidays feed, a colleague's shared calendar — is
    /// not a place an appointment can be saved, so it is not offered. It keeps its folder-tree node:
    /// reading it is what it is for.
    /// </summary>
    [Fact]
    public void NewEvent_LeavesOutCalendarsThatCannotBeWrittenTo()
    {
        var (vm, _) = PickerVm(
            new CalendarSourceInfo(Guid.Empty, "cal-default", "Calendar", CanWrite: true),
            new CalendarSourceInfo(Guid.Empty, "cal-holidays", "Holidays in the United States", CanWrite: false));

        var editor = OpenEditor(vm);

        Assert.Equal(2, editor.SaveTargetLabels.Count);   // Local, then the one writable calendar
        Assert.Contains("Work: Calendar", editor.SaveTargetLabels);
        Assert.DoesNotContain(editor.SaveTargetLabels, l => l.Contains("Holidays"));
    }

    /// <summary>
    /// An account whose calendars are not known yet — it has never synced — still gets a bare
    /// account entry, because Microsoft and Google will file into the account's default calendar
    /// when no calendar is named. Being unofferable would leave the user unable to save to the
    /// account at all.
    /// </summary>
    [Fact]
    public void NewEvent_ServerAccountWithNoKnownCalendars_StillOffersTheAccount()
    {
        var (vm, _) = PickerVm();

        var editor = OpenEditor(vm);

        Assert.Equal(2, editor.SaveTargetLabels.Count);
        Assert.Contains("Work", editor.SaveTargetLabels);
    }

    /// <summary>
    /// iCloud is the exception and stays the exception: a CalDAV PUT needs a collection URL, so an
    /// account with nothing discovered offers nothing rather than a target that cannot resolve.
    /// </summary>
    [Fact]
    public void NewEvent_ICloudWithNoKnownCalendars_OffersOnlyLocal()
    {
        var apple = new AccountModel
        {
            Id = Guid.NewGuid(), ImapHost = "imap.mail.me.com", AccountName = "Apple", SyncCalendar = true,
        };
        var vm = new CalendarViewModel(new StubCalendarService(), onlineMode: false,
            showDeclinedEvents: false, showFieldLabels: false,
            graphSync: new StubGraphCalendarSyncService(),
            graphAccountsProvider: () => new[] { apple },
            calendarSourcesProvider: () => []);

        var editor = OpenEditor(vm);

        Assert.Equal("Local Calendar (this computer)", Assert.Single(editor.SaveTargetLabels));
    }
}
