using System;
using System.Collections.Generic;
using MailKit.Security;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #396: an iCloud account hand-configured before the provider catalog existed had implicit
/// TLS selected on port 587, which is a STARTTLS port. Every send failed a second after the button
/// was pressed. The repair corrects that pairing at startup, preserves a login name found where an
/// email address belongs — and, just as importantly, leaves alone every setting where QuickMail is
/// not the authority.
/// </summary>
public class AccountStartupRepairTests
{
    private static readonly ProviderCatalog Catalog = new();

    /// <summary>The reporter's account, field for field — including no ProviderId.</summary>
    private static AccountModel ICloudAccountWithImplicitSslOn587() => new()
    {
        Id = Guid.NewGuid(),
        AccountName = "icloud",
        Username = "samuel@interfree.ca",
        AuthType = AuthType.Password,
        ImapHost = "imap.mail.me.com", ImapPort = 993, ImapUseSsl = true,
        SmtpHost = "smtp.mail.me.com", SmtpPort = 587, SmtpUseSsl = true,
    };

    // ── Transport ────────────────────────────────────────────────────────────────

    [Fact]
    public void ImplicitSslOnAStartTlsPortIsCorrected()
    {
        var account = ICloudAccountWithImplicitSslOn587();

        var repaired = AccountStartupRepair.Apply([account], Catalog);

        Assert.Single(repaired);
        Assert.False(account.SmtpUseSsl);
        Assert.Equal(587, account.SmtpPort);          // the port is the user's, and is right
    }

    /// <summary>
    /// Adopting the catalog's pairing has to mean adopting its guarantee. Without RequireStartTls the
    /// repaired account connects under StartTlsWhenAvailable, which hands the password over in
    /// cleartext to a server advertising no STARTTLS — leaving the repaired account WEAKER than the
    /// same settings entered through the Add Account dialog, which sets the flag.
    /// </summary>
    [Fact]
    public void ARepairedLegRequiresStartTlsRatherThanMerelyAttemptingIt()
    {
        var account = ICloudAccountWithImplicitSslOn587();

        AccountStartupRepair.Apply([account], Catalog);

        Assert.True(account.RequireStartTls);
        Assert.Equal(SecureSocketOptions.StartTls, MailSecurity.ForSmtp(account));
        Assert.NotEqual(SecureSocketOptions.StartTlsWhenAvailable, MailSecurity.ForSmtp(account));
    }

    /// <summary>
    /// The flag is shared by both legs, so it is only raised when a leg actually moved TO STARTTLS.
    /// Correcting a leg to implicit TLS says nothing about what the other leg's server offers.
    /// </summary>
    [Fact]
    public void CorrectingALegToImplicitTlsDoesNotRaiseTheStartTlsRequirement()
    {
        var account = ICloudAccountWithImplicitSslOn587();
        account.SmtpUseSsl = false;   // outgoing already right
        account.ImapUseSsl = false;   // incoming wrong: 993 is implicit TLS

        AccountStartupRepair.Apply([account], Catalog);

        Assert.True(account.ImapUseSsl);
        Assert.False(account.RequireStartTls);
    }

    [Fact]
    public void AnAccountAlreadyPairedCorrectlyIsNotReportedAsChanged()
    {
        var account = ICloudAccountWithImplicitSslOn587();
        account.SmtpUseSsl = false;

        var repaired = AccountStartupRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
        Assert.False(account.SmtpUseSsl);
        Assert.True(account.ImapUseSsl);
    }

    [Fact]
    public void RepairingTwiceChangesNothingTheSecondTime()
    {
        var account = ICloudAccountWithImplicitSslOn587();

        Assert.Single(AccountStartupRepair.Apply([account], Catalog));
        Assert.Empty(AccountStartupRepair.Apply([account], Catalog));
    }

