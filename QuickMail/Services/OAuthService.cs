using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using QuickMail.Models;

namespace QuickMail.Services;

public class OAuthService : IOAuthService
{
    private const string ClientId  = "bcdc84f1-d37c-4581-b14a-a01f7b3a1312";
    private const string Authority = "https://login.microsoftonline.com/common";

    // Scopes required for IMAP + SMTP access
    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
        "offline_access"
    ];

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");

    private const string CacheFileName = "msal.cache";

    private readonly IPublicClientApplication _msal;

    public OAuthService()
    {
        _msal = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(Authority)
            // http://localhost loopback redirect — standard for native apps
            .WithDefaultRedirectUri()
            .Build();

        RegisterTokenCache();
    }

    private void RegisterTokenCache()
    {
        Directory.CreateDirectory(CacheDir);

        // DPAPI-encrypted file cache so tokens survive app restarts
        var storageProps = new StorageCreationPropertiesBuilder(CacheFileName, CacheDir)
            .Build();

        var helper = MsalCacheHelper.CreateAsync(storageProps).GetAwaiter().GetResult();
        helper.RegisterCache(_msal.UserTokenCache);

        LogService.Log("OAuthService: token cache registered.");
    }

    public async Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default)
    {
        var msalAccounts = await _msal.GetAccountsAsync();
        var msalAccount  = msalAccounts.FirstOrDefault(a =>
            string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));

        if (msalAccount is not null)
        {
            try
            {
                var silent = await _msal.AcquireTokenSilent(Scopes, msalAccount).ExecuteAsync(ct);
                LogService.Log($"OAuthService: silent token acquired for {account.Username}");
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                LogService.Log("OAuthService: silent auth failed, falling back to interactive.");
            }
        }

        var result = await SignInInteractiveAsync(account, ct);
        return result.AccessToken;
    }

    public async Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default)
    {
        LogService.Log($"OAuthService: starting interactive sign-in for {account.Username}");

        var builder = _msal.AcquireTokenInteractive(Scopes)
            // Opens the system default browser; no embedded WebView dependency
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount);

        if (!string.IsNullOrEmpty(account.Username))
            builder = builder.WithLoginHint(account.Username);

        var result = await builder.ExecuteAsync(ct);
        LogService.Log($"OAuthService: interactive sign-in complete for {result.Account.Username}");
        return new OAuthResult(result.AccessToken, result.Account.Username);
    }

    public async Task SignOutAsync(AccountModel account)
    {
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
