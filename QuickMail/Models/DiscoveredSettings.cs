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
    /// Microsoft's sign-in realm lookup said the domain has a Microsoft work-or-school tenant.
    /// A suggestion, not a confirmation: the lookup answers "does this domain have an Entra ID
    /// tenant", which is not quite the same as "this domain's mail is in Exchange Online" — a
    /// company that runs its own mail but uses Microsoft 365 internally answers yes too. Callers
    /// must present this tentatively and leave the user an obvious way to overrule it.
    /// </summary>
    MicrosoftRealm,
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
public sealed record DiscoveredSettings(
    string ImapHost,
    int ImapPort,
    bool ImapUseSsl,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseSsl,
    string? ProviderId,
    string? DisplayName,
    DiscoverySource Source);
