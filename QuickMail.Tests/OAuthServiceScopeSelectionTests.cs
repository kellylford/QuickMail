using Microsoft.Identity.Client;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the per-account scope selection (#217/#218, #511/#529): personal Microsoft accounts get the
/// explicit personal Graph scopes, work/school Graph accounts get the explicit work/school Graph scopes
/// (the #529 bridge — so a Graph sign-in never validates the Exchange entitlement, #511), and IMAP
/// always uses the IMAP scopes. Guards against a future refactor silently reverting the routing.
/// </summary>
public class OAuthServiceScopeSelectionTests
{
    // A work-or-school Microsoft address on Graph asks for explicit Graph scopes, NOT `.default`
    // (#511/#529 bridge). `.default` requested the whole declared set, dragging the Exchange IMAP/SMTP
    // permissions into every fresh Graph consent — whose flaky validation produced the intermittent
    // AADSTS65006. The explicit list requests only the Graph mail permissions, so the Exchange
    // entitlement is never touched by a Graph sign-in.
    [Fact]
    public void AWorkTenantOnGraphAsksForExplicitGraphScopes()
    {
        var account = new AccountModel
        {
            Username = "kelly@icanbrew.com",
            AuthType = AuthType.OAuth2Microsoft,
            BackendKind = BackendKind.MicrosoftGraph,
            IsPersonalMicrosoftAccount = false,
        };

        var scopes = OAuthService.DefaultScopesFor(account);
        Assert.Same(OAuthService.GraphMailScopesWorkSchool, scopes);
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite", scopes);
        Assert.DoesNotContain("https://graph.microsoft.com/.default", scopes);
        Assert.DoesNotContain("https://outlook.office.com/IMAP.AccessAsUser.All", scopes);
    }

    [Fact]
    public void TheSameAccountOnTheImapBackendAsksForScopesTenantsRarelyGrant()
    {
        var account = new AccountModel
        {
            Username = "kelly@icanbrew.com",
            AuthType = AuthType.OAuth2Microsoft,
            BackendKind = BackendKind.ImapSmtp,
        };

        var scopes = OAuthService.DefaultScopesFor(account);

        Assert.Contains("https://outlook.office.com/IMAP.AccessAsUser.All", scopes);
        Assert.Contains("https://outlook.office.com/SMTP.Send", scopes);
    }

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
    public void WorkSchoolGraphAccount_UsesExplicitWorkSchoolScopes()
    {
        var account = new AccountModel { BackendKind = BackendKind.MicrosoftGraph, Username = "user@contoso.com" };
        Assert.Same(OAuthService.GraphMailScopesWorkSchool, OAuthService.DefaultScopesFor(account));
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
    public void WorkAccountOnPersonalLookingDomain_StoredFlagFalseUsesWorkSchoolScopes()
    {
        // Flag explicitly false wins over a personal-looking domain → work/school explicit Graph scopes.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "user@outlook.com",
            IsPersonalMicrosoftAccount = false,
        };
        Assert.Same(OAuthService.GraphMailScopesWorkSchool, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void CustomDomainPersonalAccount_NotYetDetected_FallsBackToWorkSchool_KnownMigrationGap()
    {
        // DELIBERATE, documented fallback (#234 review, concern 1): a personal account on a custom
        // domain that hasn't been detected yet (added before tenant detection shipped, never re-authed)
        // has a null flag → the email-domain guess says "work" → the work/school Graph scopes. Auto-heal
        // is deferred; new accounts are detected at sign-in, so this only affects pre-detection accounts
        // (near-zero while the Graph backend is feature-gated). Locked as a test so it reads as intended.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "me@myvanitydomain.com",
            IsPersonalMicrosoftAccount = null,
        };
        Assert.Same(OAuthService.GraphMailScopesWorkSchool, OAuthService.DefaultScopesFor(account));
    }

    // ── Add-account consent prompt (#391, refined by #511/#529) ─────────────
    // Forcing Prompt.Consent on add is a `.default`-only workaround (#391): `.default` issues a token
    // silently as soon as ANY grant exists, so mail consent must be forced up front. With EXPLICIT
    // scopes that doesn't apply — requesting them IS the consent trigger — and forcing it just
    // re-prompts an already-consented org on every add (#207/#208). So consent is forced only on the
    // `.default` path; explicit-scope adds fall through to the normal prompt.

    [Fact]
    public void AddAccount_OnDefaultScope_ForcesConsentPrompt()
        => Assert.Equal(Prompt.Consent,
            OAuthService.PromptForSignIn(firstConnect: true, "user@contoso.com", usesDefaultScope: true));

    [Fact]
    public void AddAccount_OnExplicitScopes_DoesNotForceConsent() // #511/#529
        => Assert.NotEqual(Prompt.Consent,
            OAuthService.PromptForSignIn(firstConnect: true, "user@contoso.com", usesDefaultScope: false));

    [Fact]
    public void ReAuth_ForKnownAccount_ForcesLogin_NotConsent()
        => Assert.Equal(Prompt.ForceLogin,
            OAuthService.PromptForSignIn(firstConnect: false, "user@contoso.com", usesDefaultScope: true));

    [Fact]
    public void ReAuth_WithNoExpectedAccount_SelectsAccount()
        => Assert.Equal(Prompt.SelectAccount,
            OAuthService.PromptForSignIn(firstConnect: false, null, usesDefaultScope: true));

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
