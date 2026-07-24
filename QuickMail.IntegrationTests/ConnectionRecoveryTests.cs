using System.Diagnostics;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Connection-fault regression tests (live-content testing plan §4.4). Each test routes IMAP
/// through a <see cref="TcpRelay"/> so a "server-side" connection drop can be injected
/// mid-session, then asserts the service recovers instead of surfacing the failure:
/// <list type="bullet">
/// <item>#311 — delete after a silent drop must transparently reconnect and complete, never
/// surface "Delete may not have completed".</item>
/// <item>#268/#278/#313 — fetch and folder-count operations must recover after a drop rather
/// than staying stuck on an error or stale state.</item>
/// </list>
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class ConnectionRecoveryTests : IDisposable
{
    private readonly GreenMailFixture _greenMail;
    private readonly TcpRelay _relay;

    public ConnectionRecoveryTests(GreenMailFixture greenMail)
    {
        _greenMail = greenMail;
        _relay = new TcpRelay(GreenMailFixture.Host, GreenMailFixture.ImapPort);
    }

    public void Dispose() => _relay.Dispose();

    private AccountModel CreateRelayedAccount(string prefix)
    {
        var account = _greenMail.CreateAccount(prefix);
        account.ImapPort = _relay.Port; // IMAP through the fault-injecting relay; SMTP direct
        return account;
    }

    [Fact]
    public async Task Delete_AfterKilledConnection_ReconnectsAndCompletes()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = CreateRelayedAccount("del-retry");
        await SeedAsync(account.Username, "to be deleted", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var summary = await WaitForMessageAsync(imap, account.Id, ct);

            // Sever the pooled connection the way a server-side drop does. The next operation
            // rents a dead-on-arrival client (younger than the pool's stale-probe threshold, so
            // it is NOT probed) — exactly the #311 scenario.
            Assert.True(_relay.KillActiveConnections() >= 1, "Kill hit no connections — nothing to recover from.");

            // Must not throw: ExecuteWithReconnectAsync retries once on a fresh connection.
            await imap.PermanentlyDeleteBatchAsync(account.Id, "INBOX", [summary.MessageId], ct);

            var after = await imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct);
            Assert.Empty(after);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    public async Task FetchAndStatus_AfterKilledConnection_RecoverOnRetry()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;
        var account = CreateRelayedAccount("fetch-recover");
        await SeedAsync(account.Username, "survives drops", ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "pw", ct);
        try
        {
            var summary = await WaitForMessageAsync(imap, account.Id, ct);

            // Plain fetches have no retry wrapper, so the FIRST attempt on the dead pooled
            // client may fail — the guarantee under test is recovery: the dead client is
            // discarded when its lease returns, so a subsequent attempt gets a fresh
            // connection and succeeds (#268/#278/#313: recover, don't stay stuck/stale).
            Assert.True(_relay.KillActiveConnections() >= 1, "Kill hit no connections — nothing to recover from.");
            var summaries = await RetryOnceAsync(
                () => imap.GetMessageSummariesAsync(account.Id, "INBOX", 10, ct));
            Assert.Single(summaries);
            Assert.Equal(summary.MessageId, summaries[0].MessageId);

            Assert.True(_relay.KillActiveConnections() >= 1, "Kill hit no connections — nothing to recover from.");
            var (total, unread) = await RetryOnceAsync(
                () => imap.GetInboxStatusAsync(account.Id, ct));
            Assert.Equal(1, total);
            Assert.Equal(1, unread);

            Assert.True(_relay.KillActiveConnections() >= 1, "Kill hit no connections — nothing to recover from.");
            var detail = await RetryOnceAsync(
                () => imap.GetMessageDetailAsync(account.Id, "INBOX", summary.MessageId, ct));
            Assert.Contains("survives drops", detail.PlainTextBody);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// One attempt is allowed to fail with a connection error (no retry wrapper on reads);
    /// the second must succeed. A second failure fails the test — that is the regression.
    /// </summary>
    private static async Task<T> RetryOnceAsync<T>(Func<Task<T>> op)
    {
        try { return await op(); }
        catch (Exception first) when (first is not OperationCanceledException)
        {
            try { return await op(); }
            catch (Exception second) when (second is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Operation failed twice after a connection drop — no recovery. " +
                    $"First: {first.GetType().Name}: {first.Message}; " +
                    $"Second: {second.GetType().Name}: {second.Message}", second);
            }
        }
    }

    private static async Task<MailMessageSummary> WaitForMessageAsync(
        ImapMailService imap, Guid accountId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var summaries = await imap.GetMessageSummariesAsync(accountId, "INBOX", 10, ct);
            if (summaries.Count > 0)
                return summaries[0];
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Seeded message never appeared in the GreenMail INBOX.");
    }

    private static async Task SeedAsync(string recipient, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "connection test";
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
