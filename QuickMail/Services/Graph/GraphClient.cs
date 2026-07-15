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
    private readonly TimeSpan _retryDelayWhenNoHeader;

    public GraphClient(IOAuthService oauth, HttpClient? http = null, TimeSpan? defaultRetryDelay = null)
    {
        _oauth = oauth;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
        // Used only when a 429 response omits a Retry-After header. Injectable so tests don't wait.
        _retryDelayWhenNoHeader = defaultRetryDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task<T?> GetAsync<T>(AccountModel account, string path, CancellationToken ct = default)
    {
        using var resp = await SendAsync(account, HttpMethod.Get, path, null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>GET a collection, following <c>@odata.nextLink</c> until exhausted.</summary>
    public Task<List<T>> GetAllPagesAsync<T>(AccountModel account, string path, CancellationToken ct = default)
        => GetAllPagesAsync<T>(account, path, null, ct);

    /// <summary>
    /// GET a collection with an explicit token scope set, following <c>@odata.nextLink</c> until
    /// exhausted. Used by contact sync (issue #256) to request Graph <c>Contacts.Read</c>/
    /// <c>People.Read</c> rather than the account's default mail scopes. A null <paramref name="scopes"/>
    /// uses the account's default scopes (identical to the two-argument overload).
    /// </summary>
    public async Task<List<T>> GetAllPagesAsync<T>(AccountModel account, string path, string[]? scopes, CancellationToken ct = default)
    {
        var all = new List<T>();
        string? next = path;
        while (!string.IsNullOrEmpty(next))
        {
            using var resp = await SendAsync(account, HttpMethod.Get, next, null, scopes, ct);
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
        using var resp = await SendAsync(account, HttpMethod.Patch, path,
            () => new StringContent(json, Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// POSTs an already-encoded raw body with an explicit content type. Used by the Graph send
    /// path: <c>/me/sendMail</c> takes a base64-encoded MIME message as <c>text/plain</c>.
    /// </summary>
    public async Task PostRawAsync(AccountModel account, string path, byte[] body, string contentType, CancellationToken ct = default)
    {
        using var resp = await SendAsync(account, HttpMethod.Post, path, () =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>POSTs a JSON body and ignores the response payload (move/copy/folder operations).</summary>
    public async Task PostAsync(AccountModel account, string path, object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var resp = await SendAsync(account, HttpMethod.Post, path,
            () => new StringContent(json, Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// POSTs an already-encoded raw body and deserializes the JSON response. Used to create a draft
    /// from MIME (<c>POST /me/messages</c>), where the created message's id is needed back.
    /// </summary>
    public async Task<T?> PostRawReadAsync<T>(
        AccountModel account, string path, byte[] body, string contentType, CancellationToken ct = default)
    {
        using var resp = await SendAsync(account, HttpMethod.Post, path, () =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return content;
        }, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>DELETEs a resource (permanent message delete, folder delete).</summary>
    public async Task DeleteAsync(AccountModel account, string path, CancellationToken ct = default)
    {
        using var resp = await SendAsync(account, HttpMethod.Delete, path, null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>GETs a raw byte payload (attachment <c>$value</c> download).</summary>
    public async Task<byte[]> GetBytesAsync(AccountModel account, string path, CancellationToken ct = default)
    {
        using var resp = await SendAsync(account, HttpMethod.Get, path, null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private Task<HttpResponseMessage> SendAsync(
        AccountModel account, HttpMethod method, string pathOrUrl, Func<HttpContent>? contentFactory, CancellationToken ct)
        => SendAsync(account, method, pathOrUrl, contentFactory, null, ct);

    private async Task<HttpResponseMessage> SendAsync(
        AccountModel account, HttpMethod method, string pathOrUrl, Func<HttpContent>? contentFactory, string[]? scopes, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : BaseUrl + pathOrUrl;

        // Up to 3 attempts to ride out HTTP 429 throttling.
        for (int attempt = 0; ; attempt++)
        {
            // Default (scopes == null): per-account default scopes (DefaultScopesFor) — `.default` for
            // work/school, explicit Mail.ReadWrite/etc. for personal Microsoft accounts (#217). An
            // explicit scope set is passed only by contact sync (Graph Contacts.Read/People.Read).
            var token = scopes is null
                ? await _oauth.GetAccessTokenAsync(account, ct)
                : await _oauth.GetAccessTokenAsync(account, scopes, ct);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (contentFactory != null)
                req.Content = contentFactory(); // fresh per attempt — content can't be resent after a 429

            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != (HttpStatusCode)429 || attempt >= 2)
                return resp;

            var delay = resp.Headers.RetryAfter?.Delta ?? _retryDelayWhenNoHeader;
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
