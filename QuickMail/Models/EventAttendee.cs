using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MimeKit;

namespace QuickMail.Models;

/// <summary>An attendee's answer to an invitation, as iCalendar's PARTSTAT and the providers report it.</summary>
public enum AttendeeResponse
{
    /// <summary>Invited, no answer yet (PARTSTAT=NEEDS-ACTION, Google "needsAction", Graph "none"/"notResponded").</summary>
    NoReply,
    Accepted,
    Tentative,
    Declined,
}

/// <summary>
/// One person invited to an appointment (issue #866). Stored on <see cref="CalendarEvent.Attendees"/>,
/// persisted as JSON in the <c>attendees_json</c> column, written to the provider as an iCalendar
/// ATTENDEE (CalDAV), a Google <c>attendees</c> entry or a Graph <c>attendees</c> entry.
/// </summary>
public sealed class EventAttendee
{
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AttendeeResponse Response { get; set; } = AttendeeResponse.NoReply;

    /// <summary>The name if there is one, otherwise the address.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Email : Name;

    /// <summary>The response as the details pane says it.</summary>
    public string ResponseText => Response switch
    {
        AttendeeResponse.Accepted  => "accepted",
        AttendeeResponse.Tentative => "tentative",
        AttendeeResponse.Declined  => "declined",
        _                          => "no reply",
    };

    public override string ToString() => $"{DisplayName} ({ResponseText})";

    /// <summary>Same person: addresses compare case-insensitively.</summary>
    public bool IsSameAddress(string? email)
        => !string.IsNullOrWhiteSpace(email)
           && string.Equals(Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parsing, formatting and persistence helpers for an appointment's attendee list.</summary>
public static class EventAttendees
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Stored(string Email, string Name, AttendeeResponse Response);

    /// <summary>Serializes for the <c>attendees_json</c> column. Empty string for no attendees.</summary>
    public static string ToJson(IReadOnlyList<EventAttendee> attendees)
        => attendees.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(attendees.Select(a => new Stored(a.Email, a.Name, a.Response)).ToList(), JsonOptions);

    /// <summary>Reads the <c>attendees_json</c> column. Anything unreadable is treated as no attendees.</summary>
    public static List<EventAttendee> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Stored>>(json, JsonOptions) ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Email))
                .Select(s => new EventAttendee { Email = s.Email, Name = s.Name ?? string.Empty, Response = s.Response })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The attendee list as the editor's Invite field shows it: <c>Name &lt;address&gt;</c> or the
    /// bare address, separated by semicolons — the form <see cref="TryParse"/> reads back.
    /// </summary>
    public static string Format(IEnumerable<EventAttendee> attendees)
        => string.Join("; ", attendees.Select(a =>
            string.IsNullOrWhiteSpace(a.Name) ? a.Email : new MailboxAddress(a.Name, a.Email).ToString()));

    /// <summary>
    /// Reads the Invite field: addresses separated by semicolons or commas, each either bare or as
    /// <c>Name &lt;address&gt;</c>. Duplicates (by address) collapse to the first. Returns false
    /// with the offending entry in <paramref name="invalid"/> when one is not an email address.
    /// <paramref name="previous"/> supplies each surviving attendee's response, so editing the list
    /// does not reset the answers already received.
    /// </summary>
    public static bool TryParse(string? text, IReadOnlyList<EventAttendee>? previous,
                                out List<EventAttendee> attendees, out string invalid)
    {
        attendees = [];
        invalid = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var raw in SplitEntries(text))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            if (!MailboxAddress.TryParse(entry, out var mailbox)
                || string.IsNullOrWhiteSpace(mailbox.Address)
                || !LooksLikeAddress(mailbox.Address))
            {
                invalid = entry;
                attendees = [];
                return false;
            }
            var email = mailbox.Address.Trim();
            if (attendees.Any(a => a.IsSameAddress(email))) continue;
            var before = previous?.FirstOrDefault(p => p.IsSameAddress(email));
            attendees.Add(new EventAttendee
            {
                Email = email,
                Name = string.IsNullOrWhiteSpace(mailbox.Name) ? before?.Name ?? string.Empty : mailbox.Name.Trim(),
                Response = before?.Response ?? AttendeeResponse.NoReply,
            });
        }
        return true;
    }

    /// <summary>True when both lists invite the same addresses, ignoring order and case.</summary>
    public static bool SameAddresses(IReadOnlyList<EventAttendee> a, IReadOnlyList<EventAttendee> b)
        => a.Count == b.Count && a.All(x => b.Any(y => y.IsSameAddress(x.Email)));

    /// <summary>
    /// Splits on semicolons and commas that are not inside a quoted display name, so
    /// <c>"Doe, Jane" &lt;jane@example.com&gt;</c> stays one entry.
    /// </summary>
    private static IEnumerable<string> SplitEntries(string text)
    {
        var start = 0;
        var quoted = false;
        var angle = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '<') angle = true;
            else if (!quoted && c == '>') angle = false;
            else if (!quoted && !angle && (c == ';' || c == ','))
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>MimeKit accepts a bare word as a local-part; an invitation needs a real domain.</summary>
    private static bool LooksLikeAddress(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 && !address.Any(char.IsWhiteSpace);
    }
}