    /// <summary>
    /// The gate that stops the repair overruling the same user twice. Saving an account in Manage
    /// Accounts backfills ProviderId, so someone who deliberately sets an unusual pairing and saves
    /// it keeps it through every later restart.
    /// </summary>
    [Fact]
    public void AnAccountTheUserHasSavedIsNeverOverruled()
    {
        var account = ICloudAccountWithImplicitSslOn587();
        account.ProviderId = ProviderCatalog.ICloudId;

        var repaired = AccountStartupRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
        Assert.True(account.SmtpUseSsl);   // their deliberate choice survives
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
            Username = "kelly@myhost.example",
            ImapHost = "mail.myhost.example", ImapPort = 993, ImapUseSsl = false,
            SmtpHost = "smtp.myhost.example", SmtpPort = 587, SmtpUseSsl = true,
        };

        var repaired = AccountStartupRepair.Apply([account], Catalog);

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

        var repaired = AccountStartupRepair.Apply([account], Catalog);

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

        var repaired = AccountStartupRepair.Apply([account], Catalog);

        Assert.Empty(repaired);
    }

    [Fact]
    public void OnlyTheAccountsThatNeededFixingAreReturned()
    {
        var broken = ICloudAccountWithImplicitSslOn587();
        var fine = ICloudAccountWithImplicitSslOn587();
        fine.SmtpUseSsl = false;
        var unknown = new AccountModel
        {
            Username = "kelly@example.test",
            ImapHost = "mail.example.test", SmtpHost = "smtp.example.test",
        };

        var repaired = AccountStartupRepair.Apply([broken, fine, unknown], Catalog);

        Assert.Single(repaired);
        Assert.Same(broken, repaired[0]);
    }

    // ── Preserving a login name found in the address field ───────────────────────

    /// <summary>
    /// The account #396 was reported from. Correcting the email address is what QuickMail now asks
    /// for — but the value being corrected IS the working login, so it has to be kept first or the
    /// user follows the instructions and loses their mail entirely.
    /// </summary>
    [Fact]
    public void ALoginNameSittingInTheAddressFieldIsPreservedBeforeTheUserIsAskedToFixIt()
    {
        var account = new AccountModel
        {
            Username = "fastfinge",
            AuthType = AuthType.Password,
            ImapHost = "imap.mail.me.com", ImapPort = 993, ImapUseSsl = true,
            SmtpHost = "smtp.mail.me.com", SmtpPort = 587, SmtpUseSsl = false,
        };

        var repaired = AccountStartupRepair.Apply([account], Catalog);

        Assert.Single(repaired);
        Assert.Equal("fastfinge", account.LoginUsername);
        Assert.Equal("fastfinge", account.AuthUsername);

        // Now the user does exactly what the refusal message tells them to.
        account.Username = "samuel@interfree.ca";
        Assert.Equal("fastfinge", account.AuthUsername);   // the login still works
    }

    [Fact]
    public void AnAccountWithARealAddressIsNotGivenALoginOverride()
    {
        var account = ICloudAccountWithImplicitSslOn587();

        AccountStartupRepair.Apply([account], Catalog);

        Assert.Null(account.LoginUsername);
    }

    [Fact]
    public void AnExistingOverrideIsNeverReplaced()
    {
        var account = new AccountModel
        {
            Username = "fastfinge",
            LoginUsername = "something-the-user-chose",
            AuthType = AuthType.Password,
        };

        AccountStartupRepair.Apply([account], Catalog);

        Assert.Equal("something-the-user-chose", account.LoginUsername);
    }

    [Theory]
    [InlineData(AuthType.OAuth2Microsoft)]
    [InlineData(AuthType.OAuth2Google)]
    public void AnOAuthAccountIsNotGivenALoginOverride(AuthType authType)
    {
        // OAuth authenticates as the mailbox the token was issued for and never reads
        // LoginUsername, so there is nothing to preserve and nothing to write.
        var account = new AccountModel { Username = "not-an-address", AuthType = authType };

        AccountStartupRepair.Apply([account], Catalog);

        Assert.Null(account.LoginUsername);
    }

    [Fact]
    public void AnEmptyOrNullInputIsHarmless()
    {
        Assert.Empty(AccountStartupRepair.Apply(new List<AccountModel>(), Catalog));
        Assert.Empty(AccountStartupRepair.Apply(null!, Catalog));
        Assert.Empty(AccountStartupRepair.Apply([ICloudAccountWithImplicitSslOn587()], null!));
    }
}
