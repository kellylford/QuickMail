// The compose window announces the sending account once the choice has settled — Enter
// on the closed From combo, the dropdown closing, or focus leaving the combo. It must NOT
// announce on every SelectionChanged: arrowing through a closed combo changes the
// selection on each keystroke and the screen reader already reads each account name, so
// per-change announcing would speak twice per arrow press.
//
// It must also stay silent when nothing changed — opening the window and tabbing straight
// past From should say nothing about the From address.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeWindowFromAccountAnnouncementTests
{
    private static readonly Guid IdeaPlaceId = Guid.NewGuid();
    private static readonly Guid SecondaryId = Guid.NewGuid();

    /// <summary>Two accounts, so switching the From address is actually possible.</summary>
    private sealed class TwoAccountService : QuickMail.Services.IAccountService
    {
        public List<AccountModel> LoadAccounts() =>
        [
            new AccountModel { Id = IdeaPlaceId, AccountName = "IdeaPlace",
                               Username = "kelly@theideaplace.net", IsDefault = true },
            new AccountModel { Id = SecondaryId, AccountName = "Secondary",
                               Username = "kelly@example.com" },
        ];
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static ComposeWindow NewShownComposeWindow(out ComboBox fromCombo)
    {
        var accounts = new TwoAccountService();
        var vm = new QuickMail.ViewModels.ComposeViewModel(
            new StubSmtpService(), accounts, new StubCredentialService(),
            new StubImapMailService(), new FakeLocalDraftService(), new StubTemplateService());

        // The window reads SenderAccount on Loaded to set its announcement baseline, so
        // the list must be populated the way a real compose does before Show().
        vm.SenderAccounts = new System.Collections.ObjectModel.ObservableCollection<AccountModel>(
            accounts.LoadAccounts());
        vm.SenderAccount = vm.SenderAccounts[0];

        var window = new ComposeWindow(
            vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ConfirmSaveOnClose = null,
        };
        window.Show();
        Drain();

        fromCombo = window.FindName("FromCombo") as ComboBox
                    ?? throw new InvalidOperationException("FromCombo is missing from ComposeWindow.xaml");
        return window;
    }

    private static List<(string Text, AnnouncementCategory Category)> Listen()
    {
        var heard = new List<(string, AnnouncementCategory)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));
        return heard;
    }

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [StaFact]
    public void EnterOnTheClosedFromCombo_AnnouncesTheAccountAsAResult()
    {
        var window = NewShownComposeWindow(out var from);
        var heard = Listen();
        try
        {
            from.SelectedIndex = 1;   // as if arrowed to the second account
            RaiseEnter(from);
            Drain();

            Assert.Contains(heard, h => h.Text == "Secondary used as From address"
                                     && h.Category == AnnouncementCategory.Result);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void ArrowingThroughTheClosedFromCombo_DoesNotAnnounceOnEverySelectionChange()
    {
        var window = NewShownComposeWindow(out var from);
        var heard = Listen();
        try
        {
            // Three selection changes, as if arrowing down and back up. The screen reader
            // reads each account itself; our announcement must wait for the choice to settle.
            from.SelectedIndex = 1;
            from.SelectedIndex = 0;
            from.SelectedIndex = 1;
            Drain();

            Assert.DoesNotContain(heard, h => h.Text.Contains("used as From address", StringComparison.Ordinal));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void LeavingTheFromCombo_AnnouncesOnlyWhenTheAccountChanged()
    {
        var window = NewShownComposeWindow(out var from);
        var subject = window.FindName("SubjectBox") as TextBox;
        Assert.NotNull(subject);

        var heard = Listen();
        try
        {
            // Focus From and leave again without touching the selection: silent.
            from.Focus();
            Drain();
            subject!.Focus();
            Drain();
            Assert.DoesNotContain(heard, h => h.Text.Contains("used as From address", StringComparison.Ordinal));

            // Now change the account and leave: announced once.
            from.Focus();
            Drain();
            from.SelectedIndex = 1;
            subject!.Focus();
            Drain();

            Assert.Single(heard, h => h.Text == "Secondary used as From address"
                                   && h.Category == AnnouncementCategory.Result);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void ASettledAccountIsNotAnnouncedTwice()
    {
        var window = NewShownComposeWindow(out var from);
        var subject = window.FindName("SubjectBox") as TextBox;
        Assert.NotNull(subject);

        var heard = Listen();
        try
        {
            // Choose with Enter, then tab away — the LostKeyboardFocus path must not repeat
            // what Enter already said.
            from.Focus();
            Drain();
            from.SelectedIndex = 1;
            RaiseEnter(from);
            Drain();
            subject!.Focus();
            Drain();

            Assert.Single(heard, h => h.Text == "Secondary used as From address");
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    private static void RaiseEnter(UIElement target) =>
        target.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(target) ?? PresentationSource.FromVisual(Window.GetWindow(target)!),
            0,
            Key.Return)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
}
