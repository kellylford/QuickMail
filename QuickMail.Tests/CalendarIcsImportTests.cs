using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Importing .ics files into the Local Calendar (issue #762): the shared ICS → row mapping, the
/// importer's rules (duplicates, cancellations, recurring series and their moved occurrences), and
/// the ViewModel's single-batch store and announcement.
/// </summary>
public class CalendarIcsImportTests
{
    private static string Cal(params string[] vevents)
        => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n"
           + string.Concat(vevents)
           + "END:VCALENDAR\r\n";

    private static string VEvent(params string[] lines)
        => "BEGIN:VEVENT\r\n" + string.Concat(lines.Select(l => l + "\r\n")) + "END:VEVENT\r\n";

    private static CalendarImportResult Plan(string ics, params CalendarEvent[] existing)
        => CalendarIcsImporter.Plan(ics, existing);

    // ── Mapping ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Timed_UtcEvent_MapsToLocalRow()
    {
        var result = Plan(Cal(VEvent("UID:a@x", "SUMMARY:Dentist", "LOCATION:Main St",
                                     "DTSTART:20261005T150000Z", "DTEND:20261005T160000Z")));

        var evt = Assert.Single(result.Events);
        Assert.Equal(CalendarEvent.LocalAccountId, evt.AccountId);
        Assert.True(evt.IsUserCreated);
        Assert.False(evt.IsGraph);
        Assert.Equal("Dentist", evt.Summary);
        Assert.Equal("Main St", evt.Location);
        Assert.Equal(new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc).Ticks, evt.StartTimeTicks);
        Assert.Equal(new DateTime(2026, 10, 5, 16, 0, 0, DateTimeKind.Utc).Ticks, evt.EndTimeTicks);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
    }

    [Fact]
    public void AllDay_ExclusiveDtEnd_ReanchorsToLocalDay()
    {
        var evt = Assert.Single(Plan(Cal(VEvent("UID:ad@x", "SUMMARY:Holiday",
            "DTSTART;VALUE=DATE:20261224", "DTEND;VALUE=DATE:20261226"))).Events);

        Assert.True(evt.IsAllDay);
        Assert.Equal(new DateTime(2026, 12, 24), evt.StartTime);
        Assert.Equal(new DateTime(2026, 12, 25, 23, 59, 59), evt.EndTime); // last day, not the exclusive end
    }

    [Fact]
    public void Tzid_IsHonored()
    {
        var evt = Assert.Single(Plan(Cal(VEvent("UID:tz@x", "SUMMARY:Call",
            "DTSTART;TZID=America/New_York:20260715T100000",
            "DTEND;TZID=America/New_York:20260715T110000"))).Events);

        // 10:00 EDT is 14:00 UTC, whatever zone the test machine is in.
        Assert.Equal(new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc).Ticks, evt.StartTimeTicks);
    }

    [Fact]
    public void FloatingTime_IsMachineLocal()
    {
        var evt = Assert.Single(Plan(Cal(VEvent("UID:fl@x", "SUMMARY:Lunch",
            "DTSTART:20260715T120000", "DTEND:20260715T130000"))).Events);

        Assert.Equal(new DateTime(2026, 7, 15, 12, 0, 0), evt.StartTime);
    }

    [Fact]
    public void CalDavMapping_StillTagsServerRows_AfterExtraction()
    {
        var ics = IcsModel.ParseAllEvents(Cal(VEvent("UID:s@x", "SUMMARY:Sync",
            "DTSTART;VALUE=DATE:20261224", "DTEND;VALUE=DATE:20261225")))[0];

        var row = GraphCalendarSyncService.MapCalDavEvent(ics, Guid.NewGuid(), "cal-url", "Home", "res-url");

        Assert.True(row.IsGraph);
        Assert.Equal("cal-url", row.CalendarId);
        Assert.Equal("Home", row.CalendarName);
        Assert.Equal("res-url", row.ResourceUrl);
        Assert.Equal(new DateTime(2026, 12, 24, 23, 59, 59), row.EndTime);
    }

    // ── Importer rules ───────────────────────────────────────────────────────────

    [Fact]
    public void MultipleEvents_AreAllImported()
    {
        var result = Plan(Cal(
            VEvent("UID:1", "SUMMARY:One", "DTSTART:20261001T100000Z"),
            VEvent("UID:2", "SUMMARY:Two", "DTSTART:20261002T100000Z"),
            VEvent("UID:3", "SUMMARY:Three", "DTSTART:20261003T100000Z")));

        Assert.Equal(3, result.Added);
        Assert.Equal(new[] { "1", "2", "3" }, result.Events.Select(e => e.Uid).Order());
    }

    [Fact]
    public void ExistingLocalUid_IsUpdated_NotDuplicated()
    {
        var existing = new CalendarEvent { Uid = "dup", AccountId = CalendarEvent.LocalAccountId, Summary = "Old" };

        var result = Plan(Cal(VEvent("UID:dup", "SUMMARY:New", "DTSTART:20261001T100000Z")), existing);

        var evt = Assert.Single(result.Events);
        Assert.Equal("New", evt.Summary);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void SameUidOnAnotherAccount_IsAddedToLocal()
    {
        // An invite already harvested from email keeps its own row; the import is a local copy.
        var invite = new CalendarEvent { Uid = "u", AccountId = Guid.NewGuid() };

        var result = Plan(Cal(VEvent("UID:u", "SUMMARY:S", "DTSTART:20261001T100000Z")), invite);

        Assert.Equal(1, result.Added);
        Assert.Equal(CalendarEvent.LocalAccountId, Assert.Single(result.Events).AccountId);
    }

    [Fact]
    public void MissingUid_GetsStableUid_SoReimportUpdates()
    {
        var ics = Cal(VEvent("SUMMARY:No uid", "DTSTART:20261001T100000Z"));

        var first = Assert.Single(Plan(ics).Events);
        Assert.False(string.IsNullOrWhiteSpace(first.Uid));

        var second = Plan(ics, first);
        Assert.Equal(first.Uid, Assert.Single(second.Events).Uid);
        Assert.Equal(1, second.Updated);
    }

    [Fact]
    public void Cancelled_IsSkippedAndCounted()
    {
        var result = Plan(Cal(
            VEvent("UID:ok", "SUMMARY:Kept", "DTSTART:20261001T100000Z"),
            VEvent("UID:gone", "SUMMARY:Off", "STATUS:CANCELLED", "DTSTART:20261002T100000Z")));

        Assert.Equal("ok", Assert.Single(result.Events).Uid);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void EventWithNoStart_IsSkippedAndCounted()
    {
        var result = Plan(Cal(VEvent("UID:nostart", "SUMMARY:Someday")));

        Assert.True(result.IsEmpty);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void RecurringSeries_KeepsRRuleAndExDates()
    {
        var evt = Assert.Single(Plan(Cal(VEvent("UID:series", "SUMMARY:Standup",
            "DTSTART:20261005T090000", "DTEND:20261005T091500",
            "RRULE:FREQ=WEEKLY;BYDAY=MO",
            "EXDATE:20261012T090000"))).Events);

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", evt.RecurrenceRule);
        Assert.Equal(new[] { new DateTime(2026, 10, 12, 9, 0, 0) }, evt.GetExDates());
    }

    [Fact]
    public void OverriddenOccurrence_BecomesExDatePlusStandalone()
    {
        var result = Plan(Cal(
            VEvent("UID:series", "SUMMARY:Standup",
                   "DTSTART:20261005T090000", "DTEND:20261005T091500", "RRULE:FREQ=WEEKLY;BYDAY=MO"),
            VEvent("UID:series", "RECURRENCE-ID:20261019T090000", "SUMMARY:Standup (moved)",
                   "DTSTART:20261020T140000", "DTEND:20261020T141500")));

        Assert.Equal(2, result.Events.Count);
        var master = result.Events.Single(e => e.Uid == "series");
        Assert.Equal(new[] { new DateTime(2026, 10, 19, 9, 0, 0) }, master.GetExDates());

        var moved = result.Events.Single(e => e.Uid != "series");
        Assert.StartsWith("series#", moved.Uid);
        Assert.Null(moved.RecurrenceRule);
        Assert.Equal("Standup (moved)", moved.Summary);
        Assert.Equal(new DateTime(2026, 10, 20, 14, 0, 0), moved.StartTime);
    }

    [Fact]
    public void OverriddenOccurrence_BeforeItsMaster_InFile_StillApplies()
    {
        var result = Plan(Cal(
            VEvent("UID:s", "RECURRENCE-ID:20261019T090000", "SUMMARY:Moved", "DTSTART:20261020T090000"),
            VEvent("UID:s", "SUMMARY:Master", "DTSTART:20261005T090000", "RRULE:FREQ=WEEKLY")));

        Assert.Single(result.Events.Single(e => e.Uid == "s").GetExDates());
    }

    [Fact]
    public void CancelledOccurrence_BecomesExDateOnly()
    {
        var result = Plan(Cal(
            VEvent("UID:s", "SUMMARY:Master", "DTSTART:20261005T090000", "RRULE:FREQ=WEEKLY"),
            VEvent("UID:s", "RECURRENCE-ID:20261012T090000", "STATUS:CANCELLED",
                   "SUMMARY:Master", "DTSTART:20261012T090000")));

        var master = Assert.Single(result.Events);
        Assert.Equal(new[] { new DateTime(2026, 10, 12, 9, 0, 0) }, master.GetExDates());
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Reimport_OfSeriesWithOverride_DoesNotDuplicateExDates()
    {
        var ics = Cal(
            VEvent("UID:s", "SUMMARY:Master", "DTSTART:20261005T090000", "RRULE:FREQ=WEEKLY"),
            VEvent("UID:s", "RECURRENCE-ID:20261019T090000", "SUMMARY:Moved", "DTSTART:20261020T090000"));

        var first = Plan(ics);
        var second = Plan(ics, first.Events.ToArray());

        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Updated);
        Assert.Single(second.Events.Single(e => e.Uid == "s").GetExDates());
    }

    [Fact]
    public void Reimport_KeepsOccurrencesTheUserRemoved()
    {
        var existing = Plan(Cal(VEvent("UID:s", "SUMMARY:M", "DTSTART:20261005T090000", "RRULE:FREQ=WEEKLY"))).Events[0];
        existing.AddExDate(new DateTime(2026, 10, 26, 9, 0, 0)); // user deleted one occurrence

        var again = Plan(Cal(VEvent("UID:s", "SUMMARY:M", "DTSTART:20261005T090000", "RRULE:FREQ=WEEKLY")), existing);

        Assert.Contains(new DateTime(2026, 10, 26, 9, 0, 0), Assert.Single(again.Events).GetExDates());
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not a calendar")]
    [InlineData("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nSUMMARY:Task\r\nEND:VTODO\r\nEND:VCALENDAR\r\n")]
    public void NothingUsable_IsEmpty(string text)
    {
        Assert.True(Plan(text).IsEmpty);
    }

    [Fact]
    public void MethodRequest_IsAPlainImport()
    {
        var evt = Assert.Single(Plan("BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\n"
            + VEvent("UID:inv", "SUMMARY:Invite", "ORGANIZER;CN=Pat:mailto:pat@example.com",
                     "DTSTART:20261001T100000Z")
            + "END:VCALENDAR\r\n").Events);

        Assert.True(evt.IsUserCreated);
        Assert.Null(evt.Method);
        Assert.Equal(CalendarResponseStatus.Accepted, evt.ResponseStatus);
    }

    [Fact]
    public void RoundTrip_ExportThenImport_GivesTheSameEvent()
    {
        var original = new CalendarEvent
        {
            Uid = "rt@quickmail",
            AccountId = CalendarEvent.LocalAccountId,
            Summary = "Review; notes, etc.",
            Location = "Room 4",
            Description = "Line one\nLine two",
            StartTimeTicks = new DateTime(2026, 11, 3, 17, 30, 0, DateTimeKind.Utc).Ticks,
            EndTimeTicks = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc).Ticks,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
        };

        var back = Assert.Single(Plan(IcsModel.ExportEvent(original)).Events);

        Assert.Equal(original.Uid, back.Uid);
        Assert.Equal(original.Summary, back.Summary);
        Assert.Equal(original.Location, back.Location);
        Assert.Equal(original.Description, back.Description);
        Assert.Equal(original.StartTimeTicks, back.StartTimeTicks);
        Assert.Equal(original.EndTimeTicks, back.EndTimeTicks);
        Assert.Equal(original.RecurrenceRule, back.RecurrenceRule);
    }

    // ── Real-world shapes ────────────────────────────────────────────────────────

    [Fact]
    public void GoogleExport_AlarmDescription_DoesNotReplaceEventDescription()
    {
        const string google = """
            BEGIN:VCALENDAR
            PRODID:-//Google Inc//Google Calendar 70.9054//EN
            VERSION:2.0
            CALSCALE:GREGORIAN
            METHOD:PUBLISH
            X-WR-CALNAME:kelly@example.com
            X-WR-TIMEZONE:America/Los_Angeles
            BEGIN:VTIMEZONE
            TZID:America/Los_Angeles
            BEGIN:DAYLIGHT
            TZOFFSETFROM:-0800
            TZOFFSETTO:-0700
            DTSTART:19700308T020000
            END:DAYLIGHT
            END:VTIMEZONE
            BEGIN:VEVENT
            DTSTART;TZID=America/Los_Angeles:20261008T090000
            DTEND;TZID=America/Los_Angeles:20261008T100000
            DTSTAMP:20260926T120000Z
            UID:4k1b2c3d4e5f@google.com
            CREATED:20260901T120000Z
            DESCRIPTION:Bring the quarterly numbers.
            LAST-MODIFIED:20260901T120000Z
            LOCATION:
            SEQUENCE:0
            STATUS:CONFIRMED
            SUMMARY:Budget review
            TRANSP:OPAQUE
            BEGIN:VALARM
            ACTION:DISPLAY
            DESCRIPTION:This is an event reminder
            TRIGGER:-P0DT0H10M0S
            END:VALARM
            END:VEVENT
            END:VCALENDAR
            """;

        var evt = Assert.Single(Plan(google).Events);
        Assert.Equal("Budget review", evt.Summary);
        Assert.Equal("Bring the quarterly numbers.", evt.Description);
        Assert.Equal(new DateTime(2026, 10, 8, 16, 0, 0, DateTimeKind.Utc).Ticks, evt.StartTimeTicks);
    }

    [Fact]
    public void OutlookExport_WindowsTimeZoneId_AndFoldedLines()
    {
        const string outlook = "BEGIN:VCALENDAR\r\n"
            + "PRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN\r\n"
            + "VERSION:2.0\r\nMETHOD:PUBLISH\r\n"
            + "BEGIN:VTIMEZONE\r\nTZID:Eastern Standard Time\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:16011104T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\n"
            + "CLASS:PUBLIC\r\n"
            + "DESCRIPTION:A long agenda line that Outlook folds across two physical lin\r\n es in the file.\\n\r\n"
            + "DTEND;TZID=\"Eastern Standard Time\":20261112T150000\r\n"
            + "DTSTART;TZID=\"Eastern Standard Time\":20261112T140000\r\n"
            + "LOCATION:Conference Room B\r\n"
            + "SUMMARY;LANGUAGE=en-us:Project sync\r\n"
            + "UID:040000008200E00074C5B7101A82E00800000000\r\n"
            + "X-MICROSOFT-CDO-BUSYSTATUS:BUSY\r\n"
            + "BEGIN:VALARM\r\nTRIGGER:-PT15M\r\nACTION:DISPLAY\r\nDESCRIPTION:Reminder\r\nEND:VALARM\r\n"
            + "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var evt = Assert.Single(Plan(outlook).Events);
        Assert.Equal("Project sync", evt.Summary);
        Assert.Equal("A long agenda line that Outlook folds across two physical lines in the file.", evt.Description);
        Assert.Equal("Conference Room B", evt.Location);
        // 14:00 EST is 19:00 UTC.
        Assert.Equal(new DateTime(2026, 11, 12, 19, 0, 0, DateTimeKind.Utc).Ticks, evt.StartTimeTicks);
    }

    [Fact]
    public void AppleExport_AllDayWithoutDtEnd_IsOneDay()
    {
        const string apple = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Apple Inc.//macOS 15.0//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:ABCD-1234\r\nDTSTART;VALUE=DATE:20261101\r\nSUMMARY:Birthday\r\n"
            + "X-APPLE-TRAVEL-ADVISORY-BEHAVIOR:AUTOMATIC\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var evt = Assert.Single(Plan(apple).Events);
        Assert.True(evt.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 1), evt.StartTime);
        Assert.Equal(new DateTime(2026, 11, 1, 23, 59, 59), evt.EndTime);
    }

    [Fact]
    public void BookingSiteFile_UnixLineEndings_NoUid()
    {
        const string booking = "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\n"
            + "DTSTART:20261115T180000Z\nDTEND:20261115T190000Z\n"
            + "SUMMARY:Haircut at Salon\nLOCATION:12 High St\\, Springfield\nEND:VEVENT\nEND:VCALENDAR\n";

        var evt = Assert.Single(Plan(booking).Events);
        Assert.Equal("Haircut at Salon", evt.Summary);
        Assert.Equal("12 High St, Springfield", evt.Location);
        Assert.False(string.IsNullOrWhiteSpace(evt.Uid));
    }

    // ── ViewModel ────────────────────────────────────────────────────────────────

    private static (CalendarViewModel Vm, StubCalendarService Svc, List<string> Spoken) MakeVm(
        bool onlineMode = false, params CalendarEvent[] stored)
    {
        var svc = new StubCalendarService { StoredEvents = stored.ToList() };
        var vm = new CalendarViewModel(svc, onlineMode, showDeclinedEvents: false);
        var spoken = new List<string>();
        vm.AnnouncementRequested += (text, category) =>
        {
            if (category == AnnouncementCategory.Result) spoken.Add(text);
        };
        return (vm, svc, spoken);
    }

    [Fact]
    public async Task ImportFiles_StoresOnceSelectsAndAnnounces()
    {
        var (vm, svc, spoken) = MakeVm();
        var focusRequested = false;
        vm.ListFocusRequested += () => focusRequested = true;
        var start = DateTime.UtcNow.AddDays(3);

        var error = await vm.ImportFilesAsync([("a.ics", Cal(
            VEvent("UID:1", "SUMMARY:First", $"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}"),
            VEvent("UID:2", "SUMMARY:Second", $"DTSTART:{start.AddDays(1):yyyyMMdd'T'HHmmss'Z'}"),
            VEvent("UID:3", "STATUS:CANCELLED", "SUMMARY:Off", $"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}")))]);

        Assert.Null(error);
        Assert.Equal(1, svc.BatchUpsertCallCount);
        Assert.Equal(2, svc.StoredEvents.Count);
        Assert.Equal("1", vm.SelectedEvent?.Uid);
        Assert.True(focusRequested);
        Assert.Equal("Imported 2 appointments to Local Calendar. 1 skipped.", Assert.Single(spoken));
    }

    [Fact]
    public async Task ImportFiles_Twice_ReportsUpdated()
    {
        var (vm, svc, spoken) = MakeVm();
        var file = ("a.ics", Cal(VEvent("UID:1", "SUMMARY:First", "DTSTART:20301001T100000Z")));

        await vm.ImportFilesAsync([file]);
        await vm.ImportFilesAsync([file]);

        Assert.Single(svc.StoredEvents);
        Assert.Equal("Updated 1 appointment in Local Calendar.", spoken.Last());
    }

    [Fact]
    public async Task ImportFiles_NothingUsable_ReturnsErrorAndSaysNothing()
    {
        var (vm, svc, spoken) = MakeVm();
        var beforeStoreRan = false;

        var error = await vm.ImportFilesAsync([("junk.ics", "not a calendar")],
                                              () => { beforeStoreRan = true; return Task.CompletedTask; });

        Assert.Equal("No calendar events were found in junk.ics.", error);
        Assert.Empty(spoken);
        Assert.False(beforeStoreRan);
        Assert.Equal(0, svc.BatchUpsertCallCount);
    }

    [Fact]
    public async Task ImportFiles_OneGoodOneEmpty_ImportsAndNamesTheEmptyOne()
    {
        var (vm, svc, spoken) = MakeVm();

        var error = await vm.ImportFilesAsync([
            ("good.ics", Cal(VEvent("UID:1", "SUMMARY:First", "DTSTART:20301001T100000Z"))),
            ("empty.ics", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")]);

        Assert.Equal("No calendar events were found in empty.ics.", error);
        Assert.Single(svc.StoredEvents);
        Assert.Single(spoken);
    }

    [Fact]
    public void ImportCommand_InOnlineMode_SaysUnavailable_AndOpensNoDialog()
    {
        var (vm, _, spoken) = MakeVm(onlineMode: true);
        var dialogRequested = false;
        vm.ImportRequested += () => dialogRequested = true;

        vm.ImportIcsCommand.Execute(null);

        Assert.False(dialogRequested);
        Assert.Equal("Calendar is unavailable in online mode.", Assert.Single(spoken));
    }

    [Fact]
    public void ImportCommand_RaisesImportRequested()
    {
        var (vm, _, _) = MakeVm();
        var dialogRequested = false;
        vm.ImportRequested += () => dialogRequested = true;

        vm.ImportIcsCommand.Execute(null);

        Assert.True(dialogRequested);
    }

    [Theory]
    [InlineData(3, 0, 0, "Imported 3 appointments to Local Calendar.")]
    [InlineData(1, 0, 2, "Imported 1 appointment to Local Calendar. 2 skipped.")]
    [InlineData(0, 4, 0, "Updated 4 appointments in Local Calendar.")]
    [InlineData(2, 5, 1, "Imported 2 appointments to Local Calendar and updated 5. 1 skipped.")]
    public void ImportSummary_Wording(int added, int updated, int skipped, string expected)
        => Assert.Equal(expected, CalendarViewModel.ImportSummary(added, updated, skipped));
}
