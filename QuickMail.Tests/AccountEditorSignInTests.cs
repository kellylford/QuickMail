using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Sign-in behavior on the account editor: the wrong-identity guard (#202) and the removal of the
/// short app-imposed interactive timeout (#203). Both live on <see cref="AccountEditorViewModel"/>
/// and are exercised here through <see cref="AddAccountViewModel"/>.
/// </summary>
public class AccountEditorSignInTests
{
    /// <summary>Configurable OAuth fake: returns a chosen username and records the token it was handed.</summary>
    private sealed class FakeOAuthService : IOAuthService
    {
        public string ReturnUsername = string.Empty;
        public bool ReturnPersonal;
        public CancellationToken LastSignInToken = new(canceled: true); // sentinel: a real call must overwrite it

        private OAuthResult Capture(CancellationToken ct)
        {
            LastSignInToken = ct;
            return new OAuthResult("token", ReturnUsername, ReturnPersonal);
        }

        public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default)
            => Task.FromResult(Capture(ct));
        public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default)
            => Task.FromResult(Capture(ct));

        public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult("token");
        public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromResult("token");
        public Task<string> GetAccessTokenSilentAsync(AccountModel account, string[] scopes, CancellationToken ct = default) => Task.FromResult("token");
        public Task EnsureSilentTokenAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestContactsConsentAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestCalendarConsentAsync(AccountModel account, CancellationToken ct = default) => Task.CompletedTask;
        public Task SignOutAsync(AccountModel account) => Task.CompletedTask;
    }

    private static (AddAccountViewModel Vm, FakeOAuthService Oauth) NewVm()
    {
        var oauth = new FakeOAuthService();
        var vm = new AddAccountViewModel(new StubFeatureGate(), new StubImapMailService(), oauth);
        return (vm, oauth);
    }

    // ── #202: wrong-identity guard ────────────────────────────────────────────────

    [Fact]
    public async Task MicrosoftSignIn_DifferentIdentity_DoesNotRebind_AndWarns()
    {
        var (vm, oauth) = NewVm();
        vm.Username = "user@contoso.com";
        oauth.ReturnUsername = "admin@contoso.com"; // an admin signed in to consent

        (string entered, string actual)? warned = null;
        vm.SignInIdentityMismatch += (e, a) => warned = (e, a);

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.Equal("user@contoso.com", vm.Username);          // NOT overwritten with the admin
        Assert.NotNull(warned);
        Assert.Equal(("user@contoso.com", "admin@contoso.com"), warned);
        Assert.Contains("not user@contoso.com", vm.StatusText); // status reflects the mismatch
    }

    [Fact]
    public async Task MicrosoftSignIn_SameIdentity_AdoptsUsername_NoWarning()
    {
        var (vm, oauth) = NewVm();
        vm.Username = "user@contoso.com";
        oauth.ReturnUsername = "User@Contoso.com"; // same identity, different casing

        var warned = false;
        vm.SignInIdentityMismatch += (_, _) => warned = true;

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.Equal("User@Contoso.com", vm.Username); // adopted (canonical casing from token)
        Assert.False(warned);
    }

    [Fact]
    public async Task MicrosoftSignIn_BlankEntry_AdoptsWhoeverSignsIn_NoWarning()
    {
        var (vm, oauth) = NewVm();
        vm.Username = string.Empty;                 // user let the provider pick the account
        oauth.ReturnUsername = "picked@contoso.com";

        var warned = false;
        vm.SignInIdentityMismatch += (_, _) => warned = true;

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.Equal("picked@contoso.com", vm.Username);
        Assert.False(warned);
    }

    // ── #203: no app-imposed interactive timeout ──────────────────────────────────

    [Fact]
    public async Task MicrosoftSignIn_UsesNonCancellingToken_NoShortTimeout()
    {
        var (vm, oauth) = NewVm();
        vm.Username = "user@contoso.com";
        oauth.ReturnUsername = "user@contoso.com";

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        // A CancellationToken.None cannot be cancelled — proving no app-imposed timeout was attached.
        Assert.False(oauth.LastSignInToken.CanBeCanceled);
    }

    [Fact]
    public async Task GoogleSignIn_UsesNonCancellingToken_NoShortTimeout()
    {
        var (vm, oauth) = NewVm();
        vm.AuthType = AuthType.OAuth2Google;
        vm.Username = "user@example.com";
        oauth.ReturnUsername = "user@example.com";

        await vm.SignInGoogleCommand.ExecuteAsync(null);

        Assert.False(oauth.LastSignInToken.CanBeCanceled);
    }
}
