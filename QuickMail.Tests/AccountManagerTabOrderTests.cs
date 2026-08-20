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
    private static AccountManagerViewModel NewVm(AuthType authType = AuthType.Password)
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog());

        // An account must be selected: with none, the whole edit form is disabled (IsEditing) and a
        // disabled control is not a tab stop at all, so there would be nothing to measure.
        // OAuth2Microsoft shows the "Sign in with Microsoft" button (IsOAuth2); Password shows the
        // password box instead. Either way the account stays on the IMAP/SMTP backend, so Advanced
        // shows the auth combo and host fields.
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            AccountName = "Test account",
            Username = "kelly@example.com",
            AuthType = authType,
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

    /// <summary>
    /// #584: the OAuth "Sign in with Microsoft" button is the LAST stop in the edit form — after the
    /// Advanced expander and everything it reveals — so re-authenticating is the final thing a
    /// keyboard user Tabs to, not a control stranded above Advanced that forces a Shift+Tab back up.
    /// The forward walk pins the position; the backward walk pins that Shift+Tab is its exact reverse.
    /// </summary>
    [StaFact]
    public void OAuthSignInIsTheLastStop_AfterAdvancedSettings()
    {
        var vm = NewVm(AuthType.OAuth2Microsoft);
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

            // The button has no x:Name, so the walker reports it by its AutomationProperties.Name.
            const string SignIn = "Sign in with Microsoft";

            // Forward (Tab): sign-in comes after the expander header and after the last Advanced
            // control (Signature), and did not jump above the main-surface fields.
            TabOrderWalker.StartAt(window, close!, "Close");
            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);
            int At(string name) => stops.IndexOf(name);

            Assert.True(At(SignIn) >= 0, $"sign-in button not reachable. Order: {order}");
            Assert.True(At("HeaderSite") >= 0, $"expander header never reached. Order: {order}");
            Assert.True(At("SignatureBox") >= 0, $"Signature never reached. Order: {order}");
            // >= 0 guarded: -1 < a real index passes vacuously and would hide the regression.
            Assert.True(At("UsernameBox") >= 0 && At("UsernameBox") < At(SignIn),
                $"email field unreachable, or sign-in jumped above the form. Order: {order}");
            Assert.True(At("HeaderSite") < At(SignIn), $"sign-in precedes the Advanced expander. Order: {order}");
            Assert.True(At("SignatureBox") < At(SignIn), $"sign-in precedes the Advanced contents. Order: {order}");

            // Backward (Shift+Tab): from Close, the stop after sign-in, going back reaches sign-in
            // first, then the Advanced contents, then the expander header — the exact reverse.
            TabOrderWalker.StartAt(window, close!, "Close");
            var back = TabOrderWalker.WalkBackward(window);
            var backOrder = string.Join(" -> ", back);
            int BackAt(string name) => back.IndexOf(name);

            Assert.True(BackAt(SignIn) >= 0, $"Shift+Tab never reached sign-in. Backward: {backOrder}");
            Assert.True(BackAt(SignIn) < BackAt("SignatureBox"),
                $"Shift+Tab reached Advanced before sign-in. Backward: {backOrder}");
            Assert.True(BackAt("SignatureBox") < BackAt("HeaderSite"),
                $"Shift+Tab order is not the reverse of Tab. Backward: {backOrder}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #584 follow-up (PR #585 review note 3): pins the common case — Advanced COLLAPSED. Its contents
    /// are then not focusable, so the expander header is the only Advanced stop, and sign-in must still
    /// sort after it (and after the main-surface fields), in both Tab and Shift+Tab directions.
    /// </summary>
    [StaFact]
    public void OAuthSignInIsTheLastStop_WithAdvancedCollapsed()
    {
        var vm = NewVm(AuthType.OAuth2Microsoft);
        var window = new AccountManagerDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try
        {
            vm.IsAdvancedExpanded = false;   // the common case: Advanced stays closed
            window.UpdateLayout();
            Drain();

            var close = window.FindName("CloseButton") as Button;
            Assert.NotNull(close);
            const string SignIn = "Sign in with Microsoft";

            TabOrderWalker.StartAt(window, close!, "Close");
            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);
            int At(string name) => stops.IndexOf(name);

            Assert.True(At(SignIn) >= 0, $"sign-in button not reachable. Order: {order}");
            Assert.True(At("HeaderSite") >= 0, $"expander header never reached. Order: {order}");
            // Collapsed: no Advanced contents are tab stops...
            Assert.True(At("SignatureBox") < 0, $"Advanced contents are reachable while collapsed. Order: {order}");
            Assert.True(At("ImapHostBox") < 0, $"Advanced contents are reachable while collapsed. Order: {order}");
            // ...and sign-in still sorts after the collapsed Advanced header and after the form fields.
            Assert.True(At("UsernameBox") >= 0 && At("UsernameBox") < At(SignIn),
                $"email field unreachable, or sign-in jumped above the form. Order: {order}");
            Assert.True(At("HeaderSite") < At(SignIn), $"sign-in precedes the Advanced header. Order: {order}");

            // Backward (Shift+Tab): from Close, going back reaches sign-in before the Advanced header.
            TabOrderWalker.StartAt(window, close!, "Close");
            var back = TabOrderWalker.WalkBackward(window);
            var backOrder = string.Join(" -> ", back);
            int BackAt(string name) => back.IndexOf(name);

            Assert.True(BackAt(SignIn) >= 0, $"Shift+Tab never reached sign-in. Backward: {backOrder}");
            Assert.True(BackAt("HeaderSite") >= 0, $"Shift+Tab never reached the Advanced header. Backward: {backOrder}");
            Assert.True(BackAt(SignIn) < BackAt("HeaderSite"),
                $"Shift+Tab reached the Advanced header before sign-in. Backward: {backOrder}");
        }
        finally { window.Close(); }
    }
}
