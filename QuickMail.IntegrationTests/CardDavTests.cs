using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// CardDAV protocol tests against local Radicale (live-content testing plan §4.3):
/// addressbook discovery and contact sync round-trip, without touching iCloud.
/// </summary>
[Collection(RadicaleCollection.Name)]
public sealed class CardDavTests
{
    private readonly RadicaleFixture _radicale;

    public CardDavTests(RadicaleFixture radicale) => _radicale = radicale;

    private static string MakeVCard(string uid, string fullName, string email) => string.Join("\r\n",
        "BEGIN:VCARD",
        "VERSION:3.0",
        $"UID:{uid}",
        $"FN:{fullName}",
        $"EMAIL:{email}",
        "END:VCARD");

    [Fact]
    public async Task DiscoverAddressbook_FindsTheUsersAddressbook()
    {
        _radicale.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var user = RadicaleFixture.CreateUser("abdiscover");
        var addressbook = await _radicale.CreateAddressbookAsync(user, "contacts", ct);

        using var client = new CardDavContactClient();
        var info = await client.DiscoverAddressbookAsync(
            RadicaleFixture.ServerUrl, user, RadicaleFixture.Password, ct);

        Assert.Equal(new Uri(addressbook).AbsolutePath, new Uri(info.Url).AbsolutePath);
    }

    [Fact]
    public async Task FetchVCards_RoundTripsSeededContacts()
    {
        _radicale.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var user = RadicaleFixture.CreateUser("abfetch");
        var addressbook = await _radicale.CreateAddressbookAsync(user, "contacts", ct);
        await _radicale.PutResourceAsync($"{addressbook}alice.vcf",
            MakeVCard("alice-1@quickmail.test", "Alice Tester", "alice@example.test"), "text/vcard", user, ct);
        await _radicale.PutResourceAsync($"{addressbook}bob.vcf",
            MakeVCard("bob-1@quickmail.test", "Bob Tester", "bob@example.test"), "text/vcard", user, ct);

        using var client = new CardDavContactClient();
        var vcards = await client.FetchVCardsAsync(addressbook, user, RadicaleFixture.Password, ct);

        Assert.Equal(2, vcards.Count);
        Assert.Contains(vcards, v => v.Contains("Alice Tester") && v.Contains("alice@example.test"));
        Assert.Contains(vcards, v => v.Contains("Bob Tester") && v.Contains("bob@example.test"));
    }
}
