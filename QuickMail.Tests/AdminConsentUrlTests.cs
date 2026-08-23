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
    public void BuildAdminConsentUrl_UsesV2OrganizationsAdminConsentWithExplicitScopes()
    {
        var url = OAuthService.BuildAdminConsentUrl("state123");

        // v2.0 + explicit scope list (NOT v1 declared-perms): only these Graph scopes are consented, so the
        // request never touches the app registration's stale Exchange Online perms (AADSTS65006).
        Assert.StartsWith("https://login.microsoftonline.com/organizations/v2.0/adminconsent?", url);
        Assert.Contains("scope=", url);
        Assert.Contains("client_id=bcdc84f1-d37c-4581-b14a-a01f7b3a1312", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", url);
        Assert.Contains("state=state123", url);
    }

    [Fact]
    public void BuildAdminConsentUrl_RequestsEveryGraphScope_AndNoExchangeScope()
    {
        var url = OAuthService.BuildAdminConsentUrl("s");
        var scopes = Uri.UnescapeDataString(url.Split("scope=")[1].Split('&')[0]).Split(' ');

        foreach (var expected in new[]
                 {
                     "https://graph.microsoft.com/Mail.ReadWrite",
                     "https://graph.microsoft.com/Mail.Send",
                     "https://graph.microsoft.com/MailboxSettings.ReadWrite",
                     "https://graph.microsoft.com/User.Read",
                     "https://graph.microsoft.com/Contacts.Read",
                     "https://graph.microsoft.com/People.Read",
                     "https://graph.microsoft.com/Calendars.ReadWrite",
                     "https://graph.microsoft.com/Mail.ReadWrite.Shared",
                     "https://graph.microsoft.com/Mail.Send.Shared",
                 })
            Assert.Contains(expected, scopes);

        // No Exchange Online (outlook.office.com): requesting the full declared set at once 65006s on that
        // resource, so the admin-consent request stays Graph-only (the correct scope for #607 regardless).
        Assert.DoesNotContain(scopes, s => s.Contains("outlook.office.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(scopes.Length, scopes.Distinct().Count()); // deduped union
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
    public void ParseRedirect_SurfacesAadErrorEvenWithoutState()
    {
        // Azure AD does not always echo state on an error redirect. An error is never a grant, so we
        // report AAD's real description rather than a misleading "state mismatch".
        var r = OAuthService.ParseAdminConsentRedirect(
            new Uri("http://localhost/?error=access_denied&error_description=Consent+required"), "xyz");

        Assert.Equal(AdminConsentStatus.Error, r!.Value.Status);
        Assert.Equal("Consent required", r.Value.Error);
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
