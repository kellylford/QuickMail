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

    public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
    {
        // Google's GetAccessTokenAsync is already silent-only — it refreshes the stored token or
        // throws, and never opens a browser — so it doubles as the silent path here. Microsoft gets
        // the real silent-only acquisition (no interactive fallback).
        return account.AuthType == AuthType.OAuth2Google
            ? _google.GetAccessTokenAsync(account.Username, ct)
            : _microsoft.GetAccessTokenSilentAsync(account, scopes, ct);
    }

    public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default)
    {
        // Google is KNOWINGLY UNPROTECTED here: its token flow uses the system browser (not the
        // embedded WebView), so a background connect could still open a browser tab — but a tab is
        // merely abandoned, not torn down mid-sign-in the way the embedded window is (#206). Treat
        // Google as a no-op (background connect proceeds as before); only the Microsoft path gets the
        // real silent-only check. Revisit if Google ever moves to an embedded/blocking sign-in.
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

    public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default)
    {
        // Google's contact scopes are requested via a fresh interactive sign-in that widens the
        // stored refresh token's grant; Microsoft's are requested by acquiring a token for the
        // Graph contact scopes (silent if already consented, interactive otherwise).
        return account.AuthType == AuthType.OAuth2Google
            ? _google.AuthorizeContactsAsync(account.Username, ct)
            : _microsoft.RequestContactsConsentAsync(account, ct);
    }

    public Task SignOutAsync(AccountModel account)
    {
        return account.AuthType == AuthType.OAuth2Google
            ? _google.SignOutAsync(account.Username)
            : _microsoft.SignOutAsync(account);
    }
}
