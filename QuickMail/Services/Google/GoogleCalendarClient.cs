using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>
/// Minimal client for the Google Calendar v3 REST API (calendar sync read-down v1), mirroring
/// <see cref="GooglePeopleClient"/>'s shape: a raw <see cref="HttpClient"/> that attaches the
/// bearer token per request and follows <c>nextPageToken</c> until the collection is exhausted.
/// Only the one read endpoint v1 needs is exposed: the primary calendar's events, with
/// <c>singleEvents=true</c> so recurring series arrive as server-expanded occurrences (each with
/// its own id) — no local RRULE handling, matching the Graph calendarView approach. A raw
/// HttpClient keeps the published binary small; no new NuGet dependency.
/// </summary>
public sealed class GoogleCalendarClient : IDisposable
{
    private const string BaseUrl = "https://www.googleapis.com/calendar/v3";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IGoogleOAuthService _oauth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GoogleCalendarClient(IGoogleOAuthService oauth, HttpClient? http = null)
    {
        _oauth    = oauth;
        _http     = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    /// <summary>
    /// All events on the user's PRIMARY calendar within the UTC window, as server-expanded
    /// occurrences (<c>singleEvents=true</c>), following <c>nextPageToken</c> until exhausted.
    /// </summary>
    internal async Task<List<GoogleCalendarEvent>> GetPrimaryEventsAsync(
        string username, DateTime timeMinUtc, DateTime timeMaxUtc, CancellationToken ct = default)
    {
        var basePath = "calendars/primary/events"
            + $"?timeMin={Uri.EscapeDataString(Rfc3339(timeMinUtc))}"
            + $"&timeMax={Uri.EscapeDataString(Rfc3339(timeMaxUtc))}"
            + "&singleEvents=true&maxResults=250";

        var all = new List<GoogleCalendarEvent>();
        string? pageToken = null;
        do
        {
            var token = await _oauth.GetAccessTokenAsync(username, ct);
            var url = $"{BaseUrl}/{basePath}" +
                      (string.IsNullOrEmpty(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Calendar API request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(body, 500)}");
            }

            var page = await resp.Content.ReadFromJsonAsync<GoogleCalendarEventsResponse>(JsonOpts, ct);
            if (page?.Items != null) all.AddRange(page.Items);
            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return all;
    }

    private static string Rfc3339(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

// ── Calendar API DTOs ────────────────────────────────────────────────────────

internal sealed class GoogleCalendarEventsResponse
{
    [JsonPropertyName("items")]         public List<GoogleCalendarEvent>? Items { get; set; }
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

/// <summary>One event occurrence from the Calendar API (<c>singleEvents=true</c>).</summary>
internal sealed class GoogleCalendarEvent
{
    [JsonPropertyName("id")]          public string Id { get; set; } = string.Empty;
    /// <summary>"confirmed" | "tentative" | "cancelled".</summary>
    [JsonPropertyName("status")]      public string? Status { get; set; }
    [JsonPropertyName("summary")]     public string? Summary { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("location")]    public string? Location { get; set; }
    [JsonPropertyName("organizer")]   public GoogleEventOrganizer? Organizer { get; set; }
    [JsonPropertyName("start")]       public GoogleEventTime? Start { get; set; }
    [JsonPropertyName("end")]         public GoogleEventTime? End { get; set; }
    [JsonPropertyName("attendees")]   public List<GoogleEventAttendee>? Attendees { get; set; }
}

/// <summary>Either <c>dateTime</c> (timed, RFC3339 with offset) or <c>date</c> (all-day, "yyyy-MM-dd").</summary>
internal sealed class GoogleEventTime
{
    [JsonPropertyName("dateTime")] public string? DateTime { get; set; }
    [JsonPropertyName("date")]     public string? Date { get; set; }
}

internal sealed class GoogleEventOrganizer
{
    [JsonPropertyName("email")]       public string? Email { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

internal sealed class GoogleEventAttendee
{
    [JsonPropertyName("self")]           public bool Self { get; set; }
    /// <summary>"needsAction" | "declined" | "tentative" | "accepted".</summary>
    [JsonPropertyName("responseStatus")] public string? ResponseStatus { get; set; }
}
