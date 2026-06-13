using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace QuickMail.Models;

/// <summary>
/// A group of messages sharing the same sender (From field), displayed as a
/// collapsible tree node in the From view.
/// </summary>
public sealed class SenderGroup : INotifyPropertyChanged
{
    /// <summary>The trimmed From string used as the grouping key (case-insensitive).</summary>
    public string SenderKey { get; init; } = string.Empty;

    /// <summary>Messages from this sender, newest first.</summary>
    public IReadOnlyList<MailMessageSummary> Messages { get; init; } = [];

    // ── Computed from the group / newest message ──────────────────────────────

    /// <summary>Display name for the sender (equals <see cref="SenderKey"/>).</summary>
    public string SenderName => SenderKey;

    /// <summary>Subject of the most recent message in this group.</summary>
    public string NewestSubject => Messages.Count > 0 ? Messages[0].Subject : string.Empty;

    /// <summary>Preview text taken from the newest message.</summary>
    public string Preview => Messages.Count > 0 ? Messages[0].Preview : string.Empty;

    /// <summary>Formatted date of the newest message.</summary>
    public string DateDisplay => Messages.Count > 0 ? Messages[0].DateDisplay : string.Empty;

    /// <summary>Number of messages in this group.</summary>
    public int Count => Messages.Count;

    /// <summary>True when at least one message in the group is unread.</summary>
    public bool HasUnread => Messages.Any(m => !m.IsRead);

    /// <summary>True when at least one message in the group is flagged.</summary>
    public bool HasFlagged => Messages.Any(m => m.IsFlagged);

    /// <summary>The flag name of the first flagged message in the group, or null if none are flagged.</summary>
    public string? FlagLabel => Messages.FirstOrDefault(m => m.IsFlagged)?.FlagLabel;

    /// <summary>The color of the first flagged message's flag, or null if none are flagged.</summary>
    public string? FlagColorHex => Messages.FirstOrDefault(m => m.IsFlagged)?.FlagColorHex;

    /// <summary>Accessibility label read by screen readers when the tree node receives focus.</summary>
    public string AutomationName
    {
        get
        {
            var countWord = Count == 1 ? "message" : "messages";
            var unread    = HasUnread ? " Has unread." : string.Empty;
            var flagged   = HasFlagged ? $" {FlagLabel}." : string.Empty;
            return string.IsNullOrWhiteSpace(Preview)
                ? $"{SenderName}. {Count} {countWord}.{flagged}{unread} {DateDisplay}."
                : $"{SenderName}. {Count} {countWord}.{flagged}{unread} {Preview}. {DateDisplay}.";
        }
    }

    // ── IsExpanded (INotifyPropertyChanged for TwoWay binding) ───────────────

    private bool _isExpanded;

    /// <summary>
    /// Whether this sender group node is expanded in the tree.
    /// Starts collapsed; raises PropertyChanged so TwoWay bindings stay in sync.
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

    public event PropertyChangedEventHandler? PropertyChanged;
}
