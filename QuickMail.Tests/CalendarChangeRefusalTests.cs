using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #326: an edit or delete the provider will not allow — an event on a read-only calendar
/// (Microsoft's Birthdays, a shared or subscribed calendar), or one of Google's own kinds of event
/// (birthday, out of office, working location, focus time, from Gmail) — must say why in plain
/// words instead of the generic "Could not update your online calendar."
/// </summary>
public class CalendarChangeRefusalTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private static CalendarEvent ServerRow(string calendarId = "cal-1", string calendarName = "Birthdays",
                                           string eventType = "") => new()
    {
        Uid = "srv-1", AccountId = AccountId, IsGraph = true, Summary = "Happy birthday!",
        CalendarId = calendarId, CalendarName = calendarName, ProviderEventType = eventType,
        StartTimeTicks = DateTime.Today.AddHours(9).ToUniversalTime().Ticks,
        EndTimeTicks = DateTime.Today.AddHours(10).ToUniversalTime().Ticks,
        ResponseStatus = CalendarResponseStatus.Accepted,
    };

    private static HttpRequestException Http(HttpStatusCode code, string body = "")
        => new($"Calendar API request failed ({(int)code} {code}): {body}", inner: null, statusCode: code);

    private const string BirthdayRestriction = """
        { "error": { "errors": [ { "domain": "calendar", "reason": "eventTypeRestriction",
        "message": "Attempt made to modify 'birthday' event in a way that is not valid for this event type." } ],
        "code": 400 } }
        """;

    // ── Read-only calendar, checked before anything is attempted ────────────────

    [Theory]
    [InlineData(true, "Your account can only read the Birthdays calendar, so this event can't be deleted here.")]
    [InlineData(false, "Your account can only read the Birthdays calendar, so this event can't be edited here.")]
    public void ReadOnlyCalendar_IsExplained(bool delete, string expected)
    {
        var sources = new[] { new CalendarSourceInfo(AccountId, "cal-1", "Birthdays", CanWrite: false) };
        Assert.Equal(expected, CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(), sources, delete));
    }

    [Fact]
    public void WritableCalendar_HasNoReason()
    {
        var sources = new[] { new CalendarSourceInfo(AccountId, "cal-1", "Calendar", CanWrite: true) };
        Assert.Null(CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(), sources, delete: true));
    }

    [Fact]
    public void UnknownCalendar_IsNotRefusedOnAGuess()
    {
        // Not in the recorded list (never enumerated) — only the attempt can tell.
        Assert.Null(CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(), [], delete: true));
    }

    [Fact]
    public void SameCalendarIdOnAnotherAccount_DoesNotMatch()
    {
        var sources = new[] { new CalendarSourceInfo(Guid.NewGuid(), "cal-1", "Birthdays", CanWrite: false) };
        Assert.Null(CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(), sources, delete: true));
    }

    [Fact]
    public void UnnamedReadOnlyCalendar_StillReadsNaturally()
    {
        var sources = new[] { new CalendarSourceInfo(AccountId, "cal-1", "", CanWrite: false) };
        Assert.Equal("Your account can only read this calendar, so this event can't be deleted here.",
            CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(calendarName: ""), sources, delete: true));
    }

    // ── Provider refused: naming the reason ─────────────────────────────────────

    [Theory]
    [InlineData("birthday", "Google doesn't let other programs change birthday events. Change the birthday on the contact in Google Contacts.")]
    [InlineData("outOfOffice", "Google doesn't let other programs change out of office events. Change it in Google Calendar.")]
    [InlineData("focusTime", "Google doesn't let other programs change focus time events. Change it in Google Calendar.")]
    [InlineData("workingLocation", "Google doesn't let other programs change working location events. Change it in Google Calendar.")]
    [InlineData("fromGmail", "Google doesn't let other programs change events Gmail created. Change it from the original message in Gmail.")]
    [InlineData("", "Google doesn't let other programs change this kind of event. Change it in Google Calendar.")]
    public void EventTypeRestriction_NamesTheKindOfEvent(string eventType, string expected)
    {
        var ex = Http(HttpStatusCode.BadRequest, BirthdayRestriction);
        Assert.Equal(expected, CalendarChangeRefusal.DescribeFailure(ex, ServerRow(eventType: eventType), "Google"));
    }

    [Fact]
    public void Forbidden_OnAGoogleKind_NamesTheKind()
    {
        var ex = Http(HttpStatusCode.Forbidden, """{ "error": { "errors": [ { "reason": "forbidden" } ], "code": 403 } }""");
        Assert.StartsWith("Google doesn't let other programs change out of office events.",
            CalendarChangeRefusal.DescribeFailure(ex, ServerRow(eventType: "outOfOffice"), "Google"));
    }

    [Theory]
    [InlineData("forbiddenForNonOrganizer")]
    [InlineData("requiredAccessLevel")]
    public void GoogleForbidden_WithATrustedReason_OnAnOrdinaryEvent_SaysTheAccountIsNotAllowed(string reason)
    {
        var ex = Http(HttpStatusCode.Forbidden, $$"""{ "error": { "errors": [ { "reason": "{{reason}}" } ] } }""");
        Assert.Equal("Google doesn't allow your account to change this event.",
            CalendarChangeRefusal.DescribeFailure(ex, ServerRow(), "Google"));
    }

    [Theory]
    [InlineData("rateLimitExceeded", "birthday")]
    [InlineData("rateLimitExceeded", "")]
    [InlineData("userRateLimitExceeded", "birthday")]
    [InlineData("quotaExceeded", "")]
    [InlineData("dailyLimitExceeded", "outOfOffice")]
    [InlineData("insufficientPermissions", "birthday")]
    [InlineData("ACCESS_TOKEN_SCOPE_INSUFFICIENT", "")]
    public void GoogleForbidden_ForALimitOrAScope_IsNotARefusal(string reason, string eventType)
    {
        // A rate limit on a birthday row must not be explained as "Google doesn't let other
        // programs change birthday events" — that would be wrong advice.
        var ex = Http(HttpStatusCode.Forbidden, $$"""{ "error": { "errors": [ { "domain": "usageLimits", "reason": "{{reason}}" } ], "code": 403 } }""");
        Assert.Equal(CalendarChangeRefusal.GenericFailure,
            CalendarChangeRefusal.DescribeFailure(ex, ServerRow(eventType: eventType), "Google"));
    }

    [Fact]
    public void GoogleForbidden_WithNoReason_IsNotTrusted()
        => Assert.Equal(CalendarChangeRefusal.GenericFailure,
            CalendarChangeRefusal.DescribeFailure(Http(HttpStatusCode.Forbidden, "{}"), ServerRow(), "Google"));

    [Theory]
    [InlineData("Calendar", "Your account can only read the Calendar, so this event can't be deleted here.")]
    [InlineData("Team calendar", "Your account can only read the Team calendar, so this event can't be deleted here.")]
    [InlineData("Holidays", "Your account can only read the Holidays calendar, so this event can't be deleted here.")]
    public void ReadOnlyCalendarName_IsNotDoubled(string name, string expected)
    {
        var sources = new[] { new CalendarSourceInfo(AccountId, "cal-1", name, CanWrite: false) };
        Assert.Equal(expected,
            CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(calendarName: name), sources, delete: true));
    }

    [Fact]
    public void ReadOnlyGoogleContactsBirthdays_PointsToGoogleContacts()
    {
        var id = CalendarChangeRefusal.GoogleContactsBirthdaysCalendarId;
        var sources = new[] { new CalendarSourceInfo(AccountId, id, "Birthdays", CanWrite: false) };
        Assert.Equal(
            "Google doesn't let other programs change birthday events. Change the birthday on the contact in Google Contacts.",
            CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(calendarId: id), sources, delete: true));
    }

    [Fact]
    public void ReadOnlyCalendar_BirthdayEvent_PointsToGoogleContacts()
    {
        var sources = new[] { new CalendarSourceInfo(AccountId, "cal-1", "Family", CanWrite: false) };
        Assert.StartsWith("Google doesn't let other programs change birthday events.",
            CalendarChangeRefusal.ReadOnlyCalendarReason(ServerRow(calendarName: "Family", eventType: "birthday"),
                                                         sources, delete: false));
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("iCloud")]
    public void Forbidden_OnAnOrdinaryEvent_SaysTheAccountIsNotAllowed(string provider)
    {
        var ex = Http(HttpStatusCode.Forbidden, """{ "error": { "code": "ErrorAccessDenied" } }""");
        Assert.Equal($"{provider} doesn't allow your account to change this event.",
            CalendarChangeRefusal.DescribeFailure(ex, ServerRow(), provider));
    }

    [Fact]
    public void BadRequest_WithoutARestriction_StaysGeneric()
    {
        // A 400 for some other reason (bad data) is not the provider protecting the event.
        var ex = Http(HttpStatusCode.BadRequest, """{ "error": { "reason": "invalid" } }""");
        Assert.Equal(CalendarChangeRefusal.GenericFailure,
            CalendarChangeRefusal.DescribeFailure(ex, ServerRow(eventType: "birthday"), "Google"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public void OtherHttpFailures_StayGeneric(HttpStatusCode code)
        => Assert.Equal(CalendarChangeRefusal.GenericFailure,
            CalendarChangeRefusal.DescribeFailure(Http(code), ServerRow(), "Microsoft"));

    [Fact]
    public void NonHttpFailures_StayGeneric()
        => Assert.Equal(CalendarChangeRefusal.GenericFailure,
            CalendarChangeRefusal.DescribeFailure(new InvalidOperationException("boom"), ServerRow(), "Google"));

    [Fact]
    public void WrappedHttpFailure_IsStillRecognised()
    {
        var ex = new InvalidOperationException("wrapper", Http(HttpStatusCode.Forbidden));
        Assert.Equal("Microsoft doesn't allow your account to change this event.",
            CalendarChangeRefusal.DescribeFailure(ex, ServerRow(), "Microsoft"));
    }

    // ── Through the calendar ViewModel ──────────────────────────────────────────

    private static (CalendarViewModel Vm, StubCalendarService Store, StubGraphCalendarSyncService Sync, List<string> Spoken)
        MakeVm(AccountModel account, params CalendarSourceInfo[] sources)
    {
        var store = new StubCalendarService();
        var sync = new StubGraphCalendarSyncService { CalendarStore = store };
        var vm = new CalendarViewModel(store, onlineMode: false, showDeclinedEvents: false,
                                       showFieldLabels: false, graphSync: sync,
                                       graphAccountsProvider: () => new[] { account },
                                       calendarSourcesProvider: () => sources);
        var spoken = new List<string>();
        // Only Result announcements: a refusal is the outcome of what the user asked for, so it must
        // reach them under the Results preference — anything spoken under another category is
        // left out here and fails the assertions that expect it.
        vm.AnnouncementRequested += (text, category) =>
        {
            if (category == AnnouncementCategory.Result) spoken.Add(text);
        };
        return (vm, store, sync, spoken);
    }

    private static AccountModel Microsoft() => new()
        { Id = AccountId, BackendKind = BackendKind.MicrosoftGraph, Username = "me@outlook.com" };

    private static AccountModel Google() => new()
        { Id = AccountId, AuthType = AuthType.OAuth2Google, Username = "me@gmail.com" };

    [Fact]
    public async Task DeleteOnAReadOnlyCalendar_ExplainsAtOnce_AndNeverAsksOrAttempts()
    {
        var (vm, store, sync, spoken) = MakeVm(Microsoft(),
            new CalendarSourceInfo(AccountId, "cal-1", "Birthdays", CanWrite: false));
        store.StoredEvents.Add(ServerRow());
        await vm.LoadAsync();
        spoken.Clear();

        var asked = false;
        vm.DeleteConfirmRequested += (_, _) => asked = true;
        vm.DeleteEventCommand.Execute(vm.VisibleEvents[0]);

        Assert.False(asked);
        Assert.Empty(sync.DeletedEvents);
        Assert.Equal("Your account can only read the Birthdays calendar, so this event can't be deleted here.",
                     Assert.Single(spoken));
    }

    [Fact]
    public async Task EditOnAReadOnlyCalendar_ExplainsAtOnce_AndOpensNoEditor()
    {
        var (vm, store, sync, spoken) = MakeVm(Microsoft(),
            new CalendarSourceInfo(AccountId, "cal-1", "Birthdays", CanWrite: false));
        store.StoredEvents.Add(ServerRow());
        await vm.LoadAsync();
        spoken.Clear();

        var opened = false;
        vm.EditorRequested += _ => opened = true;
        vm.EditEventCommand.Execute(vm.VisibleEvents[0]);

        Assert.False(opened);
        Assert.Empty(sync.UpdatedEvents);
        Assert.Equal("Your account can only read the Birthdays calendar, so this event can't be edited here.",
                     Assert.Single(spoken));
    }

    [Fact]
    public async Task DeleteOnAWritableCalendar_StillAsksAndDeletes()
    {
        var (vm, store, sync, _) = MakeVm(Microsoft(),
            new CalendarSourceInfo(AccountId, "cal-1", "Calendar", CanWrite: true));
        store.StoredEvents.Add(ServerRow(calendarName: "Calendar"));
        await vm.LoadAsync();

        Action? confirm = null;
        vm.DeleteConfirmRequested += (_, cb) => confirm = cb;
        vm.DeleteEventCommand.Execute(vm.VisibleEvents[0]);
        Assert.NotNull(confirm);
        confirm!();

        Assert.Single(sync.DeletedEvents);
    }

    [Fact]
    public async Task GoogleRefusesABirthdayDelete_SaysWhy_AndKeepsTheEvent()
    {
        var (vm, store, sync, spoken) = MakeVm(Google(),
            new CalendarSourceInfo(AccountId, "cal-1", "me@gmail.com", CanWrite: true));
        store.StoredEvents.Add(ServerRow(calendarName: "me@gmail.com", eventType: "birthday"));
        await vm.LoadAsync();
        sync.WriteFailure = Http(HttpStatusCode.BadRequest, BirthdayRestriction);
        spoken.Clear();

        Action? confirm = null;
        vm.DeleteConfirmRequested += (_, cb) => confirm = cb;
        vm.DeleteEventCommand.Execute(vm.VisibleEvents[0]);
        confirm!();

        Assert.Equal("Google doesn't let other programs change birthday events. Change the birthday on the contact in Google Contacts.",
                     Assert.Single(spoken));
        Assert.Single(store.StoredEvents);
    }

    [Fact]
    public async Task GoogleRefusesAnOutOfOfficeEdit_SaysWhy()
    {
        var (vm, store, sync, spoken) = MakeVm(Google());
        store.StoredEvents.Add(ServerRow(calendarName: "me@gmail.com", eventType: "outOfOffice"));
        await vm.LoadAsync();
        sync.WriteFailure = Http(HttpStatusCode.BadRequest,
            """{ "error": { "errors": [ { "reason": "eventTypeRestriction" } ] } }""");
        spoken.Clear();

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.EditEventCommand.Execute(vm.VisibleEvents[0]);
        Assert.NotNull(editor); // Google's kinds are tried, not blocked up front
        editor!.Title = "Away";
        editor.SaveCommand.Execute(null);

        Assert.Equal("Google doesn't let other programs change out of office events. Change it in Google Calendar.",
                     Assert.Single(spoken));
        Assert.Equal("Happy birthday!", store.StoredEvents.Single().Summary);
    }

    [Fact]
    public async Task MicrosoftForbidsADelete_SaysTheAccountIsNotAllowed()
    {
        var (vm, store, sync, spoken) = MakeVm(Microsoft());
        store.StoredEvents.Add(ServerRow());
        await vm.LoadAsync();
        sync.WriteFailure = Http(HttpStatusCode.Forbidden, """{ "error": { "code": "ErrorAccessDenied" } }""");
        spoken.Clear();

        Action? confirm = null;
        vm.DeleteConfirmRequested += (_, cb) => confirm = cb;
        vm.DeleteEventCommand.Execute(vm.VisibleEvents[0]);
        confirm!();

        Assert.Equal("Microsoft doesn't allow your account to change this event.", Assert.Single(spoken));
    }

    [Fact]
    public async Task ICloudForbidsAnEdit_NamesICloud()
    {
        var account = new AccountModel
        {
            Id = AccountId, AuthType = AuthType.Password, BackendKind = BackendKind.ImapSmtp,
            ImapHost = "imap.mail.me.com", Username = "me@icloud.com", SyncCalendar = true,
        };
        var (vm, store, sync, spoken) = MakeVm(account);
        store.StoredEvents.Add(ServerRow(calendarName: "Home"));
        await vm.LoadAsync();
        sync.WriteFailure = Http(HttpStatusCode.Forbidden);
        spoken.Clear();

        EventEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;
        vm.EditEventCommand.Execute(vm.VisibleEvents[0]);
        editor!.Title = "Moved";
        editor.SaveCommand.Execute(null);

        Assert.Equal("iCloud doesn't allow your account to change this event.", Assert.Single(spoken));
    }
}
