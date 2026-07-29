using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The built-in table of well-known mail providers. Matching happens offline against the email
/// domain, so the common cases (Gmail, Outlook, Yahoo, iCloud) need no network call at all — they
/// are tier 1 of <see cref="IAutoDiscoverService"/>.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>
    /// Every provider, in display order, with <see cref="Other"/> first. Includes
    /// <see cref="GmailGoogleSignIn"/>, which the account dialogs leave out of the picker unless
    /// the GoogleAuth feature flag is on — so bind a picker to the ViewModel's provider list, not
    /// to this.
    /// </summary>
    IReadOnlyList<MailProvider> All { get; }

    /// <summary>The catch-all entry used when nothing matches.</summary>
    MailProvider Other { get; }

    /// <summary>
    /// Gmail authenticated with Google sign-in instead of an app password. Offered only to users who
    /// turn the GoogleAuth feature flag on, because QuickMail's Google OAuth client no longer accepts
    /// new authorizations — accounts authorized before it closed still work (#369, #226).
    /// </summary>
    MailProvider GmailGoogleSignIn { get; }

    /// <summary>The provider owning this email address's domain, or null when none matches.</summary>
    MailProvider? MatchByEmail(string email);

    /// <summary>The provider with this id, or null when unknown.</summary>
    MailProvider? ById(string? id);

    /// <summary>
    /// The provider for an existing account. Prefers the persisted
    /// <see cref="AccountModel.ProviderId"/>, and falls back to matching the IMAP host so accounts
    /// created before the catalog existed still resolve correctly. Never null — returns
    /// <see cref="Other"/> when nothing matches.
    /// </summary>
    MailProvider Resolve(AccountModel account);
}
