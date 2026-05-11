using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

public partial class MailMessageSummary : ObservableObject
{
    public uint UniqueId { get; set; }
    public Guid AccountId { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private bool _isReplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private bool _isForwarded;

    [ObservableProperty]
    private string _preview = string.Empty;

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

    /// <summary>Display-friendly date: "HH:mm" for today, "dd MMM" otherwise.</summary>
    public string DateDisplay
    {
        get
        {
            var local = Date.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm")
                : local.ToString("dd MMM");
        }
    }
}
