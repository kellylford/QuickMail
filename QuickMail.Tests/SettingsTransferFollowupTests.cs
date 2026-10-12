using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
/// Issue #507 follow-ups from testing with a screen reader: Import Settings offered beside New on a
/// new install, and the Export window's list read once per row with Home and End working.
/// </summary>
[Collection("WpfTests")]
public sealed class SettingsTransferFollowupTests
{
    private static AccountManagerViewModel AccountManager() =>
        new(new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate(), new ProviderCatalog());

    private static void Drain() => TabOrderWalker.Drain();

    // These tests move real keyboard focus; release it so it cannot leak into the next test in the
    // WpfTests collection (as RowFieldsWindowCheckBoxTests does).
    private static void CloseAndReleaseFocus(Window window)
    {
        window.Close();
        Keyboard.ClearFocus();
        Drain();
    }

    private static T OffScreen<T>(T window) where T : Window
    {
        window.WindowStyle = WindowStyle.None;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        return window;
    }

    // ── Import Settings beside New, on a new install ───────────────────────────

    [Fact]
    public void ImportOffer_OnlyOnANewInstallWithNoAccountYet()
    {
        var vm = AccountManager();
        Assert.False(vm.ShowImportSettings);            // not a new install: never offered

        vm.OfferImportSettings = true;
        Assert.True(vm.ShowImportSettings);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Accounts.Add(new AccountModel { Id = Guid.NewGuid(), Username = "kelly@example.com" });

        Assert.False(vm.ShowImportSettings);            // the first account ends the offer
        Assert.Contains(nameof(AccountManagerViewModel.ShowImportSettings), raised);
    }

    [Fact]
    public void ImportOffer_StillTracksTheListAfterItIsReplaced()
    {
        // Set Default rebuilds the Accounts collection; the offer must follow the new one.
        var vm = AccountManager();
        vm.OfferImportSettings = true;
        vm.Accounts = new System.Collections.ObjectModel.ObservableCollection<AccountModel>();
        Assert.True(vm.ShowImportSettings);
        vm.Accounts.Add(new AccountModel { Id = Guid.NewGuid() });
        Assert.False(vm.ShowImportSettings);
    }

    [Theory]
    [InlineData(false, false, true)]    // nothing written yet: new
    [InlineData(true, false, true)]     // the first start wrote config.ini, no account saved: still new
    [InlineData(false, true, false)]    // an account file, even empty: not new
    [InlineData(true, true, false)]
    public void NewInstall_MeansNoAccountHasEverBeenSaved(bool configExists, bool accountsExist, bool expected)
    {
        var dir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qm-new-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            if (configExists) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "config.ini"), "[global]");
            if (accountsExist) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "accounts.json"), "[]");
            Assert.Equal(expected, SettingsImporter.IsNewInstall(dir));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ImportOffer_ChoosingItIsRecordedOnlyWhileOffered()
    {
        var vm = AccountManager();
        vm.ChooseImportSettings();
        Assert.False(vm.ImportSettingsChosen);

        vm.OfferImportSettings = true;
        vm.ChooseImportSettings();
        Assert.True(vm.ImportSettingsChosen);
    }

    [StaFact]
    public void ImportOffer_TheButtonFollowsNewInTheTabOrder()
    {
        var vm = AccountManager();
        vm.OfferImportSettings = true;
        var window = OffScreen(new AccountManagerDialog(vm));
        window.Show();
        try
        {
            window.UpdateLayout();
            Drain();
            var button = window.FindName("ImportSettingsButton") as Button;
            Assert.NotNull(button);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal("Import settings", AutomationProperties.GetName(button));

            var close = window.FindName("CloseButton") as Button;
            Assert.NotNull(close);
            TabOrderWalker.StartAt(window, close, "Close");
            var stops = TabOrderWalker.Walk(window);
            var order = string.Join(" -> ", stops);
            Assert.True(stops.IndexOf("NewButton") >= 0, order);
            Assert.True(stops.IndexOf("NewButton") < stops.IndexOf("ImportSettingsButton"), order);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void ImportOffer_NoButtonWhenNotANewInstall()
    {
        var vm = AccountManager();
        var window = OffScreen(new AccountManagerDialog(vm));
        window.Show();
        try
        {
            window.UpdateLayout();
            var button = window.FindName("ImportSettingsButton") as Button;
            Assert.NotNull(button);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    // ── The Export window's list ───────────────────────────────────────────────

    private static ExportSettingsWindow ExportWindow() =>
        OffScreen(new ExportSettingsWindow(new ExportSettingsViewModel(
            System.IO.Path.GetTempPath(),
            () => Task.FromResult<IReadOnlyList<CalendarEvent>?>([]),
            new Dictionary<Guid, IReadOnlyDictionary<string, string>>(),
            "0.8.55", "PC")));

    private static FieldCheckList CategoryList(Window w)
    {
        var list = w.FindName("CategoryList") as FieldCheckList;
        Assert.NotNull(list);
        return list;
    }

    private static CheckBox[] Rows(FieldCheckList list) =>
        Enumerable.Range(0, list.Items.Count)
            .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i))
            .OfType<DependencyObject>()
            .Select(FieldCheckList.FindCheckBox)
            .OfType<CheckBox>()
            .ToArray();

    /// <summary>
    /// The reported fault: each name was spoken twice, once for a same-named row around the check
    /// box and once for the check box. Each row must be exactly one element, a named check box.
    /// </summary>
    [StaFact]
    public void ExportList_EachCategoryIsOneCheckBoxNamedOnce()
    {
        var window = ExportWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            Drain();
            var peer = UIElementAutomationPeer.CreatePeerForElement(CategoryList(window));
            Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());
            Assert.Equal("What to export", peer.GetName());

            var children = peer.GetChildren();
            Assert.Equal(SettingsCategoryInfo.All.Count, children.Count);
            Assert.All(children, c => Assert.Equal(AutomationControlType.CheckBox, c.GetAutomationControlType()));
            Assert.Equal(SettingsCategoryInfo.All.Select(c => c.Name), children.Select(c => c.GetName()));
        }
        finally { CloseAndReleaseFocus(window); }
    }

    private static void Press(Window window, UIElement from, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
            Source = from,
        };
        from.RaiseEvent(args);
        Assert.True(args.Handled, $"{key} was not handled");
    }

    [StaFact]
    public void ExportList_HomeAndEndGoToTheFirstAndLastCategory()
    {
        var window = ExportWindow();
        window.Show();
        try
        {
            window.Activate();
            window.UpdateLayout();
            Drain();
            var list = CategoryList(window);
            var rows = Rows(list);
            Assert.Equal(SettingsCategoryInfo.All.Count, rows.Length);

            Assert.True(rows[2].Focus());
            Press(window, rows[2], Key.End);
            Assert.True(rows[^1].IsKeyboardFocused, "End did not reach the last category");

            Press(window, rows[^1], Key.Home);
            Assert.True(rows[0].IsKeyboardFocused, "Home did not reach the first category");
        }
        finally { CloseAndReleaseFocus(window); }
    }
}
