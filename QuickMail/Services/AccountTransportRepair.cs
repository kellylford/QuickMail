using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Corrects the transport-encryption flag on accounts that were configured by hand against a host
/// QuickMail now ships settings for.
///
/// The provider catalog arrived after a good many accounts already existed, and an account typed in
/// before it had no help getting the pairing right. The failure it produces is silent and total:
/// implicit TLS selected against a STARTTLS-only port sends a TLS ClientHello to a server that
/// answers with a plaintext SMTP banner, so the handshake fails a second after the user presses
/// Send, every time, with an error that names a certificate or a socket rather than a checkbox
/// (#396 — an iCloud account on port 587 with implicit SSL checked).
///
/// The rule is deliberately narrow, because rewriting a user's server settings unasked is only
/// defensible where the answer is not a judgement call: the host must be one of ours, the port must
/// be the exact port we publish for that host, and only then is the encryption mode replaced with
/// the one we publish alongside it. A host we do not recognize, or our host on a port we do not
/// publish, is left exactly as the user set it — they may well know something the table does not.
/// </summary>
internal static class AccountTransportRepair
{
    /// <summary>
    /// Applies the correction in place. Returns the accounts that changed, so the caller can persist
    /// and log them; an empty list means nothing needed fixing, which is the normal case.
    /// </summary>
    public static IReadOnlyList<AccountModel> Apply(IEnumerable<AccountModel> accounts, IProviderCatalog catalog)
    {
        var repaired = new List<AccountModel>();
        if (accounts is null || catalog is null) return repaired;

        foreach (var account in accounts)
        {
            if (account is null || account.BackendKind == BackendKind.MicrosoftGraph) continue;

            var changed = false;

            var imapProvider = MatchByImapHost(catalog, account.ImapHost);
            if (imapProvider is not null
                && account.ImapPort == imapProvider.ImapPort
                && account.ImapUseSsl != imapProvider.ImapUseSsl)
            {
                LogService.Log(
                    $"AccountTransportRepair: {account.AccountLabel} IMAP {account.ImapHost}:{account.ImapPort} " +
                    $"useSsl {account.ImapUseSsl} → {imapProvider.ImapUseSsl} (per {imapProvider.DisplayName} settings)");
                account.ImapUseSsl = imapProvider.ImapUseSsl;
                changed = true;
            }

            var smtpProvider = MatchBySmtpHost(catalog, account.SmtpHost);
            if (smtpProvider is not null
                && account.SmtpPort == smtpProvider.SmtpPort
                && account.SmtpUseSsl != smtpProvider.SmtpUseSsl)
            {
                LogService.Log(
                    $"AccountTransportRepair: {account.AccountLabel} SMTP {account.SmtpHost}:{account.SmtpPort} " +
                    $"useSsl {account.SmtpUseSsl} → {smtpProvider.SmtpUseSsl} (per {smtpProvider.DisplayName} settings)");
                account.SmtpUseSsl = smtpProvider.SmtpUseSsl;
                changed = true;
            }

            if (changed) repaired.Add(account);
        }

        return repaired;
    }

    // Matched on the host the account actually connects to, NOT on ProviderCatalog.Resolve. Resolve
    // falls back to the email domain, which would claim a gmail.com address relayed through a
    // company's own SMTP server — and this code rewrites connection settings, so a confident wrong
    // answer is worse here than no answer.
    private static MailProvider? MatchByImapHost(IProviderCatalog catalog, string? host) =>
        string.IsNullOrWhiteSpace(host)
            ? null
            : catalog.All.FirstOrDefault(p => !p.IsOther && HostEquals(p.ImapHost, host));

    private static MailProvider? MatchBySmtpHost(IProviderCatalog catalog, string? host) =>
        string.IsNullOrWhiteSpace(host)
            ? null
            : catalog.All.FirstOrDefault(p => !p.IsOther && HostEquals(p.SmtpHost, host));

    private static bool HostEquals(string? catalogHost, string accountHost) =>
        !string.IsNullOrWhiteSpace(catalogHost)
        && string.Equals(catalogHost, accountHost.Trim(), System.StringComparison.OrdinalIgnoreCase);
}
