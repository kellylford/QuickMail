namespace QuickMail.Models;

/// <summary>Controls which messages are shown in the message list.</summary>
public enum MessageFilter
{
    /// <summary>No filter — all messages are shown.</summary>
    All,

    /// <summary>Only messages that have not been read.</summary>
    Unread,

    /// <summary>Only messages that have been read.</summary>
    Read,

    /// <summary>Only messages that have one or more attachments.</summary>
    WithAttachments,

    /// <summary>Only messages that have been replied to.</summary>
    Replied,

    /// <summary>Only messages that have been forwarded.</summary>
    Forwarded,

    /// <summary>Only messages where the user's own address appears in the To field.</summary>
    ToMe,

    /// <summary>Only messages that have any named flag applied.</summary>
    Flagged,
}
