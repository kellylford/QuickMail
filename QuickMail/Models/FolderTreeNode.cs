using System.Collections.ObjectModel;
using System.ComponentModel;

namespace QuickMail.Models;

/// <summary>
/// One node in the folder tree view, used in both the main window and the folder picker.
/// </summary>
public sealed class FolderTreeNode : INotifyPropertyChanged
{
    /// <summary>Null for account-group nodes and synthetic intermediate nodes that have no real IMAP folder.</summary>
    public MailFolderModel? Folder { get; init; }

    /// <summary>True for account-level group nodes that serve as collapsible containers for folder children.</summary>
    public bool IsHeader { get; init; }

    public string Label { get; init; } = string.Empty;

    public ObservableCollection<FolderTreeNode> Children { get; } = [];

    /// <summary>
    /// Accessibility name announced by screen readers (AutomationProperties.Name).
    /// Includes the unread count for real folders. This is deliberate and confirmed by real
    /// screen-reader use: a count carried ONLY via AutomationProperties.ItemStatus is not reliably
    /// announced, so folder counts go silent when the count is not in the Name (issue #227). The
    /// visible label (<see cref="Label"/>) stays count-free — the count shows as the separate
    /// <see cref="UnreadDisplay"/> badge — so the name and the visual do not double up.
    /// Do not move the count back out of the Name without checking with a screen-reader user first.
    /// </summary>
    public string AutomationName =>
        ShowUnread ? $"{Label}, {Folder!.UnreadCount} unread" : Label;

    // Gmail's All Mail / Important / Starred report unread counts that overlap the Inbox and include
    // archived mail, so they're hidden here to avoid a misleading count (issue #227).
    private bool ShowUnread => Folder is { UnreadCount: > 0, SuppressUnreadCount: false };

    /// <summary>
    /// UIA ItemStatus string used by AutomationProperties.ItemStatus on the TreeViewItem.
    /// Announced by screen readers after the folder name, e.g. "3 unread".
    /// Empty for folders with no unread messages and for header/group nodes.
    /// </summary>
    public string ItemStatusLabel =>
        ShowUnread ? $"{Folder!.UnreadCount} unread" : string.Empty;

    /// <summary>
    /// Visual unread badge shown next to the folder label, e.g. "(5)".
    /// Empty string for folders with no unread messages and for header/group nodes.
    /// </summary>
    public string UnreadDisplay =>
        ShowUnread ? $"({Folder!.UnreadCount})" : string.Empty;

    private bool _isExpanded;

    /// <summary>
    /// Whether this tree node is expanded. Raises PropertyChanged so TwoWay bindings
    /// from the TreeViewItem.IsExpanded property reflect in the data model.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>
    /// Raises PropertyChanged for the unread-count-derived displays after the underlying
    /// <see cref="MailFolderModel.UnreadCount"/> is updated in place. Lets the tree reflect a new
    /// count (e.g. after mark-read or new mail) without rebuilding the tree — which would replace
    /// node objects and reset keyboard focus within the TreeView (issue #227).
    /// </summary>
    public void NotifyUnreadChanged()
    {
        // AutomationName carries the count for screen readers, so it must refresh too.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemStatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadDisplay)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
