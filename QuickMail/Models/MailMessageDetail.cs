using System.Collections.Generic;

namespace QuickMail.Models;

public partial class MailMessageDetail : MailMessageSummary
{
    public string Cc { get; set; } = string.Empty;
    public string ReplyTo { get; set; } = string.Empty;
    /// <summary>
    /// RFC822 Internet Message-ID header (e.g. "&lt;abc@host&gt;"), used for reply threading.
    /// Distinct from <see cref="MailMessageSummary.MessageId"/>, which is the per-folder storage
    /// key (IMAP UID / Graph message id). Microsoft Graph exposes this as "internetMessageId".
    /// </summary>
    public string InternetMessageId { get; set; } = string.Empty;
    public string PlainTextBody { get; set; } = string.Empty;
    /// <summary>HTML body from the message, if the sender included one. Preferred over PlainTextBody for display.</summary>
    public string HtmlBody { get; set; } = string.Empty;

    public List<AttachmentModel> Attachments { get; set; } = [];

    /// <summary>Parsed calendar invite, if this message contains a text/calendar MIME part.</summary>
    public IcsModel? CalendarInvite { get; set; }
}
