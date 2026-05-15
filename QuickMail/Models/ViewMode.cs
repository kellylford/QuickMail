namespace QuickMail.Models;

/// <summary>Controls how the message list is displayed.</summary>
public enum ViewMode
{
    /// <summary>Flat list ordered by date (default).</summary>
    Messages,

    /// <summary>Messages grouped into threads by normalised subject.</summary>
    Conversations,

    /// <summary>Messages grouped by sender (From address).</summary>
    From,

    /// <summary>Messages grouped by recipient (To address).</summary>
    To,
}
