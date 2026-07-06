using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Submits user-initiated bug reports directly to GitHub Issues using an app-owned,
/// narrowly-scoped credential (see docs/planning/bug-reporting-pm-dev-spec.md, Decision A) —
/// the user is never asked to sign into GitHub. Falls back to a pre-filled issue URL +
/// clipboard text (<see cref="BuildFallbackUrl"/>/<see cref="BuildReportText"/>) if the
/// direct path is unavailable or fails.
/// </summary>
public partial class BugReportService : IBugReportService, IDisposable
{
    private const string RepoOwner = "kellylford";
    private const string RepoName  = "QuickMail";
    private const string ApiUrl    = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues";

    // Distinct from CredentialService's per-account "QuickMail:{Guid}" key shape — this is a
    // single app-owned secret, not tied to any mail account.
    private const string TokenCredentialKey = "QuickMail.BugReportService.AppOwnedToken";

    private static readonly string[] IssueLabels = ["bug", "user-reported"];

    private readonly ICredentialService _credentials;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public BugReportService(ICredentialService credentials) : this(credentials, new HttpClientHandler())
    {
    }

    // Internal overload so tests can substitute a fake HttpMessageHandler instead of hitting
    // the real GitHub API.
    internal BugReportService(ICredentialService credentials, HttpMessageHandler handler)
    {
        _credentials = credentials;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QuickMail", Helpers.AppVersion.Display));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<BugReportResult> SubmitAsync(BugReportModel report, CancellationToken cancellationToken = default)
    {
        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token))
            return BugReportResult.Failed("No app-owned credential is available for automatic submission.");

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = JsonSerializer.Serialize(new
            {
                title = report.Summary,
                body = BuildReportText(report),
                labels = IssueLabels,
            });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogService.Log($"BugReportService: submit failed, status={(int)response.StatusCode}");
                return BugReportResult.Failed($"GitHub returned status {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(responseText);
            var issueUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrEmpty(issueUrl))
                return BugReportResult.Failed("GitHub did not return an issue URL.");

            return BugReportResult.Succeeded(issueUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            LogService.Log("BugReportService: submit exception", ex);
            return BugReportResult.Failed("Could not reach GitHub.");
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

        return sb.ToString();
    }

    // Checks the credential store first (in case a future settings UI ever lets a user override
    // it), otherwise falls back to the build-embedded token and caches it into the store — same
    // resolve-then-cache shape as Quill's effective_github_token(), adapted to this codebase's
    // existing ICredentialService rather than a new storage mechanism.
    private string? ResolveToken()
    {
        var stored = _credentials.GetSecret(TokenCredentialKey);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;

        if (string.IsNullOrWhiteSpace(AppOwnedToken)) return null;

        _credentials.SaveSecret(TokenCredentialKey, AppOwnedToken);
        return AppOwnedToken;
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
