namespace QuickMail.Models;

/// <summary>Which tier of <see cref="Services.IAutoDiscoverService"/> produced a result.</summary>
public enum DiscoverySource
{
    /// <summary>The built-in provider table. Offline, instant, no data left the machine.</summary>
    LocalCatalog,

    /// <summary>Mozilla's autoconfig database. Only the domain was sent.</summary>
    Ispdb,

    /// <summary>The domain's own Exchange Autodiscover endpoint.</summary>
    ExchangeAutodiscover,

    /// <summary>
    /// The domain's public DNS says where its mail is actually delivered — an MX host under
    /// <c>mail.protection.outlook.com</c>, or an <c>autodiscover</c> CNAME to Microsoft.
    ///
    /// This deliberately asks about MAIL HOSTING rather than about the organisation. An earlier
    /// version asked Microsoft's sign-in realm endpoint whether the domain had a tenant, which is a
    /// different question with a different answer: a domain keeps its Entra ID tenant after its mail
    /// moves elsewhere, and plenty of companies run their own mail while using Microsoft 365
    /// internally. Both answered "yes" and got Microsoft's servers filled in, so the user's real
    /// password was offered to smtp-mail.outlook.com and came back 535.
    /// </summary>
    DnsMailHost,
}

/// <summary>
/// Server settings found for an email domain. Ports and SSL modes follow the same convention as
/// <see cref="AccountModel"/>: <see cref="SmtpUseSsl"/> false means STARTTLS, true means implicit
/// SSL on connect.
/// </summary>
/// <param name="ProviderId">
/// Set only when the result came from the built-in catalog, so the caller can select that provider
/// in the combo. Null for network-discovered settings, which map to "Other".
/// </param>
/// <param name="DisplayName">Human-readable provider or domain name for announcements.</param>
/// <param name="RequireStartTls">
/// Carried to <see cref="AccountModel.RequireStartTls"/>. True for everything discovery produces:
/// these hosts were chosen for the user rather than by them, so a non-implicit-TLS leg must
/// negotiate STARTTLS or fail, instead of falling back to plaintext and offering the password to
/// whatever answered. Defaulted rather than passed at every call site because there is no discovery
/// result for which false would be correct — a tier that cannot promise encryption returns null.
/// </param>
public sealed record DiscoveredSettings(
    string ImapHost,
    int ImapPort,
    bool ImapUseSsl,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseSsl,
    string? ProviderId,
    string? DisplayName,
    DiscoverySource Source,
    bool RequireStartTls = true);
