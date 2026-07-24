using System;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// Reading-pane invite card (issue #329): the card must carry an aria-live status region so an RSVP
// can be confirmed reliably from inside the WebView2 — a host-window notification alone is dropped
// because focus is in the WebView2 document. The View injects sending/success/failure text into that
// region via ExecuteScriptAsync.
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

    [Fact]
    public async Task Rsvp_WhenInviteDataMissing_RaisesActionableCardStatus()
    {
        // The #297 cache-reconstruction race: the card is shown but CalendarInvite is null. Pressing
        // Accept must say something actionable through the card, not return silently (#329).
        var vm = MakeVm();
        vm.MessageDetail = new MailMessageDetail
        {
            AccountId = Guid.NewGuid(),
            MessageId = "m1",
            FolderName = "INBOX",
            // No CalendarInvite.
        };

        string? cardStatus = null;
        vm.OpenInviteCardStatus += s => cardStatus = s;

        await vm.AcceptInviteCommand.ExecuteAsync(null);

        Assert.NotNull(cardStatus);
        Assert.Contains("can't be answered", cardStatus);
    }
}
