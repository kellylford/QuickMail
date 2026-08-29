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

    // ── Watch state ───────────────────────────────────────────────────────────

    /// <summary>
    /// True when this message belongs to a conversation the user is watching (Ctrl+Shift+W).
    /// <para>Transient and derived: the watch list (<c>watches.json</c>) is the single source of
    /// truth, and this is stamped from it after a load and after a toggle. It has no database
    /// column and is never serialized — a persisted copy could disagree with the watch list, and a
    /// disagreement would have no correct resolution.</para>
    /// <para>Observable so that toggling refreshes the row's spoken text in place, without
    /// rebuilding the list and losing focus. Deliberately absent from
    /// <c>MainViewModel.ReconcileMessageState</c>: a freshly fetched summary has never been stamped,
    /// so copying it over an existing row would clear the flag on every aggregate merge.</para>
    /// </summary>
    [ObservableProperty]
    private bool _isWatched;

    // ── Local-only state ──────────────────────────────────────────────────────

    /// <summary>
    /// True for a draft written to this computer that has not reached the server's Drafts folder
    /// yet — composed offline, or saved while the connection was failing (#637).
    /// <para>Observable so the row stops saying "Not on server" in place the moment the upload pass
    /// succeeds, without rebuilding the list and moving the user's focus.</para>
    /// <para>Persisted (<c>MessageSummary.is_pending_upload</c>), unlike <see cref="IsWatched"/>:
    /// the whole point is that it survives quitting the app, and on the next launch it is the only
    /// thing distinguishing a draft that still needs uploading from one already on the server.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingUpload))]
    private bool _isPendingUpload;

    /// <summary>
    /// Why the server refused to store this draft, or null (#637).
    /// <para>The draft stays where it is with this set — it is not queued for upload any more, and
    /// nothing will retry it, but it is where the user left it. The alternative was explaining in
    /// the status bar, which the next sync sweep overwrites within seconds; anyone running with
    /// custom announcements off would then have had a draft silently stop uploading with no trace
    /// of why. The row is the durable record.</para>
    /// <para>Persisted (<c>MessageSummary.send_failed_reason</c>): a refusal the user has not seen
    /// yet must survive a restart, and the server said it once and will not say it again.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingUpload))]
    [NotifyPropertyChangedFor(nameof(DeliveryNotice))]
    private string? _sendFailedReason;

    /// <summary>
    /// True for a draft on this computer that something will actually upload (#637).
    /// <para>Not the same as <see cref="IsPendingUpload"/>: a draft the server has REFUSED keeps
    /// that flag set while <c>LoadPendingDraftsAsync</c> excludes it until the user edits and
    /// saves it again. Saying "not on server — it will go to the server when you are back online"
    /// about it is a promise the store query rules out.</para>
    /// </summary>
    public bool IsAwaitingUpload => IsPendingUpload && string.IsNullOrEmpty(SendFailedReason);

    /// <summary>
    /// What went wrong with this message, for the reading pane — empty for ordinary mail AND for a
    /// draft that is simply waiting its turn (#637).
    /// <para>Shown only when something is WRONG, by the user's own choice: a draft still queued to
    /// upload already says "not on server" in its row, and repeating it here would add a focus
    /// stop on the common case to say what the user has just been told.</para>
    /// <para>The reason a server gave for refusing a draft is persisted, and without this it was
    /// rendered nowhere: the status bar quoted it once and the next sweep overwrote the sentence,
    /// so the row said the draft had not gone up and nothing said why — while the app and the
    /// guide both tell the user to fix it and save again.</para>
    /// </summary>
    public string DeliveryNotice =>
        string.IsNullOrEmpty(SendFailedReason)
            ? string.Empty
            : $"Your mail server refused to save this draft: {SendFailedReason}. " +
               "It will not be tried again until you edit it and save it.";

    // ── Computed display ──────────────────────────────────────────────────────

    /// <summary>
    /// Short status shown in the status column.
    /// Priority: Not on server > Flag name > Replied > Fwd > New > (blank for read).
    /// </summary>
    public string StatusDisplay
    {
        get
        {
            // These outrank the rest deliberately: every other status describes a message that
            // IS on the server, and these say it is not. A draft that exists only on this
            // computer is the most consequential thing the row can tell you about it.
            //
            // "Not uploaded" rather than "Not on server" once the server has refused it:
            // the second promises a trip to the server that nothing will now make (#637).
            if (!string.IsNullOrEmpty(SendFailedReason)) return "Not uploaded";
            if (IsPendingUpload) return "Not on server";
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
            if (!string.IsNullOrEmpty(SendFailedReason))
                return "could not be uploaded, still on this computer";
            if (IsPendingUpload) return "saved on this computer, not yet on the server";
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
