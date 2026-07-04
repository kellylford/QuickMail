using System;
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
    private static MainViewModel MakeVm() =>
        new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());

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
}
