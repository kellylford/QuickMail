// Startup smoke tests — these are the tests that catch crashes before the app loads.
// They run on the GitHub Actions Windows runner and provide CI-level confidence
// that the app can at least initialise without throwing.

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests that can run without a display (no WPF window shown).
/// These cover ViewModel construction — the most common crash site.
/// </summary>
public class ViewModelConstructionTests
{
    private static (StubImapService imap, StubAccountService accounts, StubCredentialService creds,
        StubLocalStoreService store, StubSyncService sync, StubConfigService config,
        StubCommandRegistry registry, StubContactService contacts)
        MakeServices()
    {
        return (new StubImapService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubContactService());
    }

    [Fact]
    public void MainViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, store, sync, config, registry, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry);
        Assert.NotNull(vm);
    }

    [Fact]
    public void MainViewModel_LoadAccountList_DoesNotThrow()
    {
        var (imap, accounts, creds, store, sync, config, registry, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry);
        vm.LoadAccountList(); // must not throw
    }

    [Fact]
    public void ComposeViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, _, _, _, _, _) = MakeServices();
        var vm = new ComposeViewModel(new StubSmtpService(), accounts, creds, imap);
        Assert.NotNull(vm);
    }

    [Fact]
    public void AccountManagerViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, _, _, _, _, _) = MakeServices();
        var (_, _, _, store2, _, config2, _, _) = MakeServices();
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2);
        Assert.NotNull(vm);
    }
}

/// <summary>
/// XAML parse tests — verify every Window's XAML can be loaded without a
/// XamlParseException (bad StaticResource key, missing namespace, etc.).
/// Requires STA thread (via [StaFact]) but no visible window is shown.
/// A minimal Application is created once per process if needed.
/// </summary>
public class XamlParseTests
{
    /// Ensure Application.Current exists and has the app's resource dictionaries loaded —
    /// required for StaticResource / DynamicResource resolution during XAML parsing.
    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        // Merge the same resource dictionaries that App.xaml merges, so StaticResources
        // like ToolbarButton that are defined there are available to Window XAML.
        const string stylesUri = "pack://application:,,,/QuickMail;component/Styles/AccessibleStyles.xaml";
        var uri = new Uri(stylesUri, UriKind.Absolute);
        if (Application.Current.Resources.MergedDictionaries.All(d => d.Source != uri))
        {
            Application.Current.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = uri });
        }
    }

    private static void ParseXamlFile(string relativePathFromAssembly)
    {
        EnsureApplication();

        var asm = Assembly.GetAssembly(typeof(MainWindow))!;
        // XAML is embedded as a resource; the resource name mirrors the project path.
        var resourceName = relativePathFromAssembly.Replace('/', '.').Replace('\\', '.');
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"XAML resource '{resourceName}' not found in assembly. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        // XamlReader.Load triggers full XAML parsing including StaticResource resolution.
        var _ = XamlReader.Load(stream);
    }

    [StaFact]
    public void MainWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, accounts, creds, store, sync, config, registry, contacts) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry);
        // Constructing MainWindow triggers InitializeComponent() which is the real XAML parse.
        var window = new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, contacts, config, store);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void ComposeWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, accounts, creds, _, _, _, _, contacts) = MakeServices();
        var vm = new ComposeViewModel(new StubSmtpService(), accounts, creds, imap);
        var window = new ComposeWindow(vm, contacts);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AccountManagerDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, accounts, creds, _, _, _, _, _) = MakeServices();
        var (_, _, _, store2, _, config2, _, _) = MakeServices();
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2);
        var window = new AccountManagerDialog(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AddressBookWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (_, _, _, _, _, _, _, contacts) = MakeServices();
        var vm = new AddressBookViewModel(contacts);
        var window = new AddressBookWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void FolderPickerWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new FolderPickerWindow(
            accounts: [],
            cachedFolders: new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.List<QuickMail.Models.MailFolderModel>>());
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void NewFolderDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new NewFolderDialog();
        Assert.NotNull(window);
        window.Close();
    }

    private static (StubImapService imap, StubAccountService accounts, StubCredentialService creds,
        StubLocalStoreService store, StubSyncService sync, StubConfigService config,
        StubCommandRegistry registry, StubContactService contacts)
        MakeServices()
    {
        return (new StubImapService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubContactService());
    }
}

public class LocalStoreServiceTests
{
    [Fact]
    public async Task SummaryToField_PersistsAndLoads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store = new LocalStoreService(tempDir);
        store.Initialize();

        var summary = new MailMessageSummary
        {
            UniqueId = 42,
            AccountId = Guid.NewGuid(),
            FolderName = "Inbox",
            From = "Sender <sender@example.com>",
            To = "Long Recipient Name <recipient@example.com>",
            Subject = "Test subject",
            Date = DateTimeOffset.UtcNow,
            Preview = "Preview",
        };

        await store.UpsertSummariesAsync([summary]);
        var loaded = await store.LoadAllSummariesAsync();

        Assert.Single(loaded);
        Assert.Equal(summary.To, loaded[0].To);
    }
}