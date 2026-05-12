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
    /// Forces a browser-based interactive sign-in and returns the token + the
    /// signed-in username (UPN) so the caller can populate the Username field.
    /// </summary>
    Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default);

    Task SignOutAsync(AccountModel account);
}
