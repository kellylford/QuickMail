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
        public bool WithContactsPathUsed;   // true when the consent-folding overload was taken
        public AccountModel? LastSignInAccount;

        private OAuthResult Capture(AccountModel account, CancellationToken ct)
        {
            LastSignInToken = ct;
            LastSignInAccount = account;
            return new OAuthResult("token", ReturnUsername, ReturnPersonal);
        }

        public Task<OAuthResult> SignInInteractiveAsync(AccountModel account, CancellationToken ct = default)
            => Task.FromResult(Capture(account, ct));
        public Task<OAuthResult> SignInInteractiveWithContactsAsync(AccountModel account, CancellationToken ct = default)
        {
            WithContactsPathUsed = true;
            return Task.FromResult(Capture(account, ct));
        }

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
        var vm = new AddAccountViewModel(new StubFeatureGate(), new StubImapMailService(), oauth, new ProviderCatalog());
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

    // ── Single-consent fold-in: calendar (or contacts) opts the sign-in into the consent-folding path ──
    // The path itself is what carries the contact/calendar scopes into the one interactive prompt so a
    // personal account isn't re-prompted per capability. Calendar alone must take it — the old code only
    // consulted SyncContacts, so a calendar-only account fell through to a plain mail sign-in and then a
    // separate calendar consent.

    [Fact]
    public async Task MicrosoftSignIn_CalendarSyncOnly_UsesConsentFoldingPath_AndCarriesTheOptIns()
    {
        var (vm, oauth) = NewVm();
        vm.Username = "user@contoso.com";
        oauth.ReturnUsername = "user@contoso.com";
        vm.SyncContacts = false;
        vm.SyncCalendar = true;

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.True(oauth.WithContactsPathUsed);
        Assert.NotNull(oauth.LastSignInAccount);
        Assert.False(oauth.LastSignInAccount!.SyncContacts);
        Assert.True(oauth.LastSignInAccount!.SyncCalendar);
    }

    [Fact]
    public async Task MicrosoftSignIn_NoSyncRequested_UsesPlainSignIn()
    {
        var (vm, oauth) = NewVm();
        vm.Username = "user@contoso.com";
        oauth.ReturnUsername = "user@contoso.com";
        vm.SyncContacts = false;
        vm.SyncCalendar = false;

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        Assert.False(oauth.WithContactsPathUsed);
    }

    // #544 review finding 3: the known personal flag must reach the sign-in, or the fold's personal check
    // falls back to the domain guess and misses a personal account on a vanity domain. The VM learns the
    // flag from a prior sign-in (token-derived, #233); a subsequent sign-in must carry it onto tempAccount.
    [Fact]
    public async Task MicrosoftSignIn_CarriesTheKnownPersonalFlag_OnASubsequentSignIn()
    {
        var (vm, oauth) = NewVm();
        vm.Username = "me@myvanitydomain.com";
        oauth.ReturnUsername = "me@myvanitydomain.com";
        oauth.ReturnPersonal = true;               // the token says personal (vanity domain)
        vm.SyncContacts = true;

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);
        // The FIRST tempAccount couldn't know yet — the flag is only learned from this sign-in's result.
        Assert.Null(oauth.LastSignInAccount!.IsPersonalMicrosoftAccount);

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);
        // The SECOND carries the now-known flag, so the fold applies without relying on the domain guess.
        Assert.Equal(true, oauth.LastSignInAccount!.IsPersonalMicrosoftAccount);
    }
}
