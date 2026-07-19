using System;
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

    // Work/school (AAD) Graph accounts: `.default` requests EXACTLY the app-registration's declared
    // permissions, matching what admin consent grants — this is what fixed the requested-vs-declared
    // mismatch that produced the "admin granted consent but the user is still prompted" loop (#208).
    // `.default` is per-resource and cannot be combined with other scopes. Rationale:
    // docs/planning/oauth-default-scope-pm-dev-spec.md. (Personal Graph accounts use
    // GraphMailScopesPersonal below — `.default` under-delivers write for them, #217.)
    public static readonly string[] GraphMailScopes =
    [
        "https://graph.microsoft.com/.default",
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
    // Requested only when the user adds a Microsoft calendar. NOTE: work/school accounts sign in with
    // `.default`, so for them Calendars.ReadWrite must ALSO be declared on the app registration (see
    // docs/ENTRA-APP-REGISTRATION.md §3); this explicit array is what personal (MSA) accounts request.
    // Forward-declared now so M4 has the scope wired; not yet requested by any code path.
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

    internal static string[] DefaultScopesFor(AccountModel account)
    {
        if (account.BackendKind != BackendKind.MicrosoftGraph)
            return ImapSmtpScopes;
        // Personal accounts need explicit scopes to obtain write access; work/school uses `.default`.
        // Prefer the persisted, tenant-derived flag; fall back to the email-domain guess only when the
        // account hasn't been detected yet (first sign-in, or added before detection shipped).
        var isPersonal = account.IsPersonalMicrosoftAccount ?? IsPersonalMicrosoftDomain(account.Username);
        return isPersonal ? GraphMailScopesPersonal : GraphMailScopes;
    }

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

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default)
        => GetAccessTokenAsync(account, DefaultScopesFor(account), ct);

    public async Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        await EnsureTokenCacheAsync();
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));

        if (msalAccount is not null)
        {
            try
            {
                var silent = await _msal.AcquireTokenSilent(scopes, msalAccount).ExecuteAsync(ct);
                LogService.Log($"OAuthService: silent token acquired for {account.Username}");
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                LogService.Log("OAuthService: silent auth failed, falling back to interactive.");
            }
        }

        var result = await SignInInteractiveAsync(account, scopes, ct);
        return result.AccessToken;
    }

    public async Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        await EnsureTokenCacheAsync();
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));

        if (msalAccount is null)
            throw new InteractiveSignInRequiredException($"No cached sign-in for {account.Username}.");

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
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));

        // No cached account at all → the only way to a token is interactive.
        if (msalAccount is null)
            throw new InteractiveSignInRequiredException($"No cached sign-in for {account.Username}.");

        try
        {
            await _msal.AcquireTokenSilent(DefaultScopesFor(account), msalAccount).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new InteractiveSignInRequiredException($"Silent token unavailable for {account.Username}.", ex);
        }
    }

    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default)
        => SignInInteractiveAsync(account, DefaultScopesFor(account), ct);

    public async Task<OAuthResult> SignInInteractiveAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        await EnsureTokenCacheAsync();
        LogService.Log($"OAuthService: starting interactive sign-in for {account.Username}");

        var builder = _msal.AcquireTokenInteractive(scopes)
            // Embedded WebView2 window: renders in-app and closes itself on completion, returning
            // focus to QuickMail — no system-browser tab and no lingering success page.
            .WithUseEmbeddedWebView(true)
            // Force credential entry when a specific account is expected,
            // so the browser cannot silently reuse a different cached account.
            .WithPrompt(string.IsNullOrEmpty(account.Username) ? Prompt.SelectAccount : Prompt.ForceLogin);

        if (!string.IsNullOrEmpty(account.Username))
            builder = builder.WithLoginHint(account.Username);

        var result = await builder.ExecuteAsync(ct);
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
        // Microsoft: sign in for mail only. Contact scopes can't be folded in here because `.default`
        // (work/school) can't be combined with explicit scopes and personal-vs-work isn't known until
        // the token comes back. They're granted right after account creation via
        // RequestContactsConsentAsync — silent when the app registration declares Contacts.Read/People.Read.
        => SignInInteractiveAsync(account, ct);

    public async Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default)
    {
        // Acquiring a token for the contact scopes is what drives consent: silent if the user has
        // already granted them (cached refresh token covers them), interactive otherwise. The token
        // itself is discarded here — GraphContactSource acquires its own when it fetches — but the
        // grant now lives in the MSAL cache, so later silent acquisition (including background sync)
        // succeeds without another prompt.
        await GetAccessTokenAsync(account, GraphContactScopes, ct);
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
