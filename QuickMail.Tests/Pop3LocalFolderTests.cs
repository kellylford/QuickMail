// Folders the user makes on a POP3 account (#794). A POP3 server has no folders, so these live in the
// local store alone — and so does the mail filed in them, which with "keep mail on the server" off
// exists nowhere else. The tests that matter most here are the ones about where mail goes when a
// folder changes: deleting a folder must leave its mail in Trash, never destroy it.
//
// Like Pop3MailServiceTests, these run against the REAL LocalStoreService on a temp profile: moves
// and deletes are SQL, and a fake store would pass tests the shipping code fails.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class Pop3LocalFolderTests
{
    private readonly Guid _account = Guid.NewGuid();
    private readonly LocalStoreService _store;
    private readonly Pop3MailService _pop;

    public Pop3LocalFolderTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPop3Folders-{Guid.NewGuid():N}");
        _store = new LocalStoreService(new ProfileContext(dir));
        _store.Initialize();
        _pop = new Pop3MailService(_store);
    }

    private static MailMessageSummary Msg(Guid account, string id, string folder, bool isRead = false) => new()
    {
        MessageId  = id,
        AccountId  = account,
        FolderName = folder,
        From       = "List <list@example.com>",
        To         = "me@example.com",
        Subject    = $"Message {id}",
        Date       = DateTimeOffset.UtcNow,
        IsRead     = isRead,
    };

    /// <summary>Creates a folder and returns it as GetFoldersAsync lists it.</summary>
    private async Task<MailFolderModel> CreateAsync(string name, string? parent = null)
    {
        await _pop.CreateFolderAsync(_account, parent, name);
        return (await _pop.GetFoldersAsync(_account))
            .Single(f => f.DisplayName == name && f.Kind == SpecialFolderKind.None &&
                         f.ParentId == (parent ?? Pop3MailService.TopLevelParentId));
    }

    private async Task<string[]> IdsIn(string folder) =>
        [.. (await _store.GetAllMessageIdsAsync(_account, folder)).Order()];

    // ── Creating and listing ────────────────────────────────────────────────────

    [Fact]
    public async Task ACreatedFolder_IsListedAfterTheBuiltInFour()
    {
        var lists = await CreateAsync("Mailing lists");

        var folders = await _pop.GetFoldersAsync(_account);
        Assert.Equal(["Inbox", "Sent", "Drafts", "Trash"], folders.Take(4).Select(f => f.FullName));
        Assert.Equal(5, folders.Count);

        // Filed under an opaque id, not its name, so renaming it never has to touch its mail.
        Assert.StartsWith(Pop3MailService.LocalFolderPrefix, lists.FullName);
        Assert.Equal(SpecialFolderKind.None, lists.Kind);
        // Included in All Mail, like an IMAP folder of the user's own.
        Assert.False(lists.ExcludeFromAllMail);
    }

    [Fact]
    public async Task Folders_SurviveARestart()
    {
        await CreateAsync("Lists");

        var afterRestart = new Pop3MailService(_store);
        Assert.Contains(await afterRestart.GetFoldersAsync(_account), f => f.DisplayName == "Lists");
    }

    [Fact]
    public async Task Folders_BelongToTheirAccount()
    {
        await CreateAsync("Lists");
        Assert.Equal(4, (await _pop.GetFoldersAsync(Guid.NewGuid())).Count);
    }

    [Fact]
    public async Task Folders_NestUnderBuiltInAndLocalFolders_InTheTreeAndInPaths()
    {
        // A dotted name is the case that matters: with IMAP-style path parsing, "lists.xs4all.nl" at
        // the top level would split into three nested nodes. Parent references cannot misread it.
        var dotted = await CreateAsync("lists.xs4all.nl");
        var lists  = await CreateAsync("Lists", parent: "Inbox");
        var child  = await CreateAsync("QuickMail", parent: lists.FullName);

        var folders = await _pop.GetFoldersAsync(_account);
        var roots   = FolderTreeBuilder.Build(folders);

        Assert.Equal(["Inbox", "Drafts", "Sent", "Trash", "lists.xs4all.nl"], roots.Select(n => n.Label));
        var inbox = roots[0];
        var listsNode = Assert.Single(inbox.Children);
        Assert.Equal("Lists", listsNode.Label);
        Assert.Equal("QuickMail", Assert.Single(listsNode.Children).Label);
        Assert.Empty(roots[4].Children);

        // Paths are what pickers, rules and saved views show — never a raw folder id.
        var byId = folders.ToDictionary(f => f.FullName, StringComparer.Ordinal);
        Assert.Equal("Inbox/Lists/QuickMail", FolderPaths.Build(child, byId));
        Assert.Equal("lists.xs4all.nl", FolderPaths.Build(dotted, byId));
        Assert.Equal("Inbox", FolderPaths.Build(folders[0], byId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("tab\there")]
    public async Task ABadName_IsRefused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _pop.CreateFolderAsync(_account, null, name));
        Assert.Equal(4, (await _pop.GetFoldersAsync(_account)).Count);
    }

    [Fact]
    public async Task TheNameIsTrimmed()
    {
        await _pop.CreateFolderAsync(_account, null, "  Lists  ");
        Assert.Contains(await _pop.GetFoldersAsync(_account), f => f.DisplayName == "Lists");
    }

    [Fact]
    public async Task ASiblingsName_IsRefused_ButTheSameNameElsewhereIsFine()
    {
        await CreateAsync("Lists");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _pop.CreateFolderAsync(_account, null, "LISTS"));
        // The built-in four are siblings at the top level.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pop.CreateFolderAsync(_account, null, "inbox"));

        await CreateAsync("Lists", parent: "Inbox");
        await CreateAsync("Inbox", parent: "Trash");
    }

    [Fact]
    public async Task CreatingUnderAFolderThatIsGone_IsRefused()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => _pop.CreateFolderAsync(_account, Pop3MailService.LocalFolderPrefix + "gone", "Lists"));

    // ── Filing mail ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MailMovesIntoAndOutOfALocalFolder_WithCounts()
    {
        var lists = await CreateAsync("Lists");
        await _store.UpsertSummariesAsync([Msg(_account, "a", "Inbox"), Msg(_account, "b", "Inbox", isRead: true)]);

        // The same call a rule's Move to folder makes.
        await _pop.MoveMessagesAsync(_account, "Inbox", ["a", "b"], lists.FullName);
        Assert.Empty(await IdsIn("Inbox"));
        Assert.Equal(["a", "b"], await IdsIn(lists.FullName));

        var listed = (await _pop.GetFoldersAsync(_account)).Single(f => f.FullName == lists.FullName);
        Assert.Equal(2, listed.MessageCount);
        Assert.Equal(1, listed.UnreadCount);

        await _pop.CopyMessagesAsync(_account, lists.FullName, ["a"], "Inbox");
        Assert.Equal(["a"], await IdsIn("Inbox"));
        Assert.Equal(["a", "b"], await IdsIn(lists.FullName));
    }

    [Fact]
    public async Task FilingIntoADeletedFolder_IsRefused_AndTheMailStaysPut()
    {
        // A rule still pointing at a folder the user deleted.
        var lists = await CreateAsync("Lists");
        await _pop.DeleteFolderAsync(_account, lists.FullName);
        await _store.UpsertSummariesAsync([Msg(_account, "a", "Inbox")]);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _pop.MoveMessagesAsync(_account, "Inbox", ["a"], lists.FullName));
        Assert.Equal(["a"], await IdsIn("Inbox"));
    }

    [Fact]
    public async Task FilingIsByExactId_SoADifferentlyCasedIdIsRefused()
    {
        var lists = await CreateAsync("Lists");
        await _store.UpsertSummariesAsync([Msg(_account, "a", "Inbox")]);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _pop.MoveMessagesAsync(_account, "Inbox", ["a"], lists.FullName.ToUpperInvariant()));
        Assert.Equal(["a"], await IdsIn("Inbox"));
    }

    // ── Renaming and moving ─────────────────────────────────────────────────────

    [Fact]
    public async Task Renaming_KeepsTheIdAndTheMail()
    {
        var lists = await CreateAsync("Lists");
        await _store.UpsertSummariesAsync([Msg(_account, "a", lists.FullName)]);

        await _pop.RenameFolderAsync(_account, lists.FullName, "Mailing lists", null);

        var renamed = (await _pop.GetFoldersAsync(_account)).Single(f => f.FullName == lists.FullName);
        Assert.Equal("Mailing lists", renamed.DisplayName);
        Assert.Equal(Pop3MailService.TopLevelParentId, renamed.ParentId);
        Assert.Equal(["a"], await IdsIn(lists.FullName));
    }

    [Fact]
    public async Task Moving_ReparentsTheFolder_AndItsChildrenGoWithIt()
    {
        var lists = await CreateAsync("Lists");
        var child = await CreateAsync("QuickMail", parent: lists.FullName);
        var dest  = await CreateAsync("Archive");

        // What MainViewModel.MoveFolderToAsync passes: the folder's own name, a new parent.
        await _pop.RenameFolderAsync(_account, lists.FullName, lists.DisplayName, dest.FullName);

        var folders = await _pop.GetFoldersAsync(_account);
        Assert.Equal(dest.FullName, folders.Single(f => f.FullName == lists.FullName).ParentId);
        Assert.Equal(lists.FullName, folders.Single(f => f.FullName == child.FullName).ParentId);
    }

    [Fact]
    public async Task MovingIntoItselfOrItsOwnChild_IsRefused()
    {
        var lists = await CreateAsync("Lists");
        var child = await CreateAsync("QuickMail", parent: lists.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _pop.RenameFolderAsync(_account, lists.FullName, "Lists", lists.FullName));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _pop.RenameFolderAsync(_account, lists.FullName, "Lists", child.FullName));
    }

    [Fact]
    public async Task MovingNextToAFolderOfTheSameName_IsRefused()
    {
        var lists = await CreateAsync("Lists");
        await CreateAsync("Lists", parent: "Inbox");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _pop.RenameFolderAsync(_account, lists.FullName, "Lists", "Inbox"));
    }

    [Theory]
    [InlineData("Inbox")]
    [InlineData("sent")]
    [InlineData("Drafts")]
    [InlineData("TRASH")]
    public async Task TheBuiltInFour_CannotBeRenamedMovedOrDeleted(string builtIn)
    {
        var lists = await CreateAsync("Lists");

        await Assert.ThrowsAsync<NotSupportedException>(() => _pop.RenameFolderAsync(_account, builtIn, "X", null));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => _pop.RenameFolderAsync(_account, builtIn, builtIn, lists.FullName));
        await Assert.ThrowsAsync<NotSupportedException>(() => _pop.DeleteFolderAsync(_account, builtIn));
        Assert.Equal(5, (await _pop.GetFoldersAsync(_account)).Count);
    }

    // ── Deleting ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_MovesItsMailAndItsChildrensMailToTrash()
    {
        // The only copy of this mail may be in these folders. Deleting a folder must never take it.
        var lists = await CreateAsync("Lists");
        var child = await CreateAsync("QuickMail", parent: lists.FullName);
        var other = await CreateAsync("Other");
        await _store.UpsertSummariesAsync([
            Msg(_account, "in-lists", lists.FullName),
            Msg(_account, "in-child", child.FullName),
            Msg(_account, "elsewhere", other.FullName),
            Msg(_account, "already-trashed", "Trash"),
        ]);

        await _pop.DeleteFolderAsync(_account, lists.FullName);

        Assert.Equal(["already-trashed", "in-child", "in-lists"], await IdsIn("Trash"));
        Assert.Empty(await IdsIn(lists.FullName));
        Assert.Empty(await IdsIn(child.FullName));
        Assert.Equal(["elsewhere"], await IdsIn(other.FullName));

        var names = (await _pop.GetFoldersAsync(_account)).Select(f => f.DisplayName).ToList();
        Assert.DoesNotContain("Lists", names);
        Assert.DoesNotContain("QuickMail", names);
        Assert.Contains("Other", names);
    }

    [Fact]
    public async Task DeletingAFolderThatIsGone_IsRefused()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => _pop.DeleteFolderAsync(_account, Pop3MailService.LocalFolderPrefix + "gone"));

    [Fact]
    public async Task DeletingTheAccount_ForgetsItsFolders()
    {
        await CreateAsync("Lists");
        await _store.DeleteAccountDataAsync(_account);
        Assert.Empty(await _store.LoadPop3LocalFoldersAsync(_account));
    }

    // ── Copying ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copying_CopiesTheFolderItsChildrenAndAllTheirMail()
    {
        var lists = await CreateAsync("Lists");
        var child = await CreateAsync("QuickMail", parent: lists.FullName);
        var dest  = await CreateAsync("Archive");
        await _store.UpsertSummariesAsync([Msg(_account, "a", lists.FullName), Msg(_account, "b", child.FullName)]);

        await _pop.CopyFolderAsync(_account, lists.FullName, dest.FullName);

        var folders  = await _pop.GetFoldersAsync(_account);
        var copy     = folders.Single(f => f.ParentId == dest.FullName);
        var copyKid  = folders.Single(f => f.ParentId == copy.FullName);
        Assert.Equal("Lists", copy.DisplayName);
        Assert.Equal("QuickMail", copyKid.DisplayName);
        Assert.NotEqual(lists.FullName, copy.FullName);

        Assert.Equal(["a"], await IdsIn(copy.FullName));
        Assert.Equal(["b"], await IdsIn(copyKid.FullName));
        // The originals are untouched.
        Assert.Equal(["a"], await IdsIn(lists.FullName));
        Assert.Equal(["b"], await IdsIn(child.FullName));
    }

    [Fact]
    public async Task CopyingABuiltInFolder_MakesALocalFolderOfTheSameName()
    {
        var dest = await CreateAsync("Backup");
        await _store.UpsertSummariesAsync([Msg(_account, "a", "Inbox")]);

        await _pop.CopyFolderAsync(_account, "Inbox", dest.FullName);

        var copy = (await _pop.GetFoldersAsync(_account)).Single(f => f.ParentId == dest.FullName);
        Assert.Equal("Inbox", copy.DisplayName);
        Assert.Equal(SpecialFolderKind.None, copy.Kind);
        Assert.Equal(["a"], await IdsIn(copy.FullName));
        Assert.Equal(["a"], await IdsIn("Inbox"));
    }

    [Fact]
    public async Task CopyingIntoItself_IsRefused()
    {
        var lists = await CreateAsync("Lists");
        var child = await CreateAsync("QuickMail", parent: lists.FullName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _pop.CopyFolderAsync(_account, lists.FullName, child.FullName));
        Assert.Equal(6, (await _pop.GetFoldersAsync(_account)).Count);
    }

    // ── --online mode ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlineMode_ExplainsThatFoldersNeedTheLocalStore()
    {
        var online = new Pop3MailService(_store, onlineMode: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => online.CreateFolderAsync(_account, null, "Lists"));
        Assert.Contains("--online", ex.Message);
    }

    // ── What the folder tree refuses before reaching the backend ───────────────

    private sealed class AccountsStub(params AccountModel[] accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MainViewModel MakeVm(params AccountModel[] accounts)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new AccountsStub(accounts), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList();
        return vm;
    }

    [Fact]
    public void TheFolderTree_RefusesToMoveOrDeleteAPop3BuiltInFolder_AndSaysWhy()
    {
        var pop  = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.Pop3Smtp, Username = "me@pop.example" };
        var imap = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp, Username = "me@imap.example" };
        var vm   = MakeVm(pop, imap);

        var refusal = vm.FolderCrudRefusal(
            new MailFolderModel { AccountId = pop.Id, FullName = "Trash", DisplayName = "Trash", Kind = SpecialFolderKind.Trash },
            "deleted");
        Assert.Equal("Trash is one of the four built-in folders of a POP3 account, so it cannot be deleted.", refusal);

        // The user's own folders on the same account, and an IMAP account's special folders (the
        // server answers for those), are not refused here.
        Assert.Null(vm.FolderCrudRefusal(
            new MailFolderModel { AccountId = pop.Id, FullName = "folder-x", DisplayName = "Lists", ParentId = "" }, "deleted"));
        Assert.Null(vm.FolderCrudRefusal(
            new MailFolderModel { AccountId = imap.Id, FullName = "Trash", DisplayName = "Trash", Kind = SpecialFolderKind.Trash }, "moved"));
    }
}
