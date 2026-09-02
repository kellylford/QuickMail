using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace QuickMail.Services;

/// <summary>
/// Thrown by a mail backend when an operation is attempted on an account that has no live session.
/// A typed exception rather than a message string, so callers deciding "is this offline?" can match
/// on the type (<see cref="ConnectionFailure.IsConnectionFailure"/>) instead of the text.
/// </summary>
public sealed class AccountNotConnectedException : InvalidOperationException
{
    public AccountNotConnectedException() : base("Account is not connected.") { }
    public AccountNotConnectedException(string message) : base(message) { }
    public AccountNotConnectedException(string message, Exception innerException) : base(message, innerException) { }
    public AccountNotConnectedException(Guid accountId) : base($"Account {accountId} is not connected.")
    {
        AccountId = accountId;
    }

    public Guid AccountId { get; }
}

/// <summary>
/// Decides whether an exception means "the server could not be reached" (queue the work and try again
/// when the network is back) or "the server answered and said no" (the user has to see it). Shared by
/// the compose window, the Outbox, and the reading path so all three agree on what offline looks like.
/// </summary>
/// <remarks>
/// <see cref="ImapMailService"/> keeps its own narrower <c>IsConnectionDrop</c> predicate: that one
/// decides whether a pooled connection is worth retrying once, which is a different question.
/// </remarks>
public static class ConnectionFailure
{
    /// <summary>
    /// True when <paramref name="ex"/>, or anything in its inner chain, is a transport failure.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <param name="callerToken">
    /// The caller's own cancellation token. An <see cref="OperationCanceledException"/> counts as a
    /// timeout (a transport failure) only when this token did not fire; if the caller cancelled, it is
    /// not a verdict about the network at all.
    /// </param>
    public static bool IsConnectionFailure(Exception ex, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // A server that answered — even to say no — is reachable. Any such exception anywhere in the
        // chain settles it, whatever else is wrapped around it.
        var sawTransport = false;
        foreach (var e in Flatten(ex))
        {
            if (IsServerAnswered(e))
                return false;
            if (e is OperationCanceledException)
            {
                if (callerToken.IsCancellationRequested)
                    return false;
                sawTransport = true;
                continue;
            }
            if (IsTransport(e))
                sawTransport = true;
        }
        return sawTransport;
    }

    private static bool IsServerAnswered(Exception e) => e switch
    {
        SmtpCommandException => true,
        ImapCommandException => true,
        MailKit.Security.AuthenticationException => true,   // includes SaslException
        InteractiveSignInRequiredException => true,
        HttpRequestException { StatusCode: { } status } => !IsGatewayFailure(status),
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException => true,
        _ => false,
    };

    private static bool IsTransport(Exception e) => e switch
    {
        SocketException => true,
        IOException => true,
        TimeoutException => true,
        ServiceNotConnectedException => true,
        ImapProtocolException => true,
        SmtpProtocolException => true,
        SslHandshakeException => true,      // captive portals answer TLS with the wrong certificate
        AccountNotConnectedException => true,
        WebException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => IsGatewayFailure(status),
        _ => false,
    };

    // 502/503/504 come from a proxy or gateway that could not reach the real service — transport, not
    // an answer from the mail server itself.
    private static bool IsGatewayFailure(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static IEnumerable<Exception> Flatten(Exception root)
    {
        var stack = new Stack<Exception>();
        stack.Push(root);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (!seen.Add(e)) continue;
            yield return e;
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                    stack.Push(inner);
            }
            else if (e.InnerException != null)
            {
                stack.Push(e.InnerException);
            }
        }
    }
}
