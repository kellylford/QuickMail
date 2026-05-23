using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

public partial class MailMessageSummary : ObservableObject
{
    public uint UniqueId { get; set; }
    public Guid AccountId { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
    private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
    private bool _isReplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
    private bool _isForwarded;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private bool _hasAttachments;

    /// <summary>
    /// Single-word status shown in the status column.
    /// Priority: Replied > Fwd > New > (blank for read with no special flag).
    /// </summary>
    public string StatusDisplay
    {
        get
        {
            if (IsReplied)   return "Replied";
            if (IsForwarded) return "Fwd";
            if (!IsRead)     return "New";
            return string.Empty;
        }
    }

    /// <summary>
    /// Human-readable read/status label for accessibility announcements.
    /// Returns "replied", "forwarded", "unread", or "read".
    /// </summary>
    public string ReadStatusLabel
    {
        get
        {
            if (IsReplied)   return "replied";
            if (IsForwarded) return "forwarded";
            if (!IsRead)     return "unread";
            return "read";
        }
    }

    /// <summary>Display-friendly date: "h:mmA/P" for today, "M/d/yyyy" otherwise.</summary>
    public string DateDisplay
    {
        get
        {
            var local = Date.ToLocalTime();
            if (local.Date == DateTimeOffset.Now.Date)
                return local.ToString("h:mm") + (local.Hour < 12 ? "A" : "P");
            return local.ToString("M/d/yyyy");
        }
    }
}
