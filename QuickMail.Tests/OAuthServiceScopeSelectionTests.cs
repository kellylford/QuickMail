using System;
using System.IO;
using Microsoft.Identity.Client;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the per-account scope selection (#217/#218, #511): personal Microsoft accounts get the
/// explicit personal Graph scopes, work/school Graph accounts get the explicit work/school Graph scopes
/// (so a Graph sign-in never validates the Exchange entitlement, #511), and IMAP always uses the IMAP
/// scopes. Guards against a future refactor silently reverting the routing — including a well-meaning
/// "restore `.default` for work/school", which #529 ruled out for good (see OAuthService).
/// </summary>
public class OAuthServiceScopeSelectionTests
{
    // A work-or-school Microsoft address on Graph asks for explicit Graph scopes, NOT `.default`
    // (#511). `.default` requested the whole declared set, dragging the Exchange IMAP/SMTP
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

    // ResolveIsPersonalMicrosoftAccount is the shared "flag, else domain guess" classifier used by scope
    // selection AND the work/school-only capability gates (server rules #541, shared mailboxes).
    [Theory]
    [InlineData(true, "user@contoso.com", true)]    // explicit flag wins over a work-looking domain
    [InlineData(false, "me@outlook.com", false)]    // explicit flag wins over a consumer domain
    [InlineData(null, "me@outlook.com", true)]      // undetected → domain guess says personal
    [InlineData(null, "user@contoso.com", false)]   // undetected → domain guess says work
    public void ResolveIsPersonalMicrosoftAccount_PrefersFlag_FallsBackToDomain(bool? flag, string username, bool expected)
    {
        var account = new AccountModel { Username = username, IsPersonalMicrosoftAccount = flag };
        Assert.Equal(expected, OAuthService.ResolveIsPersonalMicrosoftAccount(account));
    }

    [Fact]
    public void ImapAccount_UsesImapScopes_EvenOnAPersonalDomain()
    {
        // The personal-domain check applies only to the Graph backend; IMAP always gets IMAP scopes.
        var account = new AccountModel { BackendKind = BackendKind.ImapSmtp, Username = "me@outlook.com" };
        Assert.Same(OAuthService.ImapSmtpScopes, OAuthService.DefaultScopesFor(account));
    }

    [Fact]
    public void GraphParentSharedMailbox_UsesSharedScope() // #31 PR 2
    {
        // A shared mailbox borrows its parent's token but requests the delegated .Shared scope against
        // /users/{SharedAddress}. It is always work/school Graph (the Add-shared dialog excludes personal
        // parents), and its BackendKind is stamped from the parent at creation.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph,
            Username = "support@contoso.com",
            SharedAddress = "support@contoso.com",
            IsShared = true,
        };
        Assert.Same(OAuthService.GraphMailScopesShared, OAuthService.DefaultScopesFor(account));
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite.Shared", OAuthService.GraphMailScopesShared);
        // #31 PR 4: the shared default token also carries Mail.Send.Shared, so send-as works without a
        // separate scope request (the non-shared work/school default likewise includes Mail.Send).
        Assert.Contains("https://graph.microsoft.com/Mail.Send.Shared", OAuthService.GraphMailScopesShared);
        // Not the work/school set — a shared read/send never touches /me, so no User.Read etc.
        Assert.DoesNotContain("https://graph.microsoft.com/User.Read", OAuthService.GraphMailScopesShared);
    }

    [Fact]
    public void ImapParentSharedMailbox_StillUsesImapScopes() // #31 PR 2 — IMAP-parent shared is PR 3
    {
        // An IMAP-parent shared account (BackendKind ImapSmtp) is not a Graph read; it falls through to
        // the IMAP scopes (PR 3 will use XOAUTH2 user=). The shared branch is Graph-only.
        var account = new AccountModel
        {
            BackendKind = BackendKind.ImapSmtp,
            Username = "support@example.com",
            SharedAddress = "support@example.com",
            IsShared = true,
        };
        Assert.Same(OAuthService.ImapSmtpScopes, OAuthService.DefaultScopesFor(account));
    }

    // ── Shared-mailbox token identity (#31 PR 2) ────────────────────────────────
    // A credential-less shared mailbox has no MSAL entry of its own; every token lookup resolves to the
    // PARENT account's cached sign-in. ResolveTokenIdentity is that single resolution point.

    private static OAuthService NewOAuth() =>
        new(new ProfileContext(Path.Combine(Path.GetTempPath(), "qm-oauth-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void ResolveTokenIdentity_SharedAccount_ResolvesToParent()
    {
        var parent = new AccountModel { Id = Guid.NewGuid(), Username = "user@contoso.com" };
        var oauth = NewOAuth();
        oauth.ResolveAccount = id => id == parent.Id ? parent : null;

        var shared = new AccountModel
        {
            IsShared = true, ParentAccountId = parent.Id, SharedAddress = "support@contoso.com",
            BackendKind = BackendKind.MicrosoftGraph,
        };

        Assert.Same(parent, oauth.ResolveTokenIdentity(shared));   // borrows the parent's identity
        Assert.Same(parent, oauth.ResolveTokenIdentity(parent));   // a normal account resolves to itself
    }

    [Fact]
    public void ResolveTokenIdentity_SharedWithNoResolvableParent_Throws()
    {
        // Parent signed out / removed → surfaced as InteractiveSignInRequiredException, which
        // ConnectOneAccountAsync turns into a disconnected shared account, never a silent empty state.
        var oauth = NewOAuth();
        oauth.ResolveAccount = _ => null;   // parent not found
        var shared = new AccountModel
        {
            IsShared = true, ParentAccountId = Guid.NewGuid(), SharedAddress = "support@contoso.com",
            BackendKind = BackendKind.MicrosoftGraph,
        };

        Assert.Throws<InteractiveSignInRequiredException>(() => oauth.ResolveTokenIdentity(shared));
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
        // Not Prompt.Consent — with a known username it falls through to ForceLogin, which prompts for
        // credentials but lets AAD skip consent when the org has already granted the requested scopes.
        => Assert.Equal(Prompt.ForceLogin,
            OAuthService.PromptForSignIn(firstConnect: true, "user@contoso.com", usesDefaultScope: false));

    [Fact]
    public void ReAuth_ForKnownAccount_ForcesLogin_NotConsent()
        => Assert.Equal(Prompt.ForceLogin,
            OAuthService.PromptForSignIn(firstConnect: false, "user@contoso.com", usesDefaultScope: true));

    [Fact]
    public void ReAuth_WithNoExpectedAccount_SelectsAccount()
        => Assert.Equal(Prompt.SelectAccount,
            OAuthService.PromptForSignIn(firstConnect: false, null, usesDefaultScope: true));

    // ── Single-consent fold-in for Microsoft (contacts/calendar into the mail sign-in) ─────────
    // A personal Microsoft account has no admin-consent model, so mail, contacts, and calendar were
    // three separate interactive prompts. MicrosoftExtraConsentScopes is what the mail sign-in pre-
    // consents via WithExtraScopesToConsent so the opted-in capabilities are approved in one prompt.

    [Fact]
    public void ExtraConsentScopes_NeitherRequested_IsEmpty()
        => Assert.Empty(OAuthService.MicrosoftExtraConsentScopes(syncContacts: false, syncCalendar: false));

    [Fact]
    public void ExtraConsentScopes_ContactsOnly_IsContactScopes()
        => Assert.Equal(OAuthService.GraphContactScopes,
            OAuthService.MicrosoftExtraConsentScopes(syncContacts: true, syncCalendar: false));

    [Fact]
    public void ExtraConsentScopes_CalendarOnly_IsCalendarScopes()
        => Assert.Equal(OAuthService.GraphCalendarScopes,
            OAuthService.MicrosoftExtraConsentScopes(syncContacts: false, syncCalendar: true));

    [Fact]
    public void ExtraConsentScopes_Both_UnionsContactsAndCalendar()
    {
        var scopes = OAuthService.MicrosoftExtraConsentScopes(syncContacts: true, syncCalendar: true);
        Assert.Contains("https://graph.microsoft.com/Contacts.Read", scopes);
        Assert.Contains("https://graph.microsoft.com/People.Read", scopes);
        Assert.Contains("https://graph.microsoft.com/Calendars.ReadWrite", scopes);
        // No mail scopes here — mail is the primary sign-in scope, not an "extra to consent".
        Assert.DoesNotContain("https://graph.microsoft.com/Mail.ReadWrite", scopes);
    }

    // The account-level gate: consent is folded into the mail sign-in ONLY for personal accounts. A
    // work/school account returns empty so its opted-in contact/calendar consent stays on the graceful
    // post-add path and never dead-ends a consent-restricted tenant's whole sign-in (review finding).

    [Fact]
    public void FoldForSignIn_PersonalAccount_FoldsTheOptIns()
    {
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph, Username = "me@outlook.com",
            SyncContacts = true, SyncCalendar = false,
        };
        Assert.Equal(OAuthService.GraphContactScopes, OAuthService.ExtraConsentScopesForMicrosoftSignIn(account));
    }

    [Fact]
    public void FoldForSignIn_PersonalOnVanityDomain_ByFlag_FoldsTheOptIns()
    {
        // Tenant flag says personal even though the domain looks like work — still folds.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph, Username = "me@myvanitydomain.com",
            IsPersonalMicrosoftAccount = true, SyncContacts = false, SyncCalendar = true,
        };
        Assert.Equal(OAuthService.GraphCalendarScopes, OAuthService.ExtraConsentScopesForMicrosoftSignIn(account));
    }

    [Fact]
    public void FoldForSignIn_WorkSchoolAccount_FoldsNothing_EvenWhenBothOptedIn()
    {
        // The regression guard: folding these into a consent-restricted tenant's mail sign-in would
        // dead-end the whole sign-in. Work/school must stay on the graceful post-add consent path.
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph, Username = "user@contoso.com",
            SyncContacts = true, SyncCalendar = true,
        };
        Assert.Empty(OAuthService.ExtraConsentScopesForMicrosoftSignIn(account));
    }

    [Fact]
    public void FoldForSignIn_WorkAccountOnConsumerLookingDomain_ByFlagFalse_FoldsNothing()
    {
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph, Username = "user@outlook.com",
            IsPersonalMicrosoftAccount = false, SyncContacts = true, SyncCalendar = true,
        };
        Assert.Empty(OAuthService.ExtraConsentScopesForMicrosoftSignIn(account));
    }

    [Fact]
    public void FoldForSignIn_PersonalButNoSyncRequested_FoldsNothing()
    {
        var account = new AccountModel
        {
            BackendKind = BackendKind.MicrosoftGraph, Username = "me@outlook.com",
            SyncContacts = false, SyncCalendar = false,
        };
        Assert.Empty(OAuthService.ExtraConsentScopesForMicrosoftSignIn(account));
    }

    [Fact]
    public void FoldForSignIn_PersonalOnImapBackend_FoldsNothing() // #544 review finding 1
    {
        // A personal Microsoft account DEFAULTS to the IMAP backend (outlook.office.com mail scopes).
        // Folding graph.microsoft.com contact/calendar scopes onto that sign-in is a fragile cross-
        // resource authorize (#239) on the DEFAULT consumer path — only a Graph sign-in folds.
        var account = new AccountModel
        {
            BackendKind = BackendKind.ImapSmtp, Username = "me@outlook.com",
            SyncContacts = true, SyncCalendar = true,
        };
        Assert.Empty(OAuthService.ExtraConsentScopesForMicrosoftSignIn(account));
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
