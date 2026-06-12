using System.IO;
using System.Linq;
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
}
