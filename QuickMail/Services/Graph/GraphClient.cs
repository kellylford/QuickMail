using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services.Graph;

/// <summary>
/// Thin <see cref="HttpClient"/> wrapper over the Microsoft Graph v1.0 endpoint. Adds the bearer
/// token (Graph scopes) per request, follows <c>@odata.nextLink</c> for collections, and retries
/// on HTTP 429 honoring <c>Retry-After</c>. A raw HttpClient (not the Graph SDK) keeps the
/// published binary small, per the dev spec.
/// </summary>
public sealed class GraphClient : IDisposable
{
    private const string BaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IOAuthService _oauth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GraphClient(IOAuthService oauth, HttpClient? http = null)
    {
        _oauth = oauth;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    public async Task<T?> GetAsync<T>(AccountModel account, string path, CancellationToken ct = default)
    {
        using var resp = await SendAsync(account, HttpMethod.Get, path, null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>GET a collection, following <c>@odata.nextLink</c> until exhausted.</summary>
    public async Task<List<T>> GetAllPagesAsync<T>(AccountModel account, string path, CancellationToken ct = default)
    {
        var all = new List<T>();
        string? next = path;
        while (!string.IsNullOrEmpty(next))
        {
            using var resp = await SendAsync(account, HttpMethod.Get, next, null, ct);
            await EnsureSuccessAsync(resp, ct);
            var page = await resp.Content.ReadFromJsonAsync<GraphCollection<T>>(JsonOpts, ct);
            if (page?.Value != null) all.AddRange(page.Value);
            next = page?.NextLink; // absolute URL; SendAsync passes it through unchanged
        }
        return all;
    }

    public async Task PatchAsync(AccountModel account, string path, object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var resp = await SendAsync(account, HttpMethod.Patch, path, json, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        AccountModel account, HttpMethod method, string pathOrUrl, string? jsonBody, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : BaseUrl + pathOrUrl;

        // Up to 3 attempts to ride out HTTP 429 throttling.
        for (int attempt = 0; ; attempt++)
        {
            var token = await _oauth.GetAccessTokenAsync(account, OAuthService.GraphMailScopes, ct);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != (HttpStatusCode)429 || attempt >= 2)
                return resp;

            var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            resp.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Graph request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(body, 500)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
