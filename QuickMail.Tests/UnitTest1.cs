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
    private static (StubImapMailService imap, StubAccountService accounts, StubCredentialService creds,
        StubLocalStoreService store, StubSyncService sync, StubConfigService config,
        StubCommandRegistry registry, StubContactService contacts, StubTemplateService templates)
        MakeServices()
    {
        return (new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubContactService(), new StubTemplateService());
    }

    [Fact]
    public void MainViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        Assert.NotNull(vm);
    }

    [Fact]
    public void MainViewModel_LoadAccountList_DoesNotThrow()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList(); // must not throw
    }

    [Fact]
    public void RulesManagerViewModel_ConstructsWithoutException()
    {
        var vm = new RulesManagerViewModel(new StubRuleService(), accounts: []);
        Assert.NotNull(vm);
    }

    [Fact]
    public void ComposeViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, _, _, _, _, _, templates) = MakeServices();
        var vm = new ComposeViewModel(new StubSmtpService(), accounts, creds, imap, templates);
        Assert.NotNull(vm);
    }

    [Fact]
    public void TemplatePickerViewModel_ConstructsWithoutException()
    {
        var (_, _, _, _, _, _, _, _, templates) = MakeServices();
        var vm = new TemplatePickerViewModel(templates);
        Assert.NotNull(vm);
    }

    [Fact]
    public void AccountManagerViewModel_ConstructsWithoutException()
    {
        var (imap, accounts, creds, _, _, _, _, _, _) = MakeServices();
        var (_, _, _, store2, _, config2, _, _, _) = MakeServices();
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2, new StubFeatureGate());
        Assert.NotNull(vm);
    }

    [Fact]
    public void GroupManagerViewModel_ConstructsWithoutException()
    {
        var (_, _, _, _, _, _, _, contacts, _) = MakeServices();
        var vm = new GroupManagerViewModel(contacts);
        Assert.NotNull(vm);
    }

    [Fact]
    public void AddressBookViewModel_HasGroupCollections_AfterConstruction()
    {
        var (_, _, _, _, _, _, _, contacts, _) = MakeServices();
        var vm = new AddressBookViewModel(contacts);
        Assert.NotNull(vm.Groups);
        Assert.Empty(vm.Groups);
        Assert.NotNull(vm.SelectedGroupMembers);
        Assert.Empty(vm.SelectedGroupMembers);
        Assert.False(vm.HasSelectedGroup);
        Assert.Equal(string.Empty, vm.NewGroupName);
    }

    [Fact]
    public void TutorialViewModel_ConstructsWithoutException()
    {
        var vm = new TutorialViewModel();
        Assert.NotNull(vm);
        Assert.Equal(6, vm.Steps.Count);
        Assert.False(vm.IsActive);
    }

    // ── Calendar invite tests ───────────────────────────────────────────────────

    [Fact]
    public void MainViewModel_HasCalendarInvite_IsFalseByDefault()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.False(vm.HasCalendarInvite);
    }

    [Fact]
    public void MainViewModel_BuildEventCardHtml_ReturnsEmptyWhenNoInvite()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        var html = vm.BuildEventCardHtml();

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public void MainViewModel_AcceptInviteCommand_Exists()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.NotNull(vm.AcceptInviteCommand);
        Assert.True(vm.AcceptInviteCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_DeclineInviteCommand_Exists()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.NotNull(vm.DeclineInviteCommand);
        Assert.True(vm.DeclineInviteCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_TentativeInviteCommand_Exists()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        Assert.NotNull(vm.TentativeInviteCommand);
        Assert.True(vm.TentativeInviteCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_InviteCommandsRegisteredInRegistry()
    {
        var (imap, accounts, creds, store, sync, config, registry, _, _) = MakeServices();
        // MainViewModel constructor calls RegisterCommands which registers invite commands
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());

        var acceptCmd = registry.FindById("mail.acceptInvite");
        var declineCmd = registry.FindById("mail.declineInvite");
        var tentativeCmd = registry.FindById("mail.tentativeInvite");

        Assert.NotNull(acceptCmd);
        Assert.Equal("Accept Invitation", acceptCmd!.Title);
        Assert.Equal("Mail", acceptCmd.Category);

        Assert.NotNull(declineCmd);
        Assert.Equal("Decline Invitation", declineCmd!.Title);
        Assert.Equal("Mail", declineCmd.Category);

        Assert.NotNull(tentativeCmd);
        Assert.Equal("Tentatively Accept Invitation", tentativeCmd!.Title);
        Assert.Equal("Mail", tentativeCmd.Category);
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
    /// Uses a process-wide lock so that parallel [StaFact] threads from different test
    /// classes don't race to create a second Application (WPF forbids more than one).
    private static void EnsureApplication()
    {
        // Lock on the Application type object — shared across all test classes.
        lock (typeof(Application))
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        // Merge the same resource dictionaries that App.xaml merges, so StaticResources
        // like ToolbarButton that are defined there are available to Window XAML.
        const string stylesUri = "pack://application:,,,/QuickMail;component/Styles/AccessibleStyles.xaml";
        var uri = new Uri(stylesUri, UriKind.Absolute);
        // Capture to local so nullable analysis knows it's non-null (it was just created above).
        var app = Application.Current!;
        if (app.Resources.MergedDictionaries.All(d => d.Source != uri))
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
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
        var (imap, accounts, creds, store, sync, config, registry, contacts, templates) = MakeServices();
        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(), sync, config, registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        // Constructing MainWindow triggers InitializeComponent() which is the real XAML parse.
        var window = new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, contacts, config, store, new StubViewService(), new StubRuleService(), templates, new StubFeatureGate());
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void ComposeWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, accounts, creds, _, _, config, _, contacts, templates) = MakeServices();
        var vm = new ComposeViewModel(new StubSmtpService(), accounts, creds, imap, templates);
        var window = new ComposeWindow(vm, contacts, templates, config);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AccountManagerDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, accounts, creds, _, _, _, _, _, _) = MakeServices();
        var (_, _, _, store2, _, config2, _, _, _) = MakeServices();
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2, new StubFeatureGate());
        var window = new AccountManagerDialog(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AddAccountDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, _, _, _, _, _, _, _, _) = MakeServices();
        // Gate ON so the backend combo and its bindings (AvailableBackends / SelectedBackend /
        // ShowBackendPicker / IsImapBackend) are exercised during the XAML parse.
        var gate = new StubFeatureGate { [FeatureFlag.GraphBackend] = true };
        var vm = new AddAccountViewModel(gate, imap, new StubOAuthService());
        var window = new AddAccountDialog(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AddressBookWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (_, _, _, _, _, _, _, contacts, _) = MakeServices();
        var vm = new AddressBookViewModel(contacts);
        var window = new AddressBookWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void GroupManagerWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (_, _, _, _, _, _, _, contacts, _) = MakeServices();
        var vm = new GroupManagerViewModel(contacts);
        var window = new GroupManagerWindow(vm);
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

    [StaFact]
    public void RulesManagerWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new RulesManagerViewModel(new StubRuleService(), accounts: []);
        var window = new RulesManagerWindow(vm, accounts: [], cachedFolders: new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.List<QuickMail.Models.MailFolderModel>>());
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void ViewManagerDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (_, _, _, _, _, config, registry, _, _) = MakeServices();
        var vm = new ViewManagerViewModel(
            new StubViewService(),
            config,
            registry,
            savedViews:      [],
            currentFolder:   null,
            currentAccount:  null,
            currentViewMode: QuickMail.Models.ViewMode.Messages,
            currentFilter:   QuickMail.Models.MessageFilter.All,
            currentSort:     QuickMail.Models.MessageSort.DateDescending);
        var window = new ViewManagerWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void TutorialOverlay_XamlParsesWithoutException()
    {
        EnsureApplication();
        var overlay = new TutorialOverlay();
        Assert.NotNull(overlay);
    }

    [StaFact]
    public void TemplatePickerWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (_, _, _, _, _, _, _, _, templates) = MakeServices();
        var vm = new TemplatePickerViewModel(templates);
        var window = new TemplatePickerWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void PropertiesWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new PropertiesViewModel("Test Properties", [
            new("Headers", [new("From", "alice@example.com")]),
            new("Storage", [new("Folder", "INBOX")]),
        ]);
        var window = new PropertiesWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    private static (StubImapMailService imap, StubAccountService accounts, StubCredentialService creds,
        StubLocalStoreService store, StubSyncService sync, StubConfigService config,
        StubCommandRegistry registry, StubContactService contacts, StubTemplateService templates)
        MakeServices()
    {
        return (new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubContactService(), new StubTemplateService());
    }
}

