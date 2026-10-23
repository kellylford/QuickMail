// Issue #451: aggregate-view rows shown before the folder list is known get their source-folder label
// once the folder list arrives, in place, without the user navigating away and back.
//
// The startup All Mail comes up from the message cache. When the cache holds rows that the persisted
// folder list does not cover, the folder lookup misses and the rows are correctly left unlabelled,
// never given a raw backend id. That happens on the first launch after upgrading from a version
// before #516 (0.8.38 or earlier), which saved no folder list, or after a folder save that failed or
// was cut off. A folder renamed on the server (Graph keeps the id, the display name changes) leaves
// rows with a stale label the same way. Before #451 nothing labelled them again when the folders
// arrived, and FolderDisplayName raised no change notification, so even a re-stamp could not have
// reached the row's accessible name.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class FolderLabelRestampTests
{
    private static readonly Guid WorkId     = Guid.Parse("45145145-0000-0000-0000-000000000001");
    private static readonly Guid PersonalId = Guid.Parse("45145145-0000-0000-0000-000000000002");

    // Graph-style opaque ids: the thing a row must never announce.
    private const string WorkInboxId     = "AAMkWork-inbox-id";
    private const string PersonalInboxId = "AAMkPersonal-inbox-id";
    private const string WorkProjectsId  = "AAMkWork-projects-id";

    private sealed class AccountsStub(params AccountModel[] accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MailFolderModel Folder(Guid account, string full, string display,
                                          SpecialFolderKind kind = SpecialFolderKind.None) => new()
    {
        AccountId = account, FullName = full, DisplayName = display, Kind = kind,
    };

    private static MailMessageSummary Msg(Guid account, string folder, string id, int daysAgo) => new()
    {
        MessageId = id, AccountId = account, FolderName = folder,
        From = "someone@example.com", To = "kelly@example.com", Subject = $"Subject {id}",
        Date = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private static Dictionary<Guid, List<MailFolderModel>> ServerFolders() => new()
    {
        [WorkId] =
        [
            Folder(WorkId, WorkInboxId, "Inbox", SpecialFolderKind.Inbox),
            Folder(WorkId, WorkProjectsId, "Projects"),
        ],
        [PersonalId] = [Folder(PersonalId, PersonalInboxId, "Inbox", SpecialFolderKind.Inbox)],
    };

    /// <summary>
    /// A VM over two accounts with cached messages. <paramref name="persistFolders"/> false is the
    /// #451 case: no folder list on disk, so the startup view has nothing to label rows with.
    /// The server's folder list is the returned dictionary — mutate it to simulate a rename.
    /// </summary>
    private static (MainViewModel Vm, Dictionary<Guid, List<MailFolderModel>> Server) MakeVm(
        bool persistFolders, Action<ConfigModel>? configure = null, bool includeOrphanRow = false)
    {
        var store = new StubLocalStoreService();
        if (persistFolders)
            foreach (var (id, folders) in ServerFolders())
                store.SeededFolders[id] = folders;

        store.SeededSummaries[(WorkId, WorkInboxId)]         = [Msg(WorkId, WorkInboxId, "w-inbox", 1)];
        store.SeededSummaries[(WorkId, WorkProjectsId)]      = [Msg(WorkId, WorkProjectsId, "w-proj", 2)];
        store.SeededSummaries[(PersonalId, PersonalInboxId)] = [Msg(PersonalId, PersonalInboxId, "p-inbox", 0)];
        if (includeOrphanRow)
            // A row whose folder the server never lists: it must stay unlabelled, not take its raw id.
            store.SeededSummaries[(WorkId, "AAMkWork-orphan-id")] = [Msg(WorkId, "AAMkWork-orphan-id", "w-orphan", 3)];

        var config = new StubConfigService();
        var cfg = config.Load();
        configure?.Invoke(cfg);
        config.Save(cfg);

        var accounts = new AccountsStub(
            new AccountModel { Id = WorkId,     AccountName = "Work",     Username = "k@work.example",
                               AuthType = AuthType.OAuth2Microsoft },
            new AccountModel { Id = PersonalId, AccountName = "Personal", Username = "k@home.example",
                               AuthType = AuthType.OAuth2Microsoft });

        var server = ServerFolders();
        var vm = new MainViewModel(
            new FolderedMailService(server, []), accounts, new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService());
        vm.LoadAccountList();
        return (vm, server);
    }

    /// <summary>Records every FolderDisplayName change on the given rows, keyed by message id.</summary>
    private static List<string> WatchLabels(IEnumerable<MailMessageSummary> rows)
    {
        var changes = new List<string>();
        foreach (var row in rows)
            row.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MailMessageSummary.FolderDisplayName))
                    changes.Add(((MailMessageSummary)s!).MessageId);
            };
        return changes;
    }

    private static MailMessageSummary Row(IEnumerable<MailMessageSummary> rows, string id)
        => rows.Single(m => m.MessageId == id);

    [Fact]
    public void FolderDisplayName_RaisesPropertyChanged_OnlyWhenTheValueChanges()
    {
        var row = new MailMessageSummary();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.FolderDisplayName = "Work -- Inbox";
        row.FolderDisplayName = "Work -- Inbox";   // same value: the accessible name has nothing new to say

        Assert.Equal([nameof(MailMessageSummary.FolderDisplayName)], raised);
    }

    [Fact]
    public async Task StartupWithoutPersistedFolders_RowsAreUnlabelled_ThenLabelledWhenFoldersArrive()
    {
        var (vm, _) = MakeVm(persistFolders: false);

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
        var shown = vm.Messages.ToList();
        Assert.Equal(3, shown.Count);
        // The precondition the issue describes: nothing to label with yet, and never the raw id.
        Assert.All(shown, m => Assert.Equal(string.Empty, m.FolderDisplayName));
        var changes = WatchLabels(shown);

        await vm.ConnectAllAccountsAsync();

        // The very row objects the list is showing are relabelled in place — two accounts, so the
        // global All Mail qualifies each with its account.
        Assert.Equal("Work -- Inbox",     Row(shown, "w-inbox").FolderDisplayName);
        Assert.Equal("Work -- Projects",  Row(shown, "w-proj").FolderDisplayName);
        Assert.Equal("Personal -- Inbox", Row(shown, "p-inbox").FolderDisplayName);
        // And each told its binding, which is what reaches the row's accessible name.
        Assert.Equal(["p-inbox", "w-inbox", "w-proj"], changes.Order());
        // Whatever the list shows now carries the label too.
        Assert.All(vm.Messages, m => Assert.NotEqual(string.Empty, m.FolderDisplayName));
    }

    [Fact]
    public async Task RowWhoseFolderIsNeverListed_StaysUnlabelled_NeverTheRawId()
    {
        var (vm, _) = MakeVm(persistFolders: false, includeOrphanRow: true);
        await vm.InitialLoadAsync();
        var orphan = Row(vm.Messages, "w-orphan");
        var changes = WatchLabels([orphan]);

        await vm.ConnectAllAccountsAsync();

        Assert.Equal(string.Empty, orphan.FolderDisplayName);
        Assert.Empty(changes);
        Assert.All(vm.Messages, m => Assert.DoesNotContain("AAMk", m.FolderDisplayName));
    }

    [Fact]
    public async Task FolderRenamed_RelabelsTheDisplayedRows()
    {
        var (vm, server) = MakeVm(persistFolders: true);
        await vm.InitialLoadAsync();
        var row = Row(vm.Messages, "w-proj");
        Assert.Equal("Work -- Projects", row.FolderDisplayName);   // persisted folders: labelled at once
        var changes = WatchLabels([row]);

        server[WorkId] =
        [
            Folder(WorkId, WorkInboxId, "Inbox", SpecialFolderKind.Inbox),
            Folder(WorkId, WorkProjectsId, "Client work"),
        ];
        await vm.RefreshFolderListAsync(WorkId);

        Assert.Equal("Work -- Client work", row.FolderDisplayName);
        Assert.Equal(["w-proj"], changes);
    }

    [Fact]
    public async Task RowOnlyInTheDisplayedList_IsRelabelledToo()
    {
        // The phase-2 server fetches of All Mail, per-account All Mail and saved views insert their rows
        // straight into Messages without adding them to the raw list. Those rows are on screen as well.
        var (vm, _) = MakeVm(persistFolders: false);
        await vm.InitialLoadAsync();
        var fromServer = Msg(WorkId, WorkProjectsId, "w-proj-server", 0);
        vm.Messages.Add(fromServer);
        Assert.DoesNotContain(vm.LoadedMessages, m => ReferenceEquals(m, fromServer));
        var changes = WatchLabels([fromServer]);

        await vm.ConnectAllAccountsAsync();

        Assert.Equal("Work -- Projects", fromServer.FolderDisplayName);
        Assert.Equal(["w-proj-server"], changes);
    }

    [Fact]
    public async Task RowsLeftFromThePreviousView_AreNotRelabelled()
    {
        // The user has moved to another view whose load has not landed yet: the rows still held were
        // stamped for All Mail, not for the selected view, and a folder-list rebuild must leave them alone.
        var (vm, _) = MakeVm(persistFolders: false);
        await vm.InitialLoadAsync();
        var shown = vm.Messages.ToList();
        Assert.All(shown, m => Assert.Equal(string.Empty, m.FolderDisplayName));
        var changes = WatchLabels(shown);

        vm.SelectedFolder = MainViewModel.AllInboxesFolder;   // selection moved; no load has run
        await vm.ConnectAllAccountsAsync();

        Assert.All(shown, m => Assert.Equal(string.Empty, m.FolderDisplayName));
        Assert.Empty(changes);
    }

    [Fact]
    public async Task UnchangedFolderList_RaisesNothing()
    {
        // A folder-list rebuild with the same names must not make every row re-announce its name.
        var (vm, _) = MakeVm(persistFolders: true);
        await vm.InitialLoadAsync();
        var shown = vm.Messages.ToList();
        Assert.All(shown, m => Assert.NotEqual(string.Empty, m.FolderDisplayName));
        var changes = WatchLabels(shown);

        await vm.RefreshFolderListAsync(WorkId);
        await vm.RefreshFolderListAsync(PersonalId);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task SingleFolderView_IsNotLabelled_WhenFoldersArrive()
    {
        // A real folder implies its own name; its rows carry no label, before or after a folder refresh.
        var (vm, _) = MakeVm(persistFolders: true, c =>
        {
            c.StartupFolder        = WorkProjectsId;
            c.StartupFolderAccount = WorkId.ToString();
            c.StartupFolderLabel   = "Projects";
        });
        await vm.InitialLoadAsync();
        Assert.Equal(WorkProjectsId, vm.SelectedFolder?.FullName);
        var shown = vm.Messages.ToList();
        Assert.Single(shown);

        await vm.RefreshFolderListAsync(WorkId);

        Assert.Equal(string.Empty, shown[0].FolderDisplayName);
    }
}
