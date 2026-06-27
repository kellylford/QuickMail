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

    private readonly HttpClient _http;
    private bool _disposed;

    public UpdateCheckService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QuickMail", CurrentVersion()));
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http.GetStringAsync(ApiUrl, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl) ||
                !root.TryGetProperty("html_url", out var urlEl))
                return null;

            var tag = tagEl.GetString()?.TrimStart('v') ?? "";
            var url = urlEl.GetString() ?? "";

            if (!Version.TryParse(tag, out var remote) ||
                !Version.TryParse(CurrentVersion(), out var current))
                return null;

            return remote > current ? new UpdateInfo(tag, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            LogService.Debug($"UpdateCheckService: {ex.Message}");
            return null;
        }
    }

    private static string CurrentVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
