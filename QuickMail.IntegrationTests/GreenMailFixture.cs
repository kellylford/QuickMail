using System.Net.Sockets;
using QuickMail.Models;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Shared fixture for tests that talk to a local GreenMail server (IMAP + SMTP).
///
/// The server is NOT started by the fixture — it is an external process launched by
/// <c>scripts/start-test-servers.ps1</c> locally, or by the CI integration job. The fixture
/// only probes for it:
/// <list type="bullet">
/// <item>Server reachable → tests run.</item>
/// <item>Server absent, <c>QUICKMAIL_IT_SERVERS</c> unset → tests dynamically skip, so a plain
/// <c>dotnet test</c> on the solution stays green on a dev machine without the harness.</item>
/// <item>Server absent, <c>QUICKMAIL_IT_SERVERS=1</c> (set by CI) → tests FAIL: in CI a missing
/// server is an infrastructure error that must never look like a pass.</item>
/// </list>
///
/// GreenMail runs with <c>greenmail.auth.disabled</c>: any user/password is accepted and
/// mailboxes are created on first use, so each test can invent its own isolated user.
/// </summary>
public sealed class GreenMailFixture
{
    // Explicit IPv4 loopback: GreenMail's test profile binds 127.0.0.1 only, and "localhost"
    // resolves IPv6-first (::1) on Windows, which makes both the probe and MailKit fail.
    public const string Host = "127.0.0.1";
    public const int SmtpPort = 3025;
    public const int ImapPort = 3143;

    /// <summary>Domain for test users; resolves nowhere, mailboxes exist only inside GreenMail.</summary>
    public const string MailDomain = "greenmail.test";

    private readonly bool _serversAvailable;

    public GreenMailFixture()
    {
        _serversAvailable = IsPortOpen(Host, SmtpPort) && IsPortOpen(Host, ImapPort);
    }

    /// <summary>
    /// Call first in every test. Skips (local, harness not running) or throws (CI) when
    /// GreenMail is unreachable.
    /// </summary>
    public void RequireServers()
    {
        if (_serversAvailable)
            return;

        if (Environment.GetEnvironmentVariable("QUICKMAIL_IT_SERVERS") == "1")
            throw new InvalidOperationException(
                $"QUICKMAIL_IT_SERVERS=1 but GreenMail is not reachable on {Host}:{SmtpPort}/{ImapPort}. " +
                "The CI harness failed to start; see the greenmail log artifact.");

        Assert.Skip(
            $"GreenMail is not running on {Host}:{SmtpPort}/{ImapPort}. " +
            "Start it with scripts/start-test-servers.ps1 (see docs/TESTING-INTEGRATION.md).");
    }

    /// <summary>Creates a password-auth IMAP/SMTP account pointed at GreenMail, with a unique user.</summary>
    public AccountModel CreateAccount(string userPrefix)
    {
        var user = $"{userPrefix}-{Guid.NewGuid():N}@{MailDomain}";
        return new AccountModel
        {
            AccountName = userPrefix,
            Username    = user,
            AuthType    = AuthType.Password,
            ImapHost    = Host,
            ImapPort    = ImapPort,
            ImapUseSsl  = false,
            SmtpHost    = Host,
            SmtpPort    = SmtpPort,
            SmtpUseSsl  = false,
        };
    }

    private static bool IsPortOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition(Name)]
public class GreenMailCollection : ICollectionFixture<GreenMailFixture>
{
    public const string Name = "GreenMail";
}
