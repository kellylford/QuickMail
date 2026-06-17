using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

public interface IGoogleOAuthService
{
    /// <summary>
    /// Returns a valid access token for the given username, refreshing silently if expired.
    /// Throws <see cref="System.InvalidOperationException"/> if no stored credential exists — call
    /// <see cref="SignInInteractiveAsync"/> first.
    /// </summary>
    Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Opens the system browser for Google sign-in. Stores the refresh token and returns the
    /// access token plus the confirmed Gmail address.
    /// </summary>
    Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default);

    Task SignOutAsync(string username);
}
