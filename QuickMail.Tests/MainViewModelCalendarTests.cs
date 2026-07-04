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
    public async Task RefreshAsync_OutsideCalendarView_DoesNotTouchCalendarService()
    {
        var calendarService = new StubCalendarService();
        var vm = MakeVm(calendarService);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, calendarService.RefreshCallCount);
    }
}
