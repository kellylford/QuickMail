using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using QuickMail.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// XAML parse tests — verify every Window's XAML can be loaded without a
/// XamlParseException (bad StaticResource key, missing namespace, etc.).
/// Requires STA thread (via [StaFact]) but no visible window is shown.
/// A minimal Application is created once per process if needed.
/// </summary>
[Collection("WpfTests")]
public class XamlParseTests
{
    /// <summary>
    /// The Application and the resource dictionaries StaticResource/DynamicResource need while the
    /// XAML is parsed. The shared host owns it, on a thread that outlives the run rather than
    /// whichever [StaFact] thread happened to be first (issue #211).
    ///
    /// ThemedControls is named explicitly, not left to whichever other suite happens to merge it
    /// first: MainWindow's XAML resolves ListView's themed style, so parsing it fails with "Cannot
    /// find resource named 'System.Windows.Controls.ListView'" whenever this class runs without
    /// CalendarContextMenuTests or SelectorItemAccessibilityTests alongside it — which is what any
    /// `dotnet test --filter` narrow enough to exclude them does.
    /// </summary>
    private static void EnsureApplication() =>
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

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
    public void SoundSchemeNameDialog_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new SoundSchemeNameDialog("New Sound Scheme", "Calm", _ => null);
        Assert.Equal("New Sound Scheme", window.Title);
        Assert.Equal(60, (window.FindName("NameBox") as System.Windows.Controls.TextBox)?.MaxLength);
        window.Close();
    }

    [StaFact]
    public void SoundSchemeUpdateDialog_XamlParsesAndItsButtonsSayWhatTheyDo()
    {
        EnsureApplication();
        var question = new QuickMail.Services.SoundSchemeUpdateQuestion("Update Sound Scheme", "Update Rain with the gallery's newer version? …", OffersReplaceEverything: true);
        var window = new SoundSchemeUpdateDialog(question);
        Assert.Equal("Update Sound Scheme", window.Title);
        var add = window.FindName("AddMissingButton") as System.Windows.Controls.Button;
        var replace = window.FindName("ReplaceButton") as System.Windows.Controls.Button;
        var cancel = window.FindName("CancelButton") as System.Windows.Controls.Button;
        Assert.NotNull(add);
        Assert.NotNull(replace);
        Assert.NotNull(cancel);
        // Short labels; the explanation is the help text, read with the focused first button.
        Assert.Equal("Add only what's missing", System.Windows.Automation.AutomationProperties.GetName(add));
        Assert.Equal("Replace everything", System.Windows.Automation.AutomationProperties.GetName(replace));
        Assert.Equal("Cancel", System.Windows.Automation.AutomationProperties.GetName(cancel));
        Assert.Equal(question.Text, System.Windows.Automation.AutomationProperties.GetHelpText(add));
        // Only there, so it isn't read twice; the other buttons and the window carry none.
        Assert.Equal(string.Empty, System.Windows.Automation.AutomationProperties.GetHelpText(window));
        Assert.Equal(string.Empty, System.Windows.Automation.AutomationProperties.GetHelpText(replace));
        Assert.Equal(string.Empty, System.Windows.Automation.AutomationProperties.GetHelpText(cancel));
        // Sized to its text, and its buttons wrap rather than clip at large text sizes.
        Assert.Equal(System.Windows.SizeToContent.WidthAndHeight, window.SizeToContent);
        Assert.IsType<System.Windows.Controls.WrapPanel>(System.Windows.LogicalTreeHelper.GetParent(add));
        Assert.Equal(question.Text, (window.FindName("ExplanationText") as System.Windows.Controls.TextBlock)?.Text);
        Assert.True(add.IsDefault);
        Assert.True(cancel.IsCancel);   // Escape cancels
        // Nothing chosen is Cancel.
        Assert.Equal(QuickMail.Services.SoundSchemeUpdateChoice.Cancel, window.Choice);
        window.Close();
    }

    [StaFact]
    public void SoundSchemeUpdateDialog_EachButtonGivesItsChoice()
    {
        EnsureApplication();
        var question = new QuickMail.Services.SoundSchemeUpdateQuestion("Update Sound Scheme", "Text", OffersReplaceEverything: true);

        var add = new SoundSchemeUpdateDialog(question);
        (add.FindName("AddMissingButton") as System.Windows.Controls.Button)!
            .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(QuickMail.Services.SoundSchemeUpdateChoice.Apply, add.Choice);

        var replace = new SoundSchemeUpdateDialog(question);
        (replace.FindName("ReplaceButton") as System.Windows.Controls.Button)!
            .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(QuickMail.Services.SoundSchemeUpdateChoice.ReplaceEverything, replace.Choice);
    }

    [StaFact]
    public void SoundGalleryWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var window = new SoundGalleryWindow(new SoundGalleryViewModel(new SoundGalleryService(System.IO.Path.GetTempPath())));
        Assert.Equal("Sound Scheme Gallery", window.Title);
        Assert.NotNull(window.FindName("SchemeList") as System.Windows.Controls.ListBox);
        // The details are read-only text a cursor reads through, never editable (CLAUDE.md, Modal Dialog Rules).
        var details = window.FindName("DetailsBox") as System.Windows.Controls.TextBox;
        Assert.NotNull(details);
        Assert.True(details.IsReadOnly);
        Assert.Equal("Details", System.Windows.Automation.AutomationProperties.GetName(details));
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
        var vm = new AddSharedMailboxViewModel([new AccountModel { AccountName = "Work", Username = "work@work.com", BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft }]);
        var window = new AddSharedMailboxWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    [StaFact]
    public void AdminConsentWindow_XamlParsesWithoutException() // #607
    {
        EnsureApplication();
        // Parameterless: constructing it parses the XAML (WebView2 host + theme resources); the WebView2
        // async load is in Loaded, which does not fire on mere construction, so no runtime is needed.
        var window = new AdminConsentWindow();
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
    public void AdvancedSearchWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm = new AdvancedSearchViewModel([(Guid.NewGuid(), "Work"), (Guid.NewGuid(), "Home")], "Inbox");
        var window = new AdvancedSearchWindow(vm);
        Assert.NotNull(window.FindName("WordsBox") as System.Windows.Controls.TextBox);
        Assert.NotNull(window.FindName("FromDateField") as DateTimeField);
        Assert.NotNull(window.FindName("AccountsList") as System.Windows.Controls.ItemsControl);
        Assert.NotNull(window.FindName("CurrentFolderRadio") as System.Windows.Controls.RadioButton);
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
    public void UnifiedRulesWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        // The only rules window since #550, and until now no test constructed it at all. Constructed,
        // not shown: parsing is what this guards, and showing it would run its Loaded refresh.
        var vm = new UnifiedRulesViewModel(new StubRuleService(), serverRules: null, accounts: []);
        var window = new UnifiedRulesWindow(vm, accounts: [], cachedFolders: new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.List<QuickMail.Models.MailFolderModel>>());
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