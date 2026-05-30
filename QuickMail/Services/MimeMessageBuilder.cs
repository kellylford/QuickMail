using System.IO;
using System.Linq;
using MimeKit;
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

        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Username));
        AddressParser.AddAddresses(message.To,  compose.To);
        AddressParser.AddAddresses(message.Cc,  compose.Cc);
        AddressParser.AddAddresses(message.Bcc, compose.Bcc);
        message.Subject = compose.Subject;

        if (!string.IsNullOrEmpty(compose.InReplyToMessageId))
            message.InReplyTo = compose.InReplyToMessageId;

        var loadedAttachments = compose.Attachments.Where(a => a.IsLoaded).ToList();
        if (loadedAttachments.Count > 0)
        {
            var multipart = new Multipart("mixed");
            multipart.Add(new TextPart("plain") { Text = compose.Body });
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
            message.Body = new TextPart("plain") { Text = compose.Body };
        }

        return message;
    }
}