public class LocalStoreServiceTests
{
    [Fact]
    public async Task SummaryToField_PersistsAndLoads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var summary = new MailMessageSummary
        {
            MessageId = "42",
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

    [Fact]
    public async Task HasAttachments_PersistsAndLoads()
    {
        // Regression for §1.10: ReadSummariesAsync used to omit the has_attachments
        // column, so the attachment indicator was blank on cold start until each
        // message was opened individually.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var accountId = Guid.NewGuid();
        var summary = new MailMessageSummary
        {
            MessageId  = "7",
            AccountId  = accountId,
            FolderName = "Inbox",
            From       = "x@example.com",
            Subject    = "with attachment",
            Date       = DateTimeOffset.UtcNow,
        };
        await store.UpsertSummariesAsync([summary]);

        // UpsertDetailAsync flips the has_attachments flag when attachments are present.
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId   = "7",
            AccountId   = accountId,
            FolderName  = "Inbox",
            Attachments = new() { new AttachmentModel { FileName = "doc.pdf", ContentType = "application/pdf" } },
        });

        var loaded = await store.LoadAllSummariesAsync();
        Assert.Single(loaded);
        Assert.True(loaded[0].HasAttachments);

        var loaded2 = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.True(loaded2[0].HasAttachments);

        var loaded3 = await store.LoadAllSummariesAsync(accountId);
        Assert.True(loaded3[0].HasAttachments);
    }

    [Fact]
    public async Task DeleteSummariesAsync_RemovesAllRequestedIds()
    {
        // Covers §2.11: the chunked IN-list delete must remove every requested UID,
        // including across chunk boundaries (chunkSize = 500 internally).
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var accountId = Guid.NewGuid();
        var ids = Enumerable.Range(1, 1100).ToList(); // crosses two chunks
        var summaries = ids.Select(id => new MailMessageSummary
        {
            MessageId  = id.ToString(),
            AccountId  = accountId,
            FolderName = "Inbox",
            Subject    = $"msg{id}",
            Date       = DateTimeOffset.UtcNow,
        });
        await store.UpsertSummariesAsync(summaries);

        var toDelete = ids.Where(i => i % 2 == 0).Select(i => i.ToString()).ToList(); // 550 ids
        await store.DeleteSummariesAsync(accountId, "Inbox", toDelete);

        var remaining = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.Equal(550, remaining.Count);
        Assert.All(remaining, m => Assert.Equal(1, int.Parse(m.MessageId) % 2));
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        // §2.5: data migrations are gated on PRAGMA user_version, so calling
        // Initialize() multiple times must be safe and have no further effect.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();
        store.Initialize();
        store.Initialize();
        // No exception, no schema breakage.
    }

    [Fact]
    public async Task DeleteSummariesAsync_EmptyInput_NoOp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        // Must not throw on empty input.
        await store.DeleteSummariesAsync(Guid.NewGuid(), "Inbox", Array.Empty<string>());
    }

    [Fact]
    public async Task HasAttachments_DefaultsFalse_WhenNotSet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId  = "1",
            AccountId  = Guid.NewGuid(),
            FolderName = "Inbox",
            From       = "x@example.com",
            Subject    = "no attachment",
            Date       = DateTimeOffset.UtcNow,
        }]);

        var loaded = await store.LoadAllSummariesAsync();
        Assert.Single(loaded);
        Assert.False(loaded[0].HasAttachments);
    }
}