using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public record OAuthResult(string AccessToken, string Username);

public interface IOAuthService
{
    /// <summary>
    /// Returns an access token, using the silent (cached) path first.
    /// Falls back to interactive browser sign-in if the cached token is expired.
    /// </summary>
    Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default);

    /// <summary>
    /// Returns an access token for the given explicit scope set (used by the Graph backend to
    /// request Graph scopes rather than the account's default IMAP scopes). Silent-then-interactive.
    /// </summary>
    Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default);

    /// <summary>
    /// Verifies a token can be obtained <b>silently</b> (from the cache / refresh token) for this
    /// account, without opening an interactive sign-in window. Throws
    /// <see cref="InteractiveSignInRequiredException"/> if the user would have to sign in
    /// interactively. Used by background connect to avoid popping a sign-in window under a short
    /// timeout that would be torn down mid-sign-in (#206).
    /// </summary>
    Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default);

    /// <summary>
    /// Forces a browser-based interactive sign-in and returns the token + the
    /// signed-in username (UPN) so the caller can populate the Username field.
    /// </summary>
    Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default);

    Task SignOutAsync(AccountModel account);
}
