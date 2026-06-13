using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace QuickMail.Models;

/// <summary>
/// A group of messages sharing the same normalised subject, displayed as a
/// collapsible tree node in conversation view.
/// </summary>
public sealed class ConversationGroup : INotifyPropertyChanged
{
    /// <summary>The normalised subject used as the grouping key.</summary>
    public string NormalizedSubject { get; init; } = string.Empty;

    /// <summary>Messages in this conversation, newest first.</summary>
    public IReadOnlyList<MailMessageSummary> Messages { get; init; } = [];

    // ── Computed from the newest message ─────────────────────────────────────

    /// <summary>Display subject taken from the newest message.</summary>
    public string Subject => Messages.Count > 0 ? Messages[0].Subject : NormalizedSubject;

    /// <summary>Sender name of the most recent (newest) message in the conversation.</summary>
    public string LastSenderName => Messages.Count > 0 ? Messages[0].From : string.Empty;

    /// <summary>Preview text taken from the newest message.</summary>
    public string Preview => Messages.Count > 0 ? Messages[0].Preview : string.Empty;

    /// <summary>Formatted date of the newest message.</summary>
    public string DateDisplay => Messages.Count > 0 ? Messages[0].DateDisplay : string.Empty;

    /// <summary>Number of messages in the conversation.</summary>
    public int Count => Messages.Count;

    /// <summary>True when at least one message in the conversation is unread.</summary>
    public bool HasUnread => Messages.Any(m => !m.IsRead);

    /// <summary>True when at least one message in the conversation is flagged.</summary>
    public bool HasFlagged => Messages.Any(m => m.IsFlagged);

    /// <summary>The flag name of the first flagged message in the group, or null if none are flagged.</summary>
    public string? FlagLabel => Messages.FirstOrDefault(m => m.IsFlagged)?.FlagLabel;

    /// <summary>The color of the first flagged message's flag, or null if none are flagged.</summary>
    public string? FlagColorHex => Messages.FirstOrDefault(m => m.IsFlagged)?.FlagColorHex;

    /// <summary>
    /// Accessibility label read by screen readers when the tree node receives focus.
    /// Preview is omitted when empty (e.g. when preview is disabled in account settings).
    /// </summary>
    public string AutomationName
    {
        get
        {
            var countWord = Count == 1 ? "message" : "messages";
            var sender    = string.IsNullOrWhiteSpace(LastSenderName) ? string.Empty : $" {LastSenderName}.";
            var unread    = HasUnread ? " Has unread." : string.Empty;
            var flagged   = HasFlagged ? $" {FlagLabel}." : string.Empty;
            return string.IsNullOrWhiteSpace(Preview)
                ? $"{Subject}. {Count} {countWord}.{sender}{flagged}{unread} {DateDisplay}."
                : $"{Subject}. {Count} {countWord}.{sender}{flagged}{unread} {Preview}. {DateDisplay}.";
        }
    }

    // ── IsExpanded (INotifyPropertyChanged for TwoWay binding) ───────────────

    private bool _isExpanded;

    /// <summary>
    /// Whether this conversation node is expanded in the tree.
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
