using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Move Folder and Copy Folder to the top level of the account (#954), through the view model:
/// what reaches the backend, and what the user is told. A null destination is the top level. A
/// folder already there is reported and nothing is sent — a Microsoft 365 copy beside the original
/// would otherwise make a duplicate, and IMAP refuses a rename to the same name.
/// </summary>
public class FolderMoveToTopLevelTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private static MailFolderModel Folder(string fullName, string display) => new()
    {
        AccountId = AccountId, FullName = fullName, DisplayName = display,
    };

    private static async Task<(MainViewModel Vm, FolderedMailService Mail)> MakeVmAsync()
    {
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [AccountId] =
            [
                new MailFolderModel
                {
                    AccountId = AccountId, FullName = "INBOX", DisplayName = "INBOX", Kind = SpecialFolderKind.Inbox,
                },
                Folder("INBOX/Projects", "Projects"),
                Folder("Archive", "Archive"),
            ],
        };
        var mail = new FolderedMailService(folders, []);
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        // OAuth, as AllArchiveVirtualFolderTests does: an IMAP password account needs a stored
        // password to connect, and the stub credential store has none.
        vm.Accounts.Add(new AccountModel
        {
            Id = AccountId, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft,
        });
        await vm.ConnectAllAccountsAsync();
        Assert.True(vm.CachedFolders.ContainsKey(AccountId), "the account's folders were not loaded.");
        return (vm, mail);
    }

    private static FolderTreeNode Node(MailFolderModel folder) => new() { Folder = folder, Label = folder.DisplayName };

    [Fact]
    public async Task MovingANestedFolderToTheTopLevel_SendsNoParent()
    {
        var (vm, mail) = await MakeVmAsync();

        await vm.MoveFolderToAsync(Node(Folder("INBOX/Projects", "Projects")), destination: null);

        var op = Assert.Single(mail.FolderOps);
        Assert.Equal(("move", AccountId, "INBOX/Projects", (string?)null), op);
        Assert.Equal("Folder 'Projects' moved to the top level.", vm.StatusText);
    }

    [Fact]
    public async Task CopyingANestedFolderToTheTopLevel_SendsNoParent()
    {
        var (vm, mail) = await MakeVmAsync();

        await vm.CopyFolderToAsync(Node(Folder("INBOX/Projects", "Projects")), destination: null);

        var op = Assert.Single(mail.FolderOps);
        Assert.Equal(("copy", AccountId, "INBOX/Projects", (string?)null), op);
        Assert.Equal("Folder 'Projects' copied to the top level.", vm.StatusText);
    }

    [Fact]
    public async Task MovingOrCopyingATopLevelFolderToTheTopLevel_IsReportedAndNotSent()
    {
        var (vm, mail) = await MakeVmAsync();

        await vm.MoveFolderToAsync(Node(Folder("Archive", "Archive")), destination: null);
        Assert.Equal("'Archive' is already at the top level of Work.", vm.StatusText);

        await vm.CopyFolderToAsync(Node(Folder("Archive", "Archive")), destination: null);
        Assert.Contains("already at the top level", vm.StatusText, StringComparison.Ordinal);

        Assert.Empty(mail.FolderOps);
    }

    /// <summary>A move to a folder still names that folder as the parent.</summary>
    [Fact]
    public async Task MovingToAFolder_SendsThatFolderAsTheParent()
    {
        var (vm, mail) = await MakeVmAsync();

        await vm.MoveFolderToAsync(Node(Folder("Archive", "Archive")), Folder("INBOX", "INBOX"));

        Assert.Equal(("move", AccountId, "Archive", (string?)"INBOX"), Assert.Single(mail.FolderOps));
        Assert.Equal("Folder 'Archive' moved.", vm.StatusText);
    }
}
