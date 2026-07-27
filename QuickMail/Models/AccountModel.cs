using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

public partial class AccountModel : ObservableObject
{
    // ── Persistent fields (serialized to accounts.json) ──────────────────────────

    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public AuthType AuthType { get; set; } = AuthType.Password;

    /// <summary>Which protocol stack this account uses. Fixed at account creation.</summary>
    public BackendKind BackendKind { get; set; } = BackendKind.ImapSmtp;

    /// <summary>
    /// Id of the <see cref="MailProvider"/> this account was created from ("gmail", "icloud", …).
    /// Null for accounts created before the provider catalog existed; <c>ProviderCatalog.Resolve</c>
    /// falls back to matching the IMAP host in that case, so there is no migration step.
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>
    /// True if this is a personal Microsoft account (its MSAL tenant is the consumers tenant), null if
    /// not yet detected. Set from the token at Microsoft sign-in and used to pick explicit Graph scopes
    /// for consumer accounts — which is correct even on custom domains, where the email-domain guess
    /// fails (#233). Null falls back to that domain guess.
    /// </summary>
    public bool? IsPersonalMicrosoftAccount { get; set; }

    /// <summary>Optional Azure AD tenant ID for Graph accounts. Null = "common" authority.</summary>
    public string? TenantId { get; set; }

    // IMAP
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public bool ImapAcceptInvalidCert { get; set; } = false;

    // SMTP
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = false; // STARTTLS on 587
    public bool SmtpAcceptInvalidCert { get; set; } = false;

    /// <summary>
    /// Encryption is mandatory on this account, not merely preferred.
    ///
    /// <see cref="ImapUseSsl"/> / <see cref="SmtpUseSsl"/> false means "STARTTLS", and it used to map
    /// to MailKit's <c>StartTlsWhenAvailable</c> — which, when the server advertises no STARTTLS,
    /// connects in PLAINTEXT and authenticates anyway. So "STARTTLS" was a label on the setting, not
    /// a property of the connection: a host on port 143 that simply never offers STARTTLS receives
    /// the password in the clear. With this flag set the connection uses <c>StartTls</c>, which
    /// fails instead.
    ///
    /// Default false, so accounts already in accounts.json deserialize exactly as before. Set true
    /// for server settings QuickMail chose on the user's behalf — the built-in provider catalog and
    /// every discovery tier — because those hosts arrived over the network (or from a table) rather
    /// than from the user, and a typosquatted responder naming a plaintext host must not be able to
    /// harvest the password behind a collapsed Advanced expander. Settings the user typed in
    /// Advanced settings keep the permissive behavior; that is their choice to make.
    /// </summary>
    public bool RequireStartTls { get; set; } = false;

    /// <summary>When true, this account is pre-selected when composing a new message.</summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// When true, QuickMail pulls this account's contacts and prior recipients from the mail
    /// provider into the local address book (issue #256). Off by default; only meaningful for
    /// OAuth accounts (Microsoft or Google), which are the only backends exposing a contact API.
    /// Enabling it triggers a one-time consent for read-only contact scopes.
    /// </summary>
    public bool SyncContacts { get; set; } = false;

    /// <summary>
    /// When true, QuickMail pulls this account's calendar into the calendar view (#282). Off by
    /// default. Meaningful for Microsoft and Google (calendar API) and iCloud accounts (CalDAV at
    /// caldav.icloud.com, reusing the account's app-specific password). Enabling a Microsoft
    /// account triggers a one-time calendar-scope consent; Google's calendar scope is already
    /// granted at mail sign-in; iCloud needs no extra consent.
    /// </summary>
    public bool SyncCalendar { get; set; } = false;

    /// <summary>
    /// Plain-text signature appended to new messages and replies/forwards.
    /// Empty string means no signature. Stored in accounts.json.
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// Full name of the folder the Archive action moves messages to for this account (issue #318).
    /// Null or empty means "auto-detect": QuickMail uses the folder the server flags as the special
    /// Archive folder (IMAP \Archive / Graph "archive"). Set explicitly from the folder tree's
    /// "Set as Archive Folder" command to override the auto-detected target. Per-account — there is
    /// deliberately no global archive folder.
    /// </summary>
    public string? ArchiveFolderFullName { get; set; }

    // ── Runtime-only status (not serialized, updated after each connection) ──────

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isConnected;

    /// <summary>Total unread messages across all folders for this account.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private int _totalUnread;

    // ── Computed labels ───────────────────────────────────────────────────────────

    public string AccountLabel => string.IsNullOrWhiteSpace(AccountName) ? Username : AccountName;
    public string SenderDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? AccountLabel : DisplayName;
    public string AccountLabelWithDefault => IsDefault ? $"{AccountLabel} - default" : AccountLabel;

    /// <summary>
    /// Short status line shown below the account name in the account list, and as a tooltip.
    /// TotalUnread covers all folders.
    /// Examples: "Disconnected", "Connected", "Connected — 1,630 unread"
    /// </summary>
    [JsonIgnore]
    public string StatusLabel
    {
        get
        {
            if (!IsConnected) return "Disconnected";
            return TotalUnread > 0
                ? $"Connected — {TotalUnread:N0} unread"
                : "Connected";
        }
    }

    /// <summary>
    /// Full accessible name for screen readers: account label + connection status + unread count.
    /// TotalUnread covers all folders. Placed in AutomationProperties.Name on the list item
    /// container so it is announced on focus without requiring the user to hover.
    /// Examples: "Idea Place, disconnected", "Kelly, connected", "Kelly, connected, 1630 unread"
    /// </summary>
    [JsonIgnore]
    public string AccessibleName
    {
        get
        {
            if (!IsConnected) return $"{AccountLabel}, disconnected";
            return TotalUnread > 0
                ? $"{AccountLabel}, connected, {TotalUnread} unread"
                : $"{AccountLabel}, connected";
        }
    }

    public override string ToString() => AccountLabel;
}
