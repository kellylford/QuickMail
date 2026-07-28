using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Google stopped authorizing QuickMail for new accounts (#369, #226), so Gmail signs in with an app
/// password. The users authorized before that still work, and these tests lock the deal made for
/// them: the Google path is hidden by default, the GoogleAuth flag brings it back as a first-class
/// provider entry, and an account already using it is never stranded by the flag being off.
/// </summary>
public class GoogleSignInOptInTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static StubFeatureGate Gate(bool googleAuth) =>
        new() { [FeatureFlag.GraphBackend] = true, [FeatureFlag.GoogleAuth] = googleAuth };

    private static AddAccountViewModel NewAddVm(bool googleAuth) =>
        new(Gate(googleAuth), new StubImapMailService(), new StubOAuthService(), Catalog);

    /// <summary>Types an address a character at a time, the way the bound TextBox delivers it.</summary>
    private static void TypeAddress(AddAccountViewModel vm, string address)
    {
        for (var i = 1; i <= address.Length; i++) vm.Username = address[..i];
    }

    // ── The gate itself ──────────────────────────────────────────────────────────

    [Fact]
    public void GoogleAuthIsOffByDefault()
    {
        // The whole point of the change: an option that can only fail for almost everyone must not
        // be on the screen for almost everyone.
        var gate = new ConfigFeatureGate(new ConfigModel(), Array.Empty<string>());

        Assert.False(gate.IsEnabled(FeatureFlag.GoogleAuth));
    }

    [Fact]
    public void GoogleAuthIsEnabledByConfigOrCommandLine()
    {
        var fromConfig = new ConfigModel();
        fromConfig.Features["GoogleAuth"] = "true";
        Assert.True(new ConfigFeatureGate(fromConfig, Array.Empty<string>()).IsEnabled(FeatureFlag.GoogleAuth));

        // --feature GoogleAuth, for a user who would rather not edit config.ini.
        Assert.True(new ConfigFeatureGate(new ConfigModel(), ["GoogleAuth"]).IsEnabled(FeatureFlag.GoogleAuth));
    }

    [Fact]
    public void ConfigFlagIsReadWhateverCaseItIsWrittenIn()
    {
        var cfg = new ConfigModel();
        cfg.Features["googleauth"] = "true";

        Assert.True(new ConfigFeatureGate(cfg, Array.Empty<string>()).IsEnabled(FeatureFlag.GoogleAuth));
    }

    // ── The catalog entry ────────────────────────────────────────────────────────

    [Fact]
    public void TheGoogleSignInEntryIsGmailWithoutAnAppPassword()
    {
        var entry = Catalog.GmailGoogleSignIn;

        Assert.Equal(ProviderCatalog.GmailOAuthId, entry.Id);
        Assert.Equal(AuthType.OAuth2Google, entry.DefaultAuthType);
        Assert.Equal("imap.gmail.com", entry.ImapHost);
        Assert.Equal("smtp.gmail.com", entry.SmtpHost);
        // No app-password hint: there is no password to warn about, which is the point of the entry.
        Assert.False(entry.RequiresAppPassword);
        // Domains must be non-empty or MailProvider.IsOther would make this the manual-settings
        // catch-all — the entry would open Advanced settings instead of filling the servers in.
        Assert.False(entry.IsOther);
    }

    [Theory]
    [InlineData("kelly@gmail.com")]
    [InlineData("kelly@googlemail.com")]
    public void AGmailAddressStillMatchesTheAppPasswordEntry(string address)
    {
        // Two catalog entries own gmail.com and MatchByEmail takes the first. If that order ever
        // flips, every new Gmail account lands on a sign-in path that Google refuses.
        Assert.Equal(ProviderCatalog.GmailId, Catalog.MatchByEmail(address)?.Id);
    }

    [Fact]
    public void AnAccountSavedWithGoogleSignInResolvesBackToThatEntry()
    {
        var account = new AccountModel
        {
            Username = "kelly@gmail.com",
            ProviderId = ProviderCatalog.GmailOAuthId,
            AuthType = AuthType.OAuth2Google,
            ImapHost = "imap.gmail.com",
        };

        // Resolve prefers the saved ProviderId, so the account keeps its identity even though the
        // host and the address both point at the app-password entry.
        Assert.Equal(ProviderCatalog.GmailOAuthId, Catalog.Resolve(account).Id);
    }

    // ── The picker ───────────────────────────────────────────────────────────────

    [Fact]
    public void WithTheFlagOffTheGoogleEntryIsNotInThePickerAtAll()
    {
        var vm = NewAddVm(googleAuth: false);

        Assert.DoesNotContain(vm.Providers, p => p.Id == ProviderCatalog.GmailOAuthId);
        Assert.False(vm.ShowGoogleAuthOption);
        // Absent from the list, not merely collapsed — nothing for a keyboard or a screen reader to
        // land on.
        Assert.Contains(vm.Providers, p => p.Id == ProviderCatalog.GmailId);
    }

    [Fact]
    public void WithTheFlagOnTheGoogleEntrySitsDirectlyAfterGmail()
    {
        var vm = NewAddVm(googleAuth: true);

        var gmail = vm.Providers.ToList().FindIndex(p => p.Id == ProviderCatalog.GmailId);
        var google = vm.Providers.ToList().FindIndex(p => p.Id == ProviderCatalog.GmailOAuthId);

        Assert.True(gmail >= 0);
        Assert.Equal(gmail + 1, google);
        Assert.True(vm.ShowGoogleAuthOption);
    }

    [Fact]
    public void PickingItFillsGmailServersAndAsksForGoogleSignInInsteadOfAPassword()
    {
        var vm = NewAddVm(googleAuth: true);

        vm.SelectedProvider = Catalog.GmailGoogleSignIn;

        Assert.Equal(AuthType.OAuth2Google, vm.AuthType);
        Assert.Equal("imap.gmail.com", vm.ImapHost);
        Assert.Equal(993, vm.ImapPort);
        Assert.Equal("smtp.gmail.com", vm.SmtpHost);
        Assert.Equal(587, vm.SmtpPort);
        // The password box and its app-password warning are both gone; the Sign in with Google
        // button is what appears instead.
        Assert.False(vm.IsPasswordAuth);
        Assert.False(vm.ShowAppPasswordHint);
        Assert.True(vm.IsGoogleOAuth);
        // A recognized provider answers everything, so Advanced stays shut.
        Assert.False(vm.IsAdvancedExpanded);
    }

    [Fact]
    public void TypingTheAddressDoesNotUndoTheChoice()
    {
        // The regression this guards: MatchByEmail answers "gmail" for every gmail.com address by
        // design, so without the already-matches guard the first keystroke silently swapped the
        // user's deliberate pick for the app-password entry — and then demanded a password.
        var vm = NewAddVm(googleAuth: true);
        vm.SelectedProvider = Catalog.GmailGoogleSignIn;

        TypeAddress(vm, "kelly@gmail.com");

        Assert.Equal(ProviderCatalog.GmailOAuthId, vm.SelectedProvider!.Id);
        Assert.Equal(AuthType.OAuth2Google, vm.AuthType);
    }

    [Fact]
    public void CorrectingTheAddressToAnotherProviderStillMovesOffIt()
    {
        // The guard above must not become a trap: an address that is no longer Gmail has to leave.
        var vm = NewAddVm(googleAuth: true);
        vm.SelectedProvider = Catalog.GmailGoogleSignIn;

        TypeAddress(vm, "kelly@yahoo.com");

        Assert.Equal(ProviderCatalog.YahooId, vm.SelectedProvider!.Id);
    }

    [Fact]
    public void TheChoiceSurvivesIntoTheSavedAccount()
    {
        var vm = NewAddVm(googleAuth: true);
        vm.SelectedProvider = Catalog.GmailGoogleSignIn;
        TypeAddress(vm, "kelly@gmail.com");

        var account = vm.ToAccountModel();

        Assert.Equal(ProviderCatalog.GmailOAuthId, account.ProviderId);
        Assert.Equal(AuthType.OAuth2Google, account.AuthType);
        Assert.Equal(BackendKind.ImapSmtp, account.BackendKind);
    }

    [Fact]
    public void ContactAndCalendarSyncAreOfferedForIt()
    {
        var vm = NewAddVm(googleAuth: true);

        vm.SelectedProvider = Catalog.GmailGoogleSignIn;

        // Both are granted in the same Google consent as mail, so they must be checkable before
        // sign-in rather than after (#256, #282).
        Assert.True(vm.ShowContactSyncOption);
        Assert.True(vm.ShowCalendarSyncOption);
    }

    [Fact]
    public void ItIsReadyToSaveWithoutAPassword()
    {
        var vm = NewAddVm(googleAuth: true);
        vm.SelectedProvider = Catalog.GmailGoogleSignIn;
        TypeAddress(vm, "kelly@gmail.com");

        Assert.True(vm.IsReadyToSave(out var error));
        Assert.Empty(error);
    }

    // ── Not stranding an existing account ────────────────────────────────────────

    [Fact]
    public void AnAccountAlreadyUsingGoogleSignInKeepsTheAuthOptionWhenTheFlagIsOff()
    {
        // AuthTypeIndex is 2 for OAuth2Google. If the item at index 2 were hidden, the Account
        // Manager would show a blank Authentication box for an account that is signing in fine.
        var vm = NewAddVm(googleAuth: false);

        vm.AuthType = AuthType.OAuth2Google;

        Assert.True(vm.ShowGoogleAuthOption);
        Assert.Equal(2, vm.AuthTypeIndex);
    }

    [Fact]
    public void AnAccountUsingAPasswordDoesNotGetTheAuthOptionWhenTheFlagIsOff()
    {
        var vm = NewAddVm(googleAuth: false);

        vm.AuthType = AuthType.Password;

        Assert.False(vm.ShowGoogleAuthOption);
    }

    // ── The Settings checkbox ────────────────────────────────────────────────────

    [Fact]
    public void TheSettingsCheckboxRoundTripsTheFeatureFlag()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();

        // Off by default, matching the gate.
        var vm = new SettingsViewModel(configService, registry);
        Assert.False(vm.GoogleSignIn);

        vm.GoogleSignIn = true;
        vm.SaveCommand.Execute(null);

        // Written as the same GoogleAuth flag --feature sets, not a setting of its own.
        var saved = configService.Load();
        Assert.True(new ConfigFeatureGate(saved, Array.Empty<string>()).IsEnabled(FeatureFlag.GoogleAuth));
        Assert.True(new SettingsViewModel(configService, registry).GoogleSignIn);
    }

    [Fact]
    public void TurningItBackOffWritesAnExplicitFalseRatherThanDroppingTheKey()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();
        var cfg = new ConfigModel();
        cfg.Features["GoogleAuth"] = "true";
        configService.Save(cfg);

        var vm = new SettingsViewModel(configService, registry);
        Assert.True(vm.GoogleSignIn);
        vm.GoogleSignIn = false;
        vm.SaveCommand.Execute(null);

        var saved = configService.Load();
        Assert.Equal("false", saved.Features["GoogleAuth"]);
        Assert.False(new ConfigFeatureGate(saved, Array.Empty<string>()).IsEnabled(FeatureFlag.GoogleAuth));
    }

    [Fact]
    public void SavingDoesNotLeaveTwoSpellingsOfTheFlagBehind()
    {
        // config.ini preserves whatever case the user typed. A case-sensitive dictionary would add
        // "GoogleAuth = false" beside their "googleauth = true" and the gate would read whichever
        // came first — the setting would appear not to take.
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();
        var cfg = new ConfigModel();
        cfg.Features["googleauth"] = "true";
        configService.Save(cfg);

        var vm = new SettingsViewModel(configService, registry);
        vm.GoogleSignIn = false;
        vm.SaveCommand.Execute(null);

        var saved = configService.Load();
        Assert.Single(saved.Features.Where(f =>
            string.Equals(f.Key, "GoogleAuth", StringComparison.OrdinalIgnoreCase)));
        Assert.False(new ConfigFeatureGate(saved, Array.Empty<string>()).IsEnabled(FeatureFlag.GoogleAuth));
    }
}
