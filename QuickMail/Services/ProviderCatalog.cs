using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The built-in provider table. Values are lifted verbatim from the host autofill that used to be
/// hardcoded in AddAccountViewModel, so existing accounts keep working unchanged.
/// </summary>
public sealed class ProviderCatalog : IProviderCatalog
{
    // Provider ids. Persisted to accounts.json via AccountModel.ProviderId, so treat them as a
    // stable wire format — rename a DisplayName freely, never an Id.
    public const string GmailId     = "gmail";

    /// <summary>
    /// Gmail over Google OAuth rather than an app password. A separate entry, not a variation on
    /// <see cref="GmailId"/>, because the two differ in what the user has to supply and QuickMail
    /// cannot tell from an address which one an account is entitled to. Offered only behind
    /// <see cref="FeatureFlag.GoogleAuth"/> — see <see cref="GmailOAuthProvider"/>.
    /// </summary>
    public const string GmailOAuthId = "gmail-oauth";

    public const string MicrosoftId = "microsoft";
    public const string YahooId     = "yahoo";
    public const string ICloudId    = "icloud";
    public const string OtherId     = "other";

    private static readonly MailProvider GmailProvider = new(
        Id: GmailId,
        DisplayName: "Gmail",
        Domains: ["gmail.com", "googlemail.com"],
        ImapHost: "imap.gmail.com", ImapPort: 993, ImapUseSsl: true,
        SmtpHost: "smtp.gmail.com", SmtpPort: 587, SmtpUseSsl: false,
        // Password rather than OAuth: Google OAuth sign-in is blocked for new accounts (#369, #226),
        // and an app password is the path that works today. Users whose Google authorization predates
        // the block pick GmailOAuthProvider instead, which the GoogleAuth feature flag unlocks.
        DefaultAuthType: AuthType.Password,
        SupportsOAuth: true,
        DefaultBackend: BackendKind.ImapSmtp,
        AppPasswordHint: "Gmail requires an app password, not your Google account password. "
                       + "Turn on 2-Step Verification first, then create a 16-character app password.",
        AppPasswordUrl: "https://myaccount.google.com/apppasswords",
        // POP3 also has to be switched on in Gmail's own settings before it answers (#128).
        Pop3Host: "pop.gmail.com", Pop3Port: 995);

    /// <summary>
    /// Gmail for the users whose Google authorization still works: QuickMail's Google OAuth client
    /// is closed to new grants (#369, #226), but accounts authorized before that keep signing in.
    /// Hidden unless <see cref="Models.FeatureFlag.GoogleAuth"/> is on, so the users it would only
    /// fail for never see it.
    ///
    /// Deliberately carries the gmail.com domains even though it sits AFTER
    /// <see cref="GmailProvider"/> in the catalog and so never wins a MatchByEmail: without them
    /// MailProvider.IsOther would be true and the entry would behave as the manual-settings
    /// catch-all. Typing a gmail.com address must keep landing on the app-password entry — the one
    /// that works for almost everyone — which the ordering is what guarantees.
    /// </summary>
    private static readonly MailProvider GmailOAuthProvider = new(
        Id: GmailOAuthId,
        DisplayName: "Gmail (sign in with Google)",
        Domains: ["gmail.com", "googlemail.com"],
        ImapHost: "imap.gmail.com", ImapPort: 993, ImapUseSsl: true,
        SmtpHost: "smtp.gmail.com", SmtpPort: 587, SmtpUseSsl: false,
        DefaultAuthType: AuthType.OAuth2Google,
        SupportsOAuth: true,
        DefaultBackend: BackendKind.ImapSmtp,
        // No app-password hint: this is the whole point of the entry — there is no password to enter.
        AppPasswordHint: null,
        AppPasswordUrl: null);

    private static readonly MailProvider MicrosoftProvider = new(
        Id: MicrosoftId,
        DisplayName: "Outlook.com / Microsoft 365",
        Domains: ["outlook.com", "hotmail.com", "live.com", "msn.com", "passport.com"],
        ImapHost: "outlook.office365.com", ImapPort: 993, ImapUseSsl: true,
        SmtpHost: "smtp-mail.outlook.com", SmtpPort: 587, SmtpUseSsl: false,
        DefaultAuthType: AuthType.OAuth2Microsoft,
        SupportsOAuth: true,
        // Deliberately IMAP, not Graph: this preserves the pre-catalog default for new accounts.
        // The Graph option remains available as "Connection method" under Advanced settings.
        DefaultBackend: BackendKind.ImapSmtp,
        AppPasswordHint: null,
        AppPasswordUrl: null,
        Pop3Host: "outlook.office365.com", Pop3Port: 995);

