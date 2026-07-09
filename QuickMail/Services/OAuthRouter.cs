using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Routes OAuth calls to <see cref="IOAuthService"/> (Microsoft MSAL) or
/// <see cref="IGoogleOAuthService"/> based on the account's <see cref="AuthType"/>.
/// Implements <see cref="IOAuthService"/> so all existing callers need no changes.
/// </summary>
public class OAuthRouter : IOAuthService
{
    private readonly IOAuthService _microsoft;
    private readonly IGoogleOAuthService _google;

    public OAuthRouter(IOAuthService microsoft, IGoogleOAuthService google)
    {
        _microsoft = microsoft;
        _google    = google;
    }

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default)
    {
        return account.AuthType == AuthType.OAuth2Google
            ? _google.GetAccessTokenAsync(account.Username, ct)
            : _microsoft.GetAccessTokenAsync(account, ct);
    }

    public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        // Google scopes are fixed at consent time; the scopes parameter applies only to Microsoft.
        return account.AuthType == AuthType.OAuth2Google
            ? _google.GetAccessTokenAsync(account.Username, ct)
            : _microsoft.GetAccessTokenAsync(account, scopes, ct);
    }

    public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default)
    {
        // Google's token flow is separate (system-browser AppFlow, not the embedded WebView), so the
        // #206 background-window race doesn't apply the same way. Treat Google as a no-op here to keep
        // its current behavior; the Microsoft path gets the real silent-only check.
        return account.AuthType == AuthType.OAuth2Google
            ? Task.CompletedTask
            : _microsoft.EnsureSilentTokenAsync(account, ct);
    }

    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default)
    {
        return account.AuthType == AuthType.OAuth2Google
            ? _google.SignInInteractiveAsync(account.Username, ct)
            : _microsoft.SignInInteractiveAsync(account, ct);
    }

    public Task SignOutAsync(AccountModel account)
    {
        return account.AuthType == AuthType.OAuth2Google
            ? _google.SignOutAsync(account.Username)
            : _microsoft.SignOutAsync(account);
    }
}
