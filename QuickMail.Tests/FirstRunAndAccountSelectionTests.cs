using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #834 follow-ups:
/// - First run opens Add Account directly, so on a new install Add Account carries the Import
///   Settings offer (#507) that used to sit beside New in Manage Accounts.
/// - Manage Accounts selects the account it puts focus on. It used to focus the first row without
///   selecting it, so Delete and Set Default, which act on the selection, did nothing.
/// - Delete with nothing selected says why, and a delete leaves the next account selected.
/// </summary>
[Collection("WpfTests")]
public class FirstRunAndAccountSelectionTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AccountManagerViewModel NewManagerVm(params AccountModel[] accounts)
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), Catalog);
        foreach (var a in accounts) vm.Accounts.Add(a);
        return vm;
    }

    private static AccountModel Account(string user) => new()
    {
        Id = Guid.NewGuid(), Username = user, AuthType = AuthType.Password,
        ImapHost = "imap.example.com", SmtpHost = "smtp.example.com",
    };

    private static T Named<T>(Window window, string name) where T : class
    {
        var element = window.FindName(name) as T;
        Assert.NotNull(element);
        return element!;
    }

    /// <summary>Shows the dialog modally, runs <paramref name="drive"/> inside its loop, with a watchdog.</summary>
    private static void RunModal(Window dialog, Action drive)
    {
        dialog.WindowStyle = WindowStyle.None;
        dialog.ShowInTaskbar = false;
        dialog.ShowActivated = false;
        Exception? failure = null;
        var timedOut = false;
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            timedOut = true;
            foreach (Window owned in dialog.OwnedWindows) owned.Close();
            if (dialog.IsVisible) dialog.Close();
        };
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { drive(); }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        watchdog.Start();
        dialog.ShowDialog();
        watchdog.Stop();
        if (timedOut) throw new Xunit.Sdk.XunitException("The dialog was still open after 10 seconds.");
        if (failure != null) throw new Xunit.Sdk.XunitException("Inside the dialog: " + failure);
    }

    // ── First run: Import Settings in Add Account ─────────────────────────────────────────────────

    [StaFact]
    public void ImportSettings_IsOffered_OnlyWhenAsked()
    {
        var offered = NewManagerVm().CreateAddAccountViewModel();
        offered.OfferImportSettings = true;
        var dialog = new AddAccountDialog(offered);
        RunModal(dialog, () => Assert.True(Named<Button>(dialog, "ImportSettingsButton").IsVisible));

        var plain = new AddAccountDialog(NewManagerVm().CreateAddAccountViewModel());
        RunModal(plain, () => Assert.False(Named<Button>(plain, "ImportSettingsButton").IsVisible));
    }

    [StaFact]
    public void ImportSettings_ClosesWithThatOutcome_AndAddsNothing()
    {
        var addVm = NewManagerVm().CreateAddAccountViewModel();
        addVm.OfferImportSettings = true;
        var dialog = new AddAccountDialog(addVm);
        var commits = 0;
        dialog.CommitAccount = () => { commits++; return "x"; };

        RunModal(dialog, () =>
            Named<Button>(dialog, "ImportSettingsButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));

        Assert.Equal(AddAccountOutcome.ImportSettings, dialog.Outcome);
        Assert.Equal(0, commits);
    }

    [StaFact]
    public void ImportSettings_ComesAfterTheFormAndBeforeAddAccount_InTheTabOrder()
    {
        var addVm = NewManagerVm().CreateAddAccountViewModel();
        addVm.OfferImportSettings = true;
        var dialog = new AddAccountDialog(addVm);
        RunModal(dialog, () =>
        {
            TabOrderWalker.StartAt(dialog, Named<Control>(dialog, "CancelButton"), "CancelButton");
            var stops = TabOrderWalker.Walk(dialog);
            var order = string.Join(" -> ", stops);
            Assert.Equal(new[] { "ImportSettingsButton", "AddButton", "TestConnectionButton", "CancelButton" },
                stops.TakeLast(4));
            Assert.True(stops.IndexOf("HeaderSite") < stops.IndexOf("ImportSettingsButton"), order);
        });
    }

    // ── Manage Accounts selects what it focuses ───────────────────────────────────────────────────

    [StaFact]
    public void ManageAccounts_OpensWithTheFirstAccountSelected()
    {
        var first = Account("first@example.com");
        var vm = NewManagerVm(first, Account("second@example.com"));
        Assert.Null(vm.SelectedAccount);

        RunModal(new AccountManagerDialog(vm), () => Assert.Same(first, vm.SelectedAccount));
    }

    [StaFact]
    public void ManageAccounts_KeepsAnAccountTheCallerChose()
    {
        // Account Settings… from the main window pre-selects that account.
        var second = Account("second@example.com");
        var vm = NewManagerVm(Account("first@example.com"), second);
        vm.SelectedAccount = second;

        RunModal(new AccountManagerDialog(vm), () => Assert.Same(second, vm.SelectedAccount));
    }

    // ── Delete ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithNothingSelected_SaysWhy()
    {
        var vm = NewManagerVm(Account("first@example.com"));
        var recorder = StatusAnnouncementRecorder.Watch(vm);

        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.Single(vm.Accounts);
        Assert.Equal(AnnouncementCategory.Result, recorder.Last.Category);
        Assert.Contains("Select an account", recorder.Last.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_LeavesTheNextAccountSelected_OrThePreviousWhenItWasLast()
    {
        var a = Account("a@example.com");
        var b = Account("b@example.com");
        var c = Account("c@example.com");
        var vm = NewManagerVm(a, b, c);
        vm.ConfirmAccountRemoval = _ => true;

        vm.SelectedAccount = b;
        await vm.DeleteAccountCommand.ExecuteAsync(null);
        Assert.Same(c, vm.SelectedAccount);

        await vm.DeleteAccountCommand.ExecuteAsync(null);   // c was last
        Assert.Same(a, vm.SelectedAccount);

        await vm.DeleteAccountCommand.ExecuteAsync(null);   // nothing left
        Assert.Null(vm.SelectedAccount);
    }
}
