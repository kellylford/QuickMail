using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Shows the dialog modally and runs <paramref name="drive"/> inside its loop. A watchdog closes
    /// everything after ten seconds and fails the test, so a future change that opens a message box
    /// from one of these buttons — a nested modal loop nothing would close — fails here instead of
    /// hanging the CI job.
    /// </summary>
    private static void RunModal(AddAccountDialog dialog, Action drive)
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
    public void ClosingTheWindowAtTheAddedStep_CountsAsDone()
    {
        // Escape is Done; the close box and Alt+F4 must be too, or the user lands back in Manage
        // Accounts on the new account's settings — what #834 set out to remove.
        var dialog = new AddAccountDialog(CompleteGmailVm()) { CommitAccount = () => "kelly@gmail.com" };
        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            TabOrderWalker.Drain();
            dialog.Close();
        });
        Assert.Equal(AddAccountOutcome.Done, dialog.Outcome);
    }

    [StaFact]
    public void ASecondAdd_DoesNotSaveTheAccountTwice()
    {
        // Focus is on the hidden Add button until it moves to Done; a repeated Enter in that gap
        // reaches it.
        var vm = CompleteGmailVm();
        var dialog = new AddAccountDialog(vm);
        var commits = 0;
        dialog.CommitAccount = () => { commits++; return "kelly@gmail.com"; };
        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            Press(dialog, "AddButton");
            Assert.Equal(1, commits);
        });
    }

    [StaFact]
    public void TheAddedStep_HasOnlyItsOwnAccessKeys()
    {
        // Alt+N and Alt+D are also Account name and Sender display name in the hidden form. WPF skips
        // hidden targets, so they only count while visible; this pins that the form really is hidden.
        var dialog = new AddAccountDialog(CompleteGmailVm()) { CommitAccount = () => "kelly@gmail.com" };
        RunModal(dialog, () =>
        {
            Press(dialog, "AddButton");
            dialog.UpdateLayout();
            TabOrderWalker.Drain();

            var keys = new System.Collections.Generic.List<char>();
            void Visit(DependencyObject node)
            {
                if (node is AccessText { IsVisible: true, IsEnabled: true } text && text.AccessKey != '\0')
                    keys.Add(char.ToUpperInvariant(text.AccessKey));
                for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    Visit(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
            Visit(dialog);
            Assert.Equal(new[] { 'D', 'N' }, keys.OrderBy(k => k));
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

    private sealed class InMemoryAccounts : IAccountService
    {
        public List<AccountModel> Stored { get; } = [];
        public List<AccountModel> LoadAccounts() => [.. Stored];
        public void SaveAccounts(List<AccountModel> accounts) { Stored.Clear(); Stored.AddRange(accounts); }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private sealed class AlwaysSyncsContacts : IContactSyncService
    {
        public bool CanSync(AccountModel account) => true;
        public Task<ContactSyncResult> SyncAccountAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
        public Task<ContactSyncResult> SyncAllAsync(CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
        public Task<ContactSyncResult> SyncAllDueAsync(TimeSpan minInterval, CancellationToken ct = default) => Task.FromResult(ContactSyncResult.None);
        public Task RemoveAccountContactsAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AContactConsentThatFailsAfterTheDialogCloses_IsReportedOnce_AndSavesOnlyThatAccount()
    {
        var accounts = new InMemoryAccounts();
        var consent = new TaskCompletionSource();
        var oauth = new StubOAuthService { ContactsConsent = consent.Task };
        var vm = new AccountManagerViewModel(
            accounts, new StubCredentialService(), new StubImapMailService(), oauth,
            new StubLocalStoreService(), new StubConfigService(), new StubFeatureGate(), Catalog,
            contactSync: new AlwaysSyncsContacts());
        var added = new AccountModel
        {
            Id = Guid.NewGuid(), Username = "kelly@contoso.com",
            AuthType = AuthType.OAuth2Microsoft, SyncContacts = true,
        };

        vm.CommitNewAccount(added, string.Empty);
        Assert.False(vm.NewAccountFollowUpsCompletion.IsCompleted);   // consent still running: Done can close now

        // Meanwhile the main window saves something of its own, e.g. another account.
        var other = new AccountModel { Id = Guid.NewGuid(), Username = "other@example.com" };
        accounts.Stored.Add(other);

        consent.SetException(new InvalidOperationException("declined"));
        await vm.NewAccountFollowUpsCompletion;

        var failure = Assert.Single(vm.TakeNewAccountFailures());
        Assert.Contains("contact sync couldn't be enabled", failure, StringComparison.Ordinal);
        Assert.Empty(vm.TakeNewAccountFailures());                     // said once

        Assert.False(accounts.Stored.Single(a => a.Id == added.Id).SyncContacts);
        Assert.Contains(accounts.Stored, a => a.Id == other.Id);       // the main window's save survived
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
