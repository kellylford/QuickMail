using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #396: an iCloud account hand-configured before the provider catalog existed had implicit
/// TLS selected on port 587, which is a STARTTLS port. Every send failed a second after the button
/// was pressed. The repair corrects that pairing at startup — and, just as importantly, leaves
/// alone every setting where QuickMail is not the authority.
/// </summary>
public class AccountTransportRepairTests
{
    private static readonly ProviderCatalog Catalog = new();

    /// <summary>The reporter's account, field for field.</summary>
    private static AccountModel ICloudAccountWithImplicitSslOn587() => new()
    {
        Id = Guid.NewGuid(),
        AccountName = "icloud",
        Username = "samuel@interfree.ca",
        ImapHost = "imap.mail.me.com", ImapPort = 993, ImapUseSsl = true,
        SmtpHost = "smtp.mail.me.com", SmtpPort = 587, SmtpUseSsl = true,
    };

    [Fact]
    public void ImplicitSslOnAStartTlsPortIsCorrected()
    {
        var account = ICloudAccountWithImplicitSslOn587();

        var repaired = AccountTransportRepair.Apply([account], Catalog);

        Assert.Single(repaired);
        Assert.False(account.SmtpUseSsl);
        Assert.Equal(587, account.SmtpPort);          // the port is the user's, and is right
        Assert.Equal(SecureSocketOptionsForSmtp(account), MailSecurityStartTls);
    }

    [Fact]
    public void AnAccountAlreadyPairedCorrectlyIsNotReportedAsChanged()
    {
        var account = ICloudAccountWithImplicitSslOn587();
        account.SmtpUseSsl = false;

        var repaired = AccountTransportRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
        Assert.False(account.SmtpUseSsl);
        Assert.True(account.ImapUseSsl);
    }

    /// <summary>
    /// The narrowness that makes rewriting someone's settings defensible. A host QuickMail ships no
    /// table for is a host the user knows better than we do.
    /// </summary>
    [Fact]
    public void AnUnknownHostIsLeftExactlyAsTheUserSetIt()
    {
        var account = new AccountModel
        {
            ImapHost = "mail.myhost.example", ImapPort = 993, ImapUseSsl = false,
            SmtpHost = "smtp.myhost.example", SmtpPort = 587, SmtpUseSsl = true,
        };

        var repaired = AccountTransportRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
        Assert.False(account.ImapUseSsl);
        Assert.True(account.SmtpUseSsl);
    }

    /// <summary>
    /// Our host, but a port we publish nothing about. iCloud also answers implicit TLS on 465, and
    /// an account set up that way works perfectly — "correcting" it to STARTTLS would break a
    /// working account, which is the one outcome this code must never produce.
    /// </summary>
    [Fact]
    public void OurHostOnAPortWeDoNotPublishIsLeftAlone()
    {
        var account = ICloudAccountWithImplicitSslOn587();
        account.SmtpPort = 465;   // implicit TLS here is correct, and not what the catalog lists

        var repaired = AccountTransportRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
        Assert.True(account.SmtpUseSsl);
    }

    [Fact]
    public void AGraphAccountIsSkippedEntirely()
    {
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "kelly@outlook.com",
            ImapHost = string.Empty, SmtpHost = string.Empty,
        };

        var repaired = AccountTransportRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
    }

    [Fact]
    public void TheImapLegIsCorrectedIndependentlyOfSmtp()
    {
        var account = ICloudAccountWithImplicitSslOn587();
        account.SmtpUseSsl = false;   // outgoing already right
        account.ImapUseSsl = false;   // incoming wrong: 993 is implicit TLS

        var repaired = AccountTransportRepair.Apply([account], Catalog);

        Assert.Single(repaired);
        Assert.True(account.ImapUseSsl);
        Assert.False(account.SmtpUseSsl);
    }

    [Fact]
    public void OnlyTheAccountsThatNeededFixingAreReturned()
    {
        var broken = ICloudAccountWithImplicitSslOn587();
        var fine = ICloudAccountWithImplicitSslOn587();
        fine.SmtpUseSsl = false;
        var unknown = new AccountModel { ImapHost = "mail.example.test", SmtpHost = "smtp.example.test" };

        var repaired = AccountTransportRepair.Apply([broken, fine, unknown], Catalog);

        Assert.Single(repaired);
        Assert.Same(broken, repaired[0]);
    }

    [Fact]
    public void AnEmptyOrNullInputIsHarmless()
    {
        Assert.Empty(AccountTransportRepair.Apply(new List<AccountModel>(), Catalog));
        Assert.Empty(AccountTransportRepair.Apply(null!, Catalog));
        Assert.Empty(AccountTransportRepair.Apply([ICloudAccountWithImplicitSslOn587()], null!));
    }

    // The repair's whole purpose is the option MailKit ends up connecting with, so assert on that
    // rather than only on the bool that feeds it.
    private static MailKit.Security.SecureSocketOptions SecureSocketOptionsForSmtp(AccountModel account) =>
        MailSecurity.Select(account.SmtpUseSsl, account.RequireStartTls);

    private const MailKit.Security.SecureSocketOptions MailSecurityStartTls =
        MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable;
}
