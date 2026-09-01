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
        // Qualified: unqualified this binds to MailKit's type only while
        // System.Security.Authentication is not imported, and a later using-directive would
        // silently redirect it to the type a TLS failure wraps.
        MailKit.Security.AuthenticationException or ServiceNotAuthenticatedException => false,

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
    /// Whose problem an upload failure is, and therefore what the pass should do about it.
    /// </summary>
    public enum UploadScope
    {
        /// <summary>The account was unreachable. Stop, keep everything queued, say nothing.</summary>
        Unreachable,

        /// <summary>
        /// Nothing will upload for this account until the user does something: the sign-in was
        /// refused, the secure connection failed, there is no Drafts folder to file into. Stop, and
        /// keep everything queued -- marking each draft in turn de-queued the entire backlog on one
        /// expired token, and no amount of editing drafts was the fix (#637).
        /// </summary>
        Account,

        /// <summary>
        /// This draft is the problem. Mark it and carry on, or one permanently-refused draft blocks
        /// every draft behind it for ever: the pass replays oldest-first, so it is always first.
        /// </summary>
        Message,
    }

    /// <summary>
    /// What went wrong with one draft upload, and the sentence the user reads about it.
    /// </summary>
    /// <param name="ex">The failure.</param>
    /// <param name="describe">
    /// Turns an exception message into a sentence. Supplied by the caller so this class stays free
    /// of formatting helpers.
    /// </param>
    /// <remarks>
    /// The reason names an action that actually resolves the failure. "Edit the draft and save it
    /// again" is right for a message the server would not take, and useless for a rejected login,
    /// a certificate mismatch, or an account with no Drafts folder -- all of which used to get it,
    /// so the user was sent to edit drafts for ever while nothing changed (#637).
    /// </remarks>
    public static (UploadScope Scope, string Reason) ClassifyUpload(
        Exception ex, Func<string, string> describe)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(describe);

        if (IsTransient(ex)) return (UploadScope.Unreachable, string.Empty);

        if (IsSignInRefused(ex))
            return (UploadScope.Account,
                    "Your mail server would not accept the sign-in for this account, so the drafts "
                  + "waiting on this computer could not be uploaded. Open Manage Accounts and sign "
                  + "in again.");

        if (IsSecureConnectionFailure(ex))
            return (UploadScope.Account,
                    "QuickMail could not make a secure connection to this account's mail server, so "
                  + "the drafts waiting on this computer could not be uploaded. This is a "
                  + "certificate or server-settings problem rather than anything wrong with the "
                  + "drafts.");

        if (IsNoDraftsFolder(ex))
            return (UploadScope.Account,
                    "This account has no Drafts folder on the server, so there is nowhere to upload "
                  + "the drafts waiting on this computer. Connect the account once so QuickMail can "
                  + "find it.");

        return IsServerVerdict(ex)
            ? (UploadScope.Message,
               $"Your mail server refused it: {describe(ex.Message)} Edit the draft and save it "
             + "again to try once more.")
            : (UploadScope.Message,
               $"QuickMail could not upload it: {describe(ex.Message)} The draft is still on this "
             + "computer, and saving it again is what tries once more.");
    }

    /// <summary>The server read the credentials and said no — for the account, not for a draft.</summary>
    private static bool IsSignInRefused(Exception? ex) => ex switch
    {
        null => false,
        MailKit.Security.AuthenticationException or ServiceNotAuthenticatedException => true,
        _ => ex.InnerException != null && IsSignInRefused(ex.InnerException),
    };

    /// <summary>A certificate or protocol mismatch: neither the draft's fault nor its to fix.</summary>
    private static bool IsSecureConnectionFailure(Exception? ex) => ex switch
    {
        null => false,
        SslHandshakeException => true,
        _ => ex.InnerException != null && IsSecureConnectionFailure(ex.InnerException),
    };

    /// <summary>Nowhere to file drafts on this account, which no draft can put right.</summary>
    private static bool IsNoDraftsFolder(Exception? ex) =>
        ex != null &&
        ((ex.Message?.Contains("No Drafts folder", StringComparison.OrdinalIgnoreCase) ?? false) ||
         (ex.InnerException != null && IsNoDraftsFolder(ex.InnerException)));

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
    /// here, and neither is a refused sign-in: neither is the server refusing THIS MESSAGE, and
    /// both are account-scope failures handled before this is asked (#637).
    /// </remarks>
    public static bool IsServerVerdict(Exception? ex) => ex switch
    {
        null => false,

        // A command the server answered with a failure: SMTP status codes, and everything MailKit
        // derives from CommandException for IMAP -- a renamed folder, a message it will not accept.
        SmtpCommandException or CommandException => true,

        // Graph. A null status code means nothing answered, which is the offline case.
        HttpRequestException { StatusCode: not null } => true,

        _ => ex.InnerException != null && IsServerVerdict(ex.InnerException),
    };
}
