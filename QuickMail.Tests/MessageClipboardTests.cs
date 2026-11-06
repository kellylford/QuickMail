// Issue #929: copy, cut and paste messages between folders, as in Outlook and Explorer.
//
// Cutting removes nothing: the messages move only when pasted, and a cut is cleared once pasted so a
// second paste does not try to move messages that have already gone. A copy stays and can be pasted
// again. Paste needs a real folder: All Inboxes and the like are not one place messages could go.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MessageClipboardTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Home = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MailFolderModel Folder(string fullName, SpecialFolderKind kind = SpecialFolderKind.None, Guid? account = null) => new()
    {
        AccountId = account ?? Work, FullName = fullName, DisplayName = fullName, Kind = kind,
    };

    private static MailMessageSummary Msg(string id, string folder = "INBOX", Guid? account = null, int daysAgo = 0) => new()
    {
        MessageId  = id,
        AccountId  = account ?? Work,
        FolderName = folder,
        From       = "someone@example.com",
        Subject    = $"Subject {id}",
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private static bool NotVirtual(MailFolderModel _) => false;

    // ── The clipboard on its own ─────────────────────────────────────────────────

    [Fact]
    public void Empty_PlansNothing()
    {
        var clip = new MessageClipboard();
        Assert.True(clip.IsEmpty);
        Assert.Equal(MessagePasteKind.Nothing, clip.Plan(Folder("Archive"), NotVirtual));
    }

    [Fact]
    public void Copy_PlansACopy_AndCut_PlansAMove()
    {
        var clip = new MessageClipboard();
        clip.Copy([Msg("a")]);
        Assert.False(clip.IsCut);
        Assert.Equal(MessagePasteKind.Copy, clip.Plan(Folder("Archive"), NotVirtual));

        clip.Cut([Msg("a")]);
        Assert.True(clip.IsCut);
        Assert.Equal(MessagePasteKind.Move, clip.Plan(Folder("Archive"), NotVirtual));
    }

    [Fact]
    public void CopyingNothing_LeavesWhatWasThere()
    {
        // Ctrl+C on an empty selection must not wipe out what the user copied a moment ago.
        var clip = new MessageClipboard();
        clip.Cut([Msg("a")]);
        clip.Copy([]);
        Assert.True(clip.IsCut);
        Assert.Single(clip.Messages);
    }

    [Fact]
    public void TheSameMessageTwice_IsHeldOnce()
    {
        // A conversation header and one of its messages can both be in a selection, and the list's
        // background reload hands out a fresh object for the same message (#925).
        var clip = new MessageClipboard();
        clip.Copy([Msg("a"), Msg("a"), Msg("b")]);
        Assert.Equal(["a", "b"], clip.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public void NoFolder_HeaderOrVirtualFolder_IsNoDestination()
    {
        var clip = new MessageClipboard();
        clip.Copy([Msg("a")]);

        Assert.Equal(MessagePasteKind.NoDestination, clip.Plan(null, NotVirtual));
        Assert.Equal(MessagePasteKind.NoDestination, clip.Plan(new MailFolderModel { AccountId = Guid.Empty, FullName = "x" }, NotVirtual));
        Assert.Equal(MessagePasteKind.NoDestination, clip.Plan(new MailFolderModel { AccountId = Work, FullName = "" }, NotVirtual));
        Assert.Equal(MessagePasteKind.NoDestination, clip.Plan(Folder("Archive"), _ => true));
    }

    [Fact]
    public void AllAlreadyInTheDestination_IsSameFolder_ButOneElsewhereIsNot()
    {
        var clip = new MessageClipboard();
        clip.Cut([Msg("a"), Msg("b")]);
        Assert.Equal(MessagePasteKind.SameFolder, clip.Plan(Folder("INBOX"), NotVirtual));

        clip.Cut([Msg("a"), Msg("b", folder: "Archive")]);
        Assert.Equal(MessagePasteKind.Move, clip.Plan(Folder("INBOX"), NotVirtual));
    }

    [Fact]
    public void SameFolderName_InAnotherAccount_IsNotTheSameFolder()
    {
        var clip = new MessageClipboard();
        clip.Copy([Msg("a")]);
        Assert.Equal(MessagePasteKind.Copy, clip.Plan(Folder("INBOX", account: Home), NotVirtual));
    }

    [Fact]
    public void Clear_EmptiesIt_AndRaisesChanged()
    {
        var clip = new MessageClipboard();
        var changes = 0;
        clip.Changed += (_, _) => changes++;

        clip.Cut([Msg("a")]);
        clip.Clear();
        clip.Clear(); // already empty: nothing to report

        Assert.True(clip.IsEmpty);
        Assert.False(clip.IsCut);
        Assert.Equal(2, changes);
    }

    // ── Through the view model ───────────────────────────────────────────────────

    private sealed class RecordingSyncService : ISyncService
    {
#pragma warning disable CS0067 // nothing in these tests raises them
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
        public event Action<int, int>? OfflineBodyProgressChanged;
        public event Action<int, int>? OfflineBodyPassCompleted;
        public event Action<int, int>? SenderEmailBackfillProgressChanged;
        public event Action<int, int>? SenderEmailBackfillEnded;
#pragma warning restore CS0067

        /// <summary>Folders synced on their own: what a paste into the shown folder asks for.</summary>
        public List<string> OneFolderSyncs { get; } = new();

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
        {
            OneFolderSyncs.Add(folder.FullName);
            return Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        }
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => SyncOneFolderAsync(account, folder, ct);
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult(0);
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
        public Task ApplyPendingRulesAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
        public Task BackfillOfflineBodiesAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A view model showing a three-message inbox ("a" newest), with Archive and a second
    /// account's Inbox available, and recorders for what reached the server and what was said.</summary>
    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required FolderedMailService Mail { get; init; }
        public required RecordingSyncService Sync { get; init; }
        public required StatusAnnouncementRecorder Announced { get; init; }
        public required MailFolderModel Inbox { get; init; }
        public required MailFolderModel Archive { get; init; }
        public required MailFolderModel HomeInbox { get; init; }
        public required MailFolderModel Trash { get; init; }

        public MailMessageSummary Row(string id) => Vm.Messages.First(m => m.MessageId == id);

        public static async Task<Fixture> CreateAsync()
        {
            var inbox     = Folder("INBOX", SpecialFolderKind.Inbox);
            var archive   = Folder("Archive", SpecialFolderKind.Archive);
            var trash     = Folder("Trash", SpecialFolderKind.Trash);
            var homeInbox = Folder("INBOX", SpecialFolderKind.Inbox, Home);
            var mail = new FolderedMailService(
                new Dictionary<Guid, List<MailFolderModel>> { [Work] = [inbox, archive, trash], [Home] = [homeInbox] },
                new Dictionary<(Guid, string), List<MailMessageSummary>>
                {
                    [(Work, "INBOX")]   = [Msg("a", daysAgo: 0), Msg("b", daysAgo: 1), Msg("c", daysAgo: 2)],
                    [(Work, "Archive")] = [Msg("z", folder: "Archive", daysAgo: 5)],
                    [(Work, "Trash")]   = [Msg("t", folder: "Trash", daysAgo: 6)],
                });
            var sync = new RecordingSyncService();

            var vm = new MainViewModel(
                mail, new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
                new StubOAuthService(), sync, new StubConfigService(),
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
                new StubSmtpService());

            vm.Accounts.Add(new AccountModel { Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft });
            vm.Accounts.Add(new AccountModel { Id = Home, AccountName = "Home", Username = "home@example.com", AuthType = AuthType.OAuth2Microsoft });
            await vm.ConnectAllAccountsAsync();
            await vm.SelectFolderCommand.ExecuteAsync(inbox);
            Assert.Equal(3, vm.Messages.Count);

            return new Fixture
            {
                Vm = vm, Mail = mail, Sync = sync, Announced = StatusAnnouncementRecorder.Watch(vm),
                Inbox = inbox, Archive = archive, HomeInbox = homeInbox, Trash = trash,
            };
        }
    }

    [Fact]
    public async Task CopyThenPaste_CopiesOnTheServer_AndKeepsTheCopyForAnotherPaste()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a"), f.Row("b")]);
        Assert.Equal(("2 messages copied.", AnnouncementCategory.Result), f.Announced.Last);

        await f.Vm.PasteMessagesAsync(f.Archive);

        var t = Assert.Single(f.Mail.Transfers);
        Assert.Equal(("copy", Work, "INBOX", "Archive"), (t.Op, t.AccountId, t.From, t.To));
        Assert.Equal(["a", "b"], t.Ids);
        Assert.Equal(3, f.Vm.Messages.Count);            // a copy leaves the originals where they are
        Assert.False(f.Vm.MessageClipboard.IsEmpty);     // and can be pasted again
    }

    [Fact]
    public async Task Cut_MovesNothingUntilPasted_ThenMovesAndClears()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.CutMessagesToClipboardAsync([f.Row("b")]);
        Assert.Equal(("Message cut.", AnnouncementCategory.Result), f.Announced.Last);
        Assert.Empty(f.Mail.Transfers);
        Assert.Equal(3, f.Vm.Messages.Count);

        await f.Vm.PasteMessagesAsync(f.Archive);

        var t = Assert.Single(f.Mail.Transfers);
        Assert.Equal(("move", "INBOX", "Archive"), (t.Op, t.From, t.To));
        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "b");
        Assert.True(f.Vm.MessageClipboard.IsEmpty);

        // A second paste has nothing left to move, and says so rather than asking the server again.
        await f.Vm.PasteMessagesAsync(f.Archive);
        Assert.Single(f.Mail.Transfers);
        Assert.StartsWith("Nothing to paste", f.Announced.Last.Text);
    }

    [Fact]
    public async Task AFailedMove_KeepsTheCut_SoItCanBeTriedAgain()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("b")]);
        f.Mail.FailTransfers = true;

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.True(f.Vm.MessageClipboard.IsCut);
        Assert.Contains(f.Vm.Messages, m => m.MessageId == "b");
        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.StartsWith("Failed to move", f.Announced.Last.Text);
    }

    [Fact]
    public async Task AFailedCopy_IsHeard()
    {
        // Copy to Folder used to set only the status text on failure, spoken only with background
        // status announcements on. Paste leans on it, so the failure is now a Result.
        var f = await Fixture.CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a")]);
        f.Mail.FailTransfers = true;

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.StartsWith("Failed to copy", f.Announced.Last.Text);
    }

    [Fact]
    public async Task PasteWithNothingCopied_SaysSo()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.PasteMessagesAsync(f.Archive);
        Assert.Empty(f.Mail.Transfers);
        Assert.Equal(("Nothing to paste. Copy or cut messages first.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task PasteWithNoFolder_AsksForOne()
    {
        // A folder-tree header, or a combined view: there is no one folder to put the messages in.
        var f = await Fixture.CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a")]);

        await f.Vm.PasteMessagesAsync(null);

        Assert.Empty(f.Mail.Transfers);
        Assert.Equal(("Choose a folder to paste into.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task PasteBackIntoTheSameFolder_DoesNothing_AndSaysWhy()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a")]);

        await f.Vm.PasteMessagesAsync(f.Inbox);

        Assert.Empty(f.Mail.Transfers);
        Assert.Equal(("That message is already in INBOX.", AnnouncementCategory.Result), f.Announced.Last);
        Assert.True(f.Vm.MessageClipboard.IsCut); // still waiting to be pasted somewhere real
    }

    [Fact]
    public async Task MessagesAlreadyInTheDestination_AreLeftOut_OfAMixedPaste()
    {
        // Moving a message into its own folder, or copying it there, would only make a duplicate.
        var f = await Fixture.CreateAsync();
        await f.Vm.SelectFolderCommand.ExecuteAsync(f.Archive);
        var z = f.Vm.Messages.First(m => m.MessageId == "z");
        await f.Vm.CopyMessagesToClipboardAsync([z, Msg("a")]);

        await f.Vm.PasteMessagesAsync(f.Archive);

        var t = Assert.Single(f.Mail.Transfers);
        Assert.Equal("INBOX", t.From);
        Assert.Equal(["a"], t.Ids);
    }

    [Fact]
    public async Task PasteIntoTheFolderOnScreen_FetchesIt_SoTheMessagesAppear()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a")]);
        await f.Vm.SelectFolderCommand.ExecuteAsync(f.Archive);
        f.Sync.OneFolderSyncs.Clear();

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.Equal(["Archive"], f.Sync.OneFolderSyncs);
    }

    [Fact]
    public async Task PasteIntoAnotherFolder_DoesNotFetchIt()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a")]);
        f.Sync.OneFolderSyncs.Clear();

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.Empty(f.Sync.OneFolderSyncs);
    }

    [Fact]
    public async Task ACutPastedFromTheFolderTree_DoesNotPullFocusIntoTheList()
    {
        // moveFocus: false is what the folder tree passes, so focus stays on the folder pasted into.
        var f = await Fixture.CreateAsync();
        var focusRequests = 0;
        f.Vm.MessageListFocusNowRequested += () => { focusRequests++; return true; };
        f.Vm.MessageListFocusRequested    += () => focusRequests++;
        f.Vm.SelectedMessage = f.Row("b");
        await f.Vm.CutMessagesToClipboardAsync([f.Row("b")]);

        await f.Vm.PasteMessagesAsync(f.Archive, moveFocus: false);

        Assert.Equal(0, focusRequests);
        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "b");
        Assert.NotEqual("b", f.Vm.SelectedMessage?.MessageId); // the selection still moved off the row that left
    }

    [Fact]
    public async Task OneMessagePastedFromTheFolderTree_IsAnnounced()
    {
        // A single move is silent from the list, where the row leaving and the next row being read say
        // it all (#667). From the folder tree the user is not on the list and would hear nothing.
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("b")]);

        await f.Vm.PasteMessagesAsync(f.Archive, moveFocus: false);

        Assert.Equal(("1 message moved to Archive.", AnnouncementCategory.MessageAction), f.Announced.Last);
    }
    [Fact]
    public async Task ACutWhoseRowWasReloaded_StillLeavesTheList()
    {
        // The list replaces its rows with fresh objects on a background reload (#925). The move
        // removes rows by reference, so paste must hand it the row on screen, not the copied object.
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([Msg("b")]); // same message, different object from the row

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "b");
    }

    // ── Between accounts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ACutPastedIntoAnotherAccount_CopiesTheOriginalAcross_ThenPutsItInTrash()
    {
        // Kelly's decision on #929: cut removes the original, into its own account's Trash, so a
        // mistaken move can be undone.
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("b")]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        var added = Assert.Single(f.Mail.Appended);
        Assert.Equal((Home, "INBOX", "Subject b"), (added.AccountId, added.Folder, added.Subject));
        Assert.Equal((Work, "INBOX", "b"), Assert.Single(f.Mail.Trashed));
        Assert.Empty(f.Mail.Transfers);   // the server's own MOVE cannot reach another account
        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "b");
        Assert.True(f.Vm.MessageClipboard.IsEmpty);
        Assert.Contains("INBOX in Home", f.Vm.StatusText);
    }

    [Fact]
    public async Task ACopyPastedIntoAnotherAccount_LeavesTheOriginalWhereItWas()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a"), f.Row("c")]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Equal(["Subject a", "Subject c"], f.Mail.Appended.Select(a => a.Subject));
        Assert.Empty(f.Mail.Trashed);
        Assert.Equal(3, f.Vm.Messages.Count);
        Assert.Equal(("2 messages copied to INBOX in Home.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task AcrossAccounts_ReadFlaggedAndArrivalTime_GoWithTheMessage()
    {
        var f = await Fixture.CreateAsync();
        var a = f.Row("a");
        a.IsRead = true;
        a.FlagId = "red";
        await f.Vm.CopyMessagesToClipboardAsync([a, f.Row("b")]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Equal((true, true, a.Date), (f.Mail.Appended[0].IsRead, f.Mail.Appended[0].IsFlagged, f.Mail.Appended[0].Received));
        Assert.Equal((false, false), (f.Mail.Appended[1].IsRead, f.Mail.Appended[1].IsFlagged));
    }

    [Fact]
    public async Task AMoveThatStopsPartway_TakesOutOnlyWhatWent_AndKeepsTheRestToTryAgain()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a"), f.Row("b"), f.Row("c")]);
        f.Mail.FailAppendOf.Add("b@example.com");

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        // "a" went and is in Trash; "b" was refused, so its original was never touched.
        Assert.Equal([(Work, "INBOX", "a")], f.Mail.Trashed);
        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "a");
        Assert.Contains(f.Vm.Messages, m => m.MessageId == "b");
        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.StartsWith("Moved 1 of 3 messages to INBOX in Home.", f.Announced.Last.Text);

        // The cut now holds only what is still where it was.
        Assert.True(f.Vm.MessageClipboard.IsCut);
        Assert.Equal(["b", "c"], f.Vm.MessageClipboard.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task AnOriginalThatCannotBeFetched_IsNotRemoved()
    {
        var f = await Fixture.CreateAsync();
        f.Mail.OriginalsUnavailable.Add("a");
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a")]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Empty(f.Mail.Appended);
        Assert.Empty(f.Mail.Trashed);
        Assert.Contains(f.Vm.Messages, m => m.MessageId == "a");
        Assert.StartsWith("Failed to move", f.Announced.Last.Text);
    }

    [Fact]
    public async Task IntoAnAccountThatCannotTakeMessagesFromElsewhere_ItSaysSo_AndTouchesNothing()
    {
        // Microsoft 365, for now: Graph files a message created from its original as a draft.
        var f = await Fixture.CreateAsync();
        f.Mail.CannotAppend.Add(Home);
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a")]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Empty(f.Mail.Appended);
        Assert.Equal(("Messages from another account cannot be copied into INBOX in Home yet.", AnnouncementCategory.Result), f.Announced.Last);
        Assert.False(f.Vm.CanTakeMessagesFromOtherAccounts(Home));
        Assert.True(f.Vm.CanTakeMessagesFromOtherAccounts(Work));
    }

    [Fact]
    public async Task AFolderWithTheSameNameInAnotherAccount_IsNeverMistakenForTheSourceAccounts()
    {
        // The collision CLAUDE.md warns of: the backends move by folder name over the source account's
        // connection, so "INBOX" in Home must not become a MOVE into Work's INBOX.
        var f = await Fixture.CreateAsync();
        await f.Vm.SelectFolderCommand.ExecuteAsync(f.Archive);
        var z = f.Vm.Messages.First(m => m.MessageId == "z");
        await f.Vm.CutMessagesToClipboardAsync([z]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Empty(f.Mail.Transfers);
        Assert.Equal(Home, Assert.Single(f.Mail.Appended).AccountId);
    }

    // ── Found in review ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AHeldCtrlV_PastesOnce()
    {
        // The first paste is still adding messages one at a time; the cut is cleared only when it
        // finishes, so a second paste started meanwhile would copy them all across again.
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a")]);
        f.Mail.AppendGate = new TaskCompletionSource();

        var first  = f.Vm.PasteMessagesAsync(f.HomeInbox);
        var second = f.Vm.PasteMessagesAsync(f.HomeInbox);
        Assert.True(second.IsCompleted);   // turned away at once
        f.Mail.AppendGate.SetResult();
        await first;

        Assert.Single(f.Mail.Appended);
        Assert.Single(f.Mail.Trashed);
    }

    [Fact]
    public async Task AnOriginalThatCouldNotBeRemoved_IsNotPastedAcrossASecondTime()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a"), f.Row("b")]);
        f.Mail.FailTrash = true;

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Equal(2, f.Mail.Appended.Count);
        Assert.Contains(f.Vm.Messages, m => m.MessageId == "a");   // still there, so still listed
        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.Contains("2 more were copied there, but their originals could not be removed", f.Announced.Last.Text);

        // Pasting again must not make second copies of messages already copied across.
        await f.Vm.PasteMessagesAsync(f.HomeInbox);
        Assert.Equal(2, f.Mail.Appended.Count);
    }

    [Fact]
    public async Task ACutFromTrash_IsDeletedOnceItLands_NotPutInTrashAgain()
    {
        var f = await Fixture.CreateAsync();
        await f.Vm.SelectFolderCommand.ExecuteAsync(f.Trash);
        await f.Vm.CutMessagesToClipboardAsync([f.Vm.Messages.First(m => m.MessageId == "t")]);

        await f.Vm.PasteMessagesAsync(f.HomeInbox);

        Assert.Single(f.Mail.Appended);
        Assert.Empty(f.Mail.Trashed);
        Assert.Equal([(Work, "Trash", "t")], f.Mail.PermanentlyDeleted);
    }

    [Fact]
    public async Task CopyOrCutWithNothingSelected_SaysSo()
    {
        // The Message menu reaches these with nothing selected; it used to close with no word at all.
        var f = await Fixture.CreateAsync();

        await f.Vm.CopyMessagesToClipboardAsync([]);
        Assert.Equal(("Select messages to copy first.", AnnouncementCategory.Result), f.Announced.Last);

        await f.Vm.CutMessagesToClipboardAsync([]);
        Assert.Equal(("Select messages to cut first.", AnnouncementCategory.Result), f.Announced.Last);
    }
}