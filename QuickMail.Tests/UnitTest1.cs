// Startup smoke tests — these are the tests that catch crashes before the app loads.
// They run on the GitHub Actions Windows runner and provide CI-level confidence
// that the app can at least initialise without throwing.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using QuickMail.Controls;
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
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2, new StubFeatureGate(), new ProviderCatalog());
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

    // (The no-invite empty-card case moved to EventCardRenderTests.Card_WithNoInvite_IsEmpty when
    // the card builder left MainViewModel; keeping a VM-constructing copy here only duplicated it.)

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
[Collection("WpfTests")]
public class XamlParseTests
{
    /// Ensure Application.Current exists and has the app's resource dictionaries loaded —
    /// required for StaticResource / DynamicResource resolution during XAML parsing.
    /// Uses a process-wide lock so that parallel [StaFact] threads from different test
    /// classes don't race to create a second Application (WPF forbids more than one).
    /// <summary>Delegates to the shared host: the Application must live on a thread that outlives
    /// the run, not on whichever [StaFact] thread happened to be first (issue #211).</summary>
    private static void EnsureApplication() => WpfTestHost.EnsureApplication();

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
    public void WatchedConversationsWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new WatchedConversationsViewModel(new StubWatchService(), new StubLocalStoreService());
        var window = new WatchedConversationsWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AddSharedMailboxWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        // Guards the {StaticResource BoolToVisibility} converter being declared locally — a build
        // compiles the XAML but the converter only resolves at parse time (#31).
        var vm = new AddSharedMailboxViewModel([new AccountModel { AccountName = "Work", BackendKind = BackendKind.MicrosoftGraph }]);
        var window = new AddSharedMailboxWindow(vm);
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
    public void RowFieldsWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());
        var window = new RowFieldsWindow(vm);
        Assert.NotNull(window);

        // The panes the F6 ring cycles must all exist by these names, or the cycle silently
        // strands focus. `as` + NotNull so a rename fails here, not with a NullReferenceException.
        foreach (var name in new[] { "RowTypeList", "FieldList", "OptionsPanel", "PreviewBox", "ButtonBar" })
            Assert.NotNull(window.FindName(name) as System.Windows.UIElement);

        window.Close();
    }

    [StaFact]
    public void ReportBugWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new ReportBugWindow(new StubBugReportService());
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void SpellCheckDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new SpellCheckDialogViewModel([], dictionary: null);
        var dialog = new SpellCheckDialog(vm);
        Assert.NotNull(dialog);
        dialog.Close();
    }

    [StaFact]
    public void InsertLinkDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var dialog = new InsertLinkDialog("display text");
        Assert.NotNull(dialog);
        dialog.Close();
    }

    [StaFact]
    public void AccountManagerDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var (imap, accounts, creds, _, _, _, _, _, _) = MakeServices();
        var (_, _, _, store2, _, config2, _, _, _) = MakeServices();
        var vm = new AccountManagerViewModel(accounts, creds, imap, new StubOAuthService(), store2, config2, new StubFeatureGate(), new ProviderCatalog());
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
        var vm = new AddAccountViewModel(gate, imap, new StubOAuthService(), new ProviderCatalog());
        var window = new AddAccountDialog(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void SettingsDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        var dialog = new SettingsDialog(vm);
        Assert.NotNull(dialog);
        dialog.Close();
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
    public void EventEditorWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 0, 0));
        var window = new EventEditorWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void EventEditorWindow_DateAndTimeFieldsResolveByName()
    {
        // The code-behind reaches these by name for the F6 ring and for focusing the field a
        // refused save blamed. Renaming one in XAML would otherwise fail only at runtime, and
        // silently — a broken binding writes to the debug trace and nowhere the user can see.
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 0, 0));
        var window = new EventEditorWindow(vm);

        foreach (var name in new[]
                 {
                     "StartDateField", "StartTimeField", "EndDateField", "EndTimeField",
                     "RepeatIntervalField", "RepeatUntilField",
                 })
        {
            var field = window.FindName(name) as DateTimeField;
            Assert.NotNull(field);
        }

        Assert.NotNull(window.FindName("ErrorLine") as System.Windows.Controls.TextBox);
        window.Close();
    }

    [StaFact]
    public void EventEditorWindow_DateAndTimeFieldsShowTheViewModelValue()
    {
        // Both faces of the same instant: the date field renders its date, the time field its
        // time. A binding that silently failed to resolve would leave these empty.
        EnsureApplication();
        var vm = new EventEditorViewModel(new DateTime(2026, 7, 16, 9, 0, 0));
        var window = new EventEditorWindow(vm);

        var startDate = window.FindName("StartDateField") as DateTimeField;
        var startTime = window.FindName("StartTimeField") as DateTimeField;
        Assert.NotNull(startDate);
        Assert.NotNull(startTime);

        Assert.Equal(vm.Start.ToString("D", CultureInfo.CurrentCulture), startDate!.Text);
        Assert.Equal(vm.Start.ToString("t", CultureInfo.CurrentCulture), startTime!.Text);
        window.Close();
    }

    [StaFact]
    public void GoToDateWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new GoToDateWindow(new GoToDateViewModel(new DateTime(2026, 7, 16)));
        Assert.NotNull(window);
        Assert.NotNull(window.FindName("DateField") as DateTimeField);
        window.Close();
    }

    [StaFact]
    public void ServerRuleEditorWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new ServerRuleEditorWindow(
            ServerRuleEditorViewModel.ForNew(),
            new List<AccountModel>(),
            new Dictionary<Guid, List<MailFolderModel>>());
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

    [StaFact]
    public void ForwardAttachmentDialogWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new ForwardAttachmentDialogViewModel([]);
        var window = new ForwardAttachmentDialogWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void ThemedControls_XamlParsesWithoutException()
    {
        EnsureApplication();
        var uri = new Uri("pack://application:,,,/QuickMail;component/Styles/ThemedControls.xaml", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };
        Assert.True(dict.Count > 0);
    }

    [StaFact]
    public void ThemeManagerWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new ThemeManagerViewModel(new StubThemeService(), new StubConfigService());
        var window = new ThemeManagerWindow(vm);
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
    public async Task CountSummariesByFolder_GroupsPerFolderForOneAccount()
    {
        // #462 sweep instrumentation: one grouped query yields every folder's cached count.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        MailMessageSummary Msg(string id, string folder) => new()
        {
            MessageId = id, AccountId = acct, FolderName = folder,
            From = "a@b.com", Subject = "s", Date = DateTimeOffset.UtcNow,
        };
        await store.UpsertSummariesAsync([Msg("1", "Inbox"), Msg("2", "Inbox"), Msg("3", "Archive")]);

        var counts = await store.CountSummariesByFolderAsync(acct);
        Assert.Equal(2, counts["Inbox"]);
        Assert.Equal(1, counts["Archive"]);
        Assert.False(counts.ContainsKey("Sent"));                                   // empty folder absent from the map
        Assert.Empty(await store.CountSummariesByFolderAsync(Guid.NewGuid()));       // different account → empty map
    }

    [Fact]
    public async Task CountMessagesByFolder_ReturnsTotalAndUnread_PerFolder()
    {
        // One grouped scan answers what the POP3 folder tree and Inbox status both ask for. Reading
        // it off LoadFolderReadStatesAsync instead means building a dictionary of every id in each
        // folder just to count it — a cost that grows with a store nothing prunes.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        MailMessageSummary Msg(string id, string folder, bool read) => new()
        {
            MessageId = id, AccountId = acct, FolderName = folder, IsRead = read,
            From = "a@b.com", Subject = "s", Date = DateTimeOffset.UtcNow,
        };
        await store.UpsertSummariesAsync([
            Msg("1", "Inbox", read: false), Msg("2", "Inbox", read: true), Msg("3", "Inbox", read: false),
            Msg("4", "Trash", read: true),
        ]);

        var counts = await store.CountMessagesByFolderAsync(acct);
        Assert.Equal((3, 2), counts["Inbox"]);
        Assert.Equal((1, 0), counts["Trash"]);
        Assert.False(counts.ContainsKey("Sent"));                                    // empty folder absent from the map
        Assert.Empty(await store.CountMessagesByFolderAsync(Guid.NewGuid()));         // different account → empty map
    }

    [Fact]
    public async Task LoadFolderSummariesSince_FiltersInSql_OnTheSameBoundaryTheCallerMeans()
    {
        // date_ticks is written as Date.UtcTicks, so the bound value has to be UtcTicks: a since
        // expressed in a local offset, compared as raw Ticks, silently shifts the window by the
        // offset — which for the POP3 sweep means either missing arrivals or re-announcing old mail.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct   = Guid.NewGuid();
        var cutoff = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MailMessageSummary Msg(string id, DateTimeOffset date) => new()
        {
            MessageId = id, AccountId = acct, FolderName = "Inbox", Date = date,
            From = "a@b.com", Subject = "s",
        };
        await store.UpsertSummariesAsync([
            Msg("older",  cutoff.AddMinutes(-1)),
            Msg("onTheBoundary", cutoff),
            Msg("newer",  cutoff.AddMinutes(1)),
        ]);

        var window = await store.LoadFolderSummariesSinceAsync(acct, "Inbox", cutoff);
        Assert.Equal(["newer", "onTheBoundary"], window.Select(m => m.MessageId));    // newest first, boundary included

        // The same instant written with a non-zero offset selects the same rows.
        var shifted = await store.LoadFolderSummariesSinceAsync(acct, "Inbox", cutoff.ToOffset(TimeSpan.FromHours(-5)));
        Assert.Equal(window.Select(m => m.MessageId), shifted.Select(m => m.MessageId));

        // Full loads are unaffected.
        Assert.Equal(3, (await store.LoadFolderSummariesAsync(acct, "Inbox")).Count);
    }

    [Fact]
    public async Task LoadFolderMessageStates_ReturnsIdDateAndReadState_ForTheWholeFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var acct = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.Zero);
        await store.UpsertSummariesAsync([
            new() { MessageId = "a", AccountId = acct, FolderName = "Inbox", Date = when, IsRead = true,  From = "x", Subject = "s" },
            new() { MessageId = "b", AccountId = acct, FolderName = "Inbox", Date = when, IsRead = false, From = "x", Subject = "s" },
            new() { MessageId = "c", AccountId = acct, FolderName = "Trash", Date = when, IsRead = false, From = "x", Subject = "s" },
        ]);

        var states = await store.LoadFolderMessageStatesAsync(acct, "Inbox");
        Assert.Equal(2, states.Count);
        Assert.True(states.Single(s => s.Id == "a").IsRead);
        Assert.False(states.Single(s => s.Id == "b").IsRead);
        Assert.Equal(when, states.Single(s => s.Id == "a").Date);
        Assert.Empty(await store.LoadFolderMessageStatesAsync(acct, "Sent"));
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