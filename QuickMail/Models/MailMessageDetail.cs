using System.Collections.Generic;

namespace QuickMail.Models;

public partial class MailMessageDetail : MailMessageSummary
{
    public string Cc { get; set; } = string.Empty;
    public string ReplyTo { get; set; } = string.Empty;
    // InternetMessageId (the RFC 5322 Message-ID, used for reply threading) is inherited from
    // MailMessageSummary, which now carries it for cross-folder duplicate collapse (issue #220).
    public string PlainTextBody { get; set; } = string.Empty;
    /// <summary>HTML body from the message, if the sender included one. Preferred over PlainTextBody for display.</summary>
    public string HtmlBody { get; set; } = string.Empty;

    /// <summary>
    /// Attachment parts of this message. Assigning this list also updates the inherited
    /// <see cref="MailMessageSummary.HasAttachments"/> flag, so a detail is never in the
    /// contradictory state of carrying attachments while reporting it has none.
    /// <para>
    /// This matters because no loader sets <c>HasAttachments</c> on a detail — IMAP, Graph and
    /// the local store all populate <c>Attachments</c> only. <c>MessageWindow</c> binds its
    /// attachment list's Visibility to <c>MessageDetail.HasAttachments</c>, so without this the
    /// list stayed collapsed in Window mode: Alt+A announced "No attachments." for a message that
    /// had them, and Shift+Tab from the body could never reach them (issue #439).
    /// </para>
    /// </summary>
    public List<AttachmentModel> Attachments
    {
        get => _attachments;
        set
        {
            _attachments = value ?? [];
            HasAttachments = _attachments.Count > 0;
        }
    }

    private List<AttachmentModel> _attachments = [];

    /// <summary>Parsed calendar invite, if this message contains a text/calendar MIME part.</summary>
    public IcsModel? CalendarInvite { get; set; }

    /// <summary>
    /// Raw ICS text from the text/calendar MIME part, persisted in the MessageDetail table
    /// so <c>LocalCacheCalendarProvider</c> can harvest events without re-fetching from IMAP.
    /// Empty string when the message has no calendar part.
    /// </summary>
    public string CalendarIcs { get; set; } = string.Empty;

    /// <summary>
    /// Compose mode stored in the X-QuickMail-Compose-Mode header when this message was saved as a
    /// QuickMail draft. PlainText for messages not authored by QuickMail or authored before 0.7.2.
    /// </summary>
    public ComposeMode DraftComposeMode { get; set; } = ComposeMode.PlainText;
}
