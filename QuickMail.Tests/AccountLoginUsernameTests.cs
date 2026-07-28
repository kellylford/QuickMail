using System;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #396: one box served as both the account's email address and its server login, and the two
/// are not always the same string. The reporter's iCloud login is "fastfinge" while his mail is at
/// samuel@interfree.ca — so whichever he entered, the other use of it was wrong.
/// </summary>
public class AccountLoginUsernameTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AddAccountViewModel NewAddVm() =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true },
            new StubImapMailService(), new StubOAuthService(), Catalog);

    private static AccountManagerViewModel NewManagerVm(AccountModel account)
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), Catalog);
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;
        return vm;
    }

    // ── The model ────────────────────────────────────────────────────────────────

    [Fact]
    public void WithNoOverrideTheLoginIsTheEmailAddress()
    {
        var account = new AccountModel { Username = "kelly@example.com" };

        Assert.Equal("kelly@example.com", account.AuthUsername);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyOverrideDoesNotDisplaceTheEmailAddress(string? login)
    {
        var account = new AccountModel { Username = "kelly@example.com", LoginUsername = login };

        Assert.Equal("kelly@example.com", account.AuthUsername);
    }

    [Fact]
    public void AnOverrideIsWhatTheServerIsLoggedIntoWith()
    {
        var account = new AccountModel { Username = "samuel@interfree.ca", LoginUsername = "fastfinge" };

        Assert.Equal("fastfinge", account.AuthUsername);
        // The address is untouched: it is still what goes in the From header.
        Assert.Equal("samuel@interfree.ca", account.Username);
    }

    // ── Add Account ──────────────────────────────────────────────────────────────

    [Fact]
    public void ALoginNameInTheAddressBoxIsRefusedWithAMessageNamingTheOtherField()
    {
        var vm = NewAddVm();
        vm.Username = "fastfinge";
        vm.ImapHost = "imap.mail.me.com";
        vm.SmtpHost = "smtp.mail.me.com";
        vm.Password = "app-specific-password";

        Assert.False(vm.IsReadyToSave(out var error));
        Assert.Contains("not an email address", error);
        Assert.Contains("Login username", error);
    }

    [Fact]
    public void AnAddressWithADomainIsAccepted()
    {
        var vm = NewAddVm();
        vm.Username = "samuel@interfree.ca";
        vm.ImapHost = "imap.mail.me.com";
        vm.SmtpHost = "smtp.mail.me.com";
        vm.Password = "app-specific-password";

        Assert.True(vm.IsReadyToSave(out _));
    }

    /// <summary>
    /// An intranet host has no dot and is still a real address. Recipient checking in compose is
    /// stricter on the reasoning that a typo is likelier; an account the user configured themselves
    /// gets the benefit of the doubt.
    /// </summary>
    [Fact]
    public void ADomainWithNoDotIsStillAnAddress()
    {
        var vm = NewAddVm();
        vm.Username = "kelly@mailhost";
        vm.ImapHost = "mailhost";
        vm.SmtpHost = "mailhost";
        vm.Password = "pw";

        Assert.True(vm.IsReadyToSave(out _));
    }

    [Fact]
    public void AnOverrideIsCarriedOntoTheSavedAccountAndIsNullWhenBlank()
    {
        var vm = NewAddVm();
        vm.Username = "samuel@interfree.ca";
        vm.LoginUsername = "  fastfinge  ";

        Assert.Equal("fastfinge", vm.ToAccountModel().LoginUsername);

        vm.LoginUsername = "   ";
        Assert.Null(vm.ToAccountModel().LoginUsername);
    }

    // ── Manage Accounts ──────────────────────────────────────────────────────────

    [Fact]
    public void SelectingAnAccountLoadsItsOverrideIntoTheForm()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            Username = "samuel@interfree.ca",
            LoginUsername = "fastfinge",
            AuthType = AuthType.Password,
        };

        var vm = NewManagerVm(account);

        Assert.Equal("fastfinge", vm.LoginUsername);
        Assert.Equal("samuel@interfree.ca", vm.Username);
    }

    [Fact]
    public void SavingWritesTheOverrideBackAndTrimsIt()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            Username = "samuel@interfree.ca",
            AuthType = AuthType.Password,
        };
        var vm = NewManagerVm(account);

        vm.LoginUsername = " fastfinge ";
        vm.SaveAccountCommand.Execute(null);

        Assert.Equal("fastfinge", account.LoginUsername);
        Assert.Equal("fastfinge", account.AuthUsername);
        Assert.Equal("Account saved.", vm.StatusText);
    }

    [Fact]
    public void ClearingTheOverrideSendsTheLoginBackToTheEmailAddress()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            Username = "samuel@interfree.ca",
            LoginUsername = "fastfinge",
            AuthType = AuthType.Password,
        };
        var vm = NewManagerVm(account);

        vm.LoginUsername = string.Empty;
        vm.SaveAccountCommand.Execute(null);

        Assert.Null(account.LoginUsername);
        Assert.Equal("samuel@interfree.ca", account.AuthUsername);
    }

    /// <summary>
    /// This is the path that repairs an account created before the address was checked, so the
    /// refusal has to be both blocking and audible — a Result, not background progress.
    /// </summary>
    [Fact]
    public void ManageAccountsRefusesToSaveALoginNameAsTheAddressAndSaysSoAsAResult()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            Username = "samuel@interfree.ca",
            AuthType = AuthType.Password,
        };
        var vm = NewManagerVm(account);

        vm.Username = "fastfinge";
        vm.SaveAccountCommand.Execute(null);

        Assert.Equal("samuel@interfree.ca", account.Username);   // nothing was written
        Assert.Contains("not an email address", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
    }
}
