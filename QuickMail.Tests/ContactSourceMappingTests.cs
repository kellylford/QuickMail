using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Services.Graph;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Verifies the provider DTO → <see cref="ContactModel"/> mapping for both contact sources
/// (issue #256), using a canned <see cref="HttpMessageHandler"/> so no network is touched. The
/// stub OAuth services supply an empty bearer token.
/// </summary>
public class ContactSourceMappingTests
{
    // Returns a fixed JSON body for the first URL substring that matches; 404 otherwise.
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly (string UrlContains, string Json)[] _routes;
        public CannedHandler(params (string, string)[] routes) => _routes = routes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            foreach (var (contains, json) in _routes)
                if (url.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                    });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static readonly AccountModel MsAccount =
        new() { Username = "u@x.test", AuthType = AuthType.OAuth2Microsoft };
    private static readonly AccountModel GoogleAccount =
        new() { Username = "u@gmail.test", AuthType = AuthType.OAuth2Google };

    [Fact]
    public async Task Graph_MapsSavedContactsAndPriorRecipients()
    {
        var handler = new CannedHandler(
            ("/me/contacts", """
                {"value":[{"id":"c1","displayName":"Alice","emailAddresses":[{"name":"Alice","address":"alice@x.test"}]}]}
                """),
            ("/me/people", """
                {"value":[{"id":"p1","displayName":"","scoredEmailAddresses":[{"address":"bob@x.test","relevanceScore":1.0}]}]}
                """));
        var source = new GraphContactSource(new GraphClient(new StubOAuthService(), new HttpClient(handler)));

        var result = await source.FetchAsync(MsAccount);

        var alice = result.Single(c => c.EmailAddress == "alice@x.test");
        Assert.Equal("c1", alice.SourceId);
        Assert.Equal("Alice", alice.DisplayName);
        Assert.False(alice.IsPriorRecipient);

        var bob = result.Single(c => c.EmailAddress == "bob@x.test");
        Assert.True(bob.IsPriorRecipient);
        Assert.Equal(string.Empty, bob.DisplayName); // person with no display name
        Assert.Equal(ContactSource.Microsoft, source.Source);
    }

    [Fact]
    public async Task Graph_SavedContactWinsOverSamePriorRecipient()
    {
        var handler = new CannedHandler(
            ("/me/contacts", """
                {"value":[{"id":"c1","displayName":"Dup","emailAddresses":[{"address":"dup@x.test"}]}]}
                """),
            ("/me/people", """
                {"value":[{"id":"p1","displayName":"Dup Person","scoredEmailAddresses":[{"address":"dup@x.test"}]}]}
                """));
        var source = new GraphContactSource(new GraphClient(new StubOAuthService(), new HttpClient(handler)));

        var result = await source.FetchAsync(MsAccount);

        Assert.Single(result, c => c.EmailAddress == "dup@x.test");
        Assert.False(result.Single(c => c.EmailAddress == "dup@x.test").IsPriorRecipient);
    }

    [Fact]
    public async Task Google_MapsConnectionsAndOtherContacts()
    {
        var handler = new CannedHandler(
            ("connections", """
                {"connections":[{"resourceName":"people/c1","names":[{"displayName":"Gina"}],"emailAddresses":[{"value":"gina@x.test"}]}]}
                """),
            ("otherContacts", """
                {"otherContacts":[{"resourceName":"otherContacts/c9","emailAddresses":[{"value":"otto@x.test"}]}]}
                """));
        var source = new GoogleContactSource(new GooglePeopleClient(new StubGoogleOAuthService(), new HttpClient(handler)));

        var result = await source.FetchAsync(GoogleAccount);

        var gina = result.Single(c => c.EmailAddress == "gina@x.test");
        Assert.Equal("people/c1", gina.SourceId);
        Assert.Equal("Gina", gina.DisplayName);
        Assert.False(gina.IsPriorRecipient);

        var otto = result.Single(c => c.EmailAddress == "otto@x.test");
        Assert.Equal("otherContacts/c9", otto.SourceId);
        Assert.True(otto.IsPriorRecipient);              // other contacts are prior recipients
        Assert.Equal(string.Empty, otto.DisplayName);
        Assert.Equal(ContactSource.Google, source.Source);
    }

    [Fact]
    public async Task Google_SkipsPersonWithNoEmail()
    {
        var handler = new CannedHandler(
            ("connections", """
                {"connections":[{"resourceName":"people/c1","names":[{"displayName":"NoEmail"}]}]}
                """),
            ("otherContacts", """{"otherContacts":[]}"""));
        var source = new GoogleContactSource(new GooglePeopleClient(new StubGoogleOAuthService(), new HttpClient(handler)));

        var result = await source.FetchAsync(GoogleAccount);

        Assert.Empty(result);
    }
}
