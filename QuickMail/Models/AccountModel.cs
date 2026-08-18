using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

public partial class AccountModel : ObservableObject
{
    // ── Persistent fields (serialized to accounts.json) ──────────────────────────

    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// This account's email address. Everything user-facing treats it as one: the Add Account
    /// dialog labels it "Email address", the provider catalog matches a provider from its domain,
    /// autodiscovery looks up its domain, and <see cref="Services.MimeMessageBuilder"/> puts it in
    /// the From header of every message sent from the account.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The name the mail server wants at login, when that is not the email address (#396).
    ///
    /// Null or empty for the overwhelming majority of accounts, where the two are the same thing.
    /// They come apart on providers that keep a separate account name — an iCloud account whose
    /// mail is at a custom domain logs in as the Apple ID, a hosted server may want a bare
    /// "firstname" or a "domain\user" — and before this field existed one box had to serve both
    /// roles. Whichever the user filled in, the other was wrong: a login name in the box produced
    /// MAIL FROM:&lt;name&gt; and the server rejected the message, an email address produced a
    /// login the server did not recognize.
    ///
    /// Use <see cref="AuthUsername"/> rather than reading this directly.
    /// </summary>
    public string? LoginUsername { get; set; }

    /// <summary>
    /// The identity to authenticate with: <see cref="LoginUsername"/> when the server wants a
    /// different login, otherwise the email address. Every IMAP and SMTP password authentication
    /// goes through this. OAuth deliberately does not — there the identity is the mailbox the token
    /// was issued for, which is the email address.
    /// </summary>
    [JsonIgnore]
    public string AuthUsername =>
        string.IsNullOrWhiteSpace(LoginUsername) ? Username : LoginUsername;

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

    /// <summary>
    /// Crash-safe marker for an in-progress IMAP→Graph conversion (#529 step 4). Set — and persisted —
    /// before the local cache is purged, and cleared only after the first post-convert Graph sync has
    /// baselined the account's folders. While it is true, startup seeds the SyncService rule-refire
    /// baseline for this account, so a crash between the purge and the first sync cannot re-fire client
    /// rules over pre-existing mail (the #454 failure this safeguard exists to prevent). See
    /// docs/planning/graph-migrate-existing-accounts-pm-dev-spec.md §5.3.
    /// </summary>
    public bool GraphConversionPending { get; set; }

    // IMAP
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public bool ImapAcceptInvalidCert { get; set; } = false;

    // POP3
    public string Pop3Host { get; set; } = string.Empty;
    public int Pop3Port { get; set; } = 995;
    public bool Pop3UseSsl { get; set; } = true;
    public bool Pop3AcceptInvalidCert { get; set; } = false;

    /// <summary>
    /// When true (the default), downloaded messages are left on the server so other clients can
    /// still collect them. When false, a message is deleted from the server once it is stored
    /// locally — the classic POP3 behavior.
    /// <para>QuickMail still files deletes through a local Trash folder either way: a message the
    /// user deletes is moved to Trash and only removed from the server when Trash is emptied or the
    /// message is permanently deleted. Because the local store is the only copy of POP3 mail, that
    /// second step is the point of no return.</para>
    /// </summary>
    public bool Pop3LeaveMailOnServer { get; set; } = true;

    // ── The incoming leg, whichever protocol serves it ───────────────────────────
    // Code that cares about "the server this account receives from" — grouping accounts by host at
    // connect time, deciding whether a reconfigured account still describes the same connection —
    // must not reach for the IMAP fields directly. A POP3 account leaves those at their defaults, so
    // an IMAP-only reading sees every POP3 account as the same empty host on port 993 (#128).

    /// <summary>
    /// Whether folders on this account can be created, renamed, moved, copied or deleted. False only
    /// for POP3 (#128), whose Inbox, Sent, Drafts and Trash are made up by QuickMail and are the only
    /// folders there can be.
    /// <para>A capability the UI asks about, rather than a <see cref="BackendKind"/> comparison
    /// repeated at each command that needs it: those comparisons are a state decision, they were
    /// planted at four call sites, and the one place that forgot to make it — the message move/copy
    /// picker's New Folder button — worked only because the backend's <c>NotSupportedException</c>
    /// message happened to read well.</para>
    /// </summary>
    [JsonIgnore]
    public bool SupportsFolderCrud => BackendKind != BackendKind.Pop3Smtp;

