// Startup folder resolution — issue #516.
//
// The point of the feature is that the chosen folder is on screen from the FIRST painted frame,
// loaded from the local store with no network. Applying it after connect is what these tests exist
// to prevent: that was the old default-saved-view behaviour, and the All Mail flash it produced is
// what users reported. So every test here asserts state after InitialLoadAsync alone — nothing in
// this file connects, syncs, or touches StartBackgroundSyncAsync.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MainViewModelStartupTests
{
    private static readonly Guid WorkId     = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PersonalId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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

    private static MailMessageSummary Msg(Guid account, string folder, string id, int daysAgo = 0) => new()
    {
        MessageId = id, AccountId = account, FolderName = folder,
        From = "someone@example.com", To = "kelly@example.com", Subject = $"Subject {id}",
        Date = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    /// <summary>Builds a VM over two accounts whose folders are already persisted, plus a message in
    /// each folder, and returns it with the config service so a test can set startup keys first.</summary>
    private static (MainViewModel Vm, StubConfigService Config, StubLocalStoreService Store) MakeVm(
        Action<ConfigModel>? configure = null, IViewService? views = null, bool onlineMode = false)
    {
        var store = new StubLocalStoreService();
        store.SeededFolders[WorkId] =
        [
            Folder(WorkId, "INBOX", "Inbox", SpecialFolderKind.Inbox),
            Folder(WorkId, "INBOX/Projects", "Projects"),
            Folder(WorkId, "Sent", "Sent", SpecialFolderKind.Sent),
        ];
        store.SeededFolders[PersonalId] =
        [
            Folder(PersonalId, "INBOX", "Inbox", SpecialFolderKind.Inbox),
        ];
        store.SeededSummaries[(WorkId, "INBOX")]          = [Msg(WorkId, "INBOX", "w-inbox", 1)];
        store.SeededSummaries[(WorkId, "INBOX/Projects")] = [Msg(WorkId, "INBOX/Projects", "w-proj", 2)];
        store.SeededSummaries[(WorkId, "Sent")]           = [Msg(WorkId, "Sent", "w-sent", 3)];
        store.SeededSummaries[(PersonalId, "INBOX")]      = [Msg(PersonalId, "INBOX", "p-inbox", 0)];

        var config = new StubConfigService();
        var cfg = config.Load();
        configure?.Invoke(cfg);
        config.Save(cfg);

        var accounts = new AccountsStub(
            new AccountModel { Id = WorkId,     AccountName = "Work",     Username = "k@work.example" },
            new AccountModel { Id = PersonalId, AccountName = "Personal", Username = "k@home.example" });

        var vm = new MainViewModel(
            new StubImapMailService(), accounts, new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), views ?? new StubViewService(), new StubRuleService(),
            new StubSmtpService(), onlineMode);
        vm.LoadAccountList();   // App does this before OnLoaded; the resolver scopes by account
        return (vm, config, store);
    }

    [Fact]
    public async Task NoStartupFolderConfigured_LandsInAllMail()
    {
        // The default, and what every existing install gets until it opts in.
        var (vm, _, _) = MakeVm();

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task RestoredFolderCache_PopulatesTheTreeBeforeAnythingConnects()
    {
        // The prerequisite for everything else here: no connect has happened, yet the real folders
        // are present. Before #516 the tree held only the virtual aggregates at this point.
        var (vm, _, _) = MakeVm();

        await vm.InitialLoadAsync();

        Assert.Contains(vm.Folders, f => f.AccountId == WorkId && f.FullName == "INBOX/Projects");
        Assert.Equal(3, vm.CachedFolders[WorkId].Count);
    }

    [Fact]
    public async Task RealStartupFolder_IsSelectedAndLoadedFromCache()
    {
        var (vm, _, _) = MakeVm(c =>
        {
            c.StartupFolder        = "INBOX/Projects";
            c.StartupFolderAccount = WorkId.ToString();
            c.StartupFolderLabel   = "Projects";
        });

        await vm.InitialLoadAsync();

        Assert.Equal("INBOX/Projects", vm.SelectedFolder?.FullName);
        Assert.Equal(WorkId, vm.SelectedFolder?.AccountId);
        Assert.Equal(["w-proj"], vm.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task VirtualStartupFolder_AllInboxes_UnionsEveryAccountsInbox()
    {
        // The most-requested choice, and the one that needs persisted folder KINDS to resolve —
        // without them nothing offline can tell which folder is an Inbox.
        var (vm, _, _) = MakeVm(c =>
        {
            c.StartupFolder      = "AllInboxes";
            c.StartupFolderLabel = "All Inboxes";
        });

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllInboxesFolder.FullName, vm.SelectedFolder?.FullName);
        Assert.Equal(["p-inbox", "w-inbox"], vm.Messages.Select(m => m.MessageId));   // newest first
    }

    [Fact]
    public async Task DeletedAccount_FallsBackToAllMail_AndSaysWhy()
    {
        var (vm, _, _) = MakeVm(c =>
        {
            c.StartupFolder        = "INBOX";
            c.StartupFolderAccount = Guid.NewGuid().ToString();   // never configured
            c.StartupFolderLabel   = "Inbox";
        });

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
        Assert.Contains("no longer set up", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenamedFolder_FallsBackToAllMail_AndNamesTheFolder()
    {
        // Silently showing All Mail reads as "the setting was ignored"; the user cannot tell which.
        var (vm, _, _) = MakeVm(c =>
        {
            c.StartupFolder        = "INBOX/GoneAway";
            c.StartupFolderAccount = WorkId.ToString();
            c.StartupFolderLabel   = "GoneAway";
        });

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
        Assert.Contains("GoneAway", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("All Mail", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GarbageStartupFolder_FallsBackToAllMail_WithoutThrowing()
    {
        var (vm, _, _) = MakeVm(c => c.StartupFolder = "NotAThingAtAll");

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task StartupFolderNamingAnotherAccountsFolder_DoesNotResolve()
    {
        // Folder names collide across accounts ("INBOX" everywhere). The stored account scopes it,
        // and a mismatch must fall back rather than silently open the wrong account's folder.
        var (vm, _, _) = MakeVm(c =>
        {
            c.StartupFolder        = "INBOX/Projects";     // exists, but on Work
            c.StartupFolderAccount = PersonalId.ToString();
            c.StartupFolderLabel   = "Projects";
        });

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task OnlineMode_StillHonoursTheStartupFolder()
    {
        // --online skips the local store entirely, and the old default-view application sat behind
        // an early return on this path, so the setting was ignored there. It is not any more.
        var (vm, _, _) = MakeVm(c =>
        {
            c.StartupFolder        = "INBOX/Projects";
            c.StartupFolderAccount = WorkId.ToString();
            c.StartupFolderLabel   = "Projects";
        }, onlineMode: true);

        await vm.InitialLoadAsync();

        Assert.Equal("INBOX/Projects", vm.SelectedFolder?.FullName);
        Assert.Equal("Projects", vm.SelectedFolder?.DisplayName);   // label carries Graph's opaque ids
    }

    [Fact]
    public async Task OnlineMode_VirtualStartupFolder_ResolvesToTheSentinel()
    {
        var (vm, _, _) = MakeVm(c => c.StartupFolder = "AllInboxes", onlineMode: true);

        await vm.InitialLoadAsync();

        Assert.Equal(MainViewModel.AllInboxesFolder.FullName, vm.SelectedFolder?.FullName);
    }
}
