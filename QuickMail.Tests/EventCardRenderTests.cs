using System;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

// The invite card used to be built by MainViewModel and injected only by MainWindow, so the
// standalone MessageWindow (MessageOpenMode = window) rendered the body alone. For a Zoom-style
// Exchange invitation that means the meeting's date and time were nowhere on screen: the "when"
// lives only in the ICS part — the body is join links, dial-in numbers, and nothing else.
// These tests pin both halves: the card states the time, and every body-render path carries it.
public class EventCardRenderTests
{
    private const string ZoomInviteIcs = """
        BEGIN:VCALENDAR
        METHOD:REQUEST
        PRODID:Microsoft Exchange Server 2010
        VERSION:2.0
        BEGIN:VEVENT
        ORGANIZER;CN="Gold, Amy":mailto:amy@example.com
        DESCRIPTION;LANGUAGE=en-US:Join Zoom Meeting<https://example.zoom.us/j/123>
        UID:040000008200E00074C5B7101A82E008
        SUMMARY;LANGUAGE=en-US:Web Accessibility Discussion
        DTSTART;TZID=Eastern Standard Time:20260805T160000
        DTEND;TZID=Eastern Standard Time:20260805T170000
        LOCATION;LANGUAGE=en-US:https://example.zoom.us/j/123
        END:VEVENT
        END:VCALENDAR
        """;

    private static MailMessageDetail InviteDetail(IcsModel? invite, string? html = null, string? plain = null) => new()
    {
        AccountId       = Guid.NewGuid(),
        MessageId       = "m1",
        FolderName      = "INBOX",
        Subject         = "Web Accessibility Discussion",
        HtmlBody        = html ?? "<p>Amy Gold is inviting you to a scheduled Zoom meeting.</p>",
        PlainTextBody   = plain ?? "Amy Gold is inviting you to a scheduled Zoom meeting.",
        CalendarInvite  = invite,
    };

    // A TZID-qualified DTSTART is what Exchange sends; the card must state the resulting local time.
    [Fact]
    public void Card_ForTzidInvite_StatesTheMeetingTime()
    {
        var invite = IcsModel.Parse(ZoomInviteIcs);
        Assert.NotNull(invite);
        Assert.True(invite!.StartTime.HasValue);

        // 4pm Eastern on 2026-08-05, expressed in whatever zone this machine runs in.
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var expectedStart = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 8, 5, 16, 0, 0, DateTimeKind.Unspecified), eastern).ToLocalTime();
        Assert.Equal(expectedStart, invite.StartTime!.Value.ToLocalTime());

        var card = EventCardHtmlBuilder.Build(invite, themeService: null);

        Assert.Contains("<strong>When:</strong>", card);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(expectedStart.ToString("f")), card);
        Assert.Contains("Web Accessibility Discussion", card);
    }

    [Fact]
    public void Card_WithNoInvite_IsEmpty() =>
        Assert.Equal(string.Empty, EventCardHtmlBuilder.Build(null, themeService: null));

    // THE REGRESSION: a caller that asks only for the message body still gets the card. MessageWindow
    // shipped calling exactly this three-argument shape, and dropped the invitation's date and time
    // because injection was the caller's job. The card now comes from the detail's own CalendarInvite,
    // so no surface can render an invitation without it.
    [Fact]
    public void BuildMessageHtml_CallerAsksOnlyForTheBody_StillGetsTheCard()
    {
        var detail = InviteDetail(IcsModel.Parse(ZoomInviteIcs));

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: false);

        AssertCardIsInsideBody(html);
    }

    // The three body-render paths inside BuildMessageHtml (sanitized HTML, forced plain text, and
    // the reader-mode fallback) must all carry the card.
    [Fact]
    public void BuildMessageHtml_SanitizedHtmlPath_CarriesTheCard()
    {
        var detail = InviteDetail(IcsModel.Parse(ZoomInviteIcs));

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: false, themeService: null);

        AssertCardIsInsideBody(html);
    }

    [Fact]
    public void BuildMessageHtml_PlainTextPath_CarriesTheCard()
    {
        var detail = InviteDetail(IcsModel.Parse(ZoomInviteIcs));

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: true, themeService: null);

        AssertCardIsInsideBody(html);
    }

    [Fact]
    public void BuildMessageHtml_ReaderModePath_CarriesTheCard()
    {
        // Enough tables to trip ShouldUseReaderMode, so the simplified body is used.
        var heavy  = string.Concat(System.Linq.Enumerable.Repeat("<table><tr><td>x</td></tr></table>",
                                                                 MessageBodyHtmlBuilder.MaxRichHtmlTableCount + 1));
        var detail = InviteDetail(IcsModel.Parse(ZoomInviteIcs), html: heavy);

        Assert.True(MessageBodyHtmlBuilder.ShouldUseReaderMode(heavy));
        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: false, themeService: null);

        AssertCardIsInsideBody(html);
    }

    [Fact]
    public void BuildMessageHtml_NonInvitation_HasNoCard()
    {
        var detail = InviteDetail(invite: null);

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: false, themeService: null);

        Assert.DoesNotContain("Event Invitation", html);
        Assert.DoesNotContain("qm-invite-status", html);
    }

    // The RSVP buttons post back as quickmail: links, and the live region is what confirms the reply
    // from inside the document — both must survive injection, in every window that shows a card.
    [Fact]
    public void InjectedCard_KeepsRsvpButtonsAndStatusRegion()
    {
        var detail = InviteDetail(IcsModel.Parse(ZoomInviteIcs));

        var html = MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss: null, forcePlainText: false, themeService: null);

        Assert.Contains("quickmail:ics-accept", html);
        Assert.Contains("quickmail:ics-tentative", html);
        Assert.Contains("quickmail:ics-decline", html);
        Assert.Contains("id=\"qm-invite-status\"", html);
        Assert.Contains("aria-live=\"assertive\"", html);
    }

    [Fact]
    public void StatusScript_EscapesTheText()
    {
        var js = EventCardHtmlBuilder.StatusScript("You accepted \"it\" </script>");

        Assert.Contains("qm-invite-status", js);
        Assert.Contains("textContent=", js);
        // The quoted JS literal must not close the script element or break out of the string.
        Assert.DoesNotContain("</script>", js);
    }

    /// <summary>The card must land inside the document body, ahead of the message content.</summary>
    private static void AssertCardIsInsideBody(string html)
    {
        Assert.Contains("Event Invitation", html);
        Assert.Contains("<strong>When:</strong>", html);

        var bodyIdx = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var cardIdx = html.IndexOf("Event Invitation", StringComparison.Ordinal);
        Assert.True(bodyIdx >= 0, "rendered document has no <body> tag");
        Assert.True(cardIdx > bodyIdx, "event card was placed before the <body> tag");
    }
}
