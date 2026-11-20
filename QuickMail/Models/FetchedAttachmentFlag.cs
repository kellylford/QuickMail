namespace QuickMail.Models;

/// <summary>
/// Whether one message has an attachment as the server holds it now, for the one-time attachment-flag pass (#931),
/// with that message's Message-ID beside it so the write can check the stored id still names the cached message.
/// <see cref="InternetMessageId"/> is empty when the message has none.
/// </summary>
public readonly record struct FetchedAttachmentFlag(bool HasAttachments, string InternetMessageId);
