using System;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for IcsModel.Parse() and GenerateReply() — ICS calendar invite handling.
/// </summary>
public class IcsModelTests
{
    // ── Parse: valid ICS ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidIcsWithAllFields_ReturnsPopulatedModel()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Test//EN",
            "METHOD:REQUEST",
            "BEGIN:VEVENT",
            "UID:abc123@test.com",
            "SEQUENCE:0",
            "ORGANIZER;CN=John Doe:mailto:john@example.com",
            "SUMMARY:Team Standup",
            "DESCRIPTION:Daily sync meeting",
            "LOCATION:Conference Room A",
            "DTSTART:20260115T140000Z",
            "DTEND:20260115T150000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("abc123@test.com", model!.Uid);
        Assert.Equal("0", model.Sequence);
        Assert.Equal("REQUEST", model.Method);
        Assert.Equal("john@example.com", model.Organizer);
        Assert.Equal("John Doe", model.OrganizerName);
        Assert.Equal("Team Standup", model.Summary);
        Assert.Equal("Daily sync meeting", model.Description);
        Assert.Equal("Conference Room A", model.Location);
        Assert.NotNull(model.StartTime);
        Assert.NotNull(model.EndTime);
        Assert.Equal(2026, model.StartTime!.Value.Year);
        Assert.Equal(1, model.StartTime.Value.Month);
        Assert.Equal(15, model.StartTime.Value.Day);
        Assert.Equal(14, model.StartTime.Value.Hour); // UTC
    }

    // ── Parse: TZID handling ────────────────────────────────────────────────────

    private static IcsModel? ParseWithDtstart(string dtstartLine) =>
        IcsModel.Parse(string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "UID:tz-test@test.com",
            "SUMMARY:TZ Test",
            dtstartLine,
            "END:VEVENT",
            "END:VCALENDAR"));

    [Fact]
    public void Parse_DtstartWithIanaTzid_ConvertsToCorrectInstant()
    {
        var model = ParseWithDtstart("DTSTART;TZID=America/New_York:20260115T100000");

        Assert.NotNull(model?.StartTime);
        // Expected instant computed through TimeZoneInfo so the test passes on any machine zone.
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var expectedUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Unspecified), eastern);
        Assert.Equal(expectedUtc, model!.StartTime!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parse_DtstartWithWindowsTzid_ConvertsToCorrectInstant()
    {
        var model = ParseWithDtstart("DTSTART;TZID=Eastern Standard Time:20260115T100000");

        Assert.NotNull(model?.StartTime);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var expectedUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Unspecified), eastern);
        Assert.Equal(expectedUtc, model!.StartTime!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parse_DtstartWithQuotedTzidAndExtraParams_StillResolves()
    {
        var model = ParseWithDtstart("DTSTART;VALUE=DATE-TIME;TZID=\"America/Chicago\":20260601T090000");

        Assert.NotNull(model?.StartTime);
        var central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var expectedUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Unspecified), central);
        Assert.Equal(expectedUtc, model!.StartTime!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parse_DtstartWithUnknownTzid_FallsBackToMachineLocal()
    {
        var model = ParseWithDtstart("DTSTART;TZID=Not/AZone:20260115T100000");

        Assert.NotNull(model?.StartTime);
        // Historical fallback: treated as machine-local wall-clock 10:00.
        Assert.Equal(10, model!.StartTime!.Value.Hour);
        Assert.Equal(DateTimeKind.Local, model.StartTime.Value.Kind);
    }

    [Fact]
    public void Parse_DtstartWithoutTzid_KeepsMachineLocalBehavior()
    {
        var model = ParseWithDtstart("DTSTART:20260115T100000");

        Assert.NotNull(model?.StartTime);
        Assert.Equal(10, model!.StartTime!.Value.Hour);
    }

    [Fact]
    public void ExtractTzidParam_Variants()
    {
        Assert.Equal("America/Chicago", IcsModel.ExtractTzidParam("DTSTART;TZID=America/Chicago"));
        Assert.Equal("Europe/Paris", IcsModel.ExtractTzidParam("DTSTART;VALUE=DATE-TIME;TZID=\"Europe/Paris\""));
        Assert.Null(IcsModel.ExtractTzidParam("DTSTART"));
        Assert.Null(IcsModel.ExtractTzidParam("DTSTART;VALUE=DATE"));
    }

    // ── ExportEvent (.ics export) ───────────────────────────────────────────────

    [Fact]
    public void ExportEvent_TimedEvent_RoundTripsThroughParse()
    {
        var evt = new CalendarEvent
        {
            Uid = "local-export1",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Dentist, downtown",              // comma must survive escaping
            Location = "12 Main St; Suite 4",           // semicolon too
            Description = "Bring insurance card",
            StartTimeTicks = new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc).Ticks,
            EndTimeTicks = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc).Ticks,
        };

        var ics = IcsModel.ExportEvent(evt);
        Assert.Contains("METHOD:PUBLISH", ics);
        Assert.Contains("UID:local-export1", ics);

        var parsed = IcsModel.Parse(ics);
        Assert.NotNull(parsed);
        Assert.Equal("Dentist, downtown", parsed!.Summary);
        Assert.Equal("12 Main St; Suite 4", parsed.Location);
        Assert.Equal("Bring insurance card", parsed.Description);
        Assert.Equal(evt.StartTimeTicks, parsed.StartTime!.Value.ToUniversalTime().Ticks);
        Assert.Equal(evt.EndTimeTicks, parsed.EndTime!.Value.ToUniversalTime().Ticks);
    }

    [Fact]
    public void ExportEvent_AllDay_UsesDateValuesWithExclusiveEnd()
    {
        var evt = new CalendarEvent
        {
            Uid = "local-allday1",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Holiday",
            IsAllDay = true,
            StartTimeTicks = new DateTime(2026, 7, 20).ToUniversalTime().Ticks,
            EndTimeTicks = new DateTime(2026, 7, 20, 23, 59, 59).ToUniversalTime().Ticks,
        };

        var ics = IcsModel.ExportEvent(evt);
        Assert.Contains("DTSTART;VALUE=DATE:20260720", ics);
        Assert.Contains("DTEND;VALUE=DATE:20260721", ics); // exclusive end = next day
    }

    [Fact]
    public void ExportEvent_Recurring_CarriesRRule()
    {
        var evt = new CalendarEvent
        {
            Uid = "local-rec1",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Standup",
            StartTimeTicks = DateTime.UtcNow.Ticks,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
        };

        var ics = IcsModel.ExportEvent(evt);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=TU", ics);
    }

    [Fact]
    public void Parse_MinimalValidIcs_ReturnsModel()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "SUMMARY:Lunch",
            "DTSTART:20260120T120000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("Lunch", model!.Summary);
        Assert.NotNull(model.StartTime);
        Assert.Null(model.EndTime);
        Assert.Null(model.Organizer);
        Assert.Null(model.Location);
    }

    [Fact]
    public void Parse_IcsWithDateOnlyStart_ReturnsModelWithDate()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "SUMMARY:All Day Event",
            "DTSTART:20260125",
            "DTEND:20260126",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("All Day Event", model!.Summary);
        Assert.NotNull(model.StartTime);
        Assert.Equal(2026, model.StartTime!.Value.Year);
        Assert.Equal(1, model.StartTime.Value.Month);
        Assert.Equal(25, model.StartTime.Value.Day);
    }

    [Fact]
    public void Parse_IcsWithFoldedLines_UnfoldsCorrectly()
    {
        // ICS line folding: continuation lines start with a space
        var ics = "BEGIN:VCALENDAR\r\n" +
                  "VERSION:2.0\r\n" +
                  "BEGIN:VEVENT\r\n" +
                  "SUMMARY:This is a very long summary\r\n" +
                  "  that continues on the next line\r\n" +
                  "DTSTART:20260115T140000Z\r\n" +
                  "END:VEVENT\r\n" +
                  "END:VCALENDAR\r\n";

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("This is a very long summary that continues on the next line", model!.Summary);
    }

    [Fact]
    public void Parse_IcsWithTabFoldedLines_UnfoldsCorrectly()
    {
        var ics = "BEGIN:VCALENDAR\r\n" +
                  "VERSION:2.0\r\n" +
                  "BEGIN:VEVENT\r\n" +
                  "DESCRIPTION:Line one\r\n" +
                  "\tLine two\r\n" +
                  "DTSTART:20260115T140000Z\r\n" +
                  "END:VEVENT\r\n" +
                  "END:VCALENDAR\r\n";

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("Line oneLine two", model!.Description);
    }

    [Fact]
    public void Parse_OrganizerWithoutCnParam_FallsBackToMailtoValue()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "ORGANIZER:mailto:boss@example.com",
            "SUMMARY:Review",
            "DTSTART:20260115T140000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("boss@example.com", model!.Organizer);
        Assert.Equal("boss@example.com", model.OrganizerName);
    }

    [Fact]
    public void Parse_OrganizerWithQuotedCnParam_ExtractsCorrectly()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "ORGANIZER;CN=\"Doe, John\":mailto:john@example.com",
            "SUMMARY:Review",
            "DTSTART:20260115T140000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("Doe, John", model!.OrganizerName);
    }

    [Fact]
    public void Parse_IcsWithEscapedText_UnescapesCorrectly()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "SUMMARY:Meeting\\, Q1 Review",
            "DESCRIPTION:Line 1\\nLine 2\\nLine 3",
            "LOCATION:Room A\\; Building 1",
            "DTSTART:20260115T140000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.Equal("Meeting, Q1 Review", model!.Summary);
        Assert.Equal("Line 1\nLine 2\nLine 3", model.Description);
        Assert.Equal("Room A; Building 1", model.Location);
    }

    [Fact]
    public void Parse_IcsWithDtstartTimezone_HandlesCorrectly()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "SUMMARY:Local Time Event",
            "DTSTART;TZID=America/Chicago:20260115T140000",
            "DTEND;TZID=America/Chicago:20260115T150000",
            "END:VEVENT",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.NotNull(model);
        Assert.NotNull(model!.StartTime);
        Assert.NotNull(model.EndTime);
    }

    [Fact]
    public void Parse_IcsWithoutVevent_ReturnsNull()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VTODO",
            "SUMMARY:Some task",
            "END:VTODO",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.Null(model);
    }

    [Fact]
    public void Parse_IcsWithOnlyMethod_ReturnsNull()
    {
        // No VEVENT, no start time, no summary — nothing meaningful
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "METHOD:CANCEL",
            "END:VCALENDAR");

        var model = IcsModel.Parse(ics);

        Assert.Null(model);
    }

    // ── Parse: invalid ICS ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullContent_ReturnsNull()
    {
        Assert.Null(IcsModel.Parse(null!));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(IcsModel.Parse(""));
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(IcsModel.Parse("   \r\n\t  "));
    }

    [Fact]
    public void Parse_GarbageContent_ReturnsNull()
    {
        Assert.Null(IcsModel.Parse("This is not an ICS file at all."));
    }

    [Fact]
    public void Parse_MalformedIcs_ReturnsNull()
    {
        var ics = "BEGIN:VCALENDAR\r\nGARBAGE\r\nEND:VCALENDAR\r\n";
        Assert.Null(IcsModel.Parse(ics));
    }

    // ── GenerateReply ───────────────────────────────────────────────────────────

    [Fact]
    public void GenerateReply_Accept_ContainsAcceptedPartStat()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "User Name", "ACCEPTED");

        Assert.Contains("METHOD:REPLY", reply);
        Assert.Contains("PARTSTAT=ACCEPTED", reply);
        Assert.Contains("mailto:user@example.com", reply);
        Assert.Contains("CN=User Name", reply);
        Assert.Contains("UID:abc123@test.com", reply);
        Assert.Contains("SUMMARY:Team Standup", reply);
    }

    [Fact]
    public void GenerateReply_Decline_ContainsDeclinedPartStat()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "User Name", "DECLINED");

        Assert.Contains("METHOD:REPLY", reply);
        Assert.Contains("PARTSTAT=DECLINED", reply);
    }

    [Fact]
    public void GenerateReply_Tentative_ContainsTentativePartStat()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "User Name", "TENTATIVE");

        Assert.Contains("METHOD:REPLY", reply);
        Assert.Contains("PARTSTAT=TENTATIVE", reply);
    }

    [Fact]
    public void GenerateReply_IncludesDtstamp()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "User Name", "ACCEPTED");

        Assert.Contains("DTSTAMP:", reply);
    }

    [Fact]
    public void GenerateReply_IncludesOrganizer()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "User Name", "ACCEPTED");

        Assert.Contains("ORGANIZER:john@example.com", reply);
    }

    [Fact]
    public void GenerateReply_IncludesDtstartAndDtend()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "User Name", "ACCEPTED");

        Assert.Contains("DTSTART:", reply);
        Assert.Contains("DTEND:", reply);
    }

    [Fact]
    public void GenerateReply_WithoutUid_DoesNotIncludeUidLine()
    {
        var model = new IcsModel
        {
            Summary = "No UID Event",
            StartTime = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc),
            Organizer = "boss@example.com"
        };

        var reply = model.GenerateReply("user@example.com", "User", "ACCEPTED");

        Assert.DoesNotContain("UID:", reply);
    }

    [Fact]
    public void GenerateReply_WithoutStartTime_DoesNotIncludeDtstart()
    {
        var model = new IcsModel
        {
            Summary = "No Time Event",
            Uid = "test123",
            Organizer = "boss@example.com"
        };

        var reply = model.GenerateReply("user@example.com", "User", "ACCEPTED");

        Assert.DoesNotContain("DTSTART:", reply);
    }

    [Fact]
    public void GenerateReply_EscapesSpecialCharactersInName()
    {
        var model = CreateSampleInvite();
        var reply = model.GenerateReply("user@example.com", "Doe, John; Jr.", "ACCEPTED");

        // Comma and semicolon should be escaped
        Assert.Contains("CN=Doe\\, John\\; Jr.", reply);
    }

    // ── DisplaySummary ──────────────────────────────────────────────────────────

    [Fact]
    public void DisplaySummary_IncludesAllFields()
    {
        var model = CreateSampleInvite();
        var summary = model.DisplaySummary;

        Assert.Contains("Event: Team Standup", summary);
        Assert.Contains("Organizer: John Doe", summary);
        Assert.Contains("Location: Conference Room A", summary);
        Assert.Contains("Daily sync meeting", summary);
    }

    [Fact]
    public void DisplaySummary_WithoutOrganizerName_UsesOrganizerEmail()
    {
        var model = new IcsModel
        {
            Summary = "Meeting",
            Organizer = "boss@example.com",
            StartTime = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc)
        };

        var summary = model.DisplaySummary;

        Assert.Contains("Organizer: boss@example.com", summary);
    }

    [Fact]
    public void DisplaySummary_WithoutEndTime_ShowsOnlyStart()
    {
        var model = new IcsModel
        {
            Summary = "Meeting",
            StartTime = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc)
        };

        var summary = model.DisplaySummary;

        Assert.Contains("When:", summary);
        Assert.DoesNotContain(" - ", summary); // No end time separator
    }

    [Fact]
    public void DisplaySummary_MinimalModel_DoesNotThrow()
    {
        var model = new IcsModel();
        var summary = model.DisplaySummary;

        Assert.NotNull(summary);
        Assert.Empty(summary);
    }

    // ── BriefSummary ────────────────────────────────────────────────────────────

    [Fact]
    public void BriefSummary_WithStartTime_IncludesFormattedDate()
    {
        var model = CreateSampleInvite();
        var brief = model.BriefSummary;

        Assert.Contains("Team Standup", brief);
        Assert.Contains("2026", brief); // Year from the date
    }

    [Fact]
    public void BriefSummary_WithoutSummary_UsesDefaultTitle()
    {
        var model = new IcsModel
        {
            StartTime = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc)
        };

        var brief = model.BriefSummary;

        Assert.Contains("Calendar Event", brief);
    }

    [Fact]
    public void BriefSummary_WithoutStartTime_ReturnsTitleOnly()
    {
        var model = new IcsModel { Summary = "Standup" };
        var brief = model.BriefSummary;

        Assert.Equal("Standup", brief);
    }

    // ── ParseAllEvents (CalDAV multi-VEVENT bodies) ─────────────────────────────

    [Fact]
    public void ParseAllEvents_MultiVEvent_ReturnsEachEvent_WithCalendarMethodApplied()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            "UID:one@test",
            "SUMMARY:First",
            "DTSTART:20260801T090000Z",
            "DTEND:20260801T100000Z",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:two@test",
            "SUMMARY:Second",
            "DTSTART;VALUE=DATE:20260803",
            "DTEND;VALUE=DATE:20260805",
            "END:VEVENT",
            "END:VCALENDAR");

        var events = IcsModel.ParseAllEvents(ics);

        Assert.Equal(2, events.Count);
        Assert.Equal("one@test", events[0].Uid);
        Assert.False(events[0].IsAllDay);
        Assert.Equal("PUBLISH", events[0].Method);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc), events[0].StartTime!.Value.ToUniversalTime());

        Assert.Equal("two@test", events[1].Uid);
        Assert.True(events[1].IsAllDay);
        Assert.Equal("PUBLISH", events[1].Method);
        Assert.Equal(new DateTime(2026, 8, 3), events[1].StartTime!.Value.Date);
        // DTEND stays EXCLUSIVE on the model (RFC 5545); consumers re-anchor.
        Assert.Equal(new DateTime(2026, 8, 5), events[1].EndTime!.Value.Date);
    }

    [Fact]
    public void ParseAllEvents_RecurringMaster_CapturesRRuleAndExDates()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "UID:series@test",
            "SUMMARY:Weekly sync",
            "DTSTART:20260804T090000",   // local wall-clock, no TZID
            "DTEND:20260804T093000",
            "RRULE:FREQ=WEEKLY;BYDAY=TU",
            "EXDATE:20260811T090000,20260825T090000",
            "END:VEVENT",
            "END:VCALENDAR");

        var evt = Assert.Single(IcsModel.ParseAllEvents(ics));

        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", evt.RecurrenceRule);
        Assert.Null(evt.RecurrenceId);
        Assert.Equal(
            new[] { new DateTime(2026, 8, 11, 9, 0, 0), new DateTime(2026, 8, 25, 9, 0, 0) },
            evt.ExDates);
    }

    [Fact]
    public void ParseAllEvents_UtcExDate_NormalizesToLocal()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "UID:series-utc@test",
            "SUMMARY:Daily",
            "DTSTART:20260804T160000Z",
            "RRULE:FREQ=DAILY",
            "EXDATE:20260805T160000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var evt = Assert.Single(IcsModel.ParseAllEvents(ics));

        var expectedLocal = new DateTime(2026, 8, 5, 16, 0, 0, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(expectedLocal, Assert.Single(evt.ExDates));
    }

    [Fact]
    public void ParseAllEvents_OverriddenInstance_CarriesRecurrenceId()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "UID:series@test",
            "SUMMARY:Weekly sync",
            "DTSTART:20260804T090000Z",
            "RRULE:FREQ=WEEKLY",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:series@test",
            "SUMMARY:Weekly sync (moved)",
            "RECURRENCE-ID:20260811T090000Z",
            "DTSTART:20260811T100000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var events = IcsModel.ParseAllEvents(ics);

        Assert.Equal(2, events.Count);
        Assert.Null(events[0].RecurrenceId);
        Assert.Equal("20260811T090000Z", events[1].RecurrenceId);
    }

    [Fact]
    public void ParseAllEvents_CapturesStatus()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "UID:gone@test",
            "SUMMARY:Cancelled thing",
            "DTSTART:20260801T090000Z",
            "STATUS:CANCELLED",
            "END:VEVENT",
            "END:VCALENDAR");

        Assert.Equal("CANCELLED", Assert.Single(IcsModel.ParseAllEvents(ics)).Status);
    }

    [Fact]
    public void ParseAllEvents_GarbageOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(IcsModel.ParseAllEvents(""));
        Assert.Empty(IcsModel.ParseAllEvents("not ics at all"));
        Assert.Empty(IcsModel.ParseAllEvents("BEGIN:VCALENDAR\r\nEND:VCALENDAR"));
    }

    [Fact]
    public void Parse_MultiVEventBody_ReturnsFirstEvent()
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "UID:one@test",
            "SUMMARY:First",
            "DTSTART:20260801T090000Z",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:two@test",
            "SUMMARY:Second",
            "DTSTART:20260802T090000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        Assert.Equal("one@test", IcsModel.Parse(ics)!.Uid);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IcsModel CreateSampleInvite()
    {
        return new IcsModel
        {
            Uid = "abc123@test.com",
            Sequence = "0",
            Method = "REQUEST",
            Organizer = "john@example.com",
            OrganizerName = "John Doe",
            Summary = "Team Standup",
            Description = "Daily sync meeting",
            Location = "Conference Room A",
            StartTime = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 15, 15, 0, 0, DateTimeKind.Utc)
        };
    }
}
