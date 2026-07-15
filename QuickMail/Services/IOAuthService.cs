using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <param name="IsPersonalMicrosoftAccount">
/// True when the signed-in account is a personal Microsoft account (its MSAL tenant is the well-known
/// consumers tenant) — determined authoritatively from the token rather than the email domain. Drives
/// the per-account scope selection for consumer accounts (#217/#233). Microsoft sign-in only; Google
/// leaves it false.
/// </param>
public record OAuthResult(string AccessToken, string Username, bool IsPersonalMicrosoftAccount = false);

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

    /// <summary>
    /// Ensures the account has consented to the read-only contact scopes needed for contact sync
    /// (issue #256): Graph <c>Contacts.Read</c>/<c>People.Read</c> for Microsoft accounts, the
    /// People API read scopes for Google. Silent when consent was already granted; otherwise opens
    /// an interactive sign-in. Throws if the user declines. Call from a foreground user action only.
    /// </summary>
    Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default);

    Task SignOutAsync(AccountModel account);
}
