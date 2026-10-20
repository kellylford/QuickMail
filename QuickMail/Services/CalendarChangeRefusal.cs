using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Plain-words explanations for an online calendar change that cannot or did not happen
/// (issue #326): an event on a calendar the account may only read, and the provider refusing an
/// edit or delete it manages itself — a Google birthday, an out-of-office block, an event Gmail
/// created. Pure functions, so the wording and the decisions are unit-testable without a network.
///
/// <para>
/// Two different moments, deliberately:
/// <list type="bullet">
/// <item><see cref="ReadOnlyCalendarReason"/> is checked BEFORE anything is attempted. The
/// provider's calendar list says outright that a calendar is read-only (Microsoft Graph
/// <c>canEdit</c>, Google <c>accessRole</c>), so trying the change could only fail.</item>
/// <item><see cref="DescribeFailure"/> runs AFTER the provider refused. Google's event kinds are
/// not checked up front: Google documents that birthday and Gmail events can be deleted through
/// its API and that the status events can be edited, yet in practice it refuses some of those
/// changes. Blocking them in advance would forbid changes that work; trying and then naming the
/// reason costs nothing, since nothing changes locally until the provider agrees.</item>
/// </list>
/// </para>
/// </summary>
public static class CalendarChangeRefusal
{
    /// <summary>Spoken when the failure is not one we can explain more specifically.</summary>
    public const string GenericFailure = "Could not update your online calendar.";

    /// <summary>
    /// Why <paramref name="evt"/> cannot be edited or deleted because its calendar is read-only for
    /// this account, or null when its calendar is writable or not known. An unknown calendar is
    /// treated as writable — the attempt is the only way to find out, and refusing on a guess would
    /// block changes that work.
    /// </summary>
    public static string? ReadOnlyCalendarReason(CalendarEvent evt, IEnumerable<CalendarSourceInfo> sources, bool delete)
    {
        if (!evt.IsGraph || string.IsNullOrEmpty(evt.CalendarId)) return null;
        var source = sources.FirstOrDefault(s => s.AccountId == evt.AccountId
                                                 && string.Equals(s.CalendarId, evt.CalendarId, StringComparison.Ordinal));
        if (source == null || source.CanWrite) return null;

        // Google's contact birthdays: either a birthday-type event, or the older separate
        // "Birthdays" calendar Google generates from Contacts (accessRole reader). Saying only that
        // the calendar is read-only would never send the user to Google Contacts, where the fix is.
        if (string.Equals(evt.ProviderEventType, "birthday", StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.CalendarId, GoogleContactsBirthdaysCalendarId, StringComparison.OrdinalIgnoreCase))
            return GoogleKindMessage("birthday");

        var name = (string.IsNullOrWhiteSpace(source.CalendarName) ? evt.CalendarName : source.CalendarName).Trim();
        // "the Calendar calendar" / "the Work Calendar calendar" read badly; the name already says it.
        var calendar = name.Length == 0 ? "this calendar"
            : name.EndsWith("calendar", StringComparison.OrdinalIgnoreCase) ? $"the {name}"
            : $"the {name} calendar";
        return $"Your account can only read {calendar}, so this event can't be {(delete ? "deleted" : "edited")} here.";
    }

    /// <summary>The id of the calendar Google generates from the birthdays in Google Contacts.</summary>
    internal const string GoogleContactsBirthdaysCalendarId = "addressbook#contacts@group.v.calendar.google.com";

    /// <summary>
    /// Google error reasons that mean the change itself is not allowed. Google also answers 403 for
    /// rate and quota limits (rateLimitExceeded, userRateLimitExceeded, quotaExceeded,
    /// dailyLimitExceeded) and for a token missing a scope (insufficientPermissions) — none of which
    /// say anything about the event, so explaining them as a refusal would give wrong advice.
    /// </summary>
    private static readonly HashSet<string> GoogleRefusalReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "forbiddenForNonOrganizer", "requiredAccessLevel", "forbidden", "eventTypeRestriction",
    };

    private static readonly System.Text.RegularExpressions.Regex ReasonPattern =
        new("\"reason\"\\s*:\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The <c>reason</c> values in a Google error body carried in the exception message.</summary>
    internal static IReadOnlyList<string> ErrorReasons(string message)
        => ReasonPattern.Matches(message).Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// The sentence to speak when the provider refused an edit or delete of <paramref name="evt"/>.
    /// <paramref name="provider"/> names the service ("Google", "Microsoft", "iCloud").
    /// </summary>
    public static string DescribeFailure(Exception ex, CalendarEvent evt, string provider)
    {
        var http = ex as HttpRequestException ?? ex.InnerException as HttpRequestException;
        if (http == null) return GenericFailure;

        var forbidden = http.StatusCode == HttpStatusCode.Forbidden;
        var reasons = ErrorReasons(http.Message);

        // Google: trust a refusal only when its reason says the change is not allowed. A 403 for a
        // rate limit, a quota or a missing scope is not about this event at all.
        if (string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase))
        {
            var refused = reasons.Any(GoogleRefusalReasons.Contains);
            if (!refused) return GenericFailure;

            // Google answers a change it does not allow on one of its own kinds of event with
            // 400 "eventTypeRestriction" (seen for a birthday, issue #294); a trusted 403 on such
            // an event means the same thing as far as the user is concerned.
            var typeRestricted = reasons.Contains("eventTypeRestriction", StringComparer.OrdinalIgnoreCase);
            if (typeRestricted || (forbidden && !string.IsNullOrEmpty(evt.ProviderEventType)))
                return GoogleKindMessage(evt.ProviderEventType);
            return forbidden ? $"{provider} doesn't allow your account to change this event." : GenericFailure;
        }

        return forbidden ? $"{provider} doesn't allow your account to change this event." : GenericFailure;
    }

    /// <summary>The explanation for one of Google's own kinds of event, with where to change it instead.</summary>
    internal static string GoogleKindMessage(string? eventType) => eventType?.ToLowerInvariant() switch
    {
        "birthday" =>
            "Google doesn't let other programs change birthday events. Change the birthday on the contact in Google Contacts.",
        "outofoffice" =>
            "Google doesn't let other programs change out of office events. Change it in Google Calendar.",
        "focustime" =>
            "Google doesn't let other programs change focus time events. Change it in Google Calendar.",
        "workinglocation" =>
            "Google doesn't let other programs change working location events. Change it in Google Calendar.",
        "fromgmail" =>
            "Google doesn't let other programs change events Gmail created. Change it from the original message in Gmail.",
        _ =>
            "Google doesn't let other programs change this kind of event. Change it in Google Calendar.",
    };
}
