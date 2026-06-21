using System;

namespace QuickMail.Models;

public enum SpecialFolderKind { None, Inbox, Sent, Drafts, Trash, Junk }

public class MailFolderModel
{
    public Guid AccountId { get; set; }
    public bool IsHeader { get; set; }
    public string FullName { get; set; } = string.Empty;
    /// <summary>
    /// Parent folder identifier for backends that model hierarchy by parent reference rather than
    /// by a path-separated name (Microsoft Graph). Null for IMAP, where nesting is encoded in
    /// <see cref="FullName"/> via the namespace separator. When non-null on any folder in a set,
    /// <c>FolderTreeBuilder</c> builds the hierarchy from parent IDs instead of the separator.
    /// </summary>
    public string? ParentId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int UnreadCount { get; set; }
    /// <summary>Total number of messages in the folder as reported by the server at connection time.</summary>
    public int MessageCount { get; set; }
    /// <summary>True for Trash, Junk, Sent, and Drafts — excluded from the All Mail aggregate view.</summary>
    public bool ExcludeFromAllMail { get; set; }
    /// <summary>Identifies special-purpose folders for virtual aggregate views.</summary>
    public SpecialFolderKind Kind { get; set; }

    /// <summary>Accessibility label: headers just show the name; folders include unread count.</summary>
    public string AutomationName =>
        IsHeader ? DisplayName
        : UnreadCount > 0 ? $"{DisplayName}, {UnreadCount} unread"
        : DisplayName;

    public override string ToString() =>
        IsHeader ? DisplayName
        : UnreadCount > 0 ? $"{DisplayName} ({UnreadCount})" : DisplayName;
}
