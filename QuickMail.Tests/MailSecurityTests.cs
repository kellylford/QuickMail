using MailKit.Security;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The mapping from an account's security flags to the option MailKit connects with — the place the
/// difference between "STARTTLS" as a label and STARTTLS as a guarantee is actually decided.
///
/// <c>StartTlsWhenAvailable</c> connects in PLAINTEXT and authenticates anyway when the server
/// advertises no STARTTLS. Every discovered setting used to land there, so a host on port 143 that
/// simply never offers STARTTLS received the password in the clear while the dialog said only
/// "Settings found".
/// </summary>
public class MailSecurityTests
{
    [Theory]
    // useSsl wins outright: implicit TLS on connect, nothing to negotiate.
    [InlineData(true, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(true, true, SecureSocketOptions.SslOnConnect)]
    // The fix: required means negotiate STARTTLS or fail the connection.
    [InlineData(false, true, SecureSocketOptions.StartTls)]
    // Hand-entered settings keep the historical permissive behavior.
    [InlineData(false, false, SecureSocketOptions.StartTlsWhenAvailable)]
    public void SecurityFlagsMapToTheConnectOption(bool useSsl, bool requireStartTls, SecureSocketOptions expected)
        => Assert.Equal(expected, MailSecurity.Select(useSsl, requireStartTls));

    [Fact]
    public void AnAccountThatRequiresStartTlsGetsItOnBothLegs()
    {
        // Autodiscover's shape for a custom domain: IMAP on 143 and SMTP on 587, both STARTTLS.
        var account = new AccountModel
        {
            ImapPort = 143, ImapUseSsl = false,
            SmtpPort = 587, SmtpUseSsl = false,
            RequireStartTls = true,
        };

        Assert.Equal(SecureSocketOptions.StartTls, MailSecurity.ForImap(account));
        Assert.Equal(SecureSocketOptions.StartTls, MailSecurity.ForSmtp(account));
    }

    [Fact]
    public void AnExistingAccountFromAccountsJsonIsUnchanged()
    {
        // RequireStartTls defaults false precisely so accounts already on disk deserialize into the
        // behavior they had before the field existed. Nothing about them may connect differently.
        var account = new AccountModel { ImapUseSsl = false, SmtpUseSsl = false };

        Assert.False(account.RequireStartTls);
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, MailSecurity.ForImap(account));
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, MailSecurity.ForSmtp(account));
    }

    [Fact]
    public void ImplicitSslAccountsAreUntouchedByTheFlag()
    {
        var account = new AccountModel { ImapUseSsl = true, SmtpUseSsl = true, RequireStartTls = true };

        Assert.Equal(SecureSocketOptions.SslOnConnect, MailSecurity.ForImap(account));
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailSecurity.ForSmtp(account));
    }
}
