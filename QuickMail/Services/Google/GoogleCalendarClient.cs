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
/// Minimal client for the Google Calendar v3 REST API, mirroring
/// <see cref="GooglePeopleClient"/>'s shape: a raw <see cref="HttpClient"/> that attaches the
/// bearer token per request and follows <c>nextPageToken</c> until the collection is exhausted.
/// Read path: the primary calendar's events, with <c>singleEvents=true</c> so recurring series
/// arrive as server-expanded occurrences (each with its own id) — no local RRULE handling,
/// matching the Graph calendarView approach. Write path: create/patch/delete of single events on
/// the primary calendar (the Google counterpart of the Graph <c>/me/events</c> push). A raw
/// HttpClient keeps the published binary small; no new NuGet dependency.
/// </summary>
public sealed class GoogleCalendarClient : IDisposable
{
    private const string BaseUrl = "https://www.googleapis.com/calendar/v3";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Write bodies omit null fields entirely: Google PATCH treats an explicit null as "clear this
    // field", while an omitted field is left unchanged — mirroring the Graph body-builder's
    // omit-when-blank posture.
    private static readonly JsonSerializerOptions WriteJsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
    /// The user's calendar list (<c>GET users/me/calendarList</c>), following <c>nextPageToken</c>
    /// until exhausted. One entry per calendar the account can see (its own Home/Work/Family plus
    /// any subscribed calendars), each with an id, a display summary, and a <c>primary</c> flag.
    /// </summary>
    internal async Task<List<GoogleCalendarListEntry>> GetCalendarListAsync(
        string username, CancellationToken ct = default)
    {
        var all = new List<GoogleCalendarListEntry>();
        string? pageToken = null;
        do
        {
            var token = await _oauth.GetAccessTokenAsync(username, ct);
            var url = $"{BaseUrl}/users/me/calendarList?maxResults=250" +
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

            var page = await resp.Content.ReadFromJsonAsync<GoogleCalendarListResponse>(JsonOpts, ct);
            if (page?.Items != null) all.AddRange(page.Items);
            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return all;
    }

    /// <summary>
    /// All events on the given calendar (default the user's PRIMARY) within the UTC window, as
    /// server-expanded occurrences (<c>singleEvents=true</c>), following <c>nextPageToken</c> until
    /// exhausted.
    /// </summary>
    internal async Task<List<GoogleCalendarEvent>> GetEventsAsync(
        string username, DateTime timeMinUtc, DateTime timeMaxUtc,
        string calendarId = "primary", CancellationToken ct = default)
    {
        var basePath = $"calendars/{Uri.EscapeDataString(calendarId)}/events"
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

    // ── Write path (single events, targeting the event's own calendar) ──────────
    // calendarId defaults to "primary" but MUST be the calendar the event actually lives on:
    // the read path enumerates every calendar in the account, so an event on a secondary
    // calendar can only be patched/deleted through that calendar's id. Targeting "primary"
    // for such an event returns 404 and surfaces as "could not update the online calendar".

    /// <summary>Creates an event (<c>POST calendars/{calendarId}/events</c>) and returns the server's copy.</summary>
    internal Task<GoogleCalendarEvent> CreateEventAsync(
        string username, GoogleEventWriteBody body, string calendarId = "primary", CancellationToken ct = default)
        => SendWriteAsync(username, HttpMethod.Post,
                          $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);

    /// <summary>Patches an event (<c>PATCH calendars/{calendarId}/events/{id}</c>) and returns the server's copy.
    /// Omitted (null) body fields are left unchanged on the server.</summary>
    internal Task<GoogleCalendarEvent> UpdateEventAsync(
        string username, string eventId, GoogleEventWriteBody body,
        string calendarId = "primary", CancellationToken ct = default)
        => SendWriteAsync(username, HttpMethod.Patch,
                          $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", body, ct);

    /// <summary>Deletes an event (<c>DELETE calendars/{calendarId}/events/{id}</c>).</summary>
    internal async Task DeleteEventAsync(
        string username, string eventId, string calendarId = "primary", CancellationToken ct = default)
    {
        var token = await _oauth.GetAccessTokenAsync(username, ct);
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{BaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Calendar API request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(respBody, 500)}");
        }
    }

    /// <summary>Shared POST/PATCH plumbing: bearer auth (as in the read path), JSON body, JSON response.</summary>
    private async Task<GoogleCalendarEvent> SendWriteAsync(
        string username, HttpMethod method, string path, GoogleEventWriteBody body, CancellationToken ct)
    {
        var token = await _oauth.GetAccessTokenAsync(username, ct);
        using var req = new HttpRequestMessage(method, $"{BaseUrl}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body, options: WriteJsonOpts);

        using var resp = await _http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Calendar API request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(respBody, 500)}");

        return JsonSerializer.Deserialize<GoogleCalendarEvent>(respBody, JsonOpts)
            ?? throw new InvalidOperationException("Calendar API returned no event body.");
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

/// <summary>Paging envelope for <c>users/me/calendarList</c>.</summary>
internal sealed class GoogleCalendarListResponse
{
    [JsonPropertyName("items")]         public List<GoogleCalendarListEntry>? Items { get; set; }
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

/// <summary>One calendar in the account's calendar list.</summary>
internal sealed class GoogleCalendarListEntry
{
    [JsonPropertyName("id")]      public string Id { get; set; } = string.Empty;
    /// <summary>Display name of the calendar (the tree-node label).</summary>
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    /// <summary>True for the account's own primary calendar.</summary>
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    /// <summary>True when the user has removed this calendar from their list — skip it.</summary>
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
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

/// <summary>
/// Request body for creating/patching an event — the Google counterpart of
/// <c>GraphCreateEventBody</c>. Timed events carry RFC3339 UTC <c>start.dateTime</c>/<c>end.dateTime</c>
/// stamps; all-day events carry date-only <c>start.date</c>/<c>end.date</c> with Google's EXCLUSIVE
/// end date. Null fields are omitted from the serialized JSON (PATCH leaves them unchanged).
/// </summary>
internal sealed class GoogleEventWriteBody
{
    [JsonPropertyName("summary")]     public string? Summary { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("location")]    public string? Location { get; set; }
    [JsonPropertyName("start")]       public GoogleEventTime? Start { get; set; }
    [JsonPropertyName("end")]         public GoogleEventTime? End { get; set; }
}
