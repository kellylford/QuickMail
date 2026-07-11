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

    // Per-resource `.default` scope. `.default` requests EXACTLY the delegated permissions declared
    // on the app registration (docs/ENTRA-APP-REGISTRATION.md §3) — so the requested set always
    // matches what admin consent grants, eliminating the requested-vs-declared mismatch that produced
    // the "admin granted consent but the user is still prompted" loop. Design & rationale:
    // docs/planning/oauth-default-scope-pm-dev-spec.md. Notes:
    //  - `.default` is per-resource and cannot be combined with other scopes, hence one array each.
    //  - MSAL adds openid/profile/offline_access automatically for the public-client desktop flow,
    //    so refresh tokens still issue (verified live: silent re-acquisition works with `.default`).
    //  - A new permission (e.g. MailboxSettings.ReadWrite for server rules) is now added by DECLARING
    //    it on the app registration + re-granting consent — not by editing this list.
    public static readonly string[] ImapSmtpScopes =
    [
        "https://outlook.office.com/.default",
    ];

    // Work/school (AAD) Graph accounts: `.default` requests exactly the app-registration's declared
    // permissions, which matches what admin consent grants (fixes the consent loop, #208).
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
