using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Builds a <see cref="MimeMessage"/> from a <see cref="ComposeModel"/> and
/// <see cref="AccountModel"/>, shared by <see cref="SmtpService"/> and
/// <see cref="ImapMailService"/> (draft saving).
/// </summary>
public static partial class MimeMessageBuilder
{
    /// <summary>The User-Agent string for outgoing mail — one source of truth for every sender.</summary>
    internal static readonly string AppUserAgent = "QuickMail/" + AppVersion.Display;

    /// <summary>
    /// Serializes a <see cref="MimeMessage"/> to base64-encoded ASCII bytes — the form Graph's
    /// <c>/me/sendMail</c> and <c>/me/messages</c> endpoints accept as a <c>text/plain</c> body.
    /// Encodes straight to base64 (no intermediate string, and <c>GetBuffer()</c> avoids a copy) to
    /// keep peak memory low for large/attachment-bearing messages.
    /// </summary>
    internal static async Task<byte[]> ToBase64BytesAsync(MimeMessage message, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await message.WriteToAsync(ms, ct);
        int mimeLength = (int)ms.Length;
        var body = new byte[Base64.GetMaxEncodedToUtf8Length(mimeLength)];
        Base64.EncodeToUtf8(ms.GetBuffer().AsSpan(0, mimeLength), body, out _, out _);
        return body;
    }

    /// <summary>
    /// Gives <paramref name="compose"/> the Message-ID it will be sent with, unless it already has
    /// one, and returns it. The send and the copy filed in Sent each build their own
    /// <see cref="MimeMessage"/>; without a shared id each got a fresh one, so a server that files
    /// its own copy of relayed mail ended up holding two messages with nothing to tie them (#811).
    /// The right-hand side is the sender's domain rather than this computer's name, when that
    /// domain is plain ASCII: MimeKit turns a punycode domain back into Unicode when the id is
    /// set, which puts non-ASCII bytes in the header and an id the Sent search cannot match.
    /// </summary>
    public static string EnsureOutgoingMessageId(ComposeModel compose, AccountModel account)
    {
        if (string.IsNullOrEmpty(compose.OutgoingMessageId))
            compose.OutgoingMessageId = NewMessageId(account);
        return compose.OutgoingMessageId;
    }

    /// <summary>A fresh Message-ID on the sender's domain, or the fallback (see above).</summary>
    private static string NewMessageId(AccountModel account)
    {
        var at = account.Username.LastIndexOf('@');
        var domain = at >= 0 ? account.Username[(at + 1)..].Trim() : string.Empty;
        return AsciiDomain().IsMatch(domain)
                && !domain.Contains("xn--", StringComparison.OrdinalIgnoreCase)
            ? MimeKit.Utils.MimeUtils.GenerateMessageId(domain)
            : MimeKit.Utils.MimeUtils.GenerateMessageId(FallbackMessageIdDomain);
    }

    /// <summary>Right-hand side of an outgoing Message-ID when the sender's own domain will not do.</summary>
    internal const string FallbackMessageIdDomain = "quickmail.invalid";

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$")]
    private static partial System.Text.RegularExpressions.Regex AsciiDomain();

