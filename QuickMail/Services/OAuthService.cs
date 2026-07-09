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

    // Delegated scopes per backend. An account's BackendKind selects which set is requested.
    public static readonly string[] ImapSmtpScopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
        "offline_access",
    ];

    public static readonly string[] GraphMailScopes =
    [
        "https://graph.microsoft.com/Mail.ReadWrite",
        "https://graph.microsoft.com/Mail.Send",
        // MailboxSettings.ReadWrite grants read AND write access to server-side Inbox rules
        // (the Graph messageRule API: /me/mailFolders/inbox/messageRules). It is requested up
        // front -- while the Graph backend is still behind its feature gate and no production
        // account has consented yet -- so the permission set captured at the very first sign-in
        // already includes it. That way users will NOT face a second consent prompt when the
        // server-side rules manager ships. ReadWrite supersedes the read-only MailboxSettings.Read
        // we previously requested. Rationale and feature design: docs/planning/server-rules-pm-dev-spec.md.
        "https://graph.microsoft.com/MailboxSettings.ReadWrite",
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/User.ReadBasic.All",
        "offline_access",
    ];

    private static string[] DefaultScopesFor(AccountModel account)
        => account.BackendKind == BackendKind.MicrosoftGraph ? GraphMailScopes : ImapSmtpScopes;

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
        LogService.Log($"OAuthService: interactive sign-in complete for {result.Account.Username}");
        return new OAuthResult(result.AccessToken, result.Account.Username);
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
