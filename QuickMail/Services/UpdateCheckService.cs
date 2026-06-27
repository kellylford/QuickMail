using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class UpdateCheckService : IUpdateCheckService, IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/kellylford/QuickMail/releases/latest";

    // Reflection result is static for the lifetime of the app — compute once.
    private static readonly string _currentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly HttpClient _http;
    // Internal lifetime token: cancelled in Dispose() so an in-flight request on app exit
    // is cooperatively cancelled (clean OperationCanceledException) rather than left to either
    // the 10s HttpClient timeout or an ObjectDisposedException from disposing _http mid-request.
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public UpdateCheckService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QuickMail", _currentVersion));
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            var json = await _http.GetStringAsync(ApiUrl, linked.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl) ||
                !root.TryGetProperty("html_url", out var urlEl))
                return null;

            var tag = tagEl.GetString()?.TrimStart('v') ?? "";
            var url = urlEl.GetString() ?? "";

            if (!Version.TryParse(tag, out var remote) ||
                !Version.TryParse(_currentVersion, out var current))
                return null;

            return remote > current ? new UpdateInfo(tag, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            LogService.Debug($"UpdateCheckService: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();   // signal any in-flight request before releasing the handle
        _cts.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
