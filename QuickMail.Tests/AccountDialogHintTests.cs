using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Instructions and usage tips must go through AccessibilityHelper.Announce, where the user's
/// AnnounceHints preference applies — not into AutomationProperties.HelpText, which the screen
/// reader speaks regardless of what the user has asked QuickMail for, and not into
/// AutomationProperties.Name, which must stay a short label.
///
/// These tests focus the real controls in a real window and listen for the announcement. They used
/// to be text greps over the XAML — which would have passed just as happily if every hint had been
/// DELETED rather than moved, since "no HelpText" and "a GotKeyboardFocus handler is wired up" are
/// both true of a HintFor that returns null for everything.
/// </summary>
[Collection("WpfTests")]
public class AccountDialogHintTests
{
    private static AddAccountViewModel NewAddVm() =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true },
            new StubImapMailService(), new StubOAuthService(), new ProviderCatalog());

    private static AccountManagerViewModel NewManagerVm()
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog(),
            contactSync: new StubContactSyncService(), graphCalendarSync: new StubGraphCalendarSyncService());

        // Microsoft OAuth so the contact- and calendar-sync checkboxes are shown; without a selected
        // account the whole form is disabled and nothing can take focus.
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            AccountName = "Test account",
            Username = "kelly@outlook.com",
            AuthType = AuthType.OAuth2Microsoft,
            ImapHost = "outlook.office365.com",
            SmtpHost = "smtp-mail.outlook.com",
        };
        vm.Accounts.Add(account);
        vm.SelectedAccount = account;
        return vm;
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T Named<T>(Window window, string name) where T : class
    {
        var element = window.FindName(name) as T;
        Assert.True(element is not null, $"{name} is missing from {window.GetType().Name}.");
        return element!;
    }

    /// <summary>
    /// Gives an element keyboard focus and returns every announcement that resulted, with its
    /// category — the categories matter, because each is gated by a different user preference.
    /// </summary>
    private static List<(string Text, AnnouncementCategory Category)> FocusAndListen(
        List<(string Text, AnnouncementCategory Category)> heard, FrameworkElement element)
    {
        heard.Clear();
        element.Focus();
        Keyboard.Focus(element);
        Drain();
        return heard;
    }

    private static List<(string Text, AnnouncementCategory Category)> Listen()
    {
        var heard = new List<(string, AnnouncementCategory)>();
        AccessibilityHelper.AnnouncementObserver = (text, category) => heard.Add((text, category));
        return heard;
    }

    private static void AssertHintSpoken(
        List<(string Text, AnnouncementCategory Category)> heard, string controlName)
    {
        Assert.True(
            heard.Any(h => h.Category == AnnouncementCategory.Hint && !string.IsNullOrWhiteSpace(h.Text)),
            $"focusing {controlName} produced no hint. Heard: "
            + (heard.Count == 0 ? "(nothing)" : string.Join(" | ", heard.Select(h => $"{h.Category}: {h.Text}"))));
    }

    [StaFact]
    public void EveryHintedControlInTheAddAccountDialogSpeaksItsHintOnFocus()
    {
        var vm = NewAddVm();
        var window = new AddAccountDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        var heard = Listen();
        try
        {
            vm.IsAdvancedExpanded = true;   // the SMTP checkbox lives in there
            window.UpdateLayout();
            Drain();

            foreach (var name in new[]
                     {
                         "AccountNameBox", "DisplayNameBox", "PasswordBox",
                         "SmtpImplicitSslCheckBox", "SignatureBox",
                     })
            {
                AssertHintSpoken(FocusAndListen(heard, Named<FrameworkElement>(window, name)), name);
            }
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void TheAddAccountSyncCheckboxesSpeakTheirHints()
    {
        var vm = NewAddVm();
        var window = new AddAccountDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        var heard = Listen();
        try
        {
            // Both checkboxes are shown only for an account with a contact/calendar API.
            vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);
            window.UpdateLayout();
            Drain();

            foreach (var name in new[] { "SyncContactsCheckBox", "SyncCalendarCheckBox" })
                AssertHintSpoken(FocusAndListen(heard, Named<FrameworkElement>(window, name)), name);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void EveryHintedControlInTheManageAccountsDialogSpeaksItsHintOnFocus()
    {
        var vm = NewManagerVm();
        var window = new AccountManagerDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        var heard = Listen();
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            foreach (var name in new[]
                     {
                         "AccountNameBox", "SyncContactsCheckBox", "SyncCalendarCheckBox",
                         "SmtpImplicitSslCheckBox", "SignatureBox",
                     })
            {
                AssertHintSpoken(FocusAndListen(heard, Named<FrameworkElement>(window, name)), name);
            }
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaTheory]
    [InlineData("AddAccountDialog")]
    [InlineData("AccountManagerDialog")]
    public void TheSmtpSslHintNamesBothPorts(string dialog)
    {
        // The checkbox's visible Content names port 465 and port 587, but an explicit
        // AutomationProperties.Name OVERRIDES that text — so once the Name was shortened to a label,
        // the port guidance existed for sighted users only.
        Window window;
        AccountEditorViewModel vm;
        if (dialog == "AddAccountDialog")
        {
            (window, vm) = OpenAddAccount();
        }
        else
        {
            var opened = OpenManageAccounts();
            (window, vm) = (opened.Window, opened.Vm);
        }

        var heard = Listen();
        try
        {
            vm.IsAdvancedExpanded = true;
            window.UpdateLayout();
            Drain();

            var spoken = FocusAndListen(heard, Named<FrameworkElement>(window, "SmtpImplicitSslCheckBox"))
                .Where(h => h.Category == AnnouncementCategory.Hint)
                .Select(h => h.Text)
                .ToList();

            Assert.Contains(spoken, t => t.Contains("465", StringComparison.Ordinal));
            Assert.Contains(spoken, t => t.Contains("587", StringComparison.Ordinal));
            Assert.Contains(spoken, t => t.Contains("STARTTLS", StringComparison.Ordinal));
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void AHintIsNotRepeatedWhenFocusReturnsToTheSameControl()
    {
        // Keyboard focus comes back to the same control through no action of the user's — the window
        // is re-activated when the OAuth sign-in window closes, a dropdown hands focus back. Saying
        // the hint again each time is noise.
        var (window, _) = OpenAddAccount();
        var heard = Listen();
        try
        {
            var accountName = Named<FrameworkElement>(window, "AccountNameBox");
            var displayName = Named<FrameworkElement>(window, "DisplayNameBox");

            AssertHintSpoken(FocusAndListen(heard, accountName), "AccountNameBox");

            // Model the window regaining focus: WPF raises GotKeyboardFocus for the element that
            // already has it. Re-calling Focus() would raise nothing at all and prove nothing.
            heard.Clear();
            accountName.RaiseEvent(new KeyboardFocusChangedEventArgs(
                Keyboard.PrimaryDevice, 0, null, accountName)
            {
                RoutedEvent = UIElement.GotKeyboardFocusEvent,
            });
            Drain();
            Assert.Empty(heard.Where(h => h.Category == AnnouncementCategory.Hint));

            // But a genuine move away and back still speaks: the suppression is of repeats, not of
            // the hint.
            AssertHintSpoken(FocusAndListen(heard, displayName), "DisplayNameBox");
            AssertHintSpoken(FocusAndListen(heard, accountName), "AccountNameBox (returning)");
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    [StaFact]
    public void ARefusedSaveIsAnnouncedAsAResultNotAsBackgroundProgress()
    {
        // Refusing to add the account is the outcome of the button the user just pressed. Announced
        // as Status, a user who has turned background-progress announcements off gets silence while
        // focus jumps to a field, with no reason given.
        var (window, vm) = OpenAddAccount();
        var heard = Listen();
        try
        {
            vm.Username = string.Empty;    // not ready to save
            Drain();
            heard.Clear();

            Named<Button>(window, "AddButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain();

            Assert.Contains(heard, h => h.Category == AnnouncementCategory.Result
                                     && h.Text.Contains("email address", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(heard, h => h.Category == AnnouncementCategory.Status);
        }
        finally
        {
            AccessibilityHelper.AnnouncementObserver = null;
            window.Close();
        }
    }

    private static (Window Window, AddAccountViewModel Vm) OpenAddAccount()
    {
        var vm = NewAddVm();
        var window = new AddAccountDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        Drain();
        return (window, vm);
    }

    private static (Window Window, AccountManagerViewModel Vm) OpenManageAccounts()
    {
        var vm = NewManagerVm();
        var window = new AccountManagerDialog(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        Drain();
        return (window, vm);
    }

    // ── The anti-patterns these hints exist to avoid ─────────────────────────────

    private static string ReadView(string fileName)
    {
        // Walk up from the test binary to the repo root.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "QuickMail", "Views", fileName);
        Assert.True(File.Exists(path), $"could not locate {fileName} (looked at {path})");
        return File.ReadAllText(path);
    }

    // A negative assertion, so unlike the behaviour tests above it cannot be satisfied by deleting
    // anything: HelpText is spoken by the screen reader directly and so ignores AnnounceHints.
    [Theory]
    [InlineData("AddAccountDialog.xaml")]
    [InlineData("AccountManagerDialog.xaml")]
    public void TheAccountDialogsCarryNoHelpText(string fileName)
    {
        var xaml = ReadView(fileName);

        Assert.DoesNotContain("AutomationProperties.HelpText", xaml, StringComparison.Ordinal);
    }

    // An AutomationProperties.Name is a short label. A sentence in one is an instruction wearing a
    // label's clothes, and it bypasses the announcement preferences the same way HelpText does.
    [Theory]
    [InlineData("AddAccountDialog.xaml")]
    [InlineData("AccountManagerDialog.xaml")]
    public void NoAutomationNameIsASentence(string fileName)
    {
        var xaml = ReadView(fileName);

        var offenders = System.Text.RegularExpressions.Regex
            .Matches(xaml, @"AutomationProperties\.Name=""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Contains(". ", StringComparison.Ordinal) || v.EndsWith('.'))
            .ToList();

        Assert.True(offenders.Count == 0,
            "AutomationProperties.Name must be a short label, not a sentence: " + string.Join(" | ", offenders));
    }
}
