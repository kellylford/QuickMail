using System;

namespace QuickMail.Models;

// AllMail/Important/Starred are Gmail's virtual folders (\All, \Important, \Flagged). They mirror
// content that also lives in real folders, so they are deprioritized when picking the representative
// copy for a deduplicated aggregate view (see MessageDeduplicator). They are NOT excluded from sync —
// [Gmail]/All Mail is the only home of archived mail, so excluding it would lose messages.
public enum SpecialFolderKind { None, Inbox, Sent, Drafts, Trash, Junk, AllMail, Important, Starred }

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

    /// <summary>
    /// Gmail's virtual folders — All Mail, Important, Starred — whose IMAP unread counts overlap the
    /// Inbox and labels and include old archived mail, so they don't mean "new mail." Their unread
    /// count is hidden in the folder tree and excluded from the account unread total, otherwise the
    /// tree shows a high count while the Inbox is empty and the account total double-counts (#227).
    /// </summary>
    public bool SuppressUnreadCount =>
        Kind is SpecialFolderKind.AllMail or SpecialFolderKind.Important or SpecialFolderKind.Starred;

    /// <summary>Accessibility label: headers just show the name; folders include unread count.</summary>
    public string AutomationName =>
        IsHeader ? DisplayName
        : UnreadCount > 0 ? $"{DisplayName}, {UnreadCount} unread"
        : DisplayName;

    public override string ToString() =>
        IsHeader ? DisplayName
        : UnreadCount > 0 ? $"{DisplayName} ({UnreadCount})" : DisplayName;
}
