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

    private static CalendarViewModel MakeVm(List<CalendarEvent> events, bool onlineMode = false, bool showDeclined = false)
    {
        var svc = new StubCalendarService { StoredEvents = events };
        return new CalendarViewModel(svc, onlineMode, showDeclined);
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
    public async Task LoadAsync_WithNoEvents_AnnouncesStatus()
    {
        var vm = MakeVm(new List<CalendarEvent>());

        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.AnnouncementRequested += (text, category) => { announced = text; cat = category; };

        await vm.LoadAsync();

        Assert.Empty(vm.VisibleEvents);
        Assert.Equal(AnnouncementCategory.Status, cat);
        Assert.Contains("No events", announced);
        Assert.DoesNotContain("Escape", announced);
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