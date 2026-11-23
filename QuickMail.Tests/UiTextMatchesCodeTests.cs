using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #1010: places where the app's own text promised something the code did not do. Ctrl+Enter opens a
/// new window, the Flagged filters are commands, and a message window can Move to Archive. (The
/// tab-close confirmation that did nothing is removed; WindowingPreferencesTests covers that.)
/// </summary>
public class UiTextMatchesCodeTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }

    private static string Source(string folder, string sub, string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), folder, sub, file));

    // ── 1. Ctrl+Enter ────────────────────────────────────────────────────────────

    [Fact]
    public void CtrlEnter_IsOpenInNewWindowsDefaultKey_AsSettingsSays()
    {
        var settings = Source("QuickMail", "Views", "SettingsDialog.xaml");
        Assert.Contains("In the other modes, Ctrl+Enter in the message list opens one in a new window too.", settings, StringComparison.Ordinal);

        // The registration, read from source as KeyboardShortcutsDocTests does: MainWindow cannot be stood up here.
        var main = Source("QuickMail", "Views", "MainWindow.xaml.cs");
        var registration = Regex.Match(main,
            @"id:\s*""mail\.openInWindow"".*?defaultKey:\s*Key\.(\w+),\s*defaultModifiers:\s*ModifierKeys\.(\w+)",
            RegexOptions.Singleline);
        Assert.True(registration.Success, "mail.openInWindow has no default key.");
        Assert.Equal("Return", registration.Groups[1].Value);
        Assert.Equal("Control", registration.Groups[2].Value);
    }

    [Fact]
    public void CtrlEnter_OnlyActsFromTheMessageArea_AndNotOnOutboxOrDraftRows()
    {
        // Dispatched by the window before any control sees the key, so without these it took Ctrl+Enter
        // from the folder tree, the account list and text boxes, and opened Outbox rows in a window.
        var main = Source("QuickMail", "Views", "MainWindow.xaml.cs");
        var registration = Regex.Match(main, @"id:\s*""mail\.openInWindow"".*?isAvailable:([^;]*?)\)\);", RegexOptions.Singleline);
        Assert.True(registration.Success);
        var available = registration.Groups[1].Value;
        Assert.Contains("IsMessageAreaFocused()", available, StringComparison.Ordinal);
        Assert.Contains("!_vm.IsSelectedFolderOutbox", available, StringComparison.Ordinal);
        Assert.Contains("!_vm.IsSelectedFolderDrafts", available, StringComparison.Ordinal);

        var xaml = Source("QuickMail", "Views", "MainWindow.xaml");
        Assert.Contains("sc:CommandShortcut.Id=\"mail.openInWindow\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOtherMainWindowCommand_HasCtrlEnter()
    {
        var sources = Source("QuickMail", "Views", "MainWindow.xaml.cs") + Source("QuickMail", "ViewModels", "MainViewModel.cs");
        var hits = Regex.Matches(sources,
            @"defaultKey:\s*Key\.(?:Return|Enter),\s*defaultModifiers:\s*ModifierKeys\.Control\s*[,)]");
        Assert.Single(hits);
    }

    // ── 4. The Flagged filters are commands ──────────────────────────────────────

    private static (MainViewModel Vm, CommandRegistry Registry) FlagVm()
    {
        var registry = new CommandRegistry();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), registry, new StubViewService(),
            new StubRuleService(), new StubSmtpService(),
            flagService: new StubFlagService());
        return (vm, registry);
    }

    [Fact]
    public void AllFlagged_IsACommand()
    {
        var (vm, registry) = FlagVm();
        var cmd = registry.FindById("view.filterFlagged");
        Assert.NotNull(cmd);
        Assert.Equal("View", cmd.Category);

        cmd.Execute();

        Assert.Equal(MessageFilter.Flagged, vm.ActiveFilter);
        Assert.Null(vm.ActiveFlagFilterId);
    }

    [Fact]
    public async Task EachFlag_IsACommand_OnceTheFlagsLoad()
    {
        var (vm, registry) = FlagVm();
        await vm.InitialLoadAsync();

        var id = MainViewModel.FlagFilterCommandPrefix + FlagDefinition.BuiltInFlagId;
        var cmd = registry.FindById(id);
        Assert.NotNull(cmd);
        Assert.Equal("Show Flagged: Flagged", cmd.Title);
        Assert.Equal("View", cmd.Category);

        cmd.Execute();

        Assert.Equal(MessageFilter.Flagged, vm.ActiveFilter);
        Assert.Equal(FlagDefinition.BuiltInFlagId.ToString(), vm.ActiveFlagFilterId);
    }

    [Fact]
    public void FlagCommands_FollowTheFlags_RenamedAndDeleted()
    {
        var (vm, registry) = FlagVm();
        var urgent = new FlagDefinition { Name = "Urgent", SortOrder = 1 };
        var later  = new FlagDefinition { Name = "Later", SortOrder = 2 };
        vm.FlagDefinitions.Add(urgent);
        vm.FlagDefinitions.Add(later);
        vm.RegisterFlagFilterCommands();

        urgent.Name = "Today";
        vm.FlagDefinitions.Remove(later);
        vm.RegisterFlagFilterCommands();

        var flagCommands = registry.GetAll()
            .Where(c => c.Id.StartsWith(MainViewModel.FlagFilterCommandPrefix, StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(flagCommands);
        Assert.Equal(MainViewModel.FlagFilterCommandPrefix + urgent.Id, only.Id);   // same id, so a key stays with it
        Assert.Equal("Show Flagged: Today", only.Title);
        Assert.NotNull(registry.FindById("view.filterFlagged"));                     // the prefix does not catch it
    }

    // ── 5. Move to Archive in a message window ───────────────────────────────────

    [Fact]
    public void MessageWindowArchive_RunsTheWindowsArchiveAction()
    {
        var vm = new MessageWindowViewModel();
        var archived = 0;
        vm.ArchiveAction = () => archived++;

        vm.ArchiveMessageCommand.Execute(null);

        Assert.Equal(1, archived);
    }

    [Fact]
    public void MessageWindowArchive_TakesTheMainWindowsKey()
    {
        Assert.Equal("mail.archive", MessageWindow.MainCommandIds["message.archive"]);

        var local = MessageWindow.TranslateMainOverrides(
            [new HotkeyBinding { CommandId = "mail.archive", Gesture = "Ctrl+Alt+A" }]);
        Assert.Equal(("message.archive", "Ctrl+Alt+A"), (Assert.Single(local).CommandId, local[0].Gesture));

        var xaml = Source("QuickMail", "Views", "MessageWindow.xaml");
        Assert.Contains("sc:CommandShortcut.Id=\"message.archive\"", xaml, StringComparison.Ordinal);
    }

    private static async Task<MainViewModel> ArchiveVmAsync(bool withArchive)
    {
        var workFolders = new List<MailFolderModel>
        {
            new() { AccountId = Work, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
        };
        if (withArchive)
            workFolders.Add(new MailFolderModel { AccountId = Work, FullName = "Archive", DisplayName = "Archive", Kind = SpecialFolderKind.Archive });
        var folders = new Dictionary<Guid, List<MailFolderModel>> { [Work] = workFolders };
        var vm = new MainViewModel(
            new FolderedMailService(folders, []), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new CommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        vm.Accounts.Add(new AccountModel { Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft });
        await vm.ConnectAllAccountsAsync();
        return vm;
    }

    private static MailMessageSummary In(string folder) =>
        new() { MessageId = "1", AccountId = Work, FolderName = folder, Subject = "Hello" };

    [Fact]
    public async Task MessageWindowArchive_SaysWhyWhenThereIsNoArchiveFolder()
    {
        var vm = await ArchiveVmAsync(withArchive: false);
        Assert.Equal(MainViewModel.NoArchiveFolderText, vm.ArchiveRefusalFor(In("INBOX")));
    }

    [Fact]
    public async Task MessageWindowArchive_SaysWhenTheMessageIsAlreadyArchived()
    {
        var vm = await ArchiveVmAsync(withArchive: true);
        Assert.Equal(MainViewModel.AlreadyArchivedText, vm.ArchiveRefusalFor(In("Archive")));
    }

    [Fact]
    public async Task MessageWindowArchive_RefusesAnOutboxRow()
    {
        var vm = await ArchiveVmAsync(withArchive: true);
        Assert.Equal(MainViewModel.OutboxRowHint, vm.ArchiveRefusalFor(In(MainViewModel.OutboxFolder.FullName)));
    }

    [Fact]
    public async Task MessageWindowArchive_GoesAheadOtherwise()
    {
        var vm = await ArchiveVmAsync(withArchive: true);
        Assert.Null(vm.ArchiveRefusalFor(In("INBOX")));
    }
}
