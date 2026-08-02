using System.Diagnostics;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Tier 3 of the live-content testing plan (issue #304): a thin smoke slice against a REAL
/// mail account on an owned domain. Never gates PRs — it runs on a weekly schedule and manual
/// dispatch (.github/workflows/live-smoke.yml), and skips everywhere the LIVE_* environment
/// variables are absent. This is the only tier that sees real TLS/auth negotiation and
/// provider behavior; keep it small on purpose — minutes, not comprehensive.
///
/// Hygiene rules: every message this class sends carries a run-scoped marker in the subject,
/// and each test deletes what it created before finishing, so repeated runs never pollute the
/// mailbox or each other (the workflow's concurrency group serializes runs besides).
/// </summary>
public sealed class LiveSmokeTests
{
    [Fact]
    [Trait("Category", "LiveSmoke")]
    public async Task Connect_AndListFolders()
    {
        var (account, password) = RequireLiveAccount();
        var ct = TestContext.Current.CancellationToken;

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, password, ct);
        try
        {
            var folders = await imap.GetFoldersAsync(account.Id, ct);
            Assert.Contains(folders, f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    [Trait("Category", "LiveSmoke")]
    public async Task SendToSelf_RoundTrips_ThenCleansUp()
    {
        var (account, password) = RequireLiveAccount();
        var ct = TestContext.Current.CancellationToken;

        // Namespaced subject: unique per run AND per test invocation, so polling can never
        // match a leftover from a previous (crashed) run, and cleanup targets only our mail.
        var runId = Environment.GetEnvironmentVariable("QUICKMAIL_LIVE_RUN_ID") ?? "local";
        var subject = $"[quickmail-live-smoke {runId} {Guid.NewGuid():N}]";

        var smtp = new SmtpService(new NoOpOAuthService(), new NoOpGraphSendService());
        await smtp.SendAsync(new ComposeModel
        {
            AccountId = account.Id,
            To = account.Username,
            Subject = subject,
            Body = "Automated live smoke round-trip. Deleted by the test that sent it.",
        }, account, password, ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, password, ct);
        try
        {
            // Best-effort sweep of leftovers from earlier runs (a message that arrived after a
            // previous run's poll window lingers otherwise — inert, but no reason to keep it).
            var stale = (await imap.GetMessageSummariesAsync(account.Id, "INBOX", 50, ct))
                .Where(s => s.Subject.StartsWith("[quickmail-live-smoke", StringComparison.Ordinal))
                .Select(s => s.MessageId)
                .ToList();
            if (stale.Count > 0)
                await imap.PermanentlyDeleteBatchAsync(account.Id, "INBOX", stale, ct);

            // Real servers deliver in seconds-to-a-minute; poll generously but boundedly.
            var messageId = await PollForSubjectAsync(imap, account.Id, subject, ct);
            Assert.NotNull(messageId);

            await imap.PermanentlyDeleteBatchAsync(account.Id, "INBOX", [messageId!], ct);

            var stillThere = await FindSubjectOnceAsync(imap, account.Id, subject, ct);
            Assert.Null(stillThere);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    // ── Environment plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the account from LIVE_* environment variables (populated from repository
    /// secrets by the workflow). Skips the test when they are absent — locally and on every
    /// PR run, this suite is a no-op by design.
    /// </summary>
    private static (AccountModel Account, string Password) RequireLiveAccount()
    {
        var host = Environment.GetEnvironmentVariable("LIVE_IMAP_HOST");
        var user = Environment.GetEnvironmentVariable("LIVE_USER");
        var password = Environment.GetEnvironmentVariable("LIVE_PASSWORD");
        Assert.SkipWhen(
            string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password),
            "LIVE_* environment variables not configured — live smoke runs only from live-smoke.yml with repository secrets.");

        var account = new AccountModel
        {
            AccountName = "live-smoke",
            Username    = user!,
            AuthType    = AuthType.Password,
            ImapHost    = host!,
            ImapPort    = ParseOr("LIVE_IMAP_PORT", 993),
            ImapUseSsl  = ParseOr("LIVE_IMAP_SSL", 1) == 1,
            // EnvOrNull, not a bare ??: the workflow maps every optional LIVE_* secret into env
            // unconditionally, so an unset secret arrives as an EMPTY STRING rather than absent.
            // `?? host` never fired, and SmtpHost went to "" — MailKit then threw
            // "The host name cannot be empty" before opening a socket.
            SmtpHost    = EnvOrNull("LIVE_SMTP_HOST") ?? host!,
            SmtpPort    = ParseOr("LIVE_SMTP_PORT", 587),
            SmtpUseSsl  = ParseOr("LIVE_SMTP_SSL", 0) == 1, // 0 = STARTTLS on 587 (the default posture)
        };
        return (account, password!);
    }

    /// <summary>
    /// Reads an environment variable, treating empty/whitespace as absent. Required because the
    /// workflow defines every optional LIVE_* variable whether or not its secret exists, so
    /// "unset" reaches us as "" and a plain null-coalesce would silently accept the empty value.
    /// </summary>
    private static string? EnvOrNull(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // Numeric fallbacks were never exposed to this bug: int.TryParse("") fails, so an empty
    // value already falls through to the default.
    private static int ParseOr(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    // ── Polling helpers ──────────────────────────────────────────────────────────

    private static async Task<string?> PollForSubjectAsync(
        ImapMailService imap, Guid accountId, string subject, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(90))
        {
            var found = await FindSubjectOnceAsync(imap, accountId, subject, ct);
            if (found != null)
                return found;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return null;
    }

    private static async Task<string?> FindSubjectOnceAsync(
        ImapMailService imap, Guid accountId, string subject, CancellationToken ct)
    {
        // Scans only the newest 50 summaries — fine for the DEDICATED smoke mailbox this tier
        // assumes; a busy shared mailbox could push our message past the window and flake.
        var summaries = await imap.GetMessageSummariesAsync(accountId, "INBOX", 50, ct);
        return summaries.FirstOrDefault(s => s.Subject.Contains(subject, StringComparison.Ordinal))?.MessageId;
    }
}
