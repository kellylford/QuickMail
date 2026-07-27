using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Live network checks for settings discovery, against domains chosen to cover the shapes that
/// matter. Skipped unless QUICKMAIL_LIVE_DISCOVERY=1, so CI and offline builds stay green.
///
/// Run with:
///   $env:QUICKMAIL_LIVE_DISCOVERY=1; dotnet test QuickMail.IntegrationTests --filter AutoDiscoverLive
/// </summary>
public class AutoDiscoverLiveTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUICKMAIL_LIVE_DISCOVERY") == "1";

    private sealed class Config(bool online) : IConfigService
    {
        public ConfigModel Load() => new() { AutoDiscoverOnline = online };
        public void Save(ConfigModel c) { }
    }

    private static AutoDiscoverService NewService(bool online = true) =>
        new(new ProviderCatalog(), new Config(online));

    // A Microsoft 365 tenant on its own domain that publishes no reachable Autodiscover record.
    // This is the case that used to fall all the way through to "enter your IMAP host" — for an
    // account whose only route in is a browser sign-in.
    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task AMicrosoft365CustomDomainIsIdentified()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService();

        var result = await svc.DiscoverAsync("discover@icanbrew.com", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ProviderCatalog.MicrosoftId, result!.ProviderId);
    }

    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task AKnownProviderStillResolvesOffline()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService();

        var result = await svc.DiscoverAsync("someone@gmail.com", CancellationToken.None);

        Assert.Equal(DiscoverySource.LocalCatalog, result!.Source);
    }

    // A domain with no mail service must come back empty, or the detection is worthless.
    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task ADomainWithNoMailServiceReturnsNothing()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService();

        Assert.Null(await svc.DiscoverAsync("discover@wikipedia.org", CancellationToken.None));
    }

    // The regression that motivated switching from "does this domain have a Microsoft tenant" to
    // "where does this domain's mail actually go". theideaplace.net kept its Entra ID tenant after
    // its mail moved off Exchange Online, so the tenant question answered yes, Microsoft's servers
    // were filled in, and the real password went to smtp-mail.outlook.com and came back 535.
    // Its MX points at the domain itself, so the mail-hosting question gets it right.
    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task ADomainThatLeftExchangeOnlineIsNotClaimedByMicrosoft()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService();

        var result = await svc.DiscoverAsync("discover@theideaplace.net", CancellationToken.None);

        Assert.True(result is null || result.ProviderId != ProviderCatalog.MicrosoftId,
            $"theideaplace.net was claimed by {result?.ProviderId} from {result?.Source}");
    }

    // A company that uses Microsoft 365 internally while running its own mail is the same class of
    // false positive, and the one most likely to hit other users.
    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task ATenantWhoseMailIsElsewhereIsNotClaimedByMicrosoft()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService();

        var result = await svc.DiscoverAsync("discover@fastmail.com", CancellationToken.None);

        Assert.True(result is null || result.ProviderId != ProviderCatalog.MicrosoftId,
            $"fastmail.com was claimed by {result?.ProviderId} from {result?.Source}");
    }

    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task NothingLeavesTheMachineWhenOnlineDiscoveryIsOff()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService(online: false);

        Assert.Null(await svc.DiscoverAsync("discover@icanbrew.com", CancellationToken.None));
    }
}
