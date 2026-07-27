using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the built-in provider table. The host/port values here are the ones that used to be
/// hardcoded in AddAccountViewModel — if they drift, existing accounts stop matching and every
/// new account of that provider is created with the wrong servers.
/// </summary>
public class ProviderCatalogTests
{
    private readonly ProviderCatalog _catalog = new();

    [Theory]
    [InlineData("kelly@gmail.com", ProviderCatalog.GmailId)]
    [InlineData("KELLY@GMAIL.COM", ProviderCatalog.GmailId)]
    [InlineData("kelly@googlemail.com", ProviderCatalog.GmailId)]
    [InlineData("kelly@outlook.com", ProviderCatalog.MicrosoftId)]
    [InlineData("kelly@hotmail.com", ProviderCatalog.MicrosoftId)]
    [InlineData("kelly@yahoo.com", ProviderCatalog.YahooId)]
    [InlineData("kelly@ymail.com", ProviderCatalog.YahooId)]
    [InlineData("kelly@icloud.com", ProviderCatalog.ICloudId)]
    [InlineData("kelly@me.com", ProviderCatalog.ICloudId)]
    [InlineData("kelly@mac.com", ProviderCatalog.ICloudId)]
    public void MatchByEmail_RecognizesKnownDomains(string email, string expectedId)
        => Assert.Equal(expectedId, _catalog.MatchByEmail(email)?.Id);

    [Theory]
    [InlineData("kelly@theideaplace.net")]
    [InlineData("kelly@somecollege.edu")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("trailing@")]
    public void MatchByEmail_ReturnsNullForUnknownOrMalformed(string email)
        => Assert.Null(_catalog.MatchByEmail(email));

    // A subdomain is a different mail system: notgmail.com must not match gmail.com, and neither
    // must mail.gmail.com. Suffix matching would wrongly accept both.
    [Theory]
    [InlineData("kelly@notgmail.com")]
    [InlineData("kelly@mail.gmail.com")]
    [InlineData("kelly@gmail.com.example.org")]
    public void MatchByEmail_DoesNotMatchLookalikeDomains(string email)
        => Assert.Null(_catalog.MatchByEmail(email));

    [Fact]
    public void Other_IsLastAndNeverMatchesByDomain()
    {
        Assert.Equal(ProviderCatalog.OtherId, _catalog.All[^1].Id);
        Assert.True(_catalog.Other.IsOther);
        Assert.False(_catalog.Other.MatchesEmail("anyone@anywhere.com"));
    }

    [Fact]
    public void GmailDefaultsToPasswordAuth_ButStillOffersOAuth()
    {
        var gmail = _catalog.ById(ProviderCatalog.GmailId)!;

        // #369: Google OAuth sign-in is blocked for new accounts, so an app password is the
        // default. The OAuth path must remain available under Advanced settings.
        Assert.Equal(AuthType.Password, gmail.DefaultAuthType);
        Assert.True(gmail.SupportsOAuth);
        Assert.True(gmail.RequiresAppPassword);
        Assert.Equal("https://myaccount.google.com/apppasswords", gmail.AppPasswordUrl);
    }

    [Fact]
    public void MicrosoftDefaultsToOAuthOverImap_NotGraph()
    {
        var ms = _catalog.ById(ProviderCatalog.MicrosoftId)!;

        Assert.Equal(AuthType.OAuth2Microsoft, ms.DefaultAuthType);
        // Deliberate: keeps the pre-catalog default for new accounts. Graph stays opt-in under
        // Advanced settings, so this change adds no silent behavior shift.
        Assert.Equal(BackendKind.ImapSmtp, ms.DefaultBackend);
        Assert.False(ms.RequiresAppPassword);
    }

