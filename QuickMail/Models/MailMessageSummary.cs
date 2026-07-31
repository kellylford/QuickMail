using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

public partial class MailMessageSummary : ObservableObject
{
    public string MessageId { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public string FolderName { get; set; } = string.Empty;

    /// <summary>Human-readable source location for the row's accessible name, populated only in
    /// aggregate/virtual views (All Mail, All Inboxes, saved views, From/To groups) so the row can say
    /// where the message lives (#423). Holds the folder alone ("Inbox"), or an account-qualified
    /// "&lt;account&gt; -- &lt;folder&gt;" when the aggregate spans more than one account. Empty in
    /// single-folder views, where the folder is implied.
    /// <para>Deliberately NOT an observable property: it is stamped on freshly-materialized summaries
    /// during an aggregate load, BEFORE the row's UI container is generated, so no change notification
    /// is needed. That stamp-before-insert ordering is load-bearing — a re-stamp after the row is shown
    /// would not refresh the accessible name. Preserve it if this instance is ever reused.</para></summary>
    public string FolderDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The RFC 5322 Message-ID header, a stable identity for the *same physical message* across
    /// every folder/label it appears in (unlike <see cref="MessageId"/>, which is a per-folder
    /// IMAP UID). Used to collapse duplicate copies in aggregate views — Gmail exposes one message
    /// in many IMAP folders (INBOX, All Mail, labels…), each with a different UID but the same
    /// Message-ID. Empty when the server did not supply one; empty identities are never merged.
    /// </summary>
    public string InternetMessageId { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    private bool _isRead;

    /// <summary>
    /// Positive form of <see cref="IsRead"/>. The spoken "Unread" field is phrased positively so
    /// that <see cref="Models.SpeakMode.WhenTrue"/> means "say it only when the message is unread"
    /// — the common request — rather than the inverse.
    /// </summary>
    public bool IsUnread => !IsRead;

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

    public bool IsMailingList { get; set; }

    // ── Flag state ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Guid string of the named flag applied to this message.
    /// Null when the message is not flagged.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    [NotifyPropertyChangedFor(nameof(FlagLabel))]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string? _flagId;

    /// <summary>Display name of the applied flag, denormalized for rendering. Null when unflagged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagLabel))]
    private string? _flagName;

    /// <summary>Hex color of the applied flag, denormalized for rendering. Null when unflagged.</summary>
    [ObservableProperty]
    private string? _flagColorHex;

    /// <summary>True when this message has a named flag applied.</summary>
    public bool IsFlagged => FlagId is not null;

    /// <summary>
    /// Human-readable flag name for accessibility. Empty string when not flagged.
    /// </summary>
    public string FlagLabel => FlagName ?? string.Empty;

    /// <summary>
    /// Whether the IMAP server reported this message as \Flagged.
    /// Transient — used during sync reconciliation; not persisted directly.
    /// </summary>
    public bool IsServerFlagged { get; set; }

    // ── Computed display ──────────────────────────────────────────────────────

    /// <summary>
    /// Single-word status shown in the status column.
    /// Priority: Flag name > Replied > Fwd > New > (blank for read).
    /// </summary>
    public string StatusDisplay
    {
        get
        {
            if (IsFlagged)   return FlagLabel;
            if (IsReplied)   return "Replied";
            if (IsForwarded) return "Fwd";
            if (!IsRead)     return "New";
            return string.Empty;
        }
    }

    /// <summary>
    /// Human-readable read/status label for accessibility announcements.
    /// Returns "replied", "forwarded", "unread", or "read".
    /// Flag status is announced separately via FlagLabel.
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
