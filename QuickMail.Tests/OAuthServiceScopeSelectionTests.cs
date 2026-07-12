using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the per-account scope selection (#217/#218): personal Microsoft accounts must get the
/// explicit Graph scopes (so they can write/delete), work/school Graph accounts use `.default`, and
/// IMAP always uses the IMAP scopes. Guards against a future refactor silently reverting the routing.
/// </summary>
public class OAuthServiceScopeSelectionTests
{
    [Theory]
    [InlineData("me@outlook.com")]
    [InlineData("me@hotmail.com")]
    [InlineData("ME@Live.com")] // case-insensitive
    public void PersonalGraphAccount_UsesExplicitScopes(string username)
    {
        var account = new AccountModel { BackendKind = BackendKind.MicrosoftGraph, Username = username };
        Assert.Same(OAuthService.GraphMailScopesPersonal, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void WorkSchoolGraphAccount_UsesDefaultScope()
    {
        var account = new AccountModel { BackendKind = BackendKind.MicrosoftGraph, Username = "user@contoso.com" };
        Assert.Same(OAuthService.GraphMailScopes, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void ImapAccount_UsesImapScopes_EvenOnAPersonalDomain()
    {
        // The personal-domain check applies only to the Graph backend; IMAP always gets IMAP scopes.
        var account = new AccountModel { BackendKind = BackendKind.ImapSmtp, Username = "me@outlook.com" };
        Assert.Same(OAuthService.ImapSmtpScopes, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void CustomDomainPersonalAccount_StoredTenantFlagOverridesDomainGuess() // #233
    {
        // A personal Microsoft account on a custom/vanity domain: the email-domain guess would say
        // "work" and break write, but the persisted tenant-derived flag makes it use explicit scopes.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "me@myvanitydomain.com",
            IsPersonalMicrosoftAccount = true,
        };
        Assert.Same(OAuthService.GraphMailScopesPersonal, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void WorkAccountOnPersonalLookingDomain_StoredFlagFalseUsesDefault()
    {
        // Flag explicitly false wins over a personal-looking domain → work/school (`.default`).
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "user@outlook.com",
            IsPersonalMicrosoftAccount = false,
        };
        Assert.Same(OAuthService.GraphMailScopes, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void ImapScopes_AreExplicit_NotDefault() // #239
    {
        // `.default` on the IMAP resource is invalid for personal Microsoft accounts and blocked
        // sign-in entirely (#239). The IMAP/SMTP path must request explicit scopes, which work for
        // personal and work accounts alike (the IMAP path doesn't consult the personal/work flag).
        Assert.Contains("https://outlook.office.com/IMAP.AccessAsUser.All", OAuthService.ImapSmtpScopes);
        Assert.Contains("https://outlook.office.com/SMTP.Send", OAuthService.ImapSmtpScopes);
        Assert.DoesNotContain("https://outlook.office.com/.default", OAuthService.ImapSmtpScopes);
    }

    [Theory]
    [InlineData("me@outlook.com")]  // personal domain
    [InlineData("me@outlook.cl")]   // custom-domain personal (the exact #239 reporter case)
    [InlineData("user@contoso.com")] // work
    public void ImapAccount_AlwaysGetsExplicitImapScopes_RegardlessOfType(string username)
    {
        var account = new AccountModel { BackendKind = BackendKind.ImapSmtp, Username = username };
        Assert.Same(OAuthService.ImapSmtpScopes, OAuthService.DefaultScopesFor(account));
    }
}
