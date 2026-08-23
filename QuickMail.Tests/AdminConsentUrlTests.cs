using System;
using System.Linq;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #607: the pure core of the organization admin-consent flow — the /adminconsent URL builder and the
/// redirect parser. These are the whole security-and-correctness surface (right endpoint, all scopes,
/// state/CSRF guard, grant-vs-decline-vs-error), tested without MSAL or a WebView.
/// </summary>
public class AdminConsentUrlTests
{
    [Fact]
    public void BuildAdminConsentUrl_UsesV1OrganizationsAdminConsentEndpoint()
    {
        var url = OAuthService.BuildAdminConsentUrl("state123");

        // v1 /adminconsent (NOT /v2.0): consents the app's whole DECLARED permission set, no scope list.
        Assert.StartsWith("https://login.microsoftonline.com/organizations/adminconsent?", url);
        Assert.DoesNotContain("/v2.0/", url);
        Assert.DoesNotContain("scope=", url);   // declared-perms grant carries no scope parameter
        Assert.Contains("client_id=bcdc84f1-d37c-4581-b14a-a01f7b3a1312", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", url);
        Assert.Contains("state=state123", url);
    }

    [Fact]
    public void ParseRedirect_GrantedWhenAdminConsentTrueAndStateMatches()
    {
        var r = OAuthService.ParseAdminConsentRedirect(
            new Uri("http://localhost/?admin_consent=True&tenant=abc&state=xyz"), "xyz");

        Assert.NotNull(r);
        Assert.Equal(AdminConsentStatus.Granted, r!.Value.Status);
        Assert.Null(r.Value.Error);
    }

    [Fact]
    public void ParseRedirect_ErrorCarriesDescription()
    {
        var r = OAuthService.ParseAdminConsentRedirect(
            new Uri("http://localhost/?error=access_denied&error_description=Not+an+admin&state=xyz"), "xyz");

        Assert.Equal(AdminConsentStatus.Error, r!.Value.Status);
        Assert.Equal("Not an admin", r.Value.Error);
    }

    [Fact]
    public void ParseRedirect_StateMismatchIsErrorNeverSuccess() // CSRF guard
    {
        var r = OAuthService.ParseAdminConsentRedirect(
            new Uri("http://localhost/?admin_consent=True&state=WRONG"), "xyz");

        Assert.Equal(AdminConsentStatus.Error, r!.Value.Status); // not Granted, despite admin_consent=True
    }

    [Fact]
    public void ParseRedirect_DeclinedWhenLandsOnLocalhostWithoutGrant()
    {
        var r = OAuthService.ParseAdminConsentRedirect(
            new Uri("http://localhost/?state=xyz"), "xyz");

        Assert.Equal(AdminConsentStatus.Declined, r!.Value.Status);
    }

    [Fact]
    public void ParseRedirect_NullWhileStillOnAzureAdPages()
    {
        // Not the localhost redirect yet — the caller keeps waiting rather than concluding anything.
        var r = OAuthService.ParseAdminConsentRedirect(
            new Uri("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?x=1"), "xyz");

        Assert.Null(r);
    }
}
