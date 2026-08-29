using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace QuickMail.Services;

/// <summary>
/// Whether a failed send is worth trying again later (#637).
/// <para>This is the decision that separates the outbox from a way to lose mail. A message queued
/// because the network was down goes out by itself and the user need never think about it; a
/// message queued because the server refused it sits in the queue being refused forever, and the
/// user was told it would be sent. So the transient set is deliberately narrow and closed: it is
/// the failures that mean "this computer could not reach the server", nothing else. Anything the
/// server actually answered — a rejected recipient, a refused login, a message over the size limit
/// — is reported to the user the way it was before there was a queue.</para>
/// <para><see cref="OperationCanceledException"/> is deliberately absent. It carries no information
/// on its own: from a send's own timeout it means the server never answered, and from a caller's
/// token it means the user quit or the sweep was cancelled. Only the call site knows which token
/// fired, so each one decides for itself.</para>
/// </summary>
public static class SendFailure
{
    /// <summary>
    /// True when <paramref name="ex"/> says the account was unreachable, rather than that the
    /// server considered the message or the credentials bad.
    /// </summary>
    public static bool IsTransient(Exception? ex) => ex switch
    {
        null => false,

        // A refused login is not a connectivity problem, and queueing on it would retry a
        // credential the server has already rejected on every sweep.
        AuthenticationException or ServiceNotAuthenticatedException => false,

        // A handshake that fails is nearly always a certificate or protocol mismatch the user has
        // to fix, and its inner exception is usually an IOException — so without this arm the
        // wrapper case at the bottom would read a permanent misconfiguration as a network blip.
        SslHandshakeException => false,

        // The server answered, with a verdict about this message. MailKit reports a rejected
        // recipient as one of these too (ErrorCode RecipientNotAccepted, a 5xx), so it is covered:
        // 4xx is a temporary refusal worth another attempt — greylisting, over-rate, mailbox busy —
        // and 5xx is the server saying no.
        SmtpCommandException smtp => (int)smtp.StatusCode is >= 400 and < 500,

        // Graph. A null status code means the request never got an answer (DNS, socket, TLS), which
        // is the offline case. With a status code the service replied, so only the ones that mean
        // "ask again later" are transient.
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException http => http.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,

        // The transport failed outright, or dropped mid-conversation. This says only "the failure
        // will pass"; it does NOT say the message was never handed over, and it cannot — a
        // dropped connection looks identical either side of the server's acceptance. What it
        // answers here is narrower: telling "the account is still unreachable, try again next sweep"
        // apart from "the server looked at this draft and refused it" — which has to stop
        // being retried, or it blocks every draft behind it forever (#637).
        SocketException or IOException or TimeoutException => true,
        SmtpProtocolException or ProtocolException or ServiceNotConnectedException => true,

        // A wrapper around one of the above — MailKit and HttpClient both nest the real cause.
        _ => ex.InnerException != null && IsTransient(ex.InnerException),
    };
}
