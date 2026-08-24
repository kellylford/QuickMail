using System;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the MainViewModel side of calendar-event interactions.
/// </summary>
public class MainViewModelCalendarTests
{
    private static MainViewModel MakeVm(ICalendarService? calendarService = null,
                                       IGraphCalendarSyncService? graphCalendarSync = null) =>
        new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            calendarService: calendarService, graphCalendarSyncService: graphCalendarSync);

    [Fact]
    public void OpenCalendarSourceMessage_ConstructsStubAndRoutesThroughSelectMessage()
    {
        var vm = MakeVm();
        var accountId = Guid.NewGuid();
        // SelectMessageAsync resolves SelectedAccount from Accounts before it will set
        // SelectedMessage, so the stub account must be present for the route to complete.
        vm.Accounts.Add(new AccountModel { Id = accountId });

        vm.OpenCalendarSourceMessage(accountId, "INBOX", "msg-123");

        Assert.NotNull(vm.SelectedMessage);
        Assert.Equal(accountId, vm.SelectedMessage!.AccountId);
        Assert.Equal("INBOX", vm.SelectedMessage.FolderName);
        Assert.Equal("msg-123", vm.SelectedMessage.MessageId);
        Assert.Equal("Calendar invitation", vm.SelectedMessage.Subject);
    }

    [Fact]
    public async Task RefreshAsync_WhileCalendarViewActive_DelegatesToCalendarRefresh()
    {
        var calendarService = new StubCalendarService();
        var vm = MakeVm(calendarService);
        vm.SelectedFolder = MainViewModel.CalendarFolder;

        await vm.RefreshCommand.ExecuteAsync(null);

        // Every Refresh entry point (menu, toolbar, palette, F5) binds to this same
        // command, so this confirms all of them agree while the calendar is active —
        // not just the keyboard path.
        Assert.Equal(1, calendarService.RefreshCallCount);
    }

    [Fact]
    public void CheckReminders_FiresOncePerOccurrence_WithinLeadWindow()
    {
        var soon = DateTime.Now.AddMinutes(5);
        var calendarService = new StubCalendarService
        {
            StoredEvents =
            [
                new CalendarEvent
                {
                    Uid = "rem-1", AccountId = CalendarEvent.LocalAccountId,
                    Summary = "Standup", Location = "Zoom",
                    StartTimeTicks = soon.ToUniversalTime().Ticks,
                    ResponseStatus = CalendarResponseStatus.Accepted,
                },
                new CalendarEvent   // outside the 10-minute window: must not fire
                {
                    Uid = "rem-2", AccountId = CalendarEvent.LocalAccountId,
                    Summary = "Later",
                    StartTimeTicks = DateTime.Now.AddHours(3).ToUniversalTime().Ticks,
                    ResponseStatus = CalendarResponseStatus.Accepted,
                },
            ],
        };
        var vm = MakeVm(calendarService);
        vm.RemindersEnabled = true;
        vm.ReminderLeadMinutes = 10;

        var announced = new System.Collections.Generic.List<string>();
        vm.AnnouncementRequested += (_, e) => announced.Add(e.Text);

        vm.CheckReminders();
        vm.CheckReminders();   // second pass must not re-fire

        Assert.Single(announced);
        Assert.Contains("Standup", announced[0]);
        Assert.DoesNotContain(announced, a => a.Contains("Later"));
    }

    [Fact]
    public void CheckReminders_Disabled_FiresNothing()
    {
        var calendarService = new StubCalendarService
        {
            StoredEvents =
            [
                new CalendarEvent
                {
                    Uid = "rem-3", AccountId = CalendarEvent.LocalAccountId,
                    Summary = "Soon",
                    StartTimeTicks = DateTime.Now.AddMinutes(5).ToUniversalTime().Ticks,
                    ResponseStatus = CalendarResponseStatus.Accepted,
                },
            ],
        };
        var vm = MakeVm(calendarService);
        vm.RemindersEnabled = false;   // default

        var announced = 0;
        vm.AnnouncementRequested += (_, _) => announced++;
        vm.CheckReminders();

        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task RefreshAsync_OutsideCalendarView_DoesNotTouchCalendarService()
    {
        var calendarService = new StubCalendarService();
        var vm = MakeVm(calendarService);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, calendarService.RefreshCallCount);
    }

    [Fact]
    public async Task RespondToCalendarInviteAsync_SendsFromReceivingAccount_AndUpdatesStatus()
    {
        var receivingAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();

        var smtp = new StubSmtpService();
        var localStore = new StubLocalStoreService
        {
            // The cached source invite email the calendar row points back to.
            SeededDetail = new MailMessageDetail
            {
                AccountId = receivingAccountId,
                FolderName = "INBOX",
                MessageId = "msg-1",
                CalendarInvite = new IcsModel
                {
                    Uid = "inv-1",
                    Summary = "Planning meeting",
                    Organizer = "organizer@example.com",
                    StartTime = DateTime.Today.AddHours(9),
                    EndTime = DateTime.Today.AddHours(10),
                },
            },
        };
        var calendarService = new StubCalendarService();

        var evt = new CalendarEvent
        {
            Uid = "inv-1",
            AccountId = receivingAccountId,
            Summary = "Planning meeting",
            SourceMessageId = "msg-1",
            SourceFolder = "INBOX",
            ResponseStatus = CalendarResponseStatus.Pending,
        };
        calendarService.StoredEvents.Add(evt);

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            localStore, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), smtp,
            calendarService: calendarService);

        // Both accounts present; the reply MUST route from the one that received the invite (#296).
        vm.Accounts.Add(new AccountModel
        {
            Id = otherAccountId, Username = "wrong@example.com", DisplayName = "Wrong Account",
        });
        vm.Accounts.Add(new AccountModel
        {
            Id = receivingAccountId, Username = "me@example.com", DisplayName = "Me",
        });

        await vm.RespondToCalendarInviteAsync(evt, "ACCEPTED", "accepted");

        // Exactly one reply, sent from the receiving account (not the default/first account).
        var reply = Assert.Single(smtp.SentReplies);
        Assert.Equal(receivingAccountId, reply.Account.Id);
        Assert.Equal("organizer@example.com", reply.OrganizerEmail);
        Assert.Contains("PARTSTAT=ACCEPTED", reply.Ics);

        // The calendar row now reflects the response.
        Assert.Equal(CalendarResponseStatus.Accepted,
            calendarService.StoredEvents.Find(e => e.Uid == "inv-1")!.ResponseStatus);
    }

    [Fact]
    public async Task RespondToCalendarInviteAsync_NoSourceMessage_AnnouncesAndSendsNothing()
    {
        var smtp = new StubSmtpService();
        var calendarService = new StubCalendarService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), smtp,
            calendarService: calendarService);

        var accountId = Guid.NewGuid();
        vm.Accounts.Add(new AccountModel { Id = accountId, Username = "me@example.com" });

        var announced = new System.Collections.Generic.List<string>();
        vm.AnnouncementRequested += (_, e) => announced.Add(e.Text);

        var evt = new CalendarEvent
        {
            Uid = "inv-2", AccountId = accountId,
            SourceMessageId = string.Empty,   // source email no longer available
            ResponseStatus = CalendarResponseStatus.Pending,
        };

        await vm.RespondToCalendarInviteAsync(evt, "DECLINED", "declined");

        Assert.Empty(smtp.SentReplies);
        Assert.Contains(announced, a => a.Contains("no longer available"));
    }

    // ── Opening the calendar pulls from the server (issue #519) ──────────────────

    /// <summary>
    /// Opening the calendar asks the server for its events, so an appointment added elsewhere — in
    /// Gmail, in Outlook, on a phone — is there when you look.
    ///
    /// Without this the open read the local cache and nothing else, and the only ways to pull were
    /// the 15-minute timer and F5. That is #519 verbatim: the appointment "will not be there. Press
    /// F5 to refresh the list. Now it will show up." The pull is deliberately not awaited, so the
    /// assertion has to let the fire-and-forget continuation run.
    /// </summary>
    [Fact]
    public async Task SelectingTheCalendar_PullsFromTheServer()
    {
        var sync = new StubGraphCalendarSyncService();
        var vm = MakeVm(new StubCalendarService(), sync);

        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.CalendarFolder);
        await Task.Yield();

        Assert.Equal(1, sync.SyncCallCount);
    }

    /// <summary>
    /// Going out to the mail list and straight back does not start a second pull. Opening the
    /// calendar is something a user repeats freely, and each open would otherwise be a fresh round
    /// of Graph and Google requests.
    /// </summary>
    [Fact]
    public async Task ReopeningTheCalendarImmediately_DoesNotPullAgain()
    {
        var sync = new StubGraphCalendarSyncService();
        var vm = MakeVm(new StubCalendarService(), sync);

        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.CalendarFolder);
        await Task.Yield();
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.CalendarFolder);
        await Task.Yield();

        Assert.Equal(1, sync.SyncCallCount);
    }

    /// <summary>
    /// The pull landing must not throw the user out of their place in the list. A reload builds
    /// every row afresh from the store, so the selected row stops existing as an object and the
    /// list would drop its selection — and keyboard focus — back to the top, mid-read.
    /// </summary>
    [Fact]
    public async Task AnExternalUpdate_KeepsTheSelectedAppointment()
    {
        var start = DateTime.UtcNow.AddDays(2).Ticks;
        CalendarEvent Row() => new()
        {
            Uid = "evt-1", AccountId = CalendarEvent.LocalAccountId, Summary = "Standup",
            StartTimeTicks = start, ResponseStatus = CalendarResponseStatus.Accepted,
        };
        var store = new StubCalendarService { StoredEvents = [Row()] };
        var calendarVm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false,
                                               showFieldLabels: false);
        await calendarVm.LoadAsync();
        var before = calendarVm.SelectedEvent;
        Assert.NotNull(before);

        // What a server pull does: the same appointment, rebuilt as a different object.
        store.StoredEvents = [Row()];
        calendarVm.ApplyFiltersFromExternalUpdate();

        Assert.NotNull(calendarVm.SelectedEvent);
        Assert.Equal("evt-1", calendarVm.SelectedEvent!.Uid);
        // The new object, not the stale one it was holding.
        Assert.NotSame(before, calendarVm.SelectedEvent);
        Assert.Contains(calendarVm.SelectedEvent, calendarVm.VisibleEvents);
    }
}
