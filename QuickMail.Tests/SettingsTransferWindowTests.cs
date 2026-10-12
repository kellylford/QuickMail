using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The three settings export and import windows (issue #507): their XAML loads, the elements the
/// code-behind and the F6 ring rely on exist, list rows carry their name on the container, and
/// the view models behave. Element lookups use <c>as</c> + <c>Assert.NotNull</c> (CLAUDE.md).
/// </summary>
[Collection("WpfTests")]
public sealed class SettingsTransferWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qm-settings-ui-" + Guid.NewGuid().ToString("N"));

    public SettingsTransferWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static T Hidden<T>(T window) where T : Window
    {
        window.WindowStyle = WindowStyle.None;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        return window;
    }

    private ExportSettingsViewModel ExportVm(Func<Task<IReadOnlyList<CalendarEvent>?>>? calendar = null) =>
        new(_root,
            calendar ?? (() => Task.FromResult<IReadOnlyList<CalendarEvent>?>([])),
            new Dictionary<Guid, IReadOnlyDictionary<string, string>>(),
            "0.8.55", "DESKTOP", () => new DateTime(2026, 10, 12, 9, 0, 0));

    // ── Export ─────────────────────────────────────────────────────────────────

    [StaFact]
    public void ExportWindow_LoadsWithEveryCategoryChecked()
    {
        var window = Hidden(new ExportSettingsWindow(ExportVm()));
        try
        {
            window.Show();
            Assert.NotNull(window.FindName("CategoryList") as ItemsControl);
            Assert.NotNull(window.FindName("SummaryBox") as TextBox);
            Assert.NotNull(window.FindName("ExportButton") as Button);
            Assert.NotNull(window.FindName("ButtonRow") as Panel);
            var vm = Assert.IsType<ExportSettingsViewModel>(window.DataContext);
            Assert.Equal(8, vm.Categories.Count);
            Assert.All(vm.Categories, c => Assert.True(c.IsChecked));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ExportVm_SummaryAndButtonFollowTheChoices()
    {
        var vm = ExportVm();
        Assert.StartsWith("8 of 8 selected. The file will contain email addresses, server names and signatures, your address book and your Local Calendar.",
                          vm.Summary, StringComparison.Ordinal);
        Assert.Contains("never contains passwords", vm.Summary, StringComparison.Ordinal);
        Assert.Equal("QuickMail-settings-2026-10-12.qmsettings", vm.DefaultFileName);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        foreach (var c in vm.Categories) c.IsChecked = false;
        Assert.False(vm.CanExport);
        Assert.StartsWith("0 of 8 selected. It never contains", vm.Summary, StringComparison.Ordinal);
        Assert.Contains(nameof(ExportSettingsViewModel.CanExport), raised);
    }

    [Fact]
    public async Task ExportVm_WritesTheFileAndSaysWhatItWrote()
    {
        File.WriteAllText(Path.Combine(_root, "templates.json"), "[]");
        var vm = ExportVm();
        var path = Path.Combine(_root, "out.qmsettings");

        var message = await vm.ExportAsync(path);

        Assert.StartsWith("Settings exported. 8 categories, ", message, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".partial"));
        Assert.Equal(8, SettingsBundle.Open(path).Manifest.Categories.Count);
    }

    [Fact]
    public async Task ExportVm_SaysSoWhenTheLocalCalendarCannotBeRead()
    {
        var vm = ExportVm(() => Task.FromResult<IReadOnlyList<CalendarEvent>?>(null));
        var message = await vm.ExportAsync(Path.Combine(_root, "online.qmsettings"));
        Assert.EndsWith("The Local Calendar could not be read here and was not included.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportVm_AFailureLeavesNoFileBehind()
    {
        var vm = ExportVm();
        var path = Path.Combine(_root, "missing-dir", "x.qmsettings");
        await Assert.ThrowsAnyAsync<IOException>(() => vm.ExportAsync(path));
        Assert.False(File.Exists(path));
    }

    // ── Import preview ─────────────────────────────────────────────────────────

    private string MakeBundle()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "accounts.json"), JsonSerializer.Serialize(new List<AccountModel>
        {
            new() { Id = Guid.NewGuid(), Username = "kelly@example.com", AccountName = "Home" },
        }));
        var path = Path.Combine(_root, "bundle.qmsettings");
        using var stream = File.Create(path);
        SettingsBundle.Write(new SettingsBundle.ExportInputs
        {
            ProfileDir = source,
            Categories = [SettingsCategory.Accounts, SettingsCategory.Templates],
            DeviceName = "DESKTOP",
        }, stream);
        return path;
    }

    [StaFact]
    public void ImportWindow_ListsCategoriesAndAccountsWithNamesOnTheContainers()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var vm = ImportSettingsViewModel.Load(MakeBundle(), target);
        var window = Hidden(new ImportSettingsWindow(vm, () => { }, () => null));
        try
        {
            window.Show();
            Assert.NotNull(window.FindName("SummaryBox") as TextBox);
            var categories = window.FindName("CategoryList") as ListBox;
            var accounts = window.FindName("AccountList") as ListBox;
            Assert.NotNull(categories);
            Assert.NotNull(accounts);
            Assert.NotNull(window.FindName("ImportButton") as Button);
            Assert.StartsWith("Settings from DESKTOP", vm.Summary, StringComparison.Ordinal);

            accounts.UpdateLayout();
            var row = accounts.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Assert.NotNull(row);
            // The name is on the container (CLAUDE.md) and says what will happen to the account.
            Assert.Equal("Home, will be added", System.Windows.Automation.AutomationProperties.GetName(row));
            var peer = UIElementAutomationPeer.CreatePeerForElement(categories);
            Assert.Contains(peer.GetChildren(), c => c.GetName() == "Message templates: 0 templates, replacing the 0 here");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ImportVm_StagesTheFileForTheNextStart()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "stage")).FullName;
        ImportSettingsViewModel.Load(MakeBundle(), target).Stage();
        Assert.True(File.Exists(Path.Combine(target, SettingsImporter.PendingFileName)));
    }

    // ── Settings Imported ──────────────────────────────────────────────────────

    private sealed class RecordingCredentials : ICredentialService
    {
        public Dictionary<Guid, string> Saved { get; } = [];
        public void SavePassword(Guid accountId, string password) => Saved[accountId] = password;
        public string? GetPassword(Guid accountId) => Saved.GetValueOrDefault(accountId);
        public void DeletePassword(Guid accountId) => Saved.Remove(accountId);
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    [Fact]
    public async Task SignIn_PasswordAccount_AsksForThePasswordThenSavesItAndReconnects()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), AccountName = "Home", AuthType = AuthType.Password };
        var creds = new RecordingCredentials();
        var reconnected = new List<AccountModel>();
        var vm = new SettingsImportedViewModel(
            new SettingsImportResult { Succeeded = true, AccountsNeedingSignIn = [account.Id] },
            [account], creds,
            a => { reconnected.Add(a); return Task.FromResult(true); });
        var said = new List<string>();
        vm.AnnouncementRequested += (t, _) => said.Add(t);

        Assert.True(vm.PasswordNeeded);
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(["Enter the password for Home."], said);
        Assert.Empty(reconnected);

        vm.Password = "secret";
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal("secret", creds.Saved[account.Id]);
        Assert.Equal([account], reconnected);
        Assert.Equal(string.Empty, vm.Password);
        Assert.Equal("Home, signed in", vm.Rows[0].AccessibleName);
        vm.Dispose();
    }

    [Fact]
    public async Task SignIn_MicrosoftAccount_GoesStraightToReconnect()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), AccountName = "Work", AuthType = AuthType.OAuth2Microsoft };
        var reconnected = 0;
        var vm = new SettingsImportedViewModel(
            new SettingsImportResult { Succeeded = true, AccountsNeedingSignIn = [account.Id, Guid.NewGuid()] },
            [account], new RecordingCredentials(),
            _ => { reconnected++; return Task.FromResult(false); });

        Assert.Single(vm.Rows);   // an id with no account here is left out
        Assert.False(vm.PasswordNeeded);
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(1, reconnected);
        Assert.Equal("Work did not sign in. Check the password, or try again.", vm.StatusText);
        vm.Dispose();
    }

    [StaFact]
    public void ImportedWindow_LoadsAndNamesRowsOnTheContainer()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), AccountName = "Work", AuthType = AuthType.OAuth2Microsoft };
        var vm = new SettingsImportedViewModel(
            new SettingsImportResult { Succeeded = true, DeviceName = "DESKTOP", Lines = ["Accounts: 1 account added, 0 accounts updated."], AccountsNeedingSignIn = [account.Id] },
            [account], new RecordingCredentials(), _ => Task.FromResult(false));
        var window = Hidden(new SettingsImportedWindow(vm));
        try
        {
            window.Show();
            var report = window.FindName("ReportBox") as TextBox;
            var list = window.FindName("AccountList") as ListBox;
            Assert.NotNull(report);
            Assert.NotNull(list);
            Assert.NotNull(window.FindName("PasswordBox") as PasswordBox);
            Assert.NotNull(window.FindName("SignInButton") as Button);
            Assert.NotNull(window.FindName("CopyButton") as Button);
            Assert.NotNull(window.FindName("SignInPanel") as Panel);
            Assert.StartsWith("Settings imported from DESKTOP.", report.Text, StringComparison.Ordinal);

            list.UpdateLayout();
            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Assert.NotNull(row);
            Assert.Equal("Work, needs sign-in", System.Windows.Automation.AutomationProperties.GetName(row));
            account.IsConnected = true;
            Assert.Equal("Work, signed in", System.Windows.Automation.AutomationProperties.GetName(row));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SignIn_MovingToAnotherAccountClearsThePassword()
    {
        var a = new AccountModel { Id = Guid.NewGuid(), AccountName = "A", AuthType = AuthType.Password };
        var b = new AccountModel { Id = Guid.NewGuid(), AccountName = "B", AuthType = AuthType.Password };
        var vm = new SettingsImportedViewModel(
            new SettingsImportResult { Succeeded = true, AccountsNeedingSignIn = [a.Id, b.Id] },
            [a, b], new RecordingCredentials(), _ => Task.FromResult(true));
        var cleared = 0;
        vm.PasswordConsumed += () => cleared++;

        vm.Password = "for A";
        vm.SelectedRow = vm.Rows[1];

        Assert.Equal(string.Empty, vm.Password);
        Assert.Equal(1, cleared);
        vm.Dispose();
    }

    [Fact]
    public void ImportedWindowTitle_SaysHowItWent()
    {
        Assert.Equal("Settings Imported", new SettingsImportResult { Succeeded = true }.Title);
        Assert.Equal("Settings Not Imported", new SettingsImportResult { Succeeded = false }.Title);
        Assert.Equal("Local Calendar Imported", new SettingsImportResult { Succeeded = true, Heading = "x" }.Title);
    }

    [Fact]
    public async Task ExportVm_IsNotExportingOnceItIsDoneEvenAfterAFailure()
    {
        var vm = ExportVm();
        var changes = new List<bool>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExportSettingsViewModel.CanExportNow)) changes.Add(vm.CanExportNow); };
        await Assert.ThrowsAnyAsync<IOException>(() => vm.ExportAsync(Path.Combine(_root, "nope", "x.qmsettings")));
        Assert.False(vm.IsExporting);
        Assert.True(vm.CanExportNow);
        Assert.Equal([false, true], changes);   // disabled while writing, back after
    }

    [StaFact]
    public void ImportedWindow_NoAccountsHidesTheSignInPart()
    {
        var vm = new SettingsImportedViewModel(
            new SettingsImportResult { Succeeded = false, Lines = ["This file could not be read as a QuickMail settings file."] },
            [], new RecordingCredentials(), _ => Task.FromResult(false));
        var window = Hidden(new SettingsImportedWindow(vm));
        try
        {
            window.Show();
            var list = window.FindName("AccountList") as ListBox;
            Assert.NotNull(list);
            Assert.Equal(Visibility.Collapsed, list.Visibility);
            Assert.StartsWith("Settings were not imported.", vm.Report, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }
}
