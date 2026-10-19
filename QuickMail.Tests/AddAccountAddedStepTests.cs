using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #834: after Add Account the dialog moves to an "added" step offering Add another account or
/// Done. It used to close back into Manage Accounts with the new account selected, so Tab walked
/// the user back through every setting they had just entered. These run the dialog modally,
/// because the buttons close it through DialogResult, which only a modal window accepts.
/// </summary>
[Collection("WpfTests")]
public class AddAccountAddedStepTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AddAccountViewModel CompleteGmailVm()
    {
        var vm = new AddAccountViewModel(new StubFeatureGate(), new StubImapMailService(),
            new StubOAuthService(), Catalog);
        vm.SelectedProvider = Catalog.All.First(p => p.Id == ProviderCatalog.GmailId);
        vm.Username = "kelly@gmail.com";
        vm.Password = "abcdabcdabcdabcd";
        return vm;
    }

    private static T Named<T>(Window window, string name) where T : class
    {
        var element = window.FindName(name) as T;
        Assert.NotNull(element);
        return element!;
    }

    private static void Press(Window window, string buttonName) =>
        Named<Button>(window, buttonName).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>Shows the dialog modally and runs <paramref name="drive"/> inside its loop.</summary>
    private static void RunModal(AddAccountDialog dialog, Action drive)
    {
        dialog.WindowStyle = WindowStyle.None;
        dialog.ShowInTaskbar = false;
        dialog.ShowActivated = false;
        Exception? failure = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { drive(); }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        dialog.ShowDialog();
        if (failure != null) throw new Xunit.Sdk.XunitException("Inside the dialog: " + failure);
    }

    [StaFact]
    public void AddingAnAccount_ShowsTheAddedStep_WithOnlyAddAnotherAndDoneInTheTabOrder()
    {
        var vm = CompleteGmailVm();
        var dialog = new AddAccountDialog(vm);
        var commits = 0;
        dialog.CommitAccount = () => { commits++; return "kelly@gmail.com"; };

        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            dialog.UpdateLayout();
            TabOrderWalker.Drain();

            Assert.Equal(1, commits);
            Assert.True(vm.IsAdded);
            Assert.Equal("kelly@gmail.com added.", vm.AddedMessage);
            Assert.False(Named<FrameworkElement>(dialog, "FormGrid").IsVisible);
            Assert.True(Named<FrameworkElement>(dialog, "AddedPanel").IsVisible);

            // The whole point: none of the account's settings are Tab stops any more. (Where focus
            // lands — Done — needs an active window, so the VM walkthrough checks that, not this.)
            var addAnother = Named<Button>(dialog, "AddAnotherButton");
            TabOrderWalker.StartAt(dialog, addAnother, "AddAnotherButton");
            var stops = TabOrderWalker.Walk(dialog);
            Assert.Equal(new[] { "AddAnotherButton", "DoneButton" }, stops.OrderBy(s => s));
        });
    }

    [StaFact]
    public void Done_ClosesWithTheDoneOutcome()
    {
        var vm = CompleteGmailVm();
        var dialog = new AddAccountDialog(vm) { CommitAccount = () => "kelly@gmail.com" };

        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            TabOrderWalker.Drain();
            Press(dialog, "DoneButton");
        });

        Assert.Equal(AddAccountOutcome.Done, dialog.Outcome);
    }

    [StaFact]
    public void AddAnother_ClosesWithTheAddAnotherOutcome()
    {
        var vm = CompleteGmailVm();
        var dialog = new AddAccountDialog(vm) { CommitAccount = () => "kelly@gmail.com" };

        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            TabOrderWalker.Drain();
            Press(dialog, "AddAnotherButton");
        });

        Assert.Equal(AddAccountOutcome.AddAnother, dialog.Outcome);
    }

    [StaFact]
    public void DoneIsBothTheDefaultAndTheCancelButton()
    {
        // The account is saved before the step appears, so Escape must mean Done, not "undo".
        var dialog = new AddAccountDialog(CompleteGmailVm());
        RunModal(dialog, () =>
        {
            var done = Named<Button>(dialog, "DoneButton");
            Assert.True(done.IsDefault);
            Assert.True(done.IsCancel);
        });
    }

    [StaFact]
    public void AnIncompleteForm_SavesNothingAndStaysOnTheForm()
    {
        var vm = CompleteGmailVm();
        vm.Password = string.Empty;
        var dialog = new AddAccountDialog(vm);
        var commits = 0;
        dialog.CommitAccount = () => { commits++; return "x"; };

        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            TabOrderWalker.Drain();

            Assert.Equal(0, commits);
            Assert.False(vm.IsAdded);
            Assert.True(Named<FrameworkElement>(dialog, "FormGrid").IsVisible);
            Assert.False(Named<FrameworkElement>(dialog, "AddedPanel").IsVisible);
        });

        Assert.Equal(AddAccountOutcome.None, dialog.Outcome);
    }

    [StaFact]
    public void Cancel_LeavesNoOutcome()
    {
        var dialog = new AddAccountDialog(CompleteGmailVm()) { CommitAccount = () => "x" };
        RunModal(dialog, () => Press(dialog, "CancelButton"));
        Assert.Equal(AddAccountOutcome.None, dialog.Outcome);
    }

    [Fact]
    public void CommitNewAccount_RemembersTheAccountForTheMainWindowToLandOn()
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), Catalog);
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "kelly@gmail.com" };

        vm.CommitNewAccount(account, "abcdabcdabcdabcd");

        Assert.Equal(account.Id, vm.LastAddedAccountId);
        Assert.False(vm.ShowAddedAccountAfterClose);   // only Done sets it
    }
}
