using MailKit.Security;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Turns an account's transport-security flags into the option MailKit connects with. One place,
/// because IMAP and the three SMTP entry points must agree — a verification that connected more
/// permissively than the real send would prove nothing.
///
/// The distinction that matters is <c>StartTls</c> versus <c>StartTlsWhenAvailable</c>.
/// "WhenAvailable" means: if the server advertises no STARTTLS, connect in plaintext and
/// authenticate anyway. That is a downgrade nobody asked for, which is why
/// <see cref="AccountModel.RequireStartTls"/> exists.
/// </summary>
internal static class MailSecurity
{
    /// <summary>Connect option for this account's incoming (IMAP) connection.</summary>
    public static SecureSocketOptions ForImap(AccountModel account) =>
        Select(account.ImapUseSsl, account.RequireStartTls);

    /// <summary>Connect option for this account's incoming (POP3) connection.</summary>
    public static SecureSocketOptions ForPop3(AccountModel account) =>
        Select(account.Pop3UseSsl, account.RequireStartTls);

    /// <summary>Connect option for this account's outgoing (SMTP) connection.</summary>
    public static SecureSocketOptions ForSmtp(AccountModel account) =>
        Select(account.SmtpUseSsl, account.RequireStartTls);

    /// <summary>
    /// Implicit TLS on connect wins outright. Otherwise STARTTLS is either required — negotiate it
    /// or fail — or merely attempted, which is the historical behavior kept for hand-entered
    /// settings.
    /// </summary>
    internal static SecureSocketOptions Select(bool useSsl, bool requireStartTls) =>
        useSsl ? SecureSocketOptions.SslOnConnect
        : requireStartTls ? SecureSocketOptions.StartTls
        : SecureSocketOptions.StartTlsWhenAvailable;
}
