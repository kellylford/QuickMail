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
    public void CustomDomainPersonalAccount_NotYetDetected_FallsBackToDefault_KnownMigrationGap()
    {
        // DELIBERATE, documented fallback (#234 review, concern 1): a personal account on a custom
        // domain that hasn't been detected yet (added before tenant detection shipped, never re-authed)
        // has a null flag → the email-domain guess says "work" → `.default`. Auto-heal is deferred; new
        // accounts are detected at sign-in, so this only affects pre-detection accounts (near-zero while
        // the Graph backend is feature-gated). Locked as a test so it reads as intended, not an oversight.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "me@myvanitydomain.com",
            IsPersonalMicrosoftAccount = null,
        };
        Assert.Same(OAuthService.GraphMailScopes, OAuthService.DefaultScopesFor(account));
    }
}
