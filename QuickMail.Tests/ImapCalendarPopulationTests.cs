using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ImapMailService.PopulateCalendar — the seam extracted from
/// GetMessageDetailCoreAsync (issue #304, item 3). Guards the #297 invariant:
/// CalendarInvite is never set without CalendarIcs, because CalendarIcs is the
/// column the local store caches; an invite persisted without its raw ICS loses
/// its Accept/Decline card as soon as the row is served from cache.
/// </summary>
public class ImapCalendarPopulationTests
{
    private static string ValidInviteIcs => string.Join("\r\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "METHOD:REQUEST",
        "BEGIN:VEVENT",
        "UID:invite-1@test.com",
        "ORGANIZER:mailto:organizer@example.com",
        "SUMMARY:Planning Meeting",
        "DTSTART:20260801T140000Z",
        "DTEND:20260801T150000Z",
        "END:VEVENT",
        "END:VCALENDAR");

    [Fact]
    public void PopulateCalendar_ValidInvite_SetsBothIcsAndInvite()
    {
        var detail = new MailMessageDetail();

        ImapMailService.PopulateCalendar(detail, ValidInviteIcs);

        Assert.Equal(ValidInviteIcs, detail.CalendarIcs);
        Assert.NotNull(detail.CalendarInvite);
        Assert.Equal("invite-1@test.com", detail.CalendarInvite!.Uid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void PopulateCalendar_NullOrWhitespace_SetsNeitherField(string? rawIcs)
    {
        var detail = new MailMessageDetail();

        ImapMailService.PopulateCalendar(detail, rawIcs);

        Assert.Equal(string.Empty, detail.CalendarIcs);
        Assert.Null(detail.CalendarInvite);
    }

    [Theory]
    [InlineData("this is not an ics body at all")]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR")] // no VEVENT
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nEND:VEVENT\r\nEND:VCALENDAR")] // empty VEVENT (no start/summary)
    public void PopulateCalendar_UnparseableText_KeepsRawIcsWithNullInvite(string rawIcs)
    {
        var detail = new MailMessageDetail();

        ImapMailService.PopulateCalendar(detail, rawIcs);

        // The raw text is still cached: a later parser fix retroactively revives
        // the invite card for rows that were cached while parsing failed.
        Assert.Equal(rawIcs, detail.CalendarIcs);
        Assert.Null(detail.CalendarInvite);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR")]
    public void PopulateCalendar_Invariant_InviteSetImpliesIcsSet(string? rawIcs)
    {
        var detail = new MailMessageDetail();

        ImapMailService.PopulateCalendar(detail, rawIcs);

        if (detail.CalendarInvite != null)
            Assert.False(string.IsNullOrEmpty(detail.CalendarIcs));
    }

    [Fact]
    public void PopulateCalendar_Invariant_HoldsForValidInvite()
    {
        var detail = new MailMessageDetail();

        ImapMailService.PopulateCalendar(detail, ValidInviteIcs);

        Assert.NotNull(detail.CalendarInvite);
        Assert.False(string.IsNullOrEmpty(detail.CalendarIcs));
    }
}
