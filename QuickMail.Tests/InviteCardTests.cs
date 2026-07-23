using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// Reading-pane invite card (issue #329): the card must carry an aria-live status region so a response
// can be confirmed reliably from inside the WebView2 (a host-window notification alone was getting
// dropped because focus is in the WebView2 document).
public class InviteCardTests
{
    private static MainViewModel MakeVm() => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService());

    private static MailMessageDetail DetailWithInvite() => new()
    {
        AccountId = Guid.NewGuid(),
        MessageId = "m1",
        FolderName = "INBOX",
        Subject = "Lunch",
        CalendarInvite = new IcsModel
        {
            Uid = "lunch-1",
            Summary = "Lunch",
            Organizer = "org@example.com",
            StartTime = new DateTime(2026, 7, 22, 17, 0, 0, DateTimeKind.Utc),
            Method = "REQUEST",
        },
    };

    [Fact]
    public void EventCard_WithInvite_IncludesAriaLiveStatusRegion()
    {
        var vm = MakeVm();
        vm.MessageDetail = DetailWithInvite();

        var html = vm.BuildEventCardHtml();

        Assert.Contains("id=\"qm-invite-status\"", html);
        Assert.Contains("aria-live=\"assertive\"", html);
        // Sanity: the response buttons are still present for a REQUEST.
        Assert.Contains("quickmail:ics-accept", html);
    }

    [Fact]
    public void EventCard_NoInvite_IsEmpty()
    {
        var vm = MakeVm();
        Assert.Equal(string.Empty, vm.BuildEventCardHtml());
    }
}
