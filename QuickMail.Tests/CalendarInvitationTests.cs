using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Services.Graph;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Inviting people to an appointment (issue #866): the Invite field's parsing, attendees in
/// iCalendar both ways, the per-provider writes (Google's sendUpdates, Graph's attendees, the
/// iCloud PUT and its scheduling check), the iMIP message QuickMail sends when the server does
/// not, the editor's rules, and the calendar pane's announcements.
/// </summary>
public class EventAttendeesParsingTests
{
    [Fact]
    public void TryParse_SplitsOnSemicolonsAndCommas_AndKeepsNames()
    {
        Assert.True(EventAttendees.TryParse("ann@example.com; Bob Smith <bob@example.com>, carol@example.org",
                                            null, out var list, out _));
        Assert.Equal(["ann@example.com", "bob@example.com", "carol@example.org"], list.Select(a => a.Email));
        Assert.Equal("Bob Smith", list[1].Name);
        Assert.All(list, a => Assert.Equal(AttendeeResponse.NoReply, a.Response));
    }

    [Fact]
    public void TryParse_QuotedNameWithComma_IsOneEntry()
    {
        Assert.True(EventAttendees.TryParse("\"Doe, Jane\" <jane@example.com>; ann@example.com", null, out var list, out _));
        Assert.Equal(2, list.Count);
        Assert.Equal("Doe, Jane", list[0].Name);
        Assert.Equal("jane@example.com", list[0].Email);
    }

    [Fact]
    public void TryParse_DuplicateAddresses_CollapseToFirst_IgnoringCase()
    {
        Assert.True(EventAttendees.TryParse("Ann <ann@example.com>; ANN@example.com;", null, out var list, out _));
        var only = Assert.Single(list);
        Assert.Equal("Ann", only.Name);
    }

    [Fact]
    public void TryParse_KeepsEarlierResponses_ForPeopleStillInvited()
    {
        var before = new List<EventAttendee>
        {
            new() { Email = "ann@example.com", Name = "Ann", Response = AttendeeResponse.Accepted },
            new() { Email = "bob@example.com", Response = AttendeeResponse.Declined },
        };

        Assert.True(EventAttendees.TryParse("ann@example.com; dave@example.com", before, out var list, out _));

        Assert.Equal(AttendeeResponse.Accepted, list[0].Response);
        Assert.Equal("Ann", list[0].Name); // the name typed nowhere but known from before
        Assert.Equal(AttendeeResponse.NoReply, list[1].Response);
    }

    [Theory]
    [InlineData("ann@example.com; not an address", "not an address")]
    [InlineData("bob", "bob")]
    [InlineData("ann@example.com; @example.com", "@example.com")]
    public void TryParse_RejectsAnEntryThatIsNotAnAddress_AndNamesIt(string text, string expectedInvalid)
    {
        Assert.False(EventAttendees.TryParse(text, null, out var list, out var invalid));
        Assert.Empty(list);
        Assert.Equal(expectedInvalid, invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ")]
    public void TryParse_Blank_IsNobody(string text)
    {
        Assert.True(EventAttendees.TryParse(text, null, out var list, out _));
        Assert.Empty(list);
    }

    [Fact]
    public void Format_ReadsBackThroughTryParse()
    {
        var original = new List<EventAttendee>
        {
            new() { Email = "jane@example.com", Name = "Doe, Jane" },
            new() { Email = "ann@example.com" },
        };

        Assert.True(EventAttendees.TryParse(EventAttendees.Format(original), null, out var back, out _));

        Assert.Equal(original.Select(a => (a.Email, a.Name)), back.Select(a => (a.Email, a.Name)));
    }

    [Fact]
    public void Json_RoundTrips_AndBadJsonIsNobody()
    {
        var list = new List<EventAttendee>
        {
            new() { Email = "ann@example.com", Name = "Ann", Response = AttendeeResponse.Tentative },
        };
        var back = EventAttendees.FromJson(EventAttendees.ToJson(list));
        var a = Assert.Single(back);
        Assert.Equal(("ann@example.com", "Ann", AttendeeResponse.Tentative), (a.Email, a.Name, a.Response));

        Assert.Equal(string.Empty, EventAttendees.ToJson([]));
        Assert.Empty(EventAttendees.FromJson(""));
        Assert.Empty(EventAttendees.FromJson("{not json"));
    }

    [Fact]
    public void SameAddresses_IgnoresOrderAndCase()
    {
        var a = new List<EventAttendee> { new() { Email = "x@e.com" }, new() { Email = "y@e.com" } };
        var b = new List<EventAttendee> { new() { Email = "Y@E.com" }, new() { Email = "x@e.com" } };
        Assert.True(EventAttendees.SameAddresses(a, b));
        Assert.False(EventAttendees.SameAddresses(a, [b[0]]));
    }
}

public class CalendarEventOrganizerTests
{
    [Fact]
    public void IsOrganizedBy_NoOrganizer_IsTheUsers()
        => Assert.True(new CalendarEvent().IsOrganizedBy("me@example.com"));

    [Fact]
    public void IsOrganizedBy_MatchesAnyOwnAddress_IgnoringCase()
    {
        var evt = new CalendarEvent { Organizer = "Me@iCloud.com" };
        Assert.True(evt.IsOrganizedBy("me@example.com", "me@icloud.com"));
        Assert.False(evt.IsOrganizedBy("me@example.com", null));
    }

    [Fact]
    public void IsOrganizedBy_TheCalendarItself_CountsAsTheUser()
    {
        // Google names a secondary calendar's own events as organized by the calendar's id.
        var evt = new CalendarEvent { Organizer = "abc@group.calendar.google.com", CalendarId = "abc@group.calendar.google.com" };
        Assert.True(evt.IsOrganizedBy("me@gmail.com"));
    }

    [Fact]
    public void DetailText_ListsWhoIsInvited_WithTheirAnswers()
    {
        var evt = new CalendarEvent
        {
            Summary = "Planning",
            Attendees =
            [
                new() { Email = "ann@example.com", Name = "Ann", Response = AttendeeResponse.Accepted },
                new() { Email = "bob@example.com" },
            ],
        };
        Assert.Contains("Invited: Ann (accepted), bob@example.com (no reply)", evt.DetailText);
    }
}

public class IcsAttendeeTests
{
    private static CalendarEvent Meeting() => new()
    {
        Uid = "meet-1",
        Summary = "Planning",
        StartTimeTicks = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc).Ticks,
        Organizer = "me@icloud.com",
        OrganizerName = "Kelly Ford",
        Sequence = "2",
        Attendees =
        [
            new() { Email = "ann@example.com", Name = "Doe, Ann", Response = AttendeeResponse.Accepted },
            new() { Email = "bob@example.com" },
        ],
    };

    [Fact]
    public void Parse_ReadsAttendees_WithNamesAndAnswers_IncludingFoldedLines()
    {
        const string ics =
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Planning\r\n" +
            "ORGANIZER;CN=Kelly:mailto:me@icloud.com\r\n" +
            "ATTENDEE;CN=\"Doe, Ann\";PARTSTAT=ACCEPTED;ROLE=REQ-PARTICIPANT:mailto:ann@\r\n example.com\r\n" +
            "ATTENDEE;PARTSTAT=TENTATIVE:mailto:bob@example.com\r\n" +
            "ATTENDEE;PARTSTAT=DECLINED:MAILTO:carol@example.com\r\n" +
            "ATTENDEE:mailto:dave@example.com\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var model = Assert.Single(IcsModel.ParseAllEvents(ics));

        Assert.Equal(["ann@example.com", "bob@example.com", "carol@example.com", "dave@example.com"],
                     model.Attendees.Select(a => a.Email));
        Assert.Equal("Doe, Ann", model.Attendees[0].Name);
        Assert.Equal([AttendeeResponse.Accepted, AttendeeResponse.Tentative, AttendeeResponse.Declined, AttendeeResponse.NoReply],
                     model.Attendees.Select(a => a.Response));
    }

    [Fact]
    public void ToCalendarEvent_DropsTheOrganizer_WhoAppleListsAsAnAttendee()
    {
        const string ics =
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Planning\r\n" +
            "ORGANIZER;CN=Kelly:mailto:me@icloud.com\r\n" +
            "ATTENDEE;PARTSTAT=ACCEPTED:mailto:me@icloud.com\r\n" +
            "ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:ann@example.com\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var evt = Assert.Single(IcsModel.ParseAllEvents(ics)).ToCalendarEvent(Guid.NewGuid());

        Assert.Equal("ann@example.com", Assert.Single(evt.Attendees).Email);
        Assert.Equal("me@icloud.com", evt.Organizer);
    }

    [Fact]
    public void ExportEvent_WritesOrganizerAttendeesAndSequence_AndReadsBackTheSame()
    {
        var ics = IcsModel.ExportEvent(Meeting(), includeMethod: false);

        Assert.Contains("SEQUENCE:2", ics);
        Assert.Contains("ORGANIZER;CN=\"Kelly Ford\":mailto:me@icloud.com", ics);
        var back = Assert.Single(IcsModel.ParseAllEvents(ics)).ToCalendarEvent(Guid.Empty);
        Assert.Equal(Meeting().Attendees.Select(a => (a.Email, a.Name, a.Response)),
                     back.Attendees.Select(a => (a.Email, a.Name, a.Response)));
        Assert.Equal("me@icloud.com", back.Organizer);
        // The answer still owed is asked for; the one given is not.
        Assert.Contains("RSVP=TRUE", Unfold(ics).Split('\n').Single(l => l.Contains("bob@example.com")));
        Assert.DoesNotContain("RSVP", Unfold(ics).Split('\n').Single(l => l.Contains("ann@example.com")));
    }

    [Fact]
    public void ExportEvent_NobodyInvited_WritesNoOrganizer()
    {
        var evt = Meeting();
        evt.Attendees = [];
        var ics = IcsModel.ExportEvent(evt, includeMethod: false);
        Assert.DoesNotContain("ORGANIZER", ics);
        Assert.DoesNotContain("ATTENDEE", ics);
    }

    [Fact]
    public void ExportEvent_EveryLineFitsIn75Octets()
    {
        var evt = Meeting();
        evt.Attendees.Add(new EventAttendee
        {
            Email = "someone.with.a.very.long.address.indeed@a-rather-long-domain-name.example.com",
            Name = "Zoë Ångström-Øverlândsson, Department of Unnecessarily Long Display Names",
        });

        var ics = IcsModel.ExportEvent(evt, includeMethod: false);

        foreach (var line in ics.Split("\r\n").Where(l => l.Contains("ATTENDEE") || l.StartsWith(' ')))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"{Encoding.UTF8.GetByteCount(line)} octets: {line}");
        // And it unfolds back to what was written, accents intact.
        var back = Assert.Single(IcsModel.ParseAllEvents(ics)).ToCalendarEvent(Guid.Empty);
        Assert.Contains(back.Attendees, a => a.Name.StartsWith("Zoë Ångström", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendFolded_NeverSplitsACharacter()
    {
        var sb = new StringBuilder();
        var line = "X:" + new string('é', 100); // two octets each
        IcsModel.AppendFolded(sb, line);
        var unfolded = Unfold(sb.ToString()).TrimEnd('\r', '\n');
        Assert.Equal(line, unfolded);
        Assert.All(sb.ToString().Split(Environment.NewLine).Where(l => l.Length > 0),
                   l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
    }

    [Fact]
    public void ExportScheduling_Request_HasMethodAndEveryAttendee()
    {
        var ics = IcsModel.ExportScheduling(Meeting(), "REQUEST", Meeting().Attendees);
        Assert.Contains("METHOD:REQUEST", ics);
        Assert.DoesNotContain("STATUS:CANCELLED", ics);
        Assert.Contains("ann@example.com", Unfold(ics));
        Assert.Contains("bob@example.com", Unfold(ics));
    }

    [Fact]
    public void ExportScheduling_UninvitingSomeone_NamesOnlyThem_AndLeavesTheMeetingStanding()
    {
        var bob = Meeting().Attendees[1];
        var ics = Unfold(IcsModel.ExportScheduling(Meeting(), "CANCEL", [bob]));
        Assert.Contains("METHOD:CANCEL", ics);
        Assert.DoesNotContain("STATUS:CANCELLED", ics); // RFC 5546 §3.2.5: the meeting goes ahead
        Assert.Contains("bob@example.com", ics);
        Assert.DoesNotContain("ann@example.com", ics);
        Assert.DoesNotContain("RSVP", ics);
    }

    [Fact]
    public void ExportScheduling_CancellingTheMeeting_IsStatusCancelled()
    {
        var ics = IcsModel.ExportScheduling(Meeting(), "CANCEL", Meeting().Attendees, wholeEvent: true);
        Assert.Contains("STATUS:CANCELLED", ics);
    }

    [Theory]
    [InlineData("Team: Ops")]
    [InlineData("Doe; Jane")]
    [InlineData("Doe, Jane")]
    public void ExportEvent_NamesWithPunctuation_ReadBackIntact(string name)
    {
        var evt = Meeting();
        evt.Attendees = [new() { Email = "ops@example.com", Name = name }];

        var back = Assert.Single(IcsModel.ParseAllEvents(IcsModel.ExportEvent(evt, includeMethod: false)))
            .ToCalendarEvent(Guid.Empty);

        var a = Assert.Single(back.Attendees);
        Assert.Equal(("ops@example.com", name), (a.Email, a.Name));
    }

    [Fact]
    public void Parse_AddressInTheEmailParameter_WhenTheValueIsNotMailto()
    {
        const string ics =
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Planning\r\n" +
            "ORGANIZER;CN=Kelly;EMAIL=me@icloud.com:urn:uuid:1234\r\n" +
            "ATTENDEE;EMAIL=ann@example.com:/123456/principal/\r\n" +
            "ATTENDEE:urn:uuid:9999\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var model = Assert.Single(IcsModel.ParseAllEvents(ics));

        Assert.Equal("me@icloud.com", model.Organizer);
        Assert.Equal("ann@example.com", Assert.Single(model.Attendees).Email); // the bare URN is not an address
    }

    [Fact]
    public void RoomsAndOptionalGuests_RoundTripThroughIcs()
    {
        var evt = Meeting();
        evt.Attendees =
        [
            new() { Email = "ann@example.com", Kind = AttendeeKind.Optional },
            new() { Email = "room4@example.com", Name = "Room 4", Kind = AttendeeKind.Resource },
        ];
        var ics = IcsModel.ExportEvent(evt, includeMethod: false);
        Assert.Contains("CUTYPE=ROOM", ics);

        var back = Assert.Single(IcsModel.ParseAllEvents(ics)).ToCalendarEvent(Guid.Empty);
        Assert.Equal([AttendeeKind.Optional, AttendeeKind.Resource], back.Attendees.Select(a => a.Kind));
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("", "1")]
    [InlineData("junk", "1")]
    [InlineData("0", "1")]
    [InlineData("7", "8")]
    public void NextSequence_CountsUp(string? from, string expected)
        => Assert.Equal(expected, IcsModel.NextSequence(from));

    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty).Replace("\n ", string.Empty);
}

public class CalDavSchedulingParsingTests
{
    [Theory]
    [InlineData("1, 2, 3, access-control, calendar-access, calendar-auto-schedule, calendar-managed-attachments", true)]
    [InlineData("1,3,calendar-access", false)]
    [InlineData("calendar-auto-scheduled", false)] // a token, not a substring
    [InlineData(null, false)]
    public void AdvertisesAutoSchedule_ReadsTheDavHeader(string? header, bool expected)
        => Assert.Equal(expected, CalDavCalendarClient.AdvertisesAutoSchedule(header));

    [Fact]
    public void ParseUserAddresses_KeepsMailtoHrefsInOrder()
    {
        const string xml =
            """
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response><d:href>/123/principal/</d:href><d:propstat><d:prop>
                <c:calendar-user-address-set>
                  <d:href>urn:uuid:123</d:href>
                  <d:href>mailto:me@icloud.com</d:href>
                  <d:href>MAILTO:me@me.com</d:href>
                  <d:href>/123/principal/</d:href>
                </c:calendar-user-address-set>
              </d:prop></d:propstat></d:response>
            </d:multistatus>
            """;
        Assert.Equal(["me@icloud.com", "me@me.com"], CalDavCalendarClient.ParseUserAddresses(xml));
    }

    [Fact]
    public void ChooseOrganizerAddress_PrefersTheMailAddress_ThenTheAppleId_ThenTheServersFirst()
    {
        var account = new AccountModel { Username = "me@icloud.com", LoginUsername = "me@gmail.com" };
        // The server's own spelling of the address, since that is what it matches the organizer to.
        Assert.Equal("ME@icloud.com", GraphCalendarSyncService.ChooseOrganizerAddress(account, ["me@gmail.com", "ME@icloud.com"]));
        Assert.Equal("me@gmail.com", GraphCalendarSyncService.ChooseOrganizerAddress(account, ["x@me.com", "me@gmail.com"]));
        Assert.Equal("x@me.com", GraphCalendarSyncService.ChooseOrganizerAddress(account, ["x@me.com"]));
        Assert.Equal("me@icloud.com", GraphCalendarSyncService.ChooseOrganizerAddress(account, []));
    }
}

public class ProviderAttendeeMappingTests
{
    private static CalendarEvent Edited(bool changed) => new()
    {
        Summary = "Planning",
        StartTimeTicks = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc).Ticks,
        Attendees = [new() { Email = "ann@example.com", Name = "Ann", Response = AttendeeResponse.Accepted }],
        AttendeesChanged = changed,
    };

    [Fact]
    public void GoogleBody_SendsAttendeesOnlyWhenChanged()
    {
        Assert.Null(GraphCalendarSyncService.BuildGoogleWriteBody(Edited(changed: false)).Attendees);

        var sent = Assert.Single(GraphCalendarSyncService.BuildGoogleWriteBody(Edited(changed: true)).Attendees!);
        Assert.Equal(("ann@example.com", "Ann", "accepted"), (sent.Email, sent.DisplayName, sent.ResponseStatus));
    }

    [Fact]
    public void GoogleBody_EveryoneRemoved_SendsAnEmptyList_NotNull()
    {
        var evt = Edited(changed: true);
        evt.Attendees = [];
        var json = JsonSerializer.Serialize(GraphCalendarSyncService.BuildGoogleWriteBody(evt),
                                            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.Contains("\"attendees\":[]", json);
    }

    [Fact]
    public void GraphBody_SendsRequiredAttendeesOnlyWhenChanged()
    {
        Assert.Null(GraphCalendarSyncService.BuildCreateBody(Edited(changed: false)).Attendees);

        var sent = Assert.Single(GraphCalendarSyncService.BuildCreateBody(Edited(changed: true)).Attendees!);
        Assert.Equal("ann@example.com", sent.EmailAddress!.Address);
        Assert.Equal("required", sent.Type);
        Assert.Null(sent.Status); // read-only on Graph; never written
    }

    [Fact]
    public void MapGoogleEvent_ListsInviteesWithAnswers_LeavingOutTheOrganizer()
    {
        var e = new GoogleCalendarEvent
        {
            Id = "g1",
            Start = new GoogleEventTime { DateTime = "2026-11-03T17:00:00Z" },
            End = new GoogleEventTime { DateTime = "2026-11-03T18:00:00Z" },
            Organizer = new GoogleEventOrganizer { Email = "me@gmail.com", Self = true },
            Attendees =
            [
                new() { Email = "me@gmail.com", Organizer = true, Self = true, ResponseStatus = "accepted" },
                new() { Email = "ann@example.com", DisplayName = "Ann", ResponseStatus = "tentative" },
                new() { Email = "bob@example.com", ResponseStatus = "needsAction" },
            ],
        };

        var evt = GraphCalendarSyncService.MapGoogleEvent(e, Guid.NewGuid());

        Assert.Equal(["ann@example.com", "bob@example.com"], evt.Attendees.Select(a => a.Email));
        Assert.Equal([AttendeeResponse.Tentative, AttendeeResponse.NoReply], evt.Attendees.Select(a => a.Response));
        Assert.Equal("Ann", evt.Attendees[0].Name);
        Assert.True(evt.UserIsOrganizer);
    }

    [Fact]
    public void MapGraphEvent_ListsPeopleWithAnswers_AndKeepsRoomsMarkedAsRooms()
    {
        var e = new GraphCalendarEvent
        {
            Id = "m1",
            Start = new GraphDateTimeTimeZone { DateTime = "2026-11-03T17:00:00", TimeZone = "UTC" },
            End = new GraphDateTimeTimeZone { DateTime = "2026-11-03T18:00:00", TimeZone = "UTC" },
            IsOrganizer = true,
            Attendees =
            [
                new() { EmailAddress = new GraphEmailAddress { Address = "ann@example.com", Name = "Ann" }, Type = "required",
                        Status = new GraphResponseStatus { Response = "accepted" } },
                new() { EmailAddress = new GraphEmailAddress { Address = "bob@example.com" }, Type = "optional",
                        Status = new GraphResponseStatus { Response = "tentativelyAccepted" } },
                new() { EmailAddress = new GraphEmailAddress { Address = "room1@contoso.com" }, Type = "resource" },
            ],
        };

        var evt = GraphCalendarSyncService.MapEvent(e, Guid.NewGuid());

        Assert.Equal(["ann@example.com", "bob@example.com", "room1@contoso.com"], evt.Attendees.Select(a => a.Email));
        Assert.Equal([AttendeeResponse.Accepted, AttendeeResponse.Tentative, AttendeeResponse.NoReply],
                     evt.Attendees.Select(a => a.Response));
        Assert.Equal([AttendeeKind.Required, AttendeeKind.Optional, AttendeeKind.Resource], evt.Attendees.Select(a => a.Kind));
    }

    [Fact]
    public void MapGraphEvent_TakesGraphsWordForWhoOrganizes_AndKeepsRoomsAndOptionalGuests()
    {
        var e = new GraphCalendarEvent
        {
            Id = "m2",
            Start = new GraphDateTimeTimeZone { DateTime = "2026-11-03T17:00:00", TimeZone = "UTC" },
            End = new GraphDateTimeTimeZone { DateTime = "2026-11-03T18:00:00", TimeZone = "UTC" },
            IsOrganizer = true,
            // A personal Microsoft account that signs in with a Gmail address organizes as an alias.
            Organizer = new GraphRecipient { EmailAddress = new GraphEmailAddress { Address = "outlook_ABC@outlook.com" } },
            Attendees =
            [
                new() { EmailAddress = new GraphEmailAddress { Address = "ann@example.com" }, Type = "optional" },
                new() { EmailAddress = new GraphEmailAddress { Address = "room1@contoso.com" }, Type = "resource" },
            ],
        };

        var evt = GraphCalendarSyncService.MapEvent(e, Guid.NewGuid());

        Assert.True(evt.IsOrganizedBy("me@gmail.com"));
        Assert.Equal([AttendeeKind.Optional, AttendeeKind.Resource], evt.Attendees.Select(a => a.Kind));

        // Adding someone sends the room back as a room and the optional guest as optional.
        evt.Attendees.Add(new EventAttendee { Email = "bob@example.com" });
        evt.AttendeesChanged = true;
        var body = GraphCalendarSyncService.BuildCreateBody(evt).Attendees!;
        Assert.Equal(["optional", "resource", "required"], body.Select(a => a.Type));
    }

    [Fact]
    public void GoogleBody_KeepsRoomsAndOptionalGuests()
    {
        var evt = Edited(changed: true);
        evt.Attendees =
        [
            new() { Email = "ann@example.com", Kind = AttendeeKind.Optional },
            new() { Email = "room@resource.calendar.google.com", Kind = AttendeeKind.Resource },
            new() { Email = "bob@example.com" },
        ];
        var body = GraphCalendarSyncService.BuildGoogleWriteBody(evt).Attendees!;
        Assert.Equal([(true, (bool?)null), (null, true), (null, null)], body.Select(a => (a.Optional, a.Resource)));
    }

    [Fact]
    public void NotifiesAttendees_OnlyForTheOrganizer_AndOnlyWhenSomeoneIsOrWasInvited()
    {
        var me = new AccountModel { Username = "me@example.com" };
        Assert.True(GraphCalendarSyncService.NotifiesAttendees(me, Edited(changed: true)));
        Assert.False(GraphCalendarSyncService.NotifiesAttendees(me, new CalendarEvent()));
        Assert.True(GraphCalendarSyncService.NotifiesAttendees(me,
            new CalendarEvent { PreviousAttendees = [new() { Email = "gone@example.com" }] }));
        var theirs = Edited(changed: false);
        theirs.Organizer = "boss@example.com";
        Assert.False(GraphCalendarSyncService.NotifiesAttendees(me, theirs));
    }
}

/// <summary>The writes as they reach each provider's HTTP API.</summary>
public class ProviderInvitationWriteTests : IDisposable
{
    private const string Collection = "https://p42-caldav.icloud.com/123456/calendars/home/";
    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _accountId = Guid.NewGuid();

    public ProviderInvitationWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-invite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed record Seen(string Method, string Url, string Body);

    /// <summary>Answers by what is asked rather than in order, since a create's discovery and
    /// scheduling round trips come before its PUT.</summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Xml(string xml) => new((HttpStatusCode)207)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private const string PrincipalXml =
        """<d:multistatus xmlns:d="DAV:"><d:response><d:href>/</d:href><d:propstat><d:prop><d:current-user-principal><d:href>/123456/principal/</d:href></d:current-user-principal></d:prop></d:propstat></d:response></d:multistatus>""";
    private const string HomeSetXml =
        """<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/123456/principal/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>https://p42-caldav.icloud.com/123456/calendars/</d:href></c:calendar-home-set></d:prop></d:propstat></d:response></d:multistatus>""";
    private const string CollectionsXml =
        """<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/123456/calendars/home/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>Home</d:displayname><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set></d:prop></d:propstat></d:response></d:multistatus>""";
    private const string AddressSetXml =
        """<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/123456/principal/</d:href><d:propstat><d:prop><c:calendar-user-address-set><d:href>mailto:me@icloud.com</d:href><d:href>mailto:me@me.com</d:href></c:calendar-user-address-set></d:prop></d:propstat></d:response></d:multistatus>""";

    /// <summary>An iCloud server; <paramref name="davHeader"/> is what its OPTIONS answers with.</summary>
    private static RoutingHandler ICloud(string? davHeader) => new((req, body) => ICloudRespond(req, body, davHeader));

    private static HttpResponseMessage ICloudRespond(HttpRequestMessage req, string body, string? davHeader)
    {
        if (req.Method == HttpMethod.Options)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            if (davHeader != null) resp.Headers.TryAddWithoutValidation("DAV", davHeader);
            return resp;
        }
        if (req.Method.Method == "PROPFIND")
        {
            if (body.Contains("current-user-principal")) return Xml(PrincipalXml);
            if (body.Contains("calendar-home-set")) return Xml(HomeSetXml);
            if (body.Contains("calendar-user-address-set")) return Xml(AddressSetXml);
            return Xml(CollectionsXml);
        }
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private sealed class Credentials(Guid id, string password) : ICredentialService
    {
        public void SavePassword(Guid accountId, string pw) { }
        public string? GetPassword(Guid accountId) => accountId == id ? password : null;
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    private sealed class Accounts(AccountModel account) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [account];
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private AccountModel ICloudAccount() => new()
    {
        Id = _accountId, AuthType = AuthType.Password, BackendKind = BackendKind.ImapSmtp,
        ImapHost = "imap.mail.me.com", Username = "me@icloud.com", DisplayName = "Kelly Ford", SyncCalendar = true,
    };

    private AccountModel GoogleAccount() => new()
    {
        Id = _accountId, AuthType = AuthType.OAuth2Google, BackendKind = BackendKind.ImapSmtp,
        Username = "me@gmail.com", SyncCalendar = true,
    };

    private GraphCalendarSyncService ICloudService(RoutingHandler handler) =>
        new(new Accounts(ICloudAccount()), _store,
            new GraphClient(new StubOAuthService(), new HttpClient(new RoutingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))), defaultRetryDelay: TimeSpan.Zero),
            google: null,
            calDav: new CalDavCalendarClient(new HttpClient(handler)),
            credentials: new Credentials(_accountId, "app-pass"));

    private GraphCalendarSyncService GoogleService(RoutingHandler handler) =>
        new(new Accounts(GoogleAccount()), _store,
            new GraphClient(new StubOAuthService(), new HttpClient(new RoutingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))), defaultRetryDelay: TimeSpan.Zero),
            new GoogleCalendarClient(new StubGoogleOAuthService(), new HttpClient(handler)));

    private static CalendarEvent NewMeeting(string calendarId = Collection) => new()
    {
        Uid = "meet-1",
        CalendarId = calendarId,
        Summary = "Planning",
        StartTimeTicks = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc).Ticks,
        Attendees = [new() { Email = "ann@example.com", Name = "Ann" }],
        AttendeesChanged = true,
    };

    [Fact]
    public async Task ICloudCreate_PutsTheMeeting_OrganizedAsTheServersAddressForTheUser()
    {
        var handler = ICloud("1, calendar-access, calendar-auto-schedule");
        var svc = ICloudService(handler);

        var created = await svc.CreateEventAsync(ICloudAccount(), NewMeeting(), TestContext.Current.CancellationToken);

        var put = Assert.Single(handler.Requests, r => r.Method == "PUT");
        var ics = put.Body.Replace("\r\n ", string.Empty);
        Assert.Contains("ORGANIZER;CN=\"Kelly Ford\":mailto:me@icloud.com", ics);
        Assert.Contains("mailto:ann@example.com", ics);
        Assert.Equal("me@icloud.com", created.Organizer);
        // Stored with the attendees, so the details pane and the next edit see them.
        var stored = Assert.Single(await _store.LoadCalendarEventsAsync());
        Assert.Equal("ann@example.com", Assert.Single(stored.Attendees).Email);
    }

    [Theory]
    [InlineData("1, calendar-access, calendar-auto-schedule", true)]
    [InlineData("1, calendar-access", false)]
    [InlineData(null, false)]
    public async Task ICloud_ServerSendsInvitations_IsWhatTheServerAdvertises(string? davHeader, bool expected)
    {
        var svc = ICloudService(ICloud(davHeader));
        Assert.Equal(expected, await svc.ServerSendsInvitationsAsync(ICloudAccount(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ICloud_SchedulingCheckThatFails_ReadsAsFalse_AndIsNotRemembered()
    {
        var fail = true;
        var handler = new RoutingHandler((req, body) => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : ICloudRespond(req, body, "calendar-auto-schedule"));
        var svc = ICloudService(handler);

        Assert.False(await svc.ServerSendsInvitationsAsync(ICloudAccount(), TestContext.Current.CancellationToken));
        fail = false;
        Assert.True(await svc.ServerSendsInvitationsAsync(ICloudAccount(), TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task ICloudUpdate_OfTheUsersMeeting_BumpsTheSequence()
    {
        var handler = ICloud("calendar-auto-schedule");
        var svc = ICloudService(handler);
        var evt = NewMeeting();
        evt.Organizer = "me@icloud.com";
        evt.Sequence = "3";
        evt.ResourceUrl = Collection + "RANDOM-NAME.ics";

        await svc.UpdateEventAsync(ICloudAccount(), evt, TestContext.Current.CancellationToken);

        var put = Assert.Single(handler.Requests, r => r.Method == "PUT");
        Assert.Contains("SEQUENCE:4", put.Body);
        Assert.Equal(Collection + "RANDOM-NAME.ics", put.Url);
    }

    [Fact]
    public async Task ICloudUpdate_OfSomeoneElsesMeeting_KeepsTheirSequenceAndGuestList()
    {
        var handler = ICloud("calendar-auto-schedule");
        var svc = ICloudService(handler);
        var evt = NewMeeting();
        evt.Organizer = "boss@example.com";
        evt.Sequence = "3";
        evt.AttendeesChanged = false;

        await svc.UpdateEventAsync(ICloudAccount(), evt, TestContext.Current.CancellationToken);

        var put = Assert.Single(handler.Requests, r => r.Method == "PUT").Body.Replace("\r\n ", string.Empty);
        Assert.Contains("SEQUENCE:3", put);
        Assert.Contains("ORGANIZER:mailto:boss@example.com", put);
        Assert.Contains("mailto:ann@example.com", put); // the attendees survive the round trip
    }

    [Fact]
    public async Task GoogleCreate_WithAttendees_AsksGoogleToSendTheInvitations()
    {
        var handler = new RoutingHandler((req, _) => Json(req.Method == HttpMethod.Get
            ? """{ "items": [] }"""
            : """{ "id": "g1", "start": { "dateTime": "2026-11-03T17:00:00Z" }, "end": { "dateTime": "2026-11-03T18:00:00Z" }, "attendees": [ { "email": "ann@example.com", "responseStatus": "needsAction" } ] }"""));
        var svc = GoogleService(handler);

        await svc.CreateEventAsync(GoogleAccount(), NewMeeting(calendarId: "primary"), TestContext.Current.CancellationToken);

        var post = Assert.Single(handler.Requests, r => r.Method == "POST");
        Assert.EndsWith("/events?sendUpdates=all", post.Url);
        Assert.Contains("\"attendees\":[{\"email\":\"ann@example.com\"", post.Body);
    }

    [Fact]
    public async Task GoogleCreate_NobodyInvited_SendsNothing()
    {
        var handler = new RoutingHandler((req, _) => Json(req.Method == HttpMethod.Get
            ? """{ "items": [] }"""
            : """{ "id": "g1", "start": { "dateTime": "2026-11-03T17:00:00Z" }, "end": { "dateTime": "2026-11-03T18:00:00Z" } }"""));
        var svc = GoogleService(handler);
        var evt = NewMeeting(calendarId: "primary");
        evt.Attendees = [];
        evt.AttendeesChanged = false;

        await svc.CreateEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        var post = Assert.Single(handler.Requests, r => r.Method == "POST");
        Assert.DoesNotContain("sendUpdates", post.Url);
        Assert.DoesNotContain("attendees", post.Body);
    }

    [Fact]
    public async Task GoogleUpdate_OfTheUsersMeeting_SendsTheUpdate()
    {
        var handler = new RoutingHandler((_, _) => Json("""{ "id": "g1", "start": { "dateTime": "2026-11-03T17:00:00Z" }, "end": { "dateTime": "2026-11-03T18:00:00Z" } }"""));
        var svc = GoogleService(handler);
        var evt = NewMeeting(calendarId: "primary");
        evt.Uid = "g1";
        evt.AttendeesChanged = false;

        await svc.UpdateEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        var patch = Assert.Single(handler.Requests);
        Assert.Equal("PATCH", patch.Method);
        Assert.EndsWith("/events/g1?sendUpdates=all", patch.Url);
        Assert.DoesNotContain("attendees", patch.Body); // unchanged list: the server's is left alone
    }

    [Fact]
    public async Task ICloudSync_RecordsWhoOrganizesEachMeeting_FromTheServersAddresses()
    {
        static string Vevent(string uid, string organizer) => string.Join("\n",
            "BEGIN:VCALENDAR", "BEGIN:VEVENT", $"UID:{uid}", "SUMMARY:Meeting",
            "DTSTART:20261103T170000Z", "DTEND:20261103T180000Z",
            $"ORGANIZER:mailto:{organizer}", "ATTENDEE:mailto:ann@example.com",
            "END:VEVENT", "END:VCALENDAR");
        var report =
            "<d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
            + "<d:response><d:href>/123456/calendars/home/1.ics</d:href><d:propstat><d:prop><c:calendar-data>"
            + Vevent("mine", "me@me.com") // an alias the server lists, not the account's own address
            + "</c:calendar-data></d:prop></d:propstat></d:response>"
            + "<d:response><d:href>/123456/calendars/home/2.ics</d:href><d:propstat><d:prop><c:calendar-data>"
            + Vevent("theirs", "boss@example.com")
            + "</c:calendar-data></d:prop></d:propstat></d:response></d:multistatus>";
        var handler = new RoutingHandler((req, body) => req.Method.Method == "REPORT"
            ? Xml(report)
            : ICloudRespond(req, body, "calendar-auto-schedule"));
        var svc = ICloudService(handler);

        await svc.SyncAccountCalendarAsync(ICloudAccount(), TestContext.Current.CancellationToken);

        var rows = (await _store.LoadCalendarEventsAsync()).ToDictionary(e => e.Uid);
        Assert.True(rows["mine"].UserIsOrganizer);
        Assert.True(rows["mine"].IsOrganizedBy("me@icloud.com"));
        Assert.False(rows["theirs"].UserIsOrganizer);
    }

    [Fact]
    public async Task GoogleDelete_OfTheUsersMeeting_SendsCancellations()
    {
        var handler = new RoutingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var svc = GoogleService(handler);
        var evt = NewMeeting(calendarId: "primary");
        evt.Uid = "g1";

        await svc.DeleteEventAsync(GoogleAccount(), evt, TestContext.Current.CancellationToken);

        Assert.EndsWith("/events/g1?sendUpdates=all", Assert.Single(handler.Requests).Url);
    }
}

public class CalendarInvitationServiceTests
{
    private static readonly AccountModel Me = new()
    {
        Id = Guid.NewGuid(), Username = "me@icloud.com", DisplayName = "Kelly Ford",
        ImapHost = "imap.mail.me.com", SyncCalendar = true,
    };

    private sealed class Credentials : ICredentialService
    {
        public void SavePassword(Guid accountId, string pw) { }
        public string? GetPassword(Guid accountId) => "app-pass";
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    private static (CalendarInvitationService Svc, StubGraphCalendarSyncService Sync, StubSmtpService Smtp) Make(bool serverSends)
    {
        var sync = new StubGraphCalendarSyncService { ServerSendsInvitations = serverSends };
        var smtp = new StubSmtpService();
        return (new CalendarInvitationService(sync, smtp, new Credentials()), sync, smtp);
    }

    private static CalendarEvent Meeting() => new()
    {
        Uid = "meet-1",
        Summary = "Planning",
        StartTimeTicks = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc).Ticks,
        Organizer = "me@icloud.com",
        OrganizerName = "Kelly Ford",
        Attendees = [new() { Email = "ann@example.com", Name = "Ann" }, new() { Email = "bob@example.com" }],
    };

    private static string CalendarPart(MimeMessage message)
    {
        var part = message.BodyParts.OfType<TextPart>().Single(p => p.ContentType.MediaSubtype == "calendar");
        return part.Text;
    }

    [Fact]
    public async Task ServerThatSends_NothingEmailed_ButTheOutcomeCountsWhoWasInvited()
    {
        var (svc, _, smtp) = Make(serverSends: true);

        var outcome = await svc.AfterSaveAsync(Me, Meeting(), isNew: true, TestContext.Current.CancellationToken);

        Assert.Empty(smtp.SentPrepared);
        Assert.Equal(new InvitationOutcome(2, 0, 0), outcome);
    }

    [Fact]
    public async Task ServerThatDoesNot_EmailsOneInvitation_ToEveryone()
    {
        var (svc, _, smtp) = Make(serverSends: false);

        var outcome = await svc.AfterSaveAsync(Me, Meeting(), isNew: true, TestContext.Current.CancellationToken);

        var (message, account, password) = Assert.Single(smtp.SentPrepared);
        Assert.Same(Me, account);
        Assert.Equal("app-pass", password);
        Assert.Equal(["ann@example.com", "bob@example.com"], message.To.Mailboxes.Select(m => m.Address));
        Assert.StartsWith("Invitation: Planning, ", message.Subject);
        Assert.Contains("METHOD:REQUEST", CalendarPart(message));
        Assert.Equal(new InvitationOutcome(2, 0, 0), outcome);
    }

    [Fact]
    public async Task Edit_InvitesTheAdded_UpdatesTheKept_AndCancelsForTheRemoved()
    {
        var (svc, _, smtp) = Make(serverSends: false);
        var edited = Meeting(); // Ann and Bob
        edited.PreviousAttendees = [new() { Email = "ann@example.com" }, new() { Email = "carol@example.com" }];

        var outcome = await svc.AfterSaveAsync(Me, edited, isNew: false, TestContext.Current.CancellationToken);

        Assert.Equal(3, smtp.SentPrepared.Count);
        var invite = smtp.SentPrepared[0].Message;
        Assert.StartsWith("Invitation: ", invite.Subject);
        Assert.Equal("bob@example.com", Assert.Single(invite.To.Mailboxes).Address);
        var update = smtp.SentPrepared[1].Message;
        Assert.StartsWith("Updated invitation: ", update.Subject);
        Assert.Equal("ann@example.com", Assert.Single(update.To.Mailboxes).Address);
        // Both copies of the meeting list everyone invited, as each attendee's copy should.
        Assert.Contains("bob@example.com", CalendarPart(update).Replace("\r\n ", string.Empty));
        var cancel = smtp.SentPrepared[2].Message;
        Assert.StartsWith("Cancelled: ", cancel.Subject);
        Assert.Equal("carol@example.com", Assert.Single(cancel.To.Mailboxes).Address);
        Assert.Contains("METHOD:CANCEL", CalendarPart(cancel));
        Assert.DoesNotContain("STATUS:CANCELLED", CalendarPart(cancel));
        Assert.Equal(new InvitationOutcome(1, 1, 1), outcome);
    }

    [Fact]
    public async Task RoomsAreNeitherEmailedNorCounted()
    {
        var (svc, _, smtp) = Make(serverSends: false);
        var meeting = Meeting();
        meeting.Attendees.Add(new EventAttendee { Email = "room4@example.com", Kind = AttendeeKind.Resource });

        var outcome = await svc.AfterSaveAsync(Me, meeting, isNew: true, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Assert.Single(smtp.SentPrepared).Message.To.Mailboxes, m => m.Address == "room4@example.com");
        Assert.Equal(new InvitationOutcome(2, 0, 0), outcome);
    }

    [Fact]
    public async Task AMeetingWithNoRecordedOrganizer_IsSentAsTheAccount()
    {
        var (svc, _, smtp) = Make(serverSends: false);
        var meeting = Meeting();
        meeting.Organizer = string.Empty;
        meeting.OrganizerName = string.Empty;

        await svc.AfterDeleteAsync(Me, meeting, TestContext.Current.CancellationToken);

        Assert.Contains("ORGANIZER;CN=\"Kelly Ford\":mailto:me@icloud.com", CalendarPart(Assert.Single(smtp.SentPrepared).Message));
    }

    [Fact]
    public async Task AStalledMailServer_IsReportedAsUnsent_NotWaitedOnForever()
    {
        var (svc, _, _) = Make(serverSends: false);
        var hanging = new HangingSmtp();
        svc = new CalendarInvitationService(new StubGraphCalendarSyncService { ServerSendsInvitations = false },
                                            hanging, new Credentials());
        var before = CalendarInvitationService.SendTimeout;
        CalendarInvitationService.SendTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var outcome = await svc.AfterSaveAsync(Me, Meeting(), isNew: true, TestContext.Current.CancellationToken);
            Assert.Equal("the mail server did not answer in time.", outcome.Error);
        }
        finally
        {
            CalendarInvitationService.SendTimeout = before;
        }
    }

    private sealed class HangingSmtp : ISendMailService
    {
        public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password, string organizerEmail, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPreparedAsync(MimeMessage message, AccountModel account, string? password, CancellationToken ct = default)
            => Task.Delay(Timeout.Infinite, ct);
        public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Delete_EmailsACancellation_NewerThanTheInvitation()
    {
        var (svc, _, smtp) = Make(serverSends: false);
        var meeting = Meeting();
        meeting.Sequence = "1";

        var outcome = await svc.AfterDeleteAsync(Me, meeting, TestContext.Current.CancellationToken);

        var message = Assert.Single(smtp.SentPrepared).Message;
        var ics = CalendarPart(message);
        Assert.Contains("METHOD:CANCEL", ics);
        Assert.Contains("STATUS:CANCELLED", ics);
        Assert.Contains("SEQUENCE:2", ics);
        Assert.Equal(new InvitationOutcome(0, 0, 2), outcome);
    }

    [Fact]
    public async Task SomeoneElsesMeeting_NobodyIsTold()
    {
        var (svc, _, smtp) = Make(serverSends: false);
        var theirs = Meeting();
        theirs.Organizer = "boss@example.com";

        Assert.True((await svc.AfterSaveAsync(Me, theirs, isNew: false, TestContext.Current.CancellationToken)).IsNone);
        Assert.True((await svc.AfterDeleteAsync(Me, theirs, TestContext.Current.CancellationToken)).IsNone);
        Assert.Empty(smtp.SentPrepared);
    }

    [Fact]
    public async Task SendFailure_IsReported_NotThrown()
    {
        var (svc, _, smtp) = Make(serverSends: false);
        smtp.SendFailure = new InvalidOperationException("535 Authentication failed");

        var outcome = await svc.AfterSaveAsync(Me, Meeting(), isNew: true, TestContext.Current.CancellationToken);

        Assert.Equal("535 Authentication failed", outcome.Error);
    }

    [Fact]
    public void PlainText_SaysWhatWhenWhereAndWho()
    {
        var meeting = Meeting();
        meeting.Location = "Room 4";
        meeting.Description = "Bring the budget.";

        var text = CalendarInvitationService.PlainText(meeting, "REQUEST");

        Assert.StartsWith("Planning\n", text);
        Assert.Contains("When: ", text);
        Assert.Contains("Location: Room 4", text);
        Assert.Contains("Organizer: Kelly Ford", text);
        Assert.Contains("Invited: Ann, bob@example.com", text);
        Assert.Contains("Bring the budget.", text);
        Assert.StartsWith("This meeting has been cancelled.", CalendarInvitationService.PlainText(meeting, "CANCEL"));
    }

    [Fact]
    public void BuildInvitation_IsAnAlternativeOfTextAndCalendar_WithTheMethodOnTheCalendarPart()
    {
        var message = MimeMessageBuilder.BuildInvitation(Me, "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", "request",
                                                         "Invitation: x", "x", Meeting().Attendees);

        var alt = Assert.IsType<MultipartAlternative>(message.Body);
        Assert.Equal(["text/plain", "text/calendar"], alt.Select(p => p.ContentType.MimeType));
        Assert.Equal("REQUEST", alt[1].ContentType.Parameters["method"]);
        Assert.Equal("me@icloud.com", Assert.Single(message.From.Mailboxes).Address);
        Assert.False(string.IsNullOrEmpty(message.MessageId));
        Assert.Equal("Ann", message.To.Mailboxes.First().Name);
    }
}

public class EventEditorInviteTests
{
    private static readonly Guid Apple = Guid.NewGuid();

    private static EventEditorViewModel NewEditor()
        => new(new DateTime(2026, 11, 3, 9, 0, 0),
               [new CalendarSaveTarget("Apple: Home", Apple, "https://cal/home/", "Home")]);

    [Fact]
    public void New_WithInvitees_OnAnAccountCalendar_CarriesThem()
    {
        var editor = NewEditor();
        editor.Title = "Planning";
        editor.SelectedTargetIndex = 1;
        editor.InviteText = "Ann <ann@example.com>; bob@example.com";

        Assert.True(editor.TryBuildEvent(out var evt, out var error), error);
        Assert.Equal(["ann@example.com", "bob@example.com"], evt.Attendees.Select(a => a.Email));
        Assert.True(evt.AttendeesChanged);
        Assert.Empty(evt.PreviousAttendees);
    }

    [Fact]
    public void New_WithInvitees_OnLocalCalendar_IsRefused_PointingAtTheCalendarPicker()
    {
        var editor = NewEditor();
        editor.Title = "Planning";
        editor.InviteText = "ann@example.com";
        EditorField? focused = null;
        editor.FieldFocusRequested += f => focused = f;

        editor.SaveCommand.Execute(null);

        Assert.Contains("online calendar", editor.ErrorText);
        Assert.Equal(EditorField.SaveTarget, focused);
    }

    [Fact]
    public void ChoosingLocalAfterTyping_RevalidatesOnceSaveWasTried()
    {
        var editor = NewEditor();
        editor.Title = "Planning";
        editor.SelectedTargetIndex = 1;
        editor.InviteText = "nonsense";
        editor.SaveCommand.Execute(null);
        Assert.Contains("nonsense is not an email address", editor.ErrorText);

        editor.InviteText = "ann@example.com";
        Assert.False(editor.HasError);
        editor.SelectedTargetIndex = 0;
        Assert.Contains("online calendar", editor.ErrorText);
    }

    [Fact]
    public void BadAddress_IsRefused_PointingAtInvite()
    {
        var editor = NewEditor();
        editor.Title = "Planning";
        editor.SelectedTargetIndex = 1;
        editor.InviteText = "ann@example.com; bob";
        EditorField? focused = null;
        editor.FieldFocusRequested += f => focused = f;

        editor.SaveCommand.Execute(null);

        Assert.Equal("bob is not an email address. Separate addresses with semicolons.", editor.ErrorText);
        Assert.Equal(EditorField.Invite, focused);
        // The View selects only this, so typing the fix keeps ann@example.com.
        Assert.Equal("bob", editor.InvalidInviteEntry);

        editor.InviteText = "ann@example.com; bob@example.com";
        Assert.Equal(string.Empty, editor.InvalidInviteEntry);
    }

    private static CalendarEvent ServerMeeting(string organizer) => new()
    {
        Uid = "meet-1",
        AccountId = Apple,
        IsGraph = true,
        CalendarId = "https://cal/home/",
        Summary = "Planning",
        StartTimeTicks = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc).Ticks,
        EndTimeTicks = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc).Ticks,
        Organizer = organizer,
        Sequence = "4",
        ResourceUrl = "https://cal/home/RANDOM.ics",
        Attendees = [new() { Email = "ann@example.com", Name = "Ann", Response = AttendeeResponse.Accepted }],
    };

    [Fact]
    public void Edit_ShowsWhoIsInvited_AndCarriesWhatTheEditorDoesNotShow()
    {
        var editor = new EventEditorViewModel(ServerMeeting("me@icloud.com"));
        Assert.Equal("Ann <ann@example.com>", editor.InviteText);
        Assert.False(editor.IsInviteReadOnly);

        editor.InviteText += "; bob@example.com";
        Assert.True(editor.TryBuildEvent(out var evt, out _));

        Assert.True(evt.AttendeesChanged);
        Assert.Equal(AttendeeResponse.Accepted, evt.Attendees[0].Response); // Ann's answer survives
        Assert.Equal("ann@example.com", Assert.Single(evt.PreviousAttendees).Email);
        Assert.Equal(("me@icloud.com", "4", "https://cal/home/RANDOM.ics"), (evt.Organizer, evt.Sequence, evt.ResourceUrl));
    }

    [Fact]
    public void Edit_WithoutTouchingInvite_IsNoChangeToTheGuestList()
    {
        var editor = new EventEditorViewModel(ServerMeeting("me@icloud.com"));
        editor.Title = "Planning, moved";
        Assert.True(editor.TryBuildEvent(out var evt, out _));
        Assert.False(evt.AttendeesChanged);
    }

    [Fact]
    public void Edit_SomeoneElsesMeeting_InviteIsReadOnly_AndTheListIsKept()
    {
        var editor = new EventEditorViewModel(ServerMeeting("boss@example.com"), userOrganizes: false);
        Assert.True(editor.IsInviteReadOnly);
        Assert.Contains("Only the organizer", editor.InviteHelpText);

        editor.InviteText = "intruder@example.com"; // cannot happen through a read-only box, but must not matter
        Assert.True(editor.TryBuildEvent(out var evt, out _));
        Assert.Equal("ann@example.com", Assert.Single(evt.Attendees).Email);
        Assert.False(evt.AttendeesChanged);
    }

    [Fact]
    public void Edit_MeetingWithARoom_HidesTheRoom_ButKeepsItBooked()
    {
        var meeting = ServerMeeting("me@icloud.com");
        meeting.Attendees.Add(new EventAttendee { Email = "room4@example.com", Name = "Room 4", Kind = AttendeeKind.Resource });
        var editor = new EventEditorViewModel(meeting);
        Assert.Equal("Ann <ann@example.com>", editor.InviteText);

        editor.InviteText += "; bob@example.com";
        Assert.True(editor.TryBuildEvent(out var evt, out _));

        Assert.Contains(evt.Attendees, a => a.Email == "room4@example.com" && a.IsResource);
        Assert.Equal(3, evt.Attendees.Count);
    }

    [Fact]
    public void Edit_ImportedMeetingOnLocalCalendar_SavesWithItsGuestListUntouched()
    {
        // A meeting imported from a .ics file into Local Calendar keeps its guest list. Editing its
        // title must still save: refusing would leave the user emptying a list they never typed.
        var imported = new CalendarEvent
        {
            Uid = "imported-1", AccountId = CalendarEvent.LocalAccountId, Summary = "Board meeting",
            StartTimeTicks = DateTime.UtcNow.Ticks, EndTimeTicks = DateTime.UtcNow.AddHours(1).Ticks,
            Organizer = "chair@example.com",
            Attendees = [new() { Email = "ann@example.com" }],
        };
        var editor = new EventEditorViewModel(imported, userOrganizes: false);
        editor.Title = "Board meeting (moved)";

        Assert.True(editor.TryBuildEvent(out var evt, out var error), error);
        Assert.Equal("ann@example.com", Assert.Single(evt.Attendees).Email);
        Assert.False(evt.AttendeesChanged);
    }

    [Fact]
    public void Edit_LocalAppointment_TypingAnInvitee_IsRefusedAtInvite()
    {
        var local = new CalendarEvent
        {
            Uid = "local-1", AccountId = CalendarEvent.LocalAccountId, Summary = "Dentist",
            StartTimeTicks = DateTime.UtcNow.Ticks, EndTimeTicks = DateTime.UtcNow.AddHours(1).Ticks,
        };
        var editor = new EventEditorViewModel(local);
        editor.InviteText = "ann@example.com";

        Assert.False(editor.TryBuildEvent(out _, out var error, out var field));
        Assert.Contains("online calendar", error);
        Assert.Equal(EditorField.Invite, field);
    }
}

public class CalendarPaneInvitationTests
{
    private static readonly AccountModel Google = new()
    {
        Id = Guid.NewGuid(), AuthType = AuthType.OAuth2Google, Username = "me@gmail.com",
    };

    private sealed class Credentials : ICredentialService
    {
        public void SavePassword(Guid accountId, string pw) { }
        public string? GetPassword(Guid accountId) => null;
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    private static (CalendarViewModel Vm, StubCalendarService Store, StubGraphCalendarSyncService Sync, List<string> Said) Make()
    {
        var store = new StubCalendarService();
        var sync = new StubGraphCalendarSyncService { CalendarStore = store };
        var vm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false, showFieldLabels: false,
                                       graphSync: sync, graphAccountsProvider: () => [Google],
                                       invitations: new CalendarInvitationService(sync, new StubSmtpService(), new Credentials()));
        var said = new List<string>();
        vm.AnnouncementRequested += (text, _) => said.Add(text);
        return (vm, store, sync, said);
    }

    private static CalendarEvent Row(string organizer = "", int invited = 2) => new()
    {
        Uid = "srv-1", AccountId = Google.Id, IsGraph = true, Summary = "Planning",
        StartTimeTicks = DateTime.Today.AddHours(9).ToUniversalTime().Ticks,
        EndTimeTicks = DateTime.Today.AddHours(10).ToUniversalTime().Ticks,
        ResponseStatus = CalendarResponseStatus.Accepted,
        Organizer = organizer,
        ResourceUrl = "https://cal/home/RANDOM.ics",
        Attendees = Enumerable.Range(1, invited).Select(i => new EventAttendee { Email = $"p{i}@example.com" }).ToList(),
    };

    [Theory]
    [InlineData(0, 0, 0, null, "")]
    [InlineData(1, 0, 0, null, " Invitation sent to 1 person.")]
    [InlineData(0, 3, 0, null, " Update sent to 3 people.")]
    [InlineData(1, 2, 1, null, " Invitation sent to 1 person. Update sent to 2 people. Cancellation sent to 1 person.")]
    [InlineData(0, 0, 2, null, " Cancellation sent to 2 people.")]
    [InlineData(2, 0, 0, "535 Authentication failed", " The invitations were not sent: 535 Authentication failed")]
    [InlineData(0, 0, 2, "535 Authentication failed", " The cancellation was not sent: 535 Authentication failed")]
    [InlineData(1, 2, 0, "535 Authentication failed", " The update was not sent: 535 Authentication failed")]
    public void DescribeInvitations_SaysWhoWasTold(int invited, int updated, int cancelled, string? error, string expected)
        => Assert.Equal(expected, CalendarViewModel.DescribeInvitations(new InvitationOutcome(invited, updated, cancelled, error)));

    [Fact]
    public async Task EditingAMeeting_AnnouncesTheUpdate_AndKeepsWhereTheServerStoresIt()
    {
        var (vm, store, sync, said) = Make();
        var row = Row();
        store.StoredEvents.Add(row);
        await vm.LoadAsync();
        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.EditEventCommand.Execute(row);
        editor!.Title = "Planning, moved";
        editor.SaveCommand.Execute(null);

        var pushed = Assert.Single(sync.UpdatedEvents);
        Assert.Equal("https://cal/home/RANDOM.ics", pushed.ResourceUrl);
        Assert.Contains(said, s => s.EndsWith("Update sent to 2 people.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SomeoneElsesMeeting_OpensWithInviteReadOnly()
    {
        var (vm, store, _, _) = Make();
        var row = Row(organizer: "boss@example.com");
        store.StoredEvents.Add(row);
        await vm.LoadAsync();
        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.EditEventCommand.Execute(row);

        Assert.True(editor!.IsInviteReadOnly);
    }

    [Fact]
    public async Task ImportedMeetingOnLocalCalendar_OpensWithInviteReadOnly_AndSaves()
    {
        var (vm, store, _, said) = Make();
        var row = Row(organizer: "chair@example.com");
        row.AccountId = CalendarEvent.LocalAccountId;
        row.IsGraph = false;
        store.StoredEvents.Add(row);
        await vm.LoadAsync();
        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.EditEventCommand.Execute(row);
        Assert.True(editor!.IsInviteReadOnly);
        editor.Title = "Planning, moved";
        editor.SaveCommand.Execute(null);

        Assert.False(editor.HasError);
        Assert.Equal("Planning, moved", Assert.Single(store.StoredEvents).Summary);
    }

    [Fact]
    public void DeletePrompt_OfTheUsersMeeting_SaysWhoWillBeTold()
    {
        var (vm, _, _, _) = Make();
        Assert.EndsWith("The 2 people invited will be sent a cancellation.", vm.DeletePrompt(Row()));
        Assert.EndsWith("p1@example.com will be sent a cancellation.", vm.DeletePrompt(Row(invited: 1)));
        Assert.Equal("Delete the appointment “Planning”?", vm.DeletePrompt(Row(invited: 0)));
        Assert.Equal("Delete the appointment “Planning”?", vm.DeletePrompt(Row(organizer: "boss@example.com")));
    }

    [Fact]
    public async Task CreateThatFails_KeepsALocalCopy_AndSaysNobodyWasInvited()
    {
        var (vm, store, sync, said) = Make();
        sync.CreateFailure = new InvalidOperationException("offline");
        var evt = Row();
        evt.IsGraph = false;

        await vm.SaveNewEventAsync(evt);

        var kept = Assert.Single(store.StoredEvents);
        Assert.Equal(CalendarEvent.LocalAccountId, kept.AccountId);
        Assert.Empty(kept.Attendees);
        Assert.Contains(said, s => s.EndsWith("Nobody was invited.", StringComparison.Ordinal));
    }
}

public class CalendarAttendeeStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;

    public CalendarAttendeeStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-attendees-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static CalendarEvent Meeting(Guid account, string uid) => new()
    {
        Uid = uid, AccountId = account, IsGraph = true, Summary = "Planning",
        StartTimeTicks = DateTime.UtcNow.Ticks,
        Attendees = [new() { Email = "ann@example.com", Name = "Ann", Response = AttendeeResponse.Declined }],
    };

    [Fact]
    public async Task Upsert_ThenLoad_KeepsAttendees()
    {
        await _store.UpsertCalendarEventAsync(Meeting(Guid.NewGuid(), "a"));
        var a = Assert.Single(Assert.Single(await _store.LoadCalendarEventsAsync()).Attendees);
        Assert.Equal(("ann@example.com", "Ann", AttendeeResponse.Declined), (a.Email, a.Name, a.Response));
    }

    [Fact]
    public async Task ReplaceSlice_KeepsAttendees_AndAnUpdateCanClearThem()
    {
        var account = Guid.NewGuid();
        await _store.ReplaceGraphCalendarEventsAsync(account, [Meeting(account, "b")]);
        Assert.Single(Assert.Single(await _store.LoadCalendarEventsAsync()).Attendees);

        var cleared = Meeting(account, "b");
        cleared.Attendees = [];
        await _store.UpsertCalendarEventAsync(cleared);
        Assert.Empty(Assert.Single(await _store.LoadCalendarEventsAsync()).Attendees);
    }
}
