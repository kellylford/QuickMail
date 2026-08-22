using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// IOAuthService for integration tests. Every account in this suite uses password auth against
/// GreenMail, so no OAuth path is ever taken; any call means a test misconfigured its account,
/// hence every member throws rather than silently returning a fake token.
/// </summary>
internal sealed class NoOpOAuthService : IOAuthService
{
    private static NotSupportedException Fail() =>
        new("OAuth is not available in integration tests — use AuthType.Password accounts.");

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => throw Fail();
    public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => throw Fail();
    public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => throw Fail();
    public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default) => throw Fail();
    public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default) => throw Fail();
    public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default) => throw Fail();
    public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default) => throw Fail();
    public Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default) => throw Fail();
    public Task RequestSharedMailboxConsentAsync(AccountModel parent, CancellationToken ct = default) => throw Fail();
    public Task SignOutAsync(AccountModel account) => throw Fail();
}
