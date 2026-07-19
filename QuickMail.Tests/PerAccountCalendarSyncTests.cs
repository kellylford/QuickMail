using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Per-account calendar sync opt-in (#282): the account-editor checkbox visibility rule
/// (<see cref="AccountEditorViewModel.ShowCalendarSyncOption"/>) — offered for Microsoft, Google,
/// and iCloud accounts, a superset of contact sync.
/// </summary>
public class PerAccountCalendarSyncTests
{
    private static AddAccountViewModel NewEditor() =>
        new(new StubFeatureGate(), new StubImapMailService(), new StubOAuthService());

    [Fact]
    public void ShowCalendarSyncOption_TrueForMicrosoft()
    {
        var vm = NewEditor();
        vm.AuthType = AuthType.OAuth2Microsoft;
        Assert.True(vm.ShowCalendarSyncOption);
    }

    [Fact]
    public void ShowCalendarSyncOption_TrueForGoogle()
    {
        var vm = NewEditor();
        vm.AuthType = AuthType.OAuth2Google;
        Assert.True(vm.ShowCalendarSyncOption);
    }

    [Fact]
    public void ShowCalendarSyncOption_TrueForICloud()
    {
        var vm = NewEditor();
        vm.AuthType = AuthType.Password;
        vm.ImapHost = "imap.mail.me.com";
        Assert.True(vm.ShowCalendarSyncOption);
    }

    [Fact]
    public void ShowCalendarSyncOption_TrueAfterTypingICloudAddress()
    {
        // The real user path: on the default IMAP backend, typing an iCloud address auto-fills the
        // Apple IMAP host, which must flip the calendar checkbox on.
        var vm = NewEditor();
        Assert.False(vm.ShowCalendarSyncOption);   // nothing typed yet
        vm.Username = "someone@icloud.com";
        Assert.Equal("imap.mail.me.com", vm.ImapHost);
        Assert.True(vm.ShowCalendarSyncOption);
    }

    [Fact]
    public void ShowCalendarSyncOption_FalseForPlainImap()
    {
        var vm = NewEditor();
        vm.AuthType = AuthType.Password;
        vm.ImapHost = "imap.example.com";
        Assert.False(vm.ShowCalendarSyncOption);
    }

    [Fact]
    public void ShowContactSyncOption_TrueForICloud()
    {
        // Contact sync (CardDAV) is offered for iCloud too, keyed on the iCloud IMAP host, so typing
        // an iCloud address (which auto-fills that host) must flip the contacts checkbox on.
        var vm = NewEditor();
        Assert.False(vm.ShowContactSyncOption);   // nothing typed yet
        vm.Username = "someone@icloud.com";
        Assert.Equal("imap.mail.me.com", vm.ImapHost);
        Assert.True(vm.ShowContactSyncOption);
    }

    [Fact]
    public void ShowContactSyncOption_FalseForPlainImap()
    {
        var vm = NewEditor();
        vm.AuthType = AuthType.Password;
        vm.ImapHost = "imap.example.com";
        Assert.False(vm.ShowContactSyncOption);
    }

    [Fact]
    public void ToAccountModel_KeepsCalendarFlagOnlyWhenOptionShown()
    {
        // Checked while iCloud → persists.
        var icloud = NewEditor();
        icloud.AuthType = AuthType.Password;
        icloud.ImapHost = "imap.mail.me.com";
        icloud.Username = "kelly@icloud.com";
        icloud.SyncCalendar = true;
        Assert.True(icloud.ToAccountModel().SyncCalendar);

        // Checked but a plain-IMAP account (option hidden) → guarded off, never persisted true.
        var plain = NewEditor();
        plain.AuthType = AuthType.Password;
        plain.ImapHost = "imap.example.com";
        plain.Username = "kelly@example.com";
        plain.SyncCalendar = true;
        Assert.False(plain.ToAccountModel().SyncCalendar);
    }
}
