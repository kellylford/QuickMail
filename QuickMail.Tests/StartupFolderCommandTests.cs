// Setting the startup folder from the folder tree — issue #516.
//
// The guard here deliberately differs from the move/copy one: a startup folder MAY be a top-level
// virtual aggregate, because "open me in All Inboxes" is the most-requested form of the setting.
// What it must still refuse is anything that is not a place mail lives — and it has to say why,
// since a context-menu item that silently does nothing is the dead end #250 was filed about.

using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class StartupFolderCommandTests
{
    private static readonly Guid WorkId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class AccountsStub(params AccountModel[] accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (MainViewModel Vm, StubConfigService Config) MakeVm()
    {
        var config = new StubConfigService();
        var vm = new MainViewModel(
            new StubImapMailService(),
            new AccountsStub(new AccountModel { Id = WorkId, AccountName = "Work" }),
            new StubCredentialService(), new StubLocalStoreService(), new StubOAuthService(),
            new StubSyncService(), config, new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList();
        return (vm, config);
    }

    private static FolderTreeNode Node(MailFolderModel folder, string label) =>
        new() { Folder = folder, Label = label };

    [Fact]
    public void SettingARealFolder_StoresItScopedToItsAccount()
    {
        var (vm, config) = MakeVm();
        var folder = new MailFolderModel
        {
            AccountId = WorkId, FullName = "INBOX/Projects", DisplayName = "Projects",
        };

        var message = vm.SetStartupFolder(Node(folder, "Projects"));

        var cfg = config.Load();
        Assert.Equal("INBOX/Projects", cfg.StartupFolder);
        Assert.Equal(WorkId.ToString(), cfg.StartupFolderAccount);
        Assert.Equal("Projects", cfg.StartupFolderLabel);
        Assert.Contains("Projects", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingAVirtualAggregate_StoresTheKeyWithoutTheSentinelPrefix()
    {
        // An INI file cannot carry a NUL, so the prefix is stripped on write.
        var (vm, config) = MakeVm();

        vm.SetStartupFolder(Node(MainViewModel.AllInboxesFolder, "All Inboxes"));

        var cfg = config.Load();
        Assert.Equal("AllInboxes", cfg.StartupFolder);
        Assert.Equal(string.Empty, cfg.StartupFolderAccount);
        Assert.Equal("All Inboxes", cfg.StartupFolderLabel);
    }

    [Theory]
    [InlineData("\u0000AllMail",  "All Mail")]
    [InlineData("\u0000AllSent",  "All Sent")]
    [InlineData("\u0000AllTrash", "All Trash")]
    [InlineData("\u0000AllWatched", "Watched Conversations")]
    public void EveryTopLevelAggregate_IsAcceptable(string fullName, string display)
    {
        var (vm, config) = MakeVm();
        var folder = new MailFolderModel { FullName = fullName, DisplayName = display };

        vm.SetStartupFolder(Node(folder, display));

        Assert.Equal(fullName[1..], config.Load().StartupFolder);
    }

    [Fact]
    public void AHeaderNode_IsRefusedWithAReason()
    {
        var (vm, config) = MakeVm();
        var header = new MailFolderModel { AccountId = WorkId, IsHeader = true, DisplayName = "Work" };

        var message = vm.SetStartupFolder(new FolderTreeNode { Folder = header, IsHeader = true, Label = "Work" });

        Assert.Equal(string.Empty, config.Load().StartupFolder);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void APerAccountAllMailSentinel_IsRefused_NotSilentlyStored()
    {
        // Carries a real AccountId but a \0-prefixed name no server would accept. Storing it would
        // produce a startup folder that can never resolve.
        var (vm, config) = MakeVm();
        var sentinel = new MailFolderModel
        {
            AccountId = WorkId, FullName = $"\u0000AccountMail:{WorkId}", DisplayName = "Work — All Mail",
        };

        var message = vm.SetStartupFolder(Node(sentinel, "Work — All Mail"));

        Assert.Equal(string.Empty, config.Load().StartupFolder);
        Assert.Contains("not a folder", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACalendarNode_IsRefusedWithACalendarSpecificReason()
    {
        var (vm, config) = MakeVm();
        var calendar = new MailFolderModel { FullName = "\u0000Calendar", DisplayName = "Calendar" };

        var message = vm.SetStartupFolder(Node(calendar, "Calendar"));

        Assert.Equal(string.Empty, config.Load().StartupFolder);
        Assert.Contains("calendar", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANullNode_IsRefusedWithAReason()
    {
        var (vm, config) = MakeVm();

        var message = vm.SetStartupFolder(null);

        Assert.Equal(string.Empty, config.Load().StartupFolder);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Clearing_RemovesAllThreeKeys()
    {
        var (vm, config) = MakeVm();
        vm.SetStartupFolder(Node(
            new MailFolderModel { AccountId = WorkId, FullName = "INBOX", DisplayName = "Inbox" }, "Inbox"));

        var message = vm.ClearStartupFolder();

        var cfg = config.Load();
        Assert.Equal(string.Empty, cfg.StartupFolder);
        Assert.Equal(string.Empty, cfg.StartupFolderAccount);
        Assert.Equal(string.Empty, cfg.StartupFolderLabel);
        Assert.Contains("Inbox", message, StringComparison.Ordinal);
        Assert.Contains("All Mail", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingWhenNothingIsSet_SaysSoRatherThanClaimingItCleared()
    {
        var (vm, _) = MakeVm();

        Assert.Contains("No startup folder is set", vm.ClearStartupFolder(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingReplacesAPreviousChoice_RatherThanAccumulating()
    {
        var (vm, config) = MakeVm();
        vm.SetStartupFolder(Node(
            new MailFolderModel { AccountId = WorkId, FullName = "INBOX", DisplayName = "Inbox" }, "Inbox"));

        vm.SetStartupFolder(Node(MainViewModel.AllInboxesFolder, "All Inboxes"));

        var cfg = config.Load();
        Assert.Equal("AllInboxes", cfg.StartupFolder);
        // The account must be cleared too, or the virtual key would be read as a real folder name.
        Assert.Equal(string.Empty, cfg.StartupFolderAccount);
    }
}
