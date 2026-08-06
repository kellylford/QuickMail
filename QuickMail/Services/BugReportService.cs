using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Submits user-initiated bug reports through QuickMail's relay (see relay/README.md and
/// issue #501) — the user is never asked to sign into GitHub. Falls back to a pre-filled
/// issue URL + clipboard text (<see cref="BuildFallbackUrl"/>/<see cref="BuildReportText"/>)
/// if the relay is unavailable or fails.
///
/// The relay, not this app, holds the credential that can write to the repository. What
/// ships here is a relay key that only means "may file an issue on kellylford/QuickMail" —
/// it is assumed extractable from the binary, and is rotatable without touching any GitHub
/// account. Do not reintroduce a GitHub token into this class; that was the #222/#501 defect.
/// </summary>
public partial class BugReportService : IBugReportService, IDisposable
{
    private const string RepoOwner = "kellylford";
    private const string RepoName  = "QuickMail";

    // Releases before the relay cached a real GitHub PAT here. Nothing reads it any more, so
    // on first run of an updated build we delete it rather than leave a live credential
    // sitting in the user's Credential Manager forever.
    private const string LegacyTokenCredentialKey = "QuickMail.BugReportService.AppOwnedToken";

    private readonly string _relayUrl;
    private readonly string _relayKey;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public BugReportService(ICredentialService credentials) : this(credentials, new HttpClientHandler())
    {
    }

    // Internal overload so tests can substitute a fake HttpMessageHandler instead of hitting the
    // real relay, and pin the relay coordinates. RelayUrl/RelayKey are baked in at build time
    // (empty in most builds, real values on release CI), so tests must inject known values rather
    // than depend on whichever the build happens to carry.
    internal BugReportService(
        ICredentialService credentials,
        HttpMessageHandler handler,
        string? relayUrl = null,
        string? relayKey = null)
    {
        _relayUrl = relayUrl ?? RelayUrl;
        _relayKey = relayKey ?? RelayKey;

        PurgeLegacyToken(credentials);

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QuickMail", Helpers.AppVersion.Display));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<BugReportResult> SubmitAsync(BugReportModel report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_relayUrl) || string.IsNullOrWhiteSpace(_relayKey))
            return BugReportResult.Failed("This build has no relay configured for automatic submission.");

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, _relayUrl);
            request.Headers.Add("X-QuickMail-Key", _relayKey);

            // Labels are applied by the relay, not sent from here: a client that names its own
            // labels lets anyone holding the extracted key apply arbitrary ones.
            var payload = JsonSerializer.Serialize(new
            {
                title = report.Summary,
                body = BuildReportText(report),
            });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogService.Log($"BugReportService: submit failed, status={(int)response.StatusCode}");
                return BugReportResult.Failed(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Too many reports were sent from this network recently. Try again in a minute."
                    : $"The bug-report relay returned status {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(responseText);
            var issueUrl = doc.RootElement.TryGetProperty("issueUrl", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrEmpty(issueUrl))
                return BugReportResult.Failed("The bug-report relay did not return an issue URL.");

            return BugReportResult.Succeeded(issueUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            LogService.Log("BugReportService: submit exception", ex);
            return BugReportResult.Failed("Could not reach the bug-report relay.");
        }
    }

    // Best-effort: a Credential Manager that refuses the delete must not stop the user filing a
    // bug. The stale entry is inert either way — nothing reads that key any more.
    private static void PurgeLegacyToken(ICredentialService credentials)
    {
        try
        {
            if (!string.IsNullOrEmpty(credentials.GetSecret(LegacyTokenCredentialKey)))
            {
                credentials.DeleteSecret(LegacyTokenCredentialKey);
                LogService.Log("BugReportService: removed the pre-relay GitHub token from the credential store.");
            }
        }
        catch (Exception ex)
        {
            LogService.Log("BugReportService: could not remove the pre-relay token", ex);
        }
    }

    // Browsers/shell APIs impose practical URL length limits; a very long report would
    // otherwise silently fail to open via ShellExecute. The full, untruncated text always
    // reaches the user separately via clipboard copy (ReportBugViewModel.CopyAndOpen), so
    // truncating just this URL loses nothing the user can't already paste in full.
    private const int MaxFallbackUrlBodyLength = 4000;

    public string BuildFallbackUrl(BugReportModel report)
    {
        var title = Uri.EscapeDataString(report.Summary ?? string.Empty);
        var body  = Uri.EscapeDataString(Truncate(BuildReportText(report), MaxFallbackUrlBodyLength));
        return $"https://github.com/{RepoOwner}/{RepoName}/issues/new?title={title}&body={body}&labels=bug,user-reported";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength
            ? text
            : text[..maxLength] + "\n\n…(truncated — the full report was copied to your clipboard)";

    public string BuildReportText(BugReportModel report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### What happened");
        sb.AppendLine(report.WhatHappened);

        if (!string.IsNullOrWhiteSpace(report.WhatExpected))
        {
            sb.AppendLine().AppendLine("### What you expected");
            sb.AppendLine(report.WhatExpected);
        }

        if (!string.IsNullOrWhiteSpace(report.StepsToReproduce))
        {
            sb.AppendLine().AppendLine("### Steps to reproduce");
            sb.AppendLine(report.StepsToReproduce);
        }

        sb.AppendLine().AppendLine("### Environment");
        sb.AppendLine($"- QuickMail version: {Helpers.AppVersion.Display}");
        sb.AppendLine($"- OS: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"- .NET runtime: {Environment.Version}");

        if (report.Context is { } ctx)
        {
            sb.AppendLine($"- Theme: {ctx.Theme}");
            sb.AppendLine($"- View: {ctx.View}");
            sb.AppendLine($"- Sort: {ctx.Sort}");
            if (!string.IsNullOrWhiteSpace(ctx.MessageOpenMode))
                sb.AppendLine($"- Message open mode: {ctx.MessageOpenMode}");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
