using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Two corrections applied to saved accounts at startup, both for accounts configured by hand
/// before the pieces that would have prevented the problem existed (#396).
///
/// Rewriting someone's saved settings unasked is only defensible where the answer is not a
/// judgement call, so both rules are deliberately narrow and both stop applying to an account the
/// user has since saved themselves.
/// </summary>
internal static class AccountStartupRepair
{
    /// <summary>
    /// Applies both repairs in place. Returns the accounts that changed, so the caller can persist
    /// and log them; an empty list means nothing needed fixing, which is the normal case.
    /// </summary>
    public static IReadOnlyList<AccountModel> Apply(IEnumerable<AccountModel> accounts, IProviderCatalog catalog)
    {
        var repaired = new List<AccountModel>();
        if (accounts is null || catalog is null) return repaired;

        foreach (var account in accounts)
        {
            if (account is null) continue;

            var changed = PreserveLoginUsername(account);
            changed |= RepairTransport(account, catalog);
            if (changed) repaired.Add(account);
        }

        return repaired;
    }

    /// <summary>
    /// Keeps a working login working when the user corrects the email address.
    ///
    /// An account can hold a login name where its email address belongs — one field used to serve
    /// both. QuickMail now refuses to save that, and tells the user to enter a real address; if they
    /// do only what they are asked, the login name is gone and the account stops authenticating
    /// altogether. So the moment such an account is seen, the value is copied into
    /// <see cref="AccountModel.LoginUsername"/> first, where it goes on doing the job it was doing.
    ///
    /// Password accounts only. An OAuth or Graph account authenticates as the mailbox the token was
    /// issued for and never consults LoginUsername, so there is nothing to preserve.
    /// </summary>
    private static bool PreserveLoginUsername(AccountModel account)
    {
        if (account.AuthType != AuthType.Password) return false;
        if (account.BackendKind == BackendKind.MicrosoftGraph) return false;
        if (string.IsNullOrWhiteSpace(account.Username)) return false;
        if (!string.IsNullOrWhiteSpace(account.LoginUsername)) return false;
        if (EmailAddressValidator.IsValid(account.Username)) return false;

        LogService.Log(
            $"AccountStartupRepair: {account.AccountLabel} has a login name where its email address " +
            "belongs; copied to LoginUsername so correcting the address keeps the login working.");
        account.LoginUsername = account.Username.Trim();
        return true;
    }

    /// <summary>
    /// Corrects the transport-encryption flag on an account pointed at a host QuickMail ships
    /// settings for.
    ///
    /// The failure this removes is silent and total: implicit TLS selected against a STARTTLS-only
    /// port sends a TLS ClientHello to a server that answers with a plaintext banner, so the
    /// handshake fails a second after the user presses Send, every time, with an error naming a
    /// certificate rather than a checkbox.
    ///
    /// Three conditions, all required. The account must not carry a
    /// <see cref="AccountModel.ProviderId"/> — that is what marks it as predating the catalog, and
    /// saving an account in Manage Accounts backfills the id, so a user who deliberately sets an
    /// unusual pairing and saves it is never overruled twice. The host must be one of ours. And the
    /// port must be the exact port we publish for that host: iCloud also answers implicit TLS on
    /// 465, and "correcting" an account set up that way would break a working account, which is the
    /// one outcome this code must never produce.
    /// </summary>
    private static bool RepairTransport(AccountModel account, IProviderCatalog catalog)
    {
        if (account.BackendKind == BackendKind.MicrosoftGraph) return false;
        if (!string.IsNullOrWhiteSpace(account.ProviderId)) return false;

        var changed = false;
        var adoptedStartTls = false;

        var imapProvider = MatchHost(catalog, account.ImapHost, p => p.ImapHost);
        if (imapProvider is not null
            && account.ImapPort == imapProvider.ImapPort
            && account.ImapUseSsl != imapProvider.ImapUseSsl)
        {
            LogService.Log(
                $"AccountStartupRepair: {account.AccountLabel} IMAP {account.ImapHost}:{account.ImapPort} " +
                $"useSsl {account.ImapUseSsl} → {imapProvider.ImapUseSsl} (per {imapProvider.DisplayName} settings)");
            account.ImapUseSsl = imapProvider.ImapUseSsl;
            adoptedStartTls |= !imapProvider.ImapUseSsl;
            changed = true;
        }

        var smtpProvider = MatchHost(catalog, account.SmtpHost, p => p.SmtpHost);
        if (smtpProvider is not null
            && account.SmtpPort == smtpProvider.SmtpPort
            && account.SmtpUseSsl != smtpProvider.SmtpUseSsl)
        {
            LogService.Log(
                $"AccountStartupRepair: {account.AccountLabel} SMTP {account.SmtpHost}:{account.SmtpPort} " +
                $"useSsl {account.SmtpUseSsl} → {smtpProvider.SmtpUseSsl} (per {smtpProvider.DisplayName} settings)");
            account.SmtpUseSsl = smtpProvider.SmtpUseSsl;
            adoptedStartTls |= !smtpProvider.SmtpUseSsl;
            changed = true;
        }

        // Adopting the catalog's pairing means adopting the catalog's guarantee with it. Without
        // this the repaired leg connects under StartTlsWhenAvailable, which — per MailSecurity's own
        // documentation — hands the password over in cleartext to a server advertising no STARTTLS.
        // AccountEditorViewModel.ApplyProvider sets the same flag for the same host and port, so
        // leaving it clear would make the repaired account weaker than one added through the dialog.
        //
        // Only when a leg actually moved TO STARTTLS. The flag is shared by both legs, and an
        // account whose other leg is a plaintext host we know nothing about should not start failing
        // because we corrected this one.
        if (adoptedStartTls && !account.RequireStartTls)
        {
            account.RequireStartTls = true;
            LogService.Log($"AccountStartupRepair: {account.AccountLabel} now requires STARTTLS on the repaired leg.");
        }

        return changed;
    }

    // Matched on the host the account actually connects to, NOT on ProviderCatalog.Resolve. Resolve
    // falls back to the email domain, which would claim a gmail.com address relayed through a
    // company's own server — and this code rewrites connection settings, so a confident wrong answer
    // is worse here than no answer.
    private static MailProvider? MatchHost(IProviderCatalog catalog, string? accountHost,
        System.Func<MailProvider, string> catalogHost)
    {
        if (string.IsNullOrWhiteSpace(accountHost)) return null;
        var host = accountHost.Trim();
        return catalog.All.FirstOrDefault(p =>
            !p.IsOther
            && !string.IsNullOrWhiteSpace(catalogHost(p))
            && string.Equals(catalogHost(p), host, System.StringComparison.OrdinalIgnoreCase));
    }
}
