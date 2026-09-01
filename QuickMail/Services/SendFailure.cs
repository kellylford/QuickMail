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
/// Whether a failure will pass on its own, or is a verdict that will repeat (#637).
/// <para>The draft upload pass asks this and nothing else. "Will pass" means STOP: the account is
/// almost certainly still unreachable, and trying the remaining drafts would spend a connection
/// timeout each, so they wait for the next sweep and stay visible marked "not on server". A
/// verdict means the opposite — that draft is marked with what the server said and the pass
/// carries on, because it replays oldest-first and a permanently-refused draft would otherwise be
/// first every time, silently blocking every draft behind it.</para>
/// <para>Deliberately narrow and closed: anything unrecognised is NOT transient, so it is reported
/// rather than retried forever.</para>
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

        // The backend refusing to hand out a client for an account it has not connected. It is
        // a local state, not a verdict on the draft, and it clears the moment the account
        // connects — so treating it as permanent took the draft out of the queue and told the
        // user their server had refused it, naming an account GUID (#637).
        InvalidOperationException { Message: var m } when m.Contains("is not connected", StringComparison.Ordinal) => true,

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

    /// <summary>
    /// True when the server actually answered and its answer is what failed, as opposed to the
    /// attempt never getting that far.
    /// </summary>
    /// <remarks>
    /// Asked only so that what the user reads does not claim more than is known. The upload pass
    /// used to write "Your mail server refused it: …" for everything <see cref="IsTransient"/>
    /// declined, and that set is closed and narrow by design -- so a KeyNotFoundException from an
    /// account lookup, a FormatException from parsing an id, or any plain bug in the append path
    /// reached the user, on the durable field, as a verdict from their mail server, with an
    /// instruction to save again that could not work. A handshake that fails is deliberately NOT
    /// here: a certificate or protocol mismatch is not the server refusing the message (#637).
    /// </remarks>
    public static bool IsServerVerdict(Exception? ex) => ex switch
    {
        null => false,

        // The server read the credentials and said no.
        AuthenticationException or ServiceNotAuthenticatedException => true,

        // A command the server answered with a failure: SMTP status codes, and everything MailKit
        // derives from CommandException for IMAP -- a renamed folder, a message it will not accept.
        SmtpCommandException or CommandException => true,

        // Graph. A null status code means nothing answered, which is the offline case.
        HttpRequestException { StatusCode: not null } => true,

        _ => ex.InnerException != null && IsServerVerdict(ex.InnerException),
    };
}
