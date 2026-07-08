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
    // Supplies the AutoUpdate setting; null (tests) behaves as AutoUpdate on.
    private readonly IConfigService? _configService;
    private bool _disposed;

    // Installed-path (Velopack) update state. Held so RestartToUpdateAsync can apply the
    // update on demand, and so Dispose can arm apply-on-exit once the download finished.
    private UpdateManager? _velopackManager;
    private Velopack.UpdateInfo? _pendingUpdate;
    private Task? _downloadTask;
    private volatile bool _downloadCompleted;
    private bool _restartRequested;

    public UpdateCheckService(IConfigService? configService = null, string? updateFeedOverride = null)
    {
        _configService = configService;
        _feedOverride = updateFeedOverride;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QuickMail", _currentVersion));
    }

    public bool SelfUpdatePending => _pendingUpdate is not null;

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

    public async Task<bool> RestartToUpdateAsync()
    {
        var mgr = _velopackManager;
        var update = _pendingUpdate;
        if (mgr is null || update is null) return false;
        try
        {
            // The background download normally finished long ago; waiting covers the user
            // racing to the Help menu on a slow connection.
            if (_downloadTask is { } dl) await dl.ConfigureAwait(false);
            if (!_downloadCompleted) return false;

            _restartRequested = true;
            // Preserve /debug, --profileDir, --updateFeed across the update-triggered relaunch.
            mgr.ApplyUpdatesAndRestart(update, Environment.GetCommandLineArgs()[1..]);
            return true; // not reached — ApplyUpdatesAndRestart exits the process
        }
        catch (Exception ex)
        {
            _restartRequested = false;
            LogService.Log($"UpdateCheckService: restart to update failed: {ex.Message}");
            return false;
        }
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
            var whatsNewUrl = $"{RepoUrl}/releases/tag/v{version}";

            // AutoUpdate off: notify-only. Report the update so the Help menu shows it, but
            // stage nothing — SelfUpdatePending stays false, so activating the Help entry
            // opens the release page (the pre-auto-update experience) instead of the
            // restart-to-update dialog.
            if (_configService?.Load().AutoUpdate == false)
            {
                LogService.Log($"Update {version} available; automatic updating is turned off.");
                return new Models.UpdateInfo(version, whatsNewUrl);
            }

            _velopackManager = mgr;
            _pendingUpdate = update;

            // Download in the background on the service's lifetime token (not the caller's
            // short check timeout — the package can be large). The update is applied either
            // when the user chooses "Restart to Update" (RestartToUpdateAsync) or, failing
            // that, on app exit (Dispose arms Update.exe), so the next launch runs the new
            // version. If the app exits mid-download, the next launch re-checks and resumes.
            _downloadTask = Task.Run(async () =>
            {
                try
                {
                    await mgr.DownloadUpdatesAsync(update, cancelToken: _cts.Token).ConfigureAwait(false);
                    _downloadCompleted = true;
                    LogService.Log($"Update {version} downloaded; it will be applied when QuickMail exits.");
                }
                catch (Exception ex)
                {
                    LogService.Debug($"UpdateCheckService: background download failed: {ex.Message}");
                }
            });

            // The release-tag page serves as the "what's new" link in the update dialog.
            return new Models.UpdateInfo(version, whatsNewUrl);
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

        // App is exiting: if a downloaded update was not applied via restart-to-update,
        // arm Update.exe to apply it once this process ends, so the next launch runs the
        // new version. Armed here — not right after the download — so this path and
        // ApplyUpdatesAndRestart can never both spawn an applier.
        if (_downloadCompleted && !_restartRequested &&
            _velopackManager is { } mgr && _pendingUpdate is { } update)
        {
            try
            {
                mgr.WaitExitThenApplyUpdates(update, silent: true, restart: false);
            }
            catch (Exception ex)
            {
                LogService.Debug($"UpdateCheckService: arming apply-on-exit failed: {ex.Message}");
            }
        }

        _cts.Cancel();   // signal any in-flight request before releasing the handle
        _cts.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