    /// <summary>
    /// Builds a complete MIME message from compose fields, including attachments.
    /// </summary>
    /// <param name="compose">The compose model with To, Cc, Bcc, Subject, Body, and Attachments.</param>
    /// <param name="account">The sender account (used for the From header).</param>
    /// <param name="userAgent">Optional User-Agent header value. When null the header is omitted.</param>
    public static MimeMessage Build(ComposeModel compose, AccountModel account, string? userAgent = null)
    {
        var message = new MimeMessage();

        if (userAgent != null)
            message.Headers.Add(HeaderId.UserAgent, userAgent);

        // Tag non-plain-text drafts so they can be reopened in the original mode.
        if (compose.Mode != ComposeMode.PlainText)
            message.Headers.Add("X-QuickMail-Compose-Mode", compose.Mode.ToString().ToLowerInvariant());

        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Username));
        AddressParser.AddAddresses(message.To,  compose.To);
        AddressParser.AddAddresses(message.Cc,  compose.Cc);
        AddressParser.AddAddresses(message.Bcc, compose.Bcc);
        message.Subject = compose.Subject;

        if (!string.IsNullOrEmpty(compose.OutgoingMessageId))
            message.MessageId = compose.OutgoingMessageId;
        if (compose.OutgoingDate is { } date)
            message.Date = date;

        if (!string.IsNullOrEmpty(compose.InReplyToMessageId))
            message.InReplyTo = compose.InReplyToMessageId;

        // text/plain only (existing behavior), or multipart/alternative with a
        // text/html part when the message was composed in Markdown or HTML mode.
        MimeEntity bodyEntity;
        if (!string.IsNullOrEmpty(compose.HtmlBody))
        {
            var plainText = string.IsNullOrWhiteSpace(compose.Body)
                ? HtmlStripper.ToPlainText(compose.HtmlBody)
                : compose.Body;
            // Pictures in the body travel with the HTML in multipart/related, marked inline,
            // so the HTML's cid: references resolve and they are not listed as attachments.
            // Only pictures the HTML still refers to are sent: one the user deleted from the
            // body is gone from the message too.
            MimeEntity htmlEntity = new TextPart("html") { Text = compose.HtmlBody };
            var shown = InlineImages.ReferencedContentIds(compose.HtmlBody);
            var inline = compose.InlineImages
                .Where(i => i.IsLoaded && !string.IsNullOrEmpty(i.ContentId) && shown.Contains(i.ContentId))
                .GroupBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (inline.Count > 0)
            {
                var related = new MultipartRelated { Root = htmlEntity };
                foreach (var image in inline)
                {
                    var part = MimePartFor(image, ContentDisposition.Inline);
                    part.ContentId = image.ContentId;
                    related.Add(part);
                }
                htmlEntity = related;
            }

            var alternative = new MultipartAlternative
            {
                // Least-faithful first per RFC 2046 — clients pick the last part they support.
                new TextPart("plain") { Text = plainText },
                htmlEntity,
            };
            bodyEntity = alternative;
        }
        else
        {
            bodyEntity = new TextPart("plain") { Text = compose.Body };
        }

        var loadedAttachments = compose.Attachments.Where(a => a.IsLoaded).ToList();
        if (loadedAttachments.Count > 0)
        {
            var multipart = new Multipart("mixed");
            multipart.Add(bodyEntity);
            foreach (var att in loadedAttachments)
                multipart.Add(MimePartFor(att, ContentDisposition.Attachment));
            message.Body = multipart;
        }
        else
        {
            message.Body = bodyEntity;
        }

        return message;
    }

    private static MimePart MimePartFor(AttachmentModel att, string disposition)
    {
        var slash = att.ContentType.IndexOf('/');
        var mediaType    = slash >= 0 ? att.ContentType[..slash] : "application";
        var mediaSubtype = slash >= 0 ? att.ContentType[(slash + 1)..] : "octet-stream";
        return new MimePart(mediaType, mediaSubtype)
        {
            Content                 = new MimeContent(new MemoryStream(att.Content!)),
            ContentDisposition      = new ContentDisposition(disposition),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName                = att.FileName,
        };
    }

    /// <summary>
    /// Builds a calendar REPLY message (accept/decline/tentative) addressed to the event organizer.
    /// Shared by the SMTP and Graph send paths.
    /// </summary>
    /// <param name="account">The sender account (used for the From header).</param>
    /// <param name="icsReplyContent">A full iCalendar REPLY payload.</param>
    /// <param name="organizerEmail">The event organizer's address (the reply recipient).</param>
    public static MimeMessage BuildIcsReply(AccountModel account, string icsReplyContent, string organizerEmail)
    {
        if (string.IsNullOrWhiteSpace(icsReplyContent))
            throw new ArgumentException("ICS reply content is required.", nameof(icsReplyContent));
        if (string.IsNullOrWhiteSpace(organizerEmail))
            throw new ArgumentException("Organizer email is required for ICS reply.", nameof(organizerEmail));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Username));
        message.To.Add(MailboxAddress.Parse(organizerEmail));
        message.Subject = "Calendar Response";

        var calendarPart = new TextPart("calendar")
        {
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        calendarPart.ContentType.Parameters.Add("method", "REPLY");
        calendarPart.SetText(Encoding.UTF8, icsReplyContent);

        message.Body = calendarPart;
        return message;
    }

    /// <summary>
    /// Builds a meeting invitation, update or cancellation (iMIP, RFC 6047) from the organizer's
    /// account to <paramref name="recipients"/> — what QuickMail sends when the calendar server does
    /// not send invitations itself (issue #866). <c>multipart/alternative</c> with the readable
    /// summary first and the <c>text/calendar</c> part last, which is the form mail programs show
    /// as an invitation with Accept / Decline; the calendar part names its
    /// <paramref name="method"/> (REQUEST or CANCEL) on its Content-Type, as RFC 6047 requires.
    /// </summary>
    public static MimeMessage BuildInvitation(AccountModel account, string icsContent, string method,
        string subject, string plainText, IReadOnlyList<EventAttendee> recipients)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            throw new ArgumentException("Calendar content is required.", nameof(icsContent));
        if (recipients.Count == 0)
            throw new ArgumentException("An invitation needs at least one recipient.", nameof(recipients));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Username));
        foreach (var r in recipients)
            message.To.Add(new MailboxAddress(string.IsNullOrWhiteSpace(r.Name) ? null : r.Name, r.Email));
        message.Subject = subject;
        message.MessageId = NewMessageId(account);
        message.Headers.Add(HeaderId.UserAgent, AppUserAgent);

        var text = new TextPart("plain");
        text.SetText(Encoding.UTF8, plainText);

        var calendarPart = new TextPart("calendar") { ContentTransferEncoding = ContentEncoding.Base64 };
        calendarPart.ContentType.Parameters.Add("method", method.ToUpperInvariant());
        calendarPart.SetText(Encoding.UTF8, icsContent);

        message.Body = new MultipartAlternative { text, calendarPart };
        return message;
    }
}