    private static readonly MailProvider YahooProvider = new(
        Id: YahooId,
        DisplayName: "Yahoo Mail",
        Domains: ["yahoo.com", "yahoo.co.uk", "yahoo.ca", "yahoo.com.au", "ymail.com", "rocketmail.com"],
        ImapHost: "imap.mail.yahoo.com", ImapPort: 993, ImapUseSsl: true,
        SmtpHost: "smtp.mail.yahoo.com", SmtpPort: 587, SmtpUseSsl: false,
        DefaultAuthType: AuthType.Password,
        SupportsOAuth: false,
        DefaultBackend: BackendKind.ImapSmtp,
        AppPasswordHint: "Yahoo requires an app password, not your Yahoo account password. "
                       + "Generate one under Account Security, App passwords.",
        AppPasswordUrl: "https://login.yahoo.com/account/security",
        Pop3Host: "pop.mail.yahoo.com", Pop3Port: 995);

    private static readonly MailProvider ICloudProvider = new(
        Id: ICloudId,
        DisplayName: "iCloud Mail",
        Domains: ["icloud.com", "me.com", "mac.com"],
        ImapHost: "imap.mail.me.com", ImapPort: 993, ImapUseSsl: true,
        SmtpHost: "smtp.mail.me.com", SmtpPort: 587, SmtpUseSsl: false,
        DefaultAuthType: AuthType.Password,
        SupportsOAuth: false,
        DefaultBackend: BackendKind.ImapSmtp,
        AppPasswordHint: "iCloud requires an app-specific password, not your Apple ID password. "
                       + "Generate one under Sign-In & Security, App-Specific Passwords.",
        AppPasswordUrl: "https://appleid.apple.com");
    // No Pop3Host on purpose: iCloud Mail serves IMAP only, so pre-filling a POP3 host here would
    // hand the user a server that does not answer.

    private static readonly MailProvider OtherProvider = new(
        Id: OtherId,
        DisplayName: "Other (enter settings manually)",
        Domains: [],
        ImapHost: string.Empty, ImapPort: 993, ImapUseSsl: true,
        SmtpHost: string.Empty, SmtpPort: 587, SmtpUseSsl: false,
        DefaultAuthType: AuthType.Password,
        SupportsOAuth: false,
        DefaultBackend: BackendKind.ImapSmtp,
        AppPasswordHint: null,
        AppPasswordUrl: null);

    // "Other" comes FIRST, not last. The dialog opens with it selected, so if it sat at the end of
    // the list pressing Down arrow would do nothing — the user would be parked on the one entry with
    // no settings, with no obvious way to discover that the others exist.
    //
    // GmailProvider comes before GmailOAuthProvider and the order is load-bearing: MatchByEmail and
    // the host fallback in Resolve both take the FIRST match, so a gmail.com address resolves to the
    // app-password entry. The Google sign-in entry is only ever reached by an explicit pick or by a
    // ProviderId saved on an existing account.
    private static readonly MailProvider[] Catalog =
    [
        OtherProvider,
        GmailProvider,
        GmailOAuthProvider,
        MicrosoftProvider,
        YahooProvider,
        ICloudProvider,
    ];

    /// <summary>
    /// Shared instance for the handful of call sites that identify a provider but have no DI seam
    /// (static helpers, services constructed before the catalog). The catalog is immutable lookup
    /// data, so sharing one instance is safe.
    /// </summary>
    public static ProviderCatalog Default { get; } = new();

    /// <summary>
    /// Whether this account is iCloud — the check behind CardDAV contact sync, CalDAV calendar sync,
    /// and the app-specific-password hint. Was an <c>ImapHost == "imap.mail.me.com"</c> comparison
    /// duplicated across six files; keeping the rule in one place means a saved ProviderId now
    /// answers it, with the host match as the fallback for older accounts.
    /// </summary>
    public static bool IsICloud(AccountModel account) =>
        Default.Resolve(account).Id == ICloudId;

    public IReadOnlyList<MailProvider> All => Catalog;

    public MailProvider Other => OtherProvider;

    public MailProvider GmailGoogleSignIn => GmailOAuthProvider;

    public MailProvider? MatchByEmail(string email) =>
        Catalog.FirstOrDefault(p => p.MatchesEmail(email));

    public MailProvider? ById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Catalog.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public MailProvider Resolve(AccountModel account)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Accounts created after the catalog landed carry their provider explicitly. "Other" is
        // deliberately NOT an answer: it means "no provider was identified at creation time", so it
        // must fall through to the host and username checks below. Returning it here would strand
        // an account the user set up by hand — an iCloud mailbox added via "Other" with the Apple
        // host typed in would resolve as Other forever, and contact/calendar sync would silently
        // never run even though the dialog offered the checkboxes.
        var byId = ById(account.ProviderId);
        if (byId is not null && !byId.IsOther) return byId;

        // Everything created before then is identified by the settings it was given. A Graph account
        // has no IMAP host at all, so check the backend before falling back to host matching.
        if (account.BackendKind == BackendKind.MicrosoftGraph) return MicrosoftProvider;

        var byHost = Catalog.FirstOrDefault(p =>
            !p.IsOther &&
            string.Equals(p.ImapHost, account.ImapHost, StringComparison.OrdinalIgnoreCase));
        if (byHost is not null) return byHost;

        // Last resort: the address itself. Covers an account whose host was hand-entered.
        return MatchByEmail(account.Username) ?? OtherProvider;
    }
}
