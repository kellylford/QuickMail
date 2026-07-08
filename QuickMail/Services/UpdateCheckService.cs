using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using Velopack;
using Velopack.Sources;

namespace QuickMail.Services;

public class UpdateCheckService : IUpdateCheckService, IDisposable
{
    private const string RepoUrl = "https://github.com/kellylford/QuickMail";
    private const string ApiUrl = "https://api.github.com/repos/kellylford/QuickMail/releases/latest";

    // Reflection result is static for the lifetime of the app — compute once. Uses the shared
    // display version so a hotfix build (e.g. 0.7.9.1) compares at full precision and does not
    // treat its own release as a newer version.
    private static readonly string _currentVersion = Helpers.AppVersion.Display;

    private readonly HttpClient _http;
    // Internal lifetime token: cancelled in Dispose() so an in-flight request on app exit
    // is cooperatively cancelled (clean OperationCanceledException) rather than left to either
    // the 10s HttpClient timeout or an ObjectDisposedException from disposing _http mid-request.
    private readonly CancellationTokenSource _cts = new();
    // Test/debug override (--updateFeed): a local folder or URL holding vpk pack output,
    // so the full download-and-apply cycle can be exercised without publishing a release.
    private readonly string? _feedOverride;
    private bool _disposed;

    public UpdateCheckService(string? updateFeedOverride = null)
    {
        _feedOverride = updateFeedOverride;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QuickMail", _currentVersion));
    }

    public async Task<Models.UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Velopack path only applies when running from a Velopack install
        // (%LocalAppData%\QuickMail\current\). The portable exe and dev builds fall through
        // to the GitHub API check below, which can only notify — not silently update.
        var mgr = CreateVelopackManager();
        if (mgr is not null)
            return await CheckViaVelopackAsync(mgr).ConfigureAwait(false);

        return await CheckViaGitHubApiAsync(cancellationToken).ConfigureAwait(false);
    }

    private UpdateManager? CreateVelopackManager()
    {
        try
        {
            UpdateManager mgr;
            if (_feedOverride is null)
            {
                mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            }
            else
            {
                LogService.Log($"UpdateCheckService: using update feed override: {_feedOverride}");
                mgr = new UpdateManager(_feedOverride);
            }
            return mgr.IsInstalled ? mgr : null;
        }
        catch (Exception ex)
        {
            LogService.Debug($"UpdateCheckService: Velopack manager unavailable: {ex.Message}");
            return null;
        }
    }

    private async Task<Models.UpdateInfo?> CheckViaVelopackAsync(UpdateManager mgr)
    {
        try
        {
            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return null;

            var version = update.TargetFullRelease.Version.ToString();

            // Download in the background on the service's lifetime token (not the caller's
            // short check timeout — the package can be large). Once staged, Update.exe applies
            // it after the app exits, so the next normal launch runs the new version. If the
            // app exits mid-download, the next launch simply re-checks and resumes.
            _ = Task.Run(async () =>
            {
                try
                {
                    await mgr.DownloadUpdatesAsync(update, cancelToken: _cts.Token).ConfigureAwait(false);
                    mgr.WaitExitThenApplyUpdates(update, silent: true, restart: false);
                    LogService.Log($"Update {version} downloaded; it will be applied when QuickMail exits.");
                }
                catch (Exception ex)
                {
                    LogService.Debug($"UpdateCheckService: background download failed: {ex.Message}");
                }
            });

            // Empty URL — the Help menu entry falls back to the releases page. The update
            // itself needs no user action beyond an eventual restart.
            return new Models.UpdateInfo(version, string.Empty);
        }
        catch (Exception ex)
        {
            LogService.Debug($"UpdateCheckService: Velopack check failed: {ex.Message}");
            return null;
        }
    }

    private async Task<Models.UpdateInfo?> CheckViaGitHubApiAsync(CancellationToken cancellationToken)
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

            return remote > current ? new Models.UpdateInfo(tag, url) : null;
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