    [Theory]
    [InlineData(ProviderCatalog.GmailId, "imap.gmail.com", "smtp.gmail.com")]
    [InlineData(ProviderCatalog.MicrosoftId, "outlook.office365.com", "smtp-mail.outlook.com")]
    [InlineData(ProviderCatalog.YahooId, "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData(ProviderCatalog.ICloudId, "imap.mail.me.com", "smtp.mail.me.com")]
    public void KnownProviders_CarryTheHistoricHostsPortsAndSslModes(
        string id, string imapHost, string smtpHost)
    {
        var p = _catalog.ById(id)!;

        Assert.Equal(imapHost, p.ImapHost);
        Assert.Equal(993, p.ImapPort);
        Assert.True(p.ImapUseSsl);

        Assert.Equal(smtpHost, p.SmtpHost);
        Assert.Equal(587, p.SmtpPort);
        // false means STARTTLS on 587, matching AccountModel's default. Flipping this to implicit
        // SSL on 587 breaks sending for every one of these providers.
        Assert.False(p.SmtpUseSsl);
    }

    [Fact]
    public void YahooAndICloudRequireAppPasswords()
    {
        Assert.True(_catalog.ById(ProviderCatalog.YahooId)!.RequiresAppPassword);
        Assert.True(_catalog.ById(ProviderCatalog.ICloudId)!.RequiresAppPassword);
    }

    [Fact]
    public void ById_IsNullForUnknownAndBlank()
    {
        Assert.Null(_catalog.ById("nope"));
        Assert.Null(_catalog.ById(null));
        Assert.Null(_catalog.ById("  "));
    }

    [Fact]
    public void Resolve_PrefersPersistedProviderId()
    {
        // ProviderId wins even when the host says otherwise — the user may have hand-edited hosts.
        var account = new AccountModel
        {
            ProviderId = ProviderCatalog.YahooId,
            ImapHost = "imap.gmail.com",
        };

        Assert.Equal(ProviderCatalog.YahooId, _catalog.Resolve(account).Id);
    }

    // Accounts created before the catalog existed have no ProviderId. They must still resolve, or
    // every provider-driven behavior (app-password hint, contact/calendar sync eligibility)
    // silently regresses for existing users.
    [Theory]
    [InlineData("imap.mail.me.com", ProviderCatalog.ICloudId)]
    [InlineData("imap.gmail.com", ProviderCatalog.GmailId)]
    [InlineData("outlook.office365.com", ProviderCatalog.MicrosoftId)]
    [InlineData("imap.mail.yahoo.com", ProviderCatalog.YahooId)]
    public void Resolve_FallsBackToImapHostWhenProviderIdIsNull(string imapHost, string expectedId)
    {
        var account = new AccountModel { ProviderId = null, ImapHost = imapHost };

        Assert.Equal(expectedId, _catalog.Resolve(account).Id);
    }

    [Fact]
    public void Resolve_UsesBackendForGraphAccountsWhichHaveNoImapHost()
    {
        var account = new AccountModel
        {
            ProviderId = null,
            BackendKind = BackendKind.MicrosoftGraph,
            ImapHost = string.Empty,
            Username = "kelly@contoso.com",
        };

        Assert.Equal(ProviderCatalog.MicrosoftId, _catalog.Resolve(account).Id);
    }

    [Fact]
    public void Resolve_FallsBackToUsernameDomainWhenHostWasHandEntered()
    {
        var account = new AccountModel
        {
            ProviderId = null,
            ImapHost = "imap.googlemail.com", // valid alias, not the catalog host
            Username = "kelly@gmail.com",
        };

        Assert.Equal(ProviderCatalog.GmailId, _catalog.Resolve(account).Id);
    }

    // "other" is not an answer — it records that nothing was identified when the account was made.
    // Treating it as one stranded hand-configured accounts: an iCloud mailbox added via "Other" with
    // the Apple host typed in resolved as Other forever, so ProviderCatalog.IsICloud said false and
    // contact/calendar sync silently never ran, while the dialog had happily offered the checkboxes.
    [Theory]
    [InlineData("imap.mail.me.com", ProviderCatalog.ICloudId)]
    [InlineData("imap.gmail.com", ProviderCatalog.GmailId)]
    public void Resolve_TreatsAPersistedOtherAsUnknownAndFallsBackToTheHost(string imapHost, string expectedId)
    {
        var account = new AccountModel { ProviderId = ProviderCatalog.OtherId, ImapHost = imapHost };

        Assert.Equal(expectedId, _catalog.Resolve(account).Id);
    }

    [Fact]
    public void IsICloud_AgreesWithTheDialogForAHandConfiguredICloudAccount()
    {
        // The exact regression: saved via provider "Other", Apple host typed by hand.
        var account = new AccountModel
        {
            ProviderId = ProviderCatalog.OtherId,
            ImapHost = "imap.mail.me.com",
            Username = "kelly@icloud.com",
            AuthType = AuthType.Password,
        };

        Assert.True(ProviderCatalog.IsICloud(account));
    }

    [Fact]
    public void Resolve_ReturnsOtherForAnUnrecognizedAccount()
    {
        var account = new AccountModel
        {
            ProviderId = null,
            ImapHost = "mail.theideaplace.net",
            Username = "kelly@theideaplace.net",
        };

        Assert.Equal(ProviderCatalog.OtherId, _catalog.Resolve(account).Id);
    }

    [Fact]
    public void AllProviderIdsAreUniqueAndDisplayNamesAreNonEmpty()
    {
        var ids = _catalog.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(_catalog.All, p => Assert.False(string.IsNullOrWhiteSpace(p.DisplayName)));
    }

    [Fact]
    public void EveryAppPasswordHintCarriesAUrl()
    {
        foreach (var p in _catalog.All.Where(p => p.RequiresAppPassword))
            Assert.False(string.IsNullOrWhiteSpace(p.AppPasswordUrl));
    }
}
