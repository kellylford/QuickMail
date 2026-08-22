using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Desktop;
using Microsoft.Identity.Client.Extensions.Msal;
using QuickMail.Models;

namespace QuickMail.Services;

public class OAuthService : IOAuthService
{
    private const string ClientId  = "bcdc84f1-d37c-4581-b14a-a01f7b3a1312";
    private const string Authority = "https://login.microsoftonline.com/common";

    // IMAP/SMTP-over-OAuth (Exchange Online / Outlook.com). Uses EXPLICIT scopes, NOT `.default`:
    // `.default` on `outlook.office.com` is invalid for personal Microsoft accounts — the resource
    // isn't in the app's Required Resource Access for consumer sign-in and there's no admin-consent
    // model — which broke personal-account sign-in on the IMAP path entirely (#239, sign-in error
    // "scope … is not valid … refers to a resource which is not listed"). Explicit
    // IMAP.AccessAsUser.All + SMTP.Send work for BOTH personal and work accounts, and the `.default`
    // loop-avoidance benefit (#208) never applied here anyway: the IMAP resource only ever needs these
    // two declared scopes, so there is no requested-vs-declared mismatch. MSAL adds
    // openid/profile/offline_access automatically for the public-client flow, so refresh tokens issue.
    public static readonly string[] ImapSmtpScopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
    ];

    // Work/school (AAD) Graph accounts. HISTORY: this used `.default`, which requests the app's ENTIRE
    // declared permission set. That dragged the Office 365 Exchange Online IMAP/SMTP permissions (there
    // only for the Microsoft IMAP backend) into every fresh work/school Graph consent — and when
    // Microsoft's validation of that legacy Exchange entitlement went intermittently bad, `.default`
    // consent failed with AADSTS65006 (reproducible on every fresh consent, not tenant-side). #511.
    //
    // FIX (#511): request EXPLICIT Graph mail scopes instead, so a Graph sign-in only ever validates the
    // Graph permissions it needs and never touches the Exchange entitlement. Contacts and calendar are
    // NOT here — they run through their own incremental consent flows (RequestContactsConsentAsync /
    // RequestCalendarConsentAsync), same as personal accounts, so this list is mail only.
    //
    // THIS IS PERMANENT — do NOT "restore" `.default` here. It was originally written as a temporary
    // bridge, on the plan that Microsoft IMAP would be dropped and the Exchange permissions deleted from
    // the app registration. That plan was settled the other way (#529): Microsoft IMAP is RETAINED as a
    // documented fallback while the open Graph defects (#491/#462/#454/#31) stand, so the Exchange
    // permissions must stay declared, so `.default` would drag them back into Graph consent and
    // reintroduce the AADSTS65006. The registration cleanup and the `.default` revert are off the table.
    //
    // The cost of that decision lands here: this list is HAND-MAINTAINED. Adding a work/school Graph mail
    // capability means adding its scope to this array AND declaring it on the registration — miss the
    // array and the call 403s at runtime. That is the accepted trade (a dev-time-diagnosable 403 beats
    // anything that touches users' machines); it is not an oversight to be optimized away by going back
    // to `.default`. See docs/planning/oauth-default-scope-pm-dev-spec.md for the superseded `.default`
    // design and #529 for why it stays superseded.
    // Only scopes the work/school MAIL path actually calls. Deliberately NOT `User.ReadBasic.All`
    // (a `/users` directory permission with no call site — it's a forward declaration on the app
    // registration, see docs/ENTRA-APP-REGISTRATION.md): restrictive tenants gate directory reads
    // behind admin consent, so requesting it at interactive sign-in could dead-end the mail sign-in on
    // a locked-down tenant — the exact onboarding failure this change removes. Add a scope here only
    // when a work/school mail call actually needs it.
    public static readonly string[] GraphMailScopesWorkSchool =
    [
        "https://graph.microsoft.com/Mail.ReadWrite",
        "https://graph.microsoft.com/Mail.Send",
        "https://graph.microsoft.com/MailboxSettings.ReadWrite",  // server-side rules (#333, GraphServerRuleService)
        "https://graph.microsoft.com/User.Read",                  // /me profile read (GraphMailService)
    ];

    // Shared mailboxes (#31): a shared mailbox reads and sends through its PARENT work/school account's
    // token, but against the shared address's messages (/users/{SharedAddress}/…), which needs the
    // delegated *.Shared* permissions — Mail.ReadWrite.Shared covers read/mark-read/move, and
    // Mail.Send.Shared covers send-as (PR 4). This is the shared account's DEFAULT scope set (like the
    // work/school set above includes Mail.Send for a normal account), so a single token carries both;
    // both are granted together at add time via RequestSharedMailboxConsentAsync, so no extra prompt.
    // WORK/SCHOOL ONLY: personal Microsoft accounts have no Exchange shared mailboxes, and the Add-shared
    // dialog already excludes personal parents. Hand-maintained AND must be declared on the app
    // registration or the call 403s at runtime — see docs/ENTRA-APP-REGISTRATION.md and #31. No /me is
    // ever called on the shared path, so User.Read is deliberately absent.
    public static readonly string[] GraphMailScopesShared =
    [
        "https://graph.microsoft.com/Mail.ReadWrite.Shared",
        "https://graph.microsoft.com/Mail.Send.Shared",
    ];

    // Personal Microsoft accounts (Outlook.com/Hotmail/Live): `.default` under-delivers for MSA,
    // because personal accounts have no admin-consent model — `.default` returns only the permissions
    // the user already consented to, which came back read-only (delete/move → 403 ErrorAccessDenied,
    // #217). Request the explicit delegated mail scopes so the user is prompted to consent to read AND
    // write. Org-only permissions (User.ReadBasic.All directory search, MailboxSettings.ReadWrite
    // server rules) don't apply to personal accounts and are omitted.
    public static readonly string[] GraphMailScopesPersonal =
    [
        "https://graph.microsoft.com/Mail.ReadWrite",
        "https://graph.microsoft.com/Mail.Send",
        "https://graph.microsoft.com/User.Read",
    ];


    // Read-only contact scopes for contact sync (issue #256). Explicit scopes (not `.default`) so
    // they work for BOTH personal and work/school Microsoft accounts — the same reasoning as the
    // personal-mail scopes above. Requested only when the user opts an account into contact sync.
    // Contacts.Read → /me/contacts (saved contacts); People.Read → /me/people (relevance-ranked
    // people the user has corresponded with, i.e. prior recipients).
    public static readonly string[] GraphContactScopes =
    [
        "https://graph.microsoft.com/Contacts.Read",
        "https://graph.microsoft.com/People.Read",
    ];

    // Calendar read/write scope for Graph calendar sync (full-calendar spec, M4). Explicit scope so
    // it works for BOTH personal and work/school accounts — same reasoning as GraphContactScopes.
    // Requested only when the user adds a Microsoft calendar. NOTE: Calendars.ReadWrite must ALSO be
    // declared on the app registration (see docs/ENTRA-APP-REGISTRATION.md §3) so a work/school tenant
    // can admin-consent it; this explicit array is what the calendar consent flow requests, for personal
    // and work/school alike. (This note used to say work/school "sign in with `.default`" — no longer
    // true since #511, and it was never the reason the declaration is needed.)
    // In use by GraphCalendarSyncService (#282): reads /me/calendars and /me/events and creates
    // events, so the ReadWrite (not just Read) scope is required.
    public static readonly string[] GraphCalendarScopes =
    [
        "https://graph.microsoft.com/Calendars.ReadWrite",
    ];

    // Authoritative "is this a personal Microsoft account" signal: every consumer account lives in the
    // well-known MSA "consumers" tenant. Detected from the MSAL account at sign-in and persisted on
    // AccountModel (#233), so it's correct even for personal accounts on custom/vanity domains.
    internal const string MsaConsumersTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    // FALLBACK only — the email-domain guess used before a token exists (very first sign-in) and for
    // accounts added before tenant detection shipped. Covers the common personal-account domains; a
    // personal account on a custom domain is missed here, which is exactly why the tenant id above is
    // the authoritative source once available.
    private static readonly string[] PersonalMicrosoftDomains =
    {
        "outlook.com", "hotmail.com", "live.com", "msn.com", "passport.com", "windowslive.com",
        "hotmail.co.uk", "live.co.uk", "outlook.jp", "outlook.de", "outlook.fr",
    };

    private static bool IsPersonalMicrosoftDomain(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var at = username.LastIndexOf('@');
        if (at < 0 || at == username.Length - 1) return false;
        return PersonalMicrosoftDomains.Contains(username[(at + 1)..].Trim().ToLowerInvariant());
    }

    /// <summary>
    /// The best available "is this a personal Microsoft account" answer: the persisted, tenant-derived
    /// flag (#233), falling back to the email-domain guess only when the flag hasn't been set yet (first
    /// sign-in, or an account added before detection shipped). This is the same resolution scope
    /// selection uses — callers that gate a work/school-only Microsoft capability (server-side rules,
    /// shared mailboxes) should use this rather than the raw <see cref="AccountModel.IsPersonalMicrosoftAccount"/>
    /// flag, so an undetected account is still classified instead of slipping through as work/school.
    /// </summary>
    internal static bool ResolveIsPersonalMicrosoftAccount(AccountModel account)
        => account.IsPersonalMicrosoftAccount ?? IsPersonalMicrosoftDomain(account.Username);

    internal static string[] DefaultScopesFor(AccountModel account)
    {
        // A Graph-parent shared mailbox (#31) borrows its parent's token but requests the .Shared scope
        // against /users/{SharedAddress}. It is always work/school (the Add-shared dialog excludes
        // personal parents). An IMAP-parent shared account falls through to ImapSmtpScopes (PR 3, XOAUTH2
        // user=), the same as any IMAP account.
        if (account.IsShared && account.BackendKind == BackendKind.MicrosoftGraph)
            return GraphMailScopesShared;
        if (account.BackendKind != BackendKind.MicrosoftGraph)
            return ImapSmtpScopes;
        // Both personal and work/school use explicit Graph scopes — personal for write access (#217),
        // work/school so a Graph sign-in never validates the Exchange entitlement (#511).
        return ResolveIsPersonalMicrosoftAccount(account) ? GraphMailScopesPersonal : GraphMailScopesWorkSchool;
    }

    // Prompt for an interactive sign-in. Forcing the CONSENT screen on add is a `.default`-only
    // workaround: `.default` issues a token SILENTLY as soon as ANY grant exists on the tenant — e.g.
    // from a prior contacts/calendar consent or an earlier partial sign-in — so without it the mail
    // permissions are never consented and mail calls then 403 (#391, Microsoft docs "`.default`"
    // Example 3). Prompt.Consent makes the admin/user approve the full declared set once, up front.
    //
    // #511: with EXPLICIT scopes (not `.default`) that workaround does not apply — requesting
    // `Mail.ReadWrite` IS the consent trigger, and AAD shows consent only when it isn't already granted.
    // Forcing it there just re-prompts an already-consented org on every add (the #207/#208 symptom seen
    // when explicit scopes first went in). So force consent only on the `.default` path; explicit adds
    // fall through to the normal prompt, which lets AAD skip consent when the org has already granted it.
    //
    // Re-auth (an already-added account whose token expired) uses the normal prompt either way — that
    // account already consented at add-time, so a forced re-consent on every cache loss would annoy.
    internal static Prompt PromptForSignIn(bool firstConnect, string? username, bool usesDefaultScope)
        => firstConnect && usesDefaultScope ? Prompt.Consent
           : string.IsNullOrEmpty(username) ? Prompt.SelectAccount
           : Prompt.ForceLogin;

    private readonly string _cacheDir;
    private const string CacheFileName = "msal.cache";

    private readonly IPublicClientApplication _msal;

    public OAuthService(ProfileContext profile)
    {
        _cacheDir = profile.ProfileDir;

        _msal = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(Authority)
            // Interactive sign-in renders in the embedded WebView2 window (see WithUseEmbeddedWebView
            // in SignInInteractiveAsync) — it shows in-app and closes itself on completion, so there
            // is no separate browser tab or "authentication complete" page. The redirect is still
            // http://localhost; the embedded view intercepts that navigation internally (no loopback
            // listener), and it must be registered under the app's "Mobile and desktop applications"
            // platform. Set explicitly rather than via the framework-dependent WithDefaultRedirectUri.
            // See docs/ENTRA-APP-REGISTRATION.md.
            .WithRedirectUri("http://localhost")
            // Enables the embedded WebView2 browser on net8.0-windows (Microsoft.Identity.Client.Desktop).
            .WithWindowsEmbeddedBrowserSupport()
            .Build();
    }

    // Registered lazily on first token use rather than in the constructor: the service is
    // built on the UI thread during startup, and MsalCacheHelper.CreateAsync does file I/O
    // and DPAPI work that shouldn't be waited on synchronously there. The registration task
    // is created once and shared, so concurrent first calls await the same registration.
    private Task? _cacheRegistration;
    private readonly object _cacheRegistrationLock = new();

    private Task EnsureTokenCacheAsync()
    {
        lock (_cacheRegistrationLock)
        {
            // A failed registration must not be cached: a transient fault (cache file
            // briefly locked, DPAPI hiccup) would otherwise disable every OAuth operation
            // until app restart. Drop the completed-unsuccessful task so this call retries.
            if (_cacheRegistration is { IsCompleted: true, IsCompletedSuccessfully: false })
                _cacheRegistration = null;
            return _cacheRegistration ??= RegisterTokenCacheAsync();
        }
    }

    private async Task RegisterTokenCacheAsync()
    {
        Directory.CreateDirectory(_cacheDir);

        // DPAPI-encrypted file cache so tokens survive app restarts
        var storageProps = new StorageCreationPropertiesBuilder(CacheFileName, _cacheDir)
            .Build();

        var helper = await MsalCacheHelper.CreateAsync(storageProps).ConfigureAwait(false);
        helper.RegisterCache(_msal.UserTokenCache);

        LogService.Log("OAuthService: token cache registered.");
    }

    /// <summary>
    /// Looks up a live <see cref="AccountModel"/> by id. Set once at startup to
    /// <c>mainVm.Accounts</c> (the authoritative, disk-rebuilt list) so a shared mailbox added at
    /// runtime resolves its parent without a restart. Used only by <see cref="ResolveTokenIdentity"/>;
    /// left null in tests that don't exercise the shared path.
    /// </summary>
    public Func<Guid, AccountModel?>? ResolveAccount { get; set; }

    /// <summary>
    /// The Microsoft identity whose cached sign-in backs this account's token. For a normal account
    /// that is the account itself; for a credential-less shared mailbox (#31) it is the PARENT account,
    /// whose token is borrowed to read <c>/users/{SharedAddress}</c> with the <c>.Shared</c> scope. A
    /// shared account has no MSAL entry of its own, so every MSAL identity lookup must go through here.
    /// Throws <see cref="InteractiveSignInRequiredException"/> — surfaced as "disconnected" by
    /// <c>ConnectOneAccountAsync</c>, never a silent empty state — when the parent can't be resolved.
    /// </summary>
    internal AccountModel ResolveTokenIdentity(AccountModel account)
    {
        if (!account.IsShared) return account;
        if (account.ParentAccountId is { } pid && ResolveAccount?.Invoke(pid) is { } parent)
            return parent;
        throw new InteractiveSignInRequiredException(
            $"Shared mailbox '{account.SharedAddress}' has no signed-in parent account.");
    }

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default)
        => GetAccessTokenAsync(account, DefaultScopesFor(account), ct);

    public async Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        await EnsureTokenCacheAsync();
        var identity = ResolveTokenIdentity(account);   // #31: a shared mailbox borrows its parent's token
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, identity.Username, StringComparison.OrdinalIgnoreCase));

        if (msalAccount is not null)
        {
            try
            {
                var silent = await _msal.AcquireTokenSilent(scopes, msalAccount).ExecuteAsync(ct);
                LogService.Log($"OAuthService: silent token acquired for {identity.Username}");
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                LogService.Log("OAuthService: silent auth failed, falling back to interactive.");
            }
        }

        // A shared mailbox (#31) never drives its own interactive sign-in: it has no MSAL entry, and a
        // background read (the #456 sweep) must not spawn a sign-in window. When the parent's token isn't
        // silently available the shared mailbox is simply disconnected, surfaced by the caller.
        if (account.IsShared)
            throw new InteractiveSignInRequiredException(
                $"Shared mailbox '{account.SharedAddress}' is disconnected — sign in to its parent account.");

        var result = await SignInInteractiveAsync(account, scopes, ct);
        return result.AccessToken;
    }

    public async Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        await EnsureTokenCacheAsync();
        var identity = ResolveTokenIdentity(account);   // #31: shared mailbox → parent's cached sign-in
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, identity.Username, StringComparison.OrdinalIgnoreCase));

        if (msalAccount is null)
            throw new InteractiveSignInRequiredException($"No cached sign-in for {identity.Username}.");

        try
        {
            var silent = await _msal.AcquireTokenSilent(scopes, msalAccount).ExecuteAsync(ct);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            // Deliberately do NOT fall back to interactive — the caller (contact sync) may be running
            // inside a modal message loop where an embedded WebView2 sign-in can deadlock the UI thread.
            throw new InteractiveSignInRequiredException($"Silent token unavailable for {account.Username}.", ex);
        }
    }

    public async Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default)
    {
        await EnsureTokenCacheAsync();
        var identity = ResolveTokenIdentity(account);   // #31: shared mailbox → parent's cached sign-in
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, identity.Username, StringComparison.OrdinalIgnoreCase));

        // No cached account at all → the only way to a token is interactive.
        if (msalAccount is null)
            throw new InteractiveSignInRequiredException($"No cached sign-in for {identity.Username}.");

        try
        {
            await _msal.AcquireTokenSilent(DefaultScopesFor(account), msalAccount).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new InteractiveSignInRequiredException($"Silent token unavailable for {account.Username}.", ex);
        }
    }

    // Add-account entry point. firstConnect=true; the prompt then depends on the scopes: the `.default`
    // path still forces consent (#391), while the explicit-scope paths (personal, and work/school since
    // #511) let AAD decide, so an already-consented org isn't re-prompted. See PromptForSignIn.
    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default)
        => SignInInteractiveAsync(account, DefaultScopesFor(account), firstConnect: true, extraScopesToConsent: null, ct);

    // Re-auth with explicit scopes (GetAccessTokenAsync's UI-required fallback): normal prompt, since
    // the account already consented at add-time.
    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
        => SignInInteractiveAsync(account, scopes, firstConnect: false, extraScopesToConsent: null, ct);

    // extraScopesToConsent: additional Graph scopes to pre-consent in the SAME interactive prompt (MSAL
    // WithExtraScopesToConsent). The returned token is still mail-scoped; the extra grants just land in
    // the token cache so a later silent acquisition for them succeeds without a second prompt. Used to
    // fold contact/calendar consent into the mail sign-in (see SignInInteractiveWithContactsAsync).
    private async Task<OAuthResult> SignInInteractiveAsync(AccountModel account, string[] scopes, bool firstConnect, string[]? extraScopesToConsent, CancellationToken ct)
    {
        await EnsureTokenCacheAsync();
        LogService.Log($"OAuthService: starting interactive sign-in for {account.Username}");

        // #511/#529: consent is force-prompted only on the `.default` path (see PromptForSignIn). With
        // explicit scopes AAD decides, so an already-consented org isn't re-prompted on every add.
        var usesDefaultScope = scopes.Any(s => s.EndsWith("/.default", StringComparison.Ordinal));

        // Build the interactive request. `extras` are folded in via WithExtraScopesToConsent so a personal
        // account (no admin to pre-approve them) consents to mail + contacts + calendar in one prompt.
        AcquireTokenInteractiveParameterBuilder Build(string[]? extras)
        {
            var b = _msal.AcquireTokenInteractive(scopes)
                // Embedded WebView2 window: renders in-app and closes itself on completion, returning
                // focus to QuickMail — no system-browser tab and no lingering success page.
                .WithUseEmbeddedWebView(true)
                .WithPrompt(PromptForSignIn(firstConnect, account.Username, usesDefaultScope));
            if (extras is { Length: > 0 })
                b = b.WithExtraScopesToConsent(extras);
            if (!string.IsNullOrEmpty(account.Username))
                b = b.WithLoginHint(account.Username);
            return b;
        }

        AuthenticationResult result;
        try
        {
            result = await Build(extraScopesToConsent).ExecuteAsync(ct);
        }
        catch (MsalException ex) when (extraScopesToConsent is { Length: > 0 })
        {
            // The folded acquisition failed. The common cause is the user declining consent to the extra
            // contact/calendar scopes — but MSAL reports a decline (access_denied with
            // error_subcode=cancel) as a UserCancel, identically to a window-close, discarding the
            // access_denied text (verified against MSAL 4.85.2, AuthorizationResult.FromParsedValues).
            // So the two are indistinguishable at this catch, and we retry mail-only regardless: a decline
            // still yields a working mail account (the declined capability falls back to its own post-add
            // consent), and a genuine window-close simply re-shows the mail sign-in, which the user can
            // close again to abort. The retry has no catch of its own, so a second failure propagates to
            // the caller's "Sign-in failed" handler — one retry, never a loop.
            LogService.Log($"OAuthService: sign-in with folded contact/calendar consent failed " +
                           $"({ex.ErrorCode}); retrying mail-only for {account.Username}.");
            result = await Build(null).ExecuteAsync(ct);
        }
        // Authoritative personal-vs-work detection from the token: personal Microsoft accounts sign in
        // under the well-known MSA consumers tenant. The caller persists this on the account so scope
        // selection is correct even for consumer accounts on custom domains (#233). Account is non-null
        // after a successful interactive ExecuteAsync (same assumption as the Username deref below);
        // HomeAccountId keeps its ?. defensively — a null tenant just resolves to "not personal".
        var isPersonal = string.Equals(result.Account.HomeAccountId?.TenantId, MsaConsumersTenantId,
            StringComparison.OrdinalIgnoreCase);
        LogService.Log($"OAuthService: interactive sign-in complete for {result.Account.Username} " +
                       $"(personal Microsoft account: {isPersonal}).");
        return new OAuthResult(result.AccessToken, result.Account.Username, isPersonal);
    }

    public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default)
        // Microsoft: sign in for mail AND — for a PERSONAL account only — fold the requested contact/
        // calendar consent into the same interactive prompt via WithExtraScopesToConsent, so a personal
        // account (no admin-consent model) is prompted once instead of once per capability. The extra
        // grants land in the token cache, so the post-add RequestContactsConsentAsync /
        // RequestCalendarConsentAsync calls then acquire silently.
        //
        // We DELIBERATELY do NOT fold for work/school. On a tenant that disables user consent and hasn't
        // admin-consented contacts/calendar, surfacing those scopes at the mail sign-in would dead-end the
        // WHOLE interactive acquisition — no token, so the account is never added. The unfolded path
        // degrades gracefully instead: mail signs in, the account is added, and only the post-add
        // RequestContactsConsentAsync dead-ends (caught in AccountManagerViewModel, which just disables
        // contact sync). This is the same admin-consent dead-end hazard the mail scope list avoids for
        // User.ReadBasic.All. Folding only ever helped personal accounts anyway — work/school usually has
        // admin pre-consent, so its post-add consent is already silent.
        //
        // (Pre-#511 nothing could be folded because work/school signed in with `.default`, which can't be
        // combined with explicit scopes; both paths use explicit Graph scopes now, so personal folds
        // cleanly. The account carries the two opt-in bits from the editor VM.)
        => SignInInteractiveAsync(account, DefaultScopesFor(account), firstConnect: true,
            ExtraConsentScopesForMicrosoftSignIn(account), ct);

    // The extra Graph scopes to fold into THIS account's mail sign-in consent, or empty when none should
    // be folded. Two gates, both required:
    //   • Graph backend — the primary mail scopes must be graph.microsoft.com, so the folded contact/
    //     calendar scopes ride the SAME resource. A personal Microsoft account DEFAULTS to the IMAP backend
    //     (outlook.office.com mail scopes); folding graph.microsoft.com scopes onto that sign-in is a
    //     cross-resource authorize the MSA + outlook.office.com path handles badly (#239) — and it is the
    //     DEFAULT consumer onboarding path, not an edge case. Only a Graph sign-in folds.
    //   • Personal — work/school returns empty so its opted-in consent stays on the graceful post-add path
    //     and never dead-ends a consent-restricted tenant's sign-in (see SignInInteractiveWithContactsAsync).
    // Personal is resolved via ResolveIsPersonalMicrosoftAccount (persisted flag, else the domain guess),
    // the same resolution DefaultScopesFor uses.
    internal static string[] ExtraConsentScopesForMicrosoftSignIn(AccountModel account)
        => account.BackendKind == BackendKind.MicrosoftGraph && ResolveIsPersonalMicrosoftAccount(account)
            ? MicrosoftExtraConsentScopes(account.SyncContacts, account.SyncCalendar)
            : Array.Empty<string>();

    // The extra Graph scopes to pre-consent alongside the mail sign-in for the two opt-in bits. Empty
    // when neither is requested (the caller then attaches no WithExtraScopesToConsent). Pure and
    // deterministic so the fold-in policy is unit-tested without standing up MSAL.
    internal static string[] MicrosoftExtraConsentScopes(bool syncContacts, bool syncCalendar)
    {
        if (!syncContacts && !syncCalendar) return Array.Empty<string>();
        var scopes = new List<string>();
        if (syncContacts) scopes.AddRange(GraphContactScopes);
        if (syncCalendar) scopes.AddRange(GraphCalendarScopes);
        return scopes.ToArray();
    }

    public async Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default)
    {
        // Acquiring a token for the contact scopes is what drives consent: silent if the user has
        // already granted them (cached refresh token covers them), interactive otherwise. The token
        // itself is discarded here — GraphContactSource acquires its own when it fetches — but the
        // grant now lives in the MSAL cache, so later silent acquisition (including background sync)
        // succeeds without another prompt.
        await GetAccessTokenAsync(account, GraphContactScopes, ct);
    }

    public async Task RequestSharedMailboxConsentAsync(AccountModel parent, CancellationToken ct = default)
    {
        // #31: the one-time consent that lets a shared mailbox be read and sent-as through this PARENT
        // account's token. Consents the same scope set the shared account then uses per-token
        // (GraphMailScopesShared = read + send), so one prompt covers everything. Acquiring a token for
        // those scopes drives it — silent if the grant already exists (e.g. the tenant admin-consented),
        // otherwise the real Microsoft consent window, where the user consents, an admin grants for the
        // org, or it is declined/pending. The token is discarded; the grant now lives in the MSAL cache,
        // so the shared account's later silent acquisition succeeds. Runs on the PARENT (not IsShared),
        // so the interactive fallback is allowed — a shared account is silent-only and would throw
        // instead. A decline/pending consent throws MsalException up to the caller, which leaves the
        // shared mailbox added-but-disconnected.
        await GetAccessTokenAsync(parent, GraphMailScopesShared, ct);
    }

    public async Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default)
    {
        // Same pattern as RequestContactsConsentAsync: acquiring a token for the calendar scopes
        // drives the one-time consent (silent if already granted, interactive otherwise), so the
        // background calendar sync's silent GraphCalendarScopes acquisition then succeeds. The token
        // is discarded here — GraphCalendarSyncService acquires its own when it syncs. Google needs
        // no equivalent: its calendar scope is already requested at mail sign-in.
        await GetAccessTokenAsync(account, GraphCalendarScopes, ct);
    }

    public async Task SignOutAsync(AccountModel account)
    {
        await EnsureTokenCacheAsync();
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));

        if (msalAccount is not null)
        {
            await _msal.RemoveAsync(msalAccount);
            LogService.Log($"OAuthService: signed out {account.Username}");
        }
    }
}
