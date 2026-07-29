using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tab order in the Manage Accounts dialog, measured by walking WPF's real focus traversal — the
/// same method as <see cref="AddAccountTabOrderTests"/>, and for the same reason: reading TabIndex
/// values off the XAML tells you what was written, not where focus goes.
///
/// This dialog had no such test, which is how it regressed. Removing the explicit TabIndex values
/// from inside the Advanced expander was right, but the left column's New / Delete / Set Default
/// buttons had none either — so they defaulted to int.MaxValue along with the expander contents,
/// while the form's fields kept low explicit indices, and the account-list buttons landed in the
/// MIDDLE of the form: password box, then New/Delete/Set Default, then Advanced settings.
/// </summary>
[Collection("WpfTests")]
public class AccountManagerTabOrderTests
{
    private static AccountManagerViewModel NewVm()
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog());

        // An account must be selected: with none, the whole edit form is disabled (IsEditing) and a
        // disabled control is not a tab stop at all, so there would be nothing to measure.
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            AccountName = "Test account",
            Username = "kelly@example.com",
            AuthType = AuthType.Password,
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
        };
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;
        return vm;
    }

    /// <summary>
    /// Walks Tab from the currently focused control and records each stop. Uses MoveFocus, not
    /// PredictFocus — PredictFocus only supports directional navigation and throws on Next.
    /// </summary>


    private static void Drain() => TabOrderWalker.Drain();

    [StaFact]
    public void TheAccountListAndItsButtonsComeBeforeTheEditForm()
    {
        var vm = NewVm();
        var window = new AccountManagerDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            // Start from the Close button, the last stop in the dialog, so the walk wraps once and
            // records every other stop in order.
            var close = window.FindName("CloseButton") as Button;
            Assert.NotNull(close);
            TabOrderWalker.StartAt(window, close!, "Close");

            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);
            int At(string name) => stops.IndexOf(name);

            Assert.True(At("AccountListBox") >= 0, $"account list never reached. Order: {order}");
            Assert.True(At("NewButton") >= 0, $"New never reached. Order: {order}");
            Assert.True(At("AccountNameBox") >= 0, $"account name never reached. Order: {order}");

            // The left column, in visual order (list above its buttons)...
            Assert.True(At("AccountListBox") < At("NewButton"), $"Order: {order}");
            Assert.True(At("NewButton") < At("Delete account"), $"Order: {order}");
            Assert.True(At("Delete account") < At("Set as default"), $"Order: {order}");

            // ...and all of it before the form. This is the regression: those three buttons used to
            // land between the password box and Advanced settings.
            Assert.True(At("Set as default") < At("AccountNameBox"),
                $"the account-list buttons fall inside the edit form. Order: {order}");
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheEditFormIsInVisualOrderAndAdvancedComesLast()
    {
        var vm = NewVm();
        var window = new AccountManagerDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            var close = window.FindName("CloseButton") as Button;
            Assert.NotNull(close);
            TabOrderWalker.StartAt(window, close!, "Close");

            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);
            int At(string name) => stops.IndexOf(name);

            // The read-only provider value is reachable at all — it is a TextBlock, so it is only in
            // the tab order because Focusable is set — and it sits with the fields it describes
            // rather than at the very end.
            Assert.True(At("ProviderText") >= 0, $"the provider value is not reachable. Order: {order}");
            Assert.True(At("ProviderText") < At("AccountNameBox"), $"Order: {order}");

            Assert.True(At("AccountNameBox") < At("DisplayNameBox"), $"Order: {order}");
            Assert.True(At("DisplayNameBox") < At("UsernameBox"), $"Order: {order}");
            Assert.True(At("UsernameBox") < At("PasswordBox"), $"Order: {order}");

            // "HeaderSite" is the name WPF's Expander template gives its header ToggleButton — the
            // thing that takes focus when you Tab to "Advanced settings". Everything it reveals must
            // come after it, or a keyboard user has to Shift+Tab backwards to reach settings they
            // just opened.
            var expanderAt = At("HeaderSite");
            Assert.True(expanderAt >= 0, $"expander header never reached. Order: {order}");
            Assert.True(At("PasswordBox") < expanderAt, $"Order: {order}");
            Assert.True(expanderAt < At("AuthTypeCombo"), $"Order: {order}");
            Assert.True(At("AuthTypeCombo") < At("ImapHostBox"), $"Order: {order}");
            Assert.True(At("ImapHostBox") < At("SmtpHostBox"), $"Order: {order}");
            Assert.True(At("SmtpHostBox") < At("SignatureBox"), $"Order: {order}");
        }
        finally { window.Close(); }
    }
}
