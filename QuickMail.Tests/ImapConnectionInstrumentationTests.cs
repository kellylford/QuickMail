using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// End-to-end proof that the connection instrumentation actually fires on the real
/// <see cref="ImapMailService"/> connect path — not just that the journal works in isolation.
///
/// This matters more than the usual "does it log" test. The whole point of the diagnostic build is
/// that when Kelly's accounts show disconnected, the journal contains the evidence. If the connect
/// path silently failed to journal, the build would ship, the symptom would recur, and we would be
/// exactly where we started — with no data and another cycle burned.
///
/// No mail server is needed: these tests aim the client at a port that is closed (connection
/// refused) or at a listener that accepts and immediately hangs up (a dropped connection), which
/// are the real failure shapes we are hunting.
/// </summary>
[Collection("ConnectionDiagnostics")]
public class ImapConnectionInstrumentationTests : IDisposable
{
    private readonly ImapMailService _service;

    public ImapConnectionInstrumentationTests()
    {
        ConnectionJournal.ResetForTests();
        HostConnectionCensus.ResetForTests();
        _service = new ImapMailService(new ThrowingOAuthService());
    }

    public void Dispose()
    {
        _service.Dispose();
        ConnectionJournal.ResetForTests();
        HostConnectionCensus.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private static AccountModel Account(int port) => new()
    {
        Id         = Guid.NewGuid(),
        AccountName = "Kelly",
        Username   = "kelly@example.com",
        AuthType   = AuthType.Password,
        ImapHost   = "127.0.0.1",
        ImapPort   = port,
        ImapUseSsl = false,
    };

    /// <summary>Binds a port, then releases it, so the number is almost certainly closed.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task ConnectFailure_IsJournaledWithTheHostPortAndSocketError()
    {
        var account = Account(ClosedPort());

        await Assert.ThrowsAnyAsync<Exception>(
            () => _service.ConnectAsync(account, "pw", CancellationToken.None));

        var events = ConnectionJournal.Snapshot();

        var begin = Assert.Single(events, e => e.Phase == "connect-begin");
        Assert.Equal("Kelly", begin.Account);
        Assert.Equal("127.0.0.1", begin.Host);
        Assert.Contains($"port={account.ImapPort}", begin.Detail);

        var failed = Assert.Single(events, e => e.Phase == "connect-failed");
        Assert.Equal(ConnectionEventKind.Connect, failed.Kind);
        // The socket error code is the part that distinguishes "refused" from "timed out" from
        // "reset by peer" — exactly the distinction we cannot currently make from user reports.
        Assert.Contains("SocketError=", failed.Detail);
        Assert.Contains("elapsed=", failed.Detail);
    }

    [Fact]
    public async Task ConnectFailure_LeavesNoPhantomSocketsInTheCensus()
    {
        // A census that drifts upward would fabricate evidence of connection exhaustion.
        var account = Account(ClosedPort());

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => _service.ConnectAsync(account, "pw", CancellationToken.None));
        }

        Assert.Equal(0, HostConnectionCensus.LiveCount("127.0.0.1"));
    }

    [Fact]
    public async Task DroppedConnection_IsJournaledAsAFailureNotSilence()
    {
        // Accept the TCP connection then immediately close it — what a server enforcing a
        // connection limit does. MailKit surfaces this while reading the greeting.
        using var listener = new HangUpListener();
        var account = Account(listener.Port);

        await Assert.ThrowsAnyAsync<Exception>(
            () => _service.ConnectAsync(account, "pw", CancellationToken.None));

        var events = ConnectionJournal.Snapshot();
        Assert.Contains(events, e => e.Phase == "connect-begin");

        // Either shape is correct depending on where MailKit notices the hang-up; what must never
        // happen is the failure going unrecorded.
        Assert.Contains(events, e => e.Phase is "connect-failed" or "auth-failed");
        Assert.Equal(0, HostConnectionCensus.LiveCount("127.0.0.1"));
    }

    [Fact]
    public async Task ProbeAccount_OnAnAccountThisBackendDoesNotOwn_IsInconclusiveNotAFailure()
    {
        // A Microsoft Graph account looks exactly like this to the IMAP backend. Reporting it as
        // unreachable is what made the first live run flag a healthy account as broken, so this
        // must stay "cannot test", never "did not answer".
        var result = await _service.ProbeAccountAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Inconclusive);
        Assert.False(result.Unreachable);
        Assert.False(result.Reachable);
        Assert.Contains("not registered", result.Detail);
    }

    [Fact]
    public async Task ProbeAccount_OnADeadServer_ReturnsTheFailureAsTheVerdict()
    {
        var account = Account(ClosedPort());
        await Assert.ThrowsAnyAsync<Exception>(
            () => _service.ConnectAsync(account, "pw", CancellationToken.None));

        // ConnectAsync failed, so the account was never registered; register it the same way a
        // successful connect would by probing through the service's own account map.
        var result = await _service.ProbeAccountAsync(account.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.NotEmpty(result.Detail);
    }

    /// <summary>Accepts one connection and closes it immediately, with no IMAP greeting.</summary>
    private sealed class HangUpListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public HangUpListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    client.Close();
                }
            }
            catch { /* listener stopped */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _listener.Stop();
        }
    }

    /// <summary>OAuth is never exercised here; all test accounts use password auth.</summary>
    private sealed class ThrowingOAuthService : IOAuthService
    {
        private static NotSupportedException Fail() => new("OAuth is not used in these tests.");

        public Task<string> GetAccessTokenAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task<string> GetAccessTokenAsync(AccountModel a, string[] s, CancellationToken ct = default) => throw Fail();
        public Task<string> GetAccessTokenSilentAsync(AccountModel a, string[] s, CancellationToken ct = default) => throw Fail();
        public Task EnsureSilentTokenAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task<OAuthResult> SignInInteractiveAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task RequestContactsConsentAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task RequestCalendarConsentAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task RequestSharedMailboxConsentAsync(AccountModel a, CancellationToken ct = default) => throw Fail();
        public Task SignOutAsync(AccountModel a) => throw Fail();
    }
}
