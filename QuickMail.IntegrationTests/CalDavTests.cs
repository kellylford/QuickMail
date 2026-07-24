using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// CalDAV protocol tests against a local Radicale server (live-content testing plan §4.3):
/// discovery, multi-calendar enumeration, event sync round-trip, and — the #293 bug shape at
/// the DAV level — write targeting: a create/edit/delete must land in the calendar it was
/// aimed at, and only there.
/// </summary>
[Collection(RadicaleCollection.Name)]
public sealed class CalDavTests
{
    private readonly RadicaleFixture _radicale;

    // Fetch window covering every event these tests seed (all in Sep 2026).
    private static readonly DateTime RangeStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RangeEnd   = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public CalDavTests(RadicaleFixture radicale) => _radicale = radicale;

    private static string MakeIcs(string uid, string summary) => string.Join("\r\n",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "PRODID:-//QuickMail Integration Tests//EN",
        "BEGIN:VEVENT",
        $"UID:{uid}",
        $"SUMMARY:{summary}",
        "DTSTART:20260910T100000Z",
        "DTEND:20260910T110000Z",
        "END:VEVENT",
        "END:VCALENDAR");

    [Fact]
    public async Task Discovery_EnumeratesAllOfTheUsersCalendars()
    {
        _radicale.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var user = RadicaleFixture.CreateUser("discover");
        var work = await _radicale.CreateCalendarAsync(user, "work", ct);
        var home = await _radicale.CreateCalendarAsync(user, "home", ct);

        using var client = new CalDavCalendarClient();
        var calendars = await client.DiscoverCalendarsAsync(
            RadicaleFixture.ServerUrl, user, RadicaleFixture.Password, ct);

        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => new Uri(work).AbsolutePath == new Uri(c.Url).AbsolutePath);
        Assert.Contains(calendars, c => new Uri(home).AbsolutePath == new Uri(c.Url).AbsolutePath);
    }

    [Fact]
    public async Task FetchEvents_RoundTripsSeededEvent()
    {
        _radicale.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var user = RadicaleFixture.CreateUser("fetch");
        var calendar = await _radicale.CreateCalendarAsync(user, "cal", ct);
        await _radicale.PutResourceAsync($"{calendar}seeded-1.ics",
            MakeIcs("seeded-1@quickmail.test", "Seeded Event"), "text/calendar", user, ct);

        using var client = new CalDavCalendarClient();
        var events = await client.FetchEventIcsAsync(calendar, user, RadicaleFixture.Password, RangeStart, RangeEnd, ct);

        var (href, ics) = Assert.Single(events);
        Assert.EndsWith("seeded-1.ics", href);
        var parsed = IcsModel.Parse(ics);
        Assert.NotNull(parsed);
        Assert.Equal("seeded-1@quickmail.test", parsed!.Uid);
        Assert.Equal("Seeded Event", parsed.Summary);
    }

    [Fact]
    public async Task PutEvent_LandsOnlyInTheTargetedCalendar()
    {
        _radicale.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var user = RadicaleFixture.CreateUser("target");
        var primary   = await _radicale.CreateCalendarAsync(user, "primary", ct);
        var secondary = await _radicale.CreateCalendarAsync(user, "secondary", ct);

        // The #293 bug shape: the write went to the hardcoded primary calendar regardless of
        // target. Aim the client's write at the SECONDARY calendar and assert it landed there
        // and only there.
        const string uid = "targeted-1@quickmail.test";
        using var client = new CalDavCalendarClient();
        var resourceUrl = CalDavCalendarClient.EventResourceUrl(secondary, uid);
        await client.PutEventAsync(resourceUrl, MakeIcs(uid, "Targeted Event"),
            user, RadicaleFixture.Password, ifNoneMatch: true, ct);

        var inSecondary = await client.FetchEventIcsAsync(secondary, user, RadicaleFixture.Password, RangeStart, RangeEnd, ct);
        var inPrimary   = await client.FetchEventIcsAsync(primary, user, RadicaleFixture.Password, RangeStart, RangeEnd, ct);
        Assert.Single(inSecondary);
        Assert.Empty(inPrimary);

        // Delete must also hit the targeted calendar's resource.
        await client.DeleteEventAsync(resourceUrl, user, RadicaleFixture.Password, ct);
        var afterDelete = await client.FetchEventIcsAsync(secondary, user, RadicaleFixture.Password, RangeStart, RangeEnd, ct);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task PutEvent_EditRoundTrip_UpdatesInPlace()
    {
        _radicale.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var user = RadicaleFixture.CreateUser("edit");
        var calendar = await _radicale.CreateCalendarAsync(user, "cal", ct);

        const string uid = "edit-1@quickmail.test";
        using var client = new CalDavCalendarClient();
        var resourceUrl = CalDavCalendarClient.EventResourceUrl(calendar, uid);
        await client.PutEventAsync(resourceUrl, MakeIcs(uid, "Before"), user, RadicaleFixture.Password, ifNoneMatch: true, ct);
        await client.PutEventAsync(resourceUrl, MakeIcs(uid, "After"), user, RadicaleFixture.Password, ifNoneMatch: false, ct);

        var events = await client.FetchEventIcsAsync(calendar, user, RadicaleFixture.Password, RangeStart, RangeEnd, ct);
        var (_, ics) = Assert.Single(events); // updated in place, not duplicated
        Assert.Equal("After", IcsModel.Parse(ics)!.Summary);
    }
}
