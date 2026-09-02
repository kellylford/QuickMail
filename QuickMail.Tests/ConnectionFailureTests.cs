using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The classifier is the one decision that separates "queue it and try later" from "show the user
/// the error". Getting a transport failure wrong strands a message in the compose window on airport
/// wifi; getting a server rejection wrong hides a bounced address inside the Outbox. Both directions
/// are pinned here for every type the send, draft and reading paths can throw.
/// </summary>
public class ConnectionFailureTests
{
    public static IEnumerable<object[]> Transport() => Rows(
        new SocketException((int)SocketError.HostUnreachable),
        new IOException("connection reset"),
        new TimeoutException(),
        new ServiceNotConnectedException(),
        new ImapProtocolException("bye"),
        new SmtpProtocolException("bye"),
        new AccountNotConnectedException(Guid.NewGuid()),
        new HttpRequestException("name resolution failed"),
        new HttpRequestException("gateway", null, HttpStatusCode.ServiceUnavailable),
        new HttpRequestException("gateway", null, HttpStatusCode.BadGateway),
        new HttpRequestException("gateway", null, HttpStatusCode.GatewayTimeout),
        new WebException("dns", WebExceptionStatus.NameResolutionFailure),
        new InvalidOperationException("wrapper", new SocketException((int)SocketError.TimedOut)),
        new AggregateException(new SocketException((int)SocketError.TimedOut)),
        new AggregateException(new InvalidOperationException("outer", new IOException("inner")))
    );

    public static IEnumerable<object[]> ServerAnswered() => Rows(
        new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 no such user"),
        new ImapCommandException(ImapCommandResponse.No, "NO [ALERT] denied"),
        new MailKit.Security.AuthenticationException("bad password"),
        new SaslException("XOAUTH2", SaslErrorCode.InvalidChallenge, "denied"),
        new InteractiveSignInRequiredException("sign in"),
        new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
        new HttpRequestException("not found", null, HttpStatusCode.NotFound),
        new HttpRequestException("throttled", null, HttpStatusCode.TooManyRequests),
        new HttpRequestException("bad request", null, HttpStatusCode.BadRequest),
        new HttpRequestException("server error", null, HttpStatusCode.InternalServerError),
        new FileNotFoundException("attachment gone"),
        new InvalidOperationException("Network is disabled in --ui-probe mode; sending is forbidden."),
        new ArgumentException("nonsense"),
        // A server rejection wrapped around a transport failure is still a rejection: the server spoke.
        new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "554",
            new SocketException((int)SocketError.ConnectionReset))
    );

    private static IEnumerable<object[]> Rows(params Exception[] exceptions) => exceptions.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(Transport))]
    public void TransportFailuresQueue(Exception ex)
    {
        Assert.True(ConnectionFailure.IsConnectionFailure(ex));
    }

    [Theory]
    [MemberData(nameof(ServerAnswered))]
    public void ServerRejectionsDoNot(Exception ex)
    {
        Assert.False(ConnectionFailure.IsConnectionFailure(ex));
    }

    [Fact]
    public void TimeoutCancellationIsTransport()
    {
        // The compose window sends under a 30-second CancellationTokenSource; when that fires the
        // caller's own token is still live, so the cancellation means "the server never answered".
        using var callerCts = new CancellationTokenSource();
        Assert.True(ConnectionFailure.IsConnectionFailure(new OperationCanceledException(), callerCts.Token));
        Assert.True(ConnectionFailure.IsConnectionFailure(new TaskCanceledException(), callerCts.Token));
    }

    [Fact]
    public void UserCancellationIsNotTransport()
    {
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        Assert.False(ConnectionFailure.IsConnectionFailure(new OperationCanceledException(callerCts.Token), callerCts.Token));
    }

    [Fact]
    public void HttpTimeoutWrappingATimeoutExceptionIsTransport()
    {
        // HttpClient on .NET 8 reports its own timeout as TaskCanceledException with an inner TimeoutException.
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException());
        Assert.True(ConnectionFailure.IsConnectionFailure(ex));
    }

    [Fact]
    public void AccountNotConnectedCarriesTheAccountId()
    {
        var id = Guid.NewGuid();
        var ex = new AccountNotConnectedException(id);
        Assert.Equal(id, ex.AccountId);
        Assert.Contains(id.ToString(), ex.Message, StringComparison.Ordinal);
        // It stays an InvalidOperationException so existing catch blocks keep working.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
