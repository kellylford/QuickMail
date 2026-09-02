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

    /// <summary>
    /// The account this node belongs to, for account header/placeholder nodes (#31). Header nodes carry
    /// no <see cref="Folder"/>, so without this two same-named accounts produce the same node key and
    /// collide (their expansion state and selection get confused). Null for non-account nodes.
    /// </summary>
    public System.Guid? AccountId { get; init; }

    /// <summary>True for the top-level node of a shared mailbox (#31) — drives the "shared mailbox"
    /// qualifier in <see cref="AutomationName"/>.</summary>
    public bool IsSharedAccount { get; init; }

    /// <summary>
    /// True for the Calendar node and every node beneath it. Drives which context menu the tree
    /// item gets: the mail folder actions (New Folder, Move, Delete…) mean nothing on a calendar,
    /// and every one of them silently does nothing when activated there.
    /// </summary>
    public bool IsCalendarNode { get; init; }

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
        ShowUnread ? $"{Label}, {Folder!.UnreadCount} {CountNoun}"
        : IsDefaultCalendar ? $"{Label}, default calendar"
        : IsSharedAccount ? $"{Label}, shared mailbox"
        : Label;

    private bool _isDefaultCalendar;

    /// <summary>
    /// True for the one calendar node that new appointments are created on by default (issue #497).
    /// Carried in <see cref="AutomationName"/> for the same reason the unread count is (#227): a
    /// state that lives only in ItemStatus is not reliably announced, and a default the user cannot
    /// hear is a default they cannot check.
    /// </summary>
    public bool IsDefaultCalendar
    {
        get => _isDefaultCalendar;
        set
        {
            if (_isDefaultCalendar == value) return;
            _isDefaultCalendar = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefaultCalendar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultCalendarDisplay)));
        }
    }

    /// <summary>Visual marker next to a default calendar's label. Empty for every other node.</summary>
    public string DefaultCalendarDisplay => IsDefaultCalendar ? "(default)" : string.Empty;

    // Gmail's All Mail / Important / Starred report unread counts that overlap the Inbox and include
    // archived mail, so they're hidden here to avoid a misleading count (issue #227).
    private bool ShowUnread => Folder is { UnreadCount: > 0, SuppressUnreadCount: false };

    // The Outbox count is mail waiting to leave, not mail waiting to be read (#637): "3 unread" on it
    // would be a lie the screen reader repeats on every visit.
    private string CountNoun => Folder?.Kind == SpecialFolderKind.Outbox ? "waiting" : "unread";

    /// <summary>
    /// UIA ItemStatus string used by AutomationProperties.ItemStatus on the TreeViewItem.
    /// Announced by screen readers after the folder name, e.g. "3 unread".
    /// Empty for folders with no unread messages and for header/group nodes.
    /// </summary>
    public string ItemStatusLabel =>
        ShowUnread ? $"{Folder!.UnreadCount} {CountNoun}" : string.Empty;

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