    /// <summary>The host this account receives mail from. Empty for Graph, which has no server.</summary>
    [JsonIgnore]
    public string IncomingHost => BackendKind switch
    {
        BackendKind.Pop3Smtp       => Pop3Host,
        BackendKind.MicrosoftGraph => string.Empty,
        _                          => ImapHost,
    };

    /// <summary>The port that goes with <see cref="IncomingHost"/>. 0 for Graph.</summary>
    [JsonIgnore]
    public int IncomingPort => BackendKind switch
    {
        BackendKind.Pop3Smtp       => Pop3Port,
        BackendKind.MicrosoftGraph => 0,
        _                          => ImapPort,
    };

    /// <summary>Implicit TLS on the incoming connection. False for Graph, which has none.</summary>
    [JsonIgnore]
    public bool IncomingUseSsl => BackendKind switch
    {
        BackendKind.Pop3Smtp       => Pop3UseSsl,
        BackendKind.MicrosoftGraph => false,
        _                          => ImapUseSsl,
    };

    /// <summary>Whether the incoming connection accepts an invalid certificate.</summary>
    [JsonIgnore]
    public bool IncomingAcceptInvalidCert => BackendKind switch
    {
        BackendKind.Pop3Smtp       => Pop3AcceptInvalidCert,
        BackendKind.MicrosoftGraph => false,
        _                          => ImapAcceptInvalidCert,
    };

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

    // ── Shared mailbox linkage (#31) ─────────────────────────────────────────────

    /// <summary>
    /// True if this account is a Microsoft 365 / Exchange shared mailbox added by address (Approach B):
    /// a first-class account with its own <see cref="Id"/>, but no credentials of its own — it reads and
    /// sends through its <see cref="ParentAccountId"/>'s token and backend. Optional; defaults false, so
    /// existing accounts round-trip unchanged (no migration).
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// For a shared account (<see cref="IsShared"/>), the <see cref="Id"/> of the parent account whose
    /// token authorizes access and whose backend serves it. Null for a normal account.
    /// </summary>
    public Guid? ParentAccountId { get; set; }

    /// <summary>
    /// For a shared account, the shared mailbox's own SMTP address — the <c>/users/{SharedAddress}</c>
    /// target for a Graph parent, or the XOAUTH2 <c>user=</c> for an IMAP parent. Null for a normal
    /// account.
    /// </summary>
    public string? SharedAddress { get; set; }

    /// <summary>
    /// The shared mailboxes (#31) that read through <paramref name="parent"/>, taken from
    /// <paramref name="all"/>. Empty when <paramref name="parent"/> is itself shared — a shared mailbox
    /// is never a parent. Both account-delete paths (the Account Manager and the main-window account
    /// context menu) call this so a parent's removal cascades to its shared mailboxes identically.
    /// </summary>
    public static List<AccountModel> SharedChildrenOf(AccountModel parent, IEnumerable<AccountModel> all) =>
        parent.IsShared
            ? new List<AccountModel>()
            : all.Where(a => a.IsShared && a.ParentAccountId == parent.Id).ToList();

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
    /// Examples: "Idea Place, disconnected", "Kelly, connected", "Kelly, connected, 1630 unread".
    /// A shared mailbox (#31) inserts the qualifier right after the label:
    /// "Support, shared mailbox, connected, 12 unread".
    /// </summary>
    [JsonIgnore]
    public string AccessibleName
    {
        get
        {
            var shared = IsShared ? ", shared mailbox" : "";
            if (!IsConnected) return $"{AccountLabel}{shared}, disconnected";
            return TotalUnread > 0
                ? $"{AccountLabel}{shared}, connected, {TotalUnread} unread"
                : $"{AccountLabel}{shared}, connected";
        }
    }

    public override string ToString() => AccountLabel;
}
