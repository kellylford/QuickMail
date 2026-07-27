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

    // A domain with no mail service and no Microsoft tenant must still come back empty, or the
    // "appears to be Microsoft 365" suggestion would be worthless.
    [Fact]
    [Trait("Category", "LiveDiscovery")]
    public async Task ADomainWithNoMailServiceReturnsNothing()
    {
        Assert.SkipUnless(Enabled, "Set QUICKMAIL_LIVE_DISCOVERY=1 to run live discovery checks.");
        using var svc = NewService();

        Assert.Null(await svc.DiscoverAsync("discover@wikipedia.org", CancellationToken.None));
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
