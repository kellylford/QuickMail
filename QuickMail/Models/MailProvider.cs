using System;
using System.Linq;

namespace QuickMail.Models;

/// <summary>
/// A well-known mail provider and the server settings QuickMail already knows for it.
///
/// The catalog of these (<see cref="Services.IProviderCatalog"/>) replaces the hardcoded
/// domain-sniffing and host autofill that used to live in AddAccountViewModel: picking a provider —
/// or typing an address whose domain matches one — fills every IMAP/SMTP field, so the user never
/// sees those controls unless they open Advanced settings.
/// </summary>
/// <param name="Id">Stable identifier persisted to <see cref="AccountModel.ProviderId"/>.</param>
/// <param name="DisplayName">User-facing name shown in the provider combo box.</param>
/// <param name="Domains">Email domains that identify this provider. Empty for <c>other</c>.</param>
/// <param name="DefaultAuthType">Authentication method selected when this provider is chosen.</param>
/// <param name="SupportsOAuth">
/// True when an OAuth sign-in path exists for this provider, even if <see cref="DefaultAuthType"/>
/// is <see cref="AuthType.Password"/>. Gmail is the case that matters: app passwords are the default
/// because Google OAuth sign-in is blocked for new accounts (#369), but the OAuth option stays
/// available under Advanced settings for users it still works for.
/// </param>
/// <param name="AppPasswordHint">
/// Warning shown above the password box when the provider requires an app-specific password rather
/// than the account password. Null means no hint.
/// </param>
/// <param name="AppPasswordUrl">Where the user generates that app password. Null when there is none.</param>
/// <param name="Pop3Host">
/// This provider's POP3 host, for the users who choose POP3 as the connection method (#128). Null
/// when the provider offers no POP3 service (iCloud) or when POP3 is not a sensible route to it —
/// the POP3 host box is then left for the user to fill in rather than pre-filled with a guess.
/// Optional and trailing so every existing construction site is untouched.
/// </param>
/// <param name="Pop3Port">POP3 port to go with <paramref name="Pop3Host"/>. 995 is implicit TLS.</param>
public sealed record MailProvider(
    string Id,
    string DisplayName,
    string[] Domains,
    string ImapHost,
    int ImapPort,
    bool ImapUseSsl,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseSsl,
    AuthType DefaultAuthType,
    bool SupportsOAuth,
    BackendKind DefaultBackend,
    string? AppPasswordHint,
    string? AppPasswordUrl,
    string? Pop3Host = null,
    int Pop3Port = 995)
{
    /// <summary>True when QuickMail knows a POP3 host for this provider.</summary>
    public bool SupportsPop3 => !string.IsNullOrEmpty(Pop3Host);

    /// <summary>The catch-all entry: no known settings, user fills in Advanced settings by hand.</summary>
    public bool IsOther => Domains.Length == 0;

    /// <summary>True when this provider needs an app-specific password rather than the account password.</summary>
    public bool RequiresAppPassword => !string.IsNullOrEmpty(AppPasswordHint);

    /// <summary>True when the email address's domain belongs to this provider.</summary>
    public bool MatchesEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return false;
        var domain = email[(at + 1)..].Trim();
        return Domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A screen reader reads a ComboBox item's accessible name from ToString(), not from
    /// DisplayMemberPath — see the Accessibility Checklist in CLAUDE.md. Without this override the
    /// provider combo would announce the full record declaration, punctuation and all.
    /// </summary>
    public override string ToString() => DisplayName;
}
