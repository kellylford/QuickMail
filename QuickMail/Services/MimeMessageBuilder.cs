using System;
using System.Buffers.Text;
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
public static class MimeMessageBuilder
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
            var alternative = new MultipartAlternative
            {
                // Least-faithful first per RFC 2046 — clients pick the last part they support.
                new TextPart("plain") { Text = plainText },
                new TextPart("html")  { Text = compose.HtmlBody },
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
            {
                var slash = att.ContentType.IndexOf('/');
                var mediaType    = slash >= 0 ? att.ContentType[..slash] : "application";
                var mediaSubtype = slash >= 0 ? att.ContentType[(slash + 1)..] : "octet-stream";
                var mimePart = new MimePart(mediaType, mediaSubtype)
                {
                    Content                 = new MimeContent(new MemoryStream(att.Content!)),
                    ContentDisposition      = new ContentDisposition(ContentDisposition.Attachment),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName                = att.FileName,
                };
                multipart.Add(mimePart);
            }
            message.Body = multipart;
        }
        else
        {
            message.Body = bodyEntity;
        }

        return message;
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
}
