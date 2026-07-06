using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Result of a bug-report submission attempt.</summary>
public sealed class BugReportResult
{
    public bool Success { get; }
    public string? IssueUrl { get; }
    public string? ErrorMessage { get; }

    private BugReportResult(bool success, string? issueUrl, string? errorMessage)
    {
        Success = success;
        IssueUrl = issueUrl;
        ErrorMessage = errorMessage;
    }

    public static BugReportResult Succeeded(string issueUrl) => new(true, issueUrl, null);
    public static BugReportResult Failed(string errorMessage) => new(false, null, errorMessage);
}

public interface IBugReportService
{
    /// <summary>
    /// Submits the report directly to GitHub using the app-owned credential. Never throws —
    /// failures (missing credential, network error, non-success API response) are reported
    /// via <see cref="BugReportResult.Success"/> being false.
    /// </summary>
    Task<BugReportResult> SubmitAsync(BugReportModel report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a pre-filled GitHub "new issue" URL for the fallback path. The user still needs
    /// their own GitHub account to submit through this URL — see the fallback-path nuance in
    /// docs/planning/bug-reporting-pm-dev-spec.md §2.1.
    /// </summary>
    string BuildFallbackUrl(BugReportModel report);

    /// <summary>The full report text, formatted exactly as it would be sent, for clipboard copy.</summary>
    string BuildReportText(BugReportModel report);
}
