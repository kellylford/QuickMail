using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>
/// Minimal client for the Google People API (issue #256), mirroring <c>GraphClient</c>'s shape: a
/// raw <see cref="HttpClient"/> that attaches the bearer token per request and follows
/// <c>nextPageToken</c> until the collection is exhausted. Only the two read endpoints contact sync
/// needs are exposed: saved connections and "other contacts" (prior recipients). A raw HttpClient
/// keeps the published binary small, matching the Graph backend's approach.
/// </summary>
public sealed class GooglePeopleClient : IDisposable
{
    private const string BaseUrl = "https://people.googleapis.com/v1";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IGoogleOAuthService _oauth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GooglePeopleClient(IGoogleOAuthService oauth, HttpClient? http = null)
    {
        _oauth    = oauth;
        _http     = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    /// <summary>Saved contacts (<c>people/me/connections</c>).</summary>
    public Task<List<GooglePerson>> GetConnectionsAsync(string username, CancellationToken ct = default)
        => PageAsync(username, "people/me/connections?personFields=names,emailAddresses&pageSize=1000",
                     other: false, ct);

    /// <summary>"Other contacts" — people emailed but not saved, i.e. prior recipients.</summary>
    public Task<List<GooglePerson>> GetOtherContactsAsync(string username, CancellationToken ct = default)
        => PageAsync(username, "otherContacts?readMask=names,emailAddresses&pageSize=1000",
                     other: true, ct);

    private async Task<List<GooglePerson>> PageAsync(string username, string path, bool other, CancellationToken ct)
    {
        var all = new List<GooglePerson>();
        string? pageToken = null;
        do
        {
            var token = await _oauth.GetAccessTokenAsync(username, ct);
            var url = $"{BaseUrl}/{path}" +
                      (string.IsNullOrEmpty(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"People API request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(body, 500)}");
            }

            var page  = await resp.Content.ReadFromJsonAsync<GooglePeopleResponse>(JsonOpts, ct);
            var items = other ? page?.OtherContacts : page?.Connections;
            if (items != null) all.AddRange(items);
            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return all;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

// ── People API DTOs ──────────────────────────────────────────────────────────

public sealed class GooglePerson
{
    [JsonPropertyName("resourceName")]  public string? ResourceName  { get; set; }
    [JsonPropertyName("names")]         public List<GoogleName>?  Names          { get; set; }
    [JsonPropertyName("emailAddresses")] public List<GoogleEmail>? EmailAddresses { get; set; }
}

public sealed class GoogleName
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class GoogleEmail
{
    [JsonPropertyName("value")] public string? Value { get; set; }
}

internal sealed class GooglePeopleResponse
{
    [JsonPropertyName("connections")]   public List<GooglePerson>? Connections   { get; set; }
    [JsonPropertyName("otherContacts")] public List<GooglePerson>? OtherContacts { get; set; }
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}
