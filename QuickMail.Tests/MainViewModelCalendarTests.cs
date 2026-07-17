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
    private static MainViewModel MakeVm(ICalendarService? calendarService = null) =>
        new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            calendarService: calendarService);

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
}
