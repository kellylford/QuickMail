using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #682: a client-side rule can copy, and can do more than one thing. It used to do exactly one of Mark as read,
/// Mark as unread, Move or Delete, so the editor refused Copy to folder and any second action on an account with
/// client-side rules only — although QuickMail itself could do all of it.
/// </summary>
public class ClientRuleActionsTests
{
    private static ServerRuleEditorViewModel Named(string name = "File it")
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = name;
        vm.UseSubjectContains = true;
        vm.SubjectContains = "digest";   // a condition, so Move and Delete are allowed
        return vm;
    }

    // ── The editor: what an account with client-side rules only can now save ─

    [Fact]
    public void CopyToFolder_OnAClientOnlyAccount_SavesAsAClientRule()
    {
        var vm = Named();
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "INBOX/Kept";

        Assert.True(vm.Validate());
        Assert.Equal(RuleRunsWhere.Client, vm.Classify(accountSupportsServerRules: false).Kind);
    }

    [Fact]
    public void MoreThanOneAction_OnAClientOnlyAccount_SavesAsAClientRule()
    {
        var vm = Named();
        vm.MarkAsRead = true;
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "INBOX/Kept";
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "INBOX/Digests";

        Assert.True(vm.Validate());
        Assert.Equal(RuleRunsWhere.Client, vm.Classify(accountSupportsServerRules: false).Kind);
    }

    [Fact]
    public void MarkAsUnread_WithCopy_OnAWorkAccount_IsAClientRule_NotAConflict()
    {
        // Mark as unread keeps it off the server. Before #682 the second action kept it off the client as well, so the
        // rule could be saved nowhere.
        var vm = Named();
        vm.MarkAsUnread = true;
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "folder-id";

        Assert.Equal(RuleRunsWhere.Client, vm.Classify(accountSupportsServerRules: true).Kind);
    }

    [Fact]
    public void SeveralActions_OnAWorkAccount_StillGoToTheServer()
    {
        // Where the server can do it, it still does: a server rule runs while QuickMail is closed.
        var vm = Named();
        vm.MarkAsRead = true;
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "folder-id";

        Assert.Equal(RuleRunsWhere.Server, vm.Classify(accountSupportsServerRules: true).Kind);
    }

    [Fact]
    public void MoveAndDelete_OnAClientOnlyAccount_IsRefused_AndNamed()
    {
        var vm = Named();
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "INBOX/Digests";
        vm.Delete = true;

        var result = vm.Classify(accountSupportsServerRules: false);

        Assert.True(result.IsConflict);
        // Not "isn't available in a client-side rule": moving and deleting together is not a server feature the user
        // could go and switch on — a server rule can carry both — so the message says what a client rule can't do.
        Assert.Equal(
            "This account only supports client-side rules, which can't both move and delete a message. Change one to save.",
            result.ConflictError);
    }

    [Fact]
    public void MarkAsUnread_WithMoveOrDelete_IsRefused_BecauseTheyThrowTheMarkAway()
    {
        // Marking unread happens only in QuickMail's own copy of the message, which a move or delete then discards.
        var moving = Named();
        moving.MarkAsUnread = true;
        moving.MoveToFolder = true;
        moving.MoveToFolderId = "INBOX/Digests";

        Assert.False(moving.Validate());
        Assert.Equal(ServerRuleEditorViewModel.UnreadWithMoveError, moving.ActionsError);

        var deleting = Named();
        deleting.MarkAsUnread = true;
        deleting.Delete = true;

        Assert.False(deleting.Validate());
        Assert.Equal(ServerRuleEditorViewModel.UnreadWithMoveError, deleting.ActionsError);
    }

    [Fact]
    public void ACopyRule_WithNoCondition_IsRefused()
    {
        // With no condition it copies every message that arrives, and Run on Existing Mail copies the whole Inbox
        // again on each run — nothing dedupes the copies.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = "Copy everything";
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "INBOX/Kept";

        Assert.False(vm.Validate());
        Assert.Equal(ServerRuleEditorViewModel.NoConditionError, vm.ActionsError);
    }

    [Fact]
    public void ReadAndUnread_Together_AreRefused_AndSaidOnce()
    {
        var vm = Named();
        vm.MarkAsRead = true;
        vm.MarkAsUnread = true;
        var announced = new List<string>();
        vm.AnnouncementRequested += (text, _) => announced.Add(text);

        Assert.False(vm.Validate());
        Assert.Equal(ServerRuleEditorViewModel.ReadAndUnreadError, vm.ActionsError);
        Assert.Equal([ServerRuleEditorViewModel.ReadAndUnreadError], announced);
    }

    // ── What is saved ───────────────────────────────────────────────────────

    [Fact]
    public void SavedRule_ListsEveryAction_AndItsMainOneIsTheLastToRun()
    {
        var vm = Named();
        vm.MoveToFolder = true;            // ticked first: the order they run in is not the order they were ticked
        vm.MoveToFolderId = "INBOX/Digests";
        vm.MarkAsRead = true;
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "INBOX/Kept";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal([RuleAction.MarkAsRead, RuleAction.CopyToFolder, RuleAction.MoveToFolder], rule.Actions);
        // What a QuickMail from before #682 reads, and so the one thing it does.
        Assert.Equal(RuleAction.MoveToFolder, rule.Action);
        Assert.Equal("INBOX/Digests", rule.TargetFolder);
        Assert.Equal("INBOX/Kept", rule.CopyTargetFolder);
    }

    [Fact]
    public void SavedRule_ThatCopiesAndMarks_NamesTheMarkingAsItsMainAction()
    {
        // Copy is new in #682. A QuickMail from before it reads Action alone and has no copy to perform, so naming the
        // copy there would leave it doing nothing at all instead of the marking it is perfectly able to do.
        var vm = Named();
        vm.MarkAsRead = true;
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "INBOX/Kept";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(RuleAction.MarkAsRead, rule.Action);
        Assert.Equal([RuleAction.MarkAsRead, RuleAction.CopyToFolder], rule.Actions);
    }

    [Fact]
    public void SavedRule_ThatCopiesAndDeletes_NeverNamesTheDelete()
    {
        // Otherwise a QuickMail from before #682 bins the message and never makes the copy — the destructive half of
        // the rule without the half it exists for. Naming the copy leaves such a build doing nothing instead.
        var copyThenDelete = Named();
        copyThenDelete.CopyToFolder = true;
        copyThenDelete.CopyToFolderId = "INBOX/Kept";
        copyThenDelete.Delete = true;

        var rule = copyThenDelete.ToClientRule(Guid.NewGuid());

        Assert.Equal(RuleAction.CopyToFolder, rule.Action);
        Assert.Equal([RuleAction.CopyToFolder, RuleAction.Delete], rule.Actions);

        // With something else it understands, that is what it does — the marking, not the delete.
        var markCopyDelete = Named();
        markCopyDelete.MarkAsRead = true;
        markCopyDelete.CopyToFolder = true;
        markCopyDelete.CopyToFolderId = "INBOX/Kept";
        markCopyDelete.Delete = true;

        Assert.Equal(RuleAction.MarkAsRead, markCopyDelete.ToClientRule(Guid.NewGuid()).Action);
    }

    [Fact]
    public void TwoWrongTicksAtOnce_AreSaidAsOneThing()
    {
        // Every error is spoken as a single sentence, so a form with Mark as read, Mark as unread AND Move must not
        // say the unread tick is wrong twice over.
        var vm = Named();
        vm.MarkAsRead = true;
        vm.MarkAsUnread = true;
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "INBOX/Digests";
        var announced = new List<string>();
        vm.AnnouncementRequested += (text, _) => announced.Add(text);

        Assert.False(vm.Validate());
        Assert.Equal(ServerRuleEditorViewModel.ReadAndUnreadError, vm.ActionsError);
        Assert.Equal([ServerRuleEditorViewModel.ReadAndUnreadError], announced);
    }

    [Fact]
    public void SavedRule_ThatOnlyCopies_HasNothingElseToName()
    {
        var vm = Named();
        vm.CopyToFolder = true;
        vm.CopyToFolderId = "INBOX/Kept";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(RuleAction.CopyToFolder, rule.Action);
        Assert.Null(rule.Actions);
        Assert.Equal("INBOX/Kept", rule.CopyTargetFolder);
    }

    [Fact]
    public void SavedRule_WithOneAction_IsStoredAsBefore()
    {
        var vm = Named();
        vm.MarkAsRead = true;

        var rule = vm.ToClientRule(Guid.NewGuid());
        var json = JsonSerializer.Serialize(rule);

        Assert.Equal(RuleAction.MarkAsRead, rule.Action);
        Assert.Null(rule.Actions);
        // Nothing new in the file for a rule that uses nothing new.
        Assert.DoesNotContain("\"Actions\"", json);
        Assert.DoesNotContain("\"CopyTargetFolder\"", json);
    }

    [Fact]
    public void EditingASavedRule_OpensWithEveryActionTicked()
    {
        var saved = new MailRule
        {
            Name = "Keep and file",
            SubjectContains = "digest",
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.MarkAsRead, RuleAction.CopyToFolder, RuleAction.MoveToFolder],
            TargetFolder = "INBOX/Digests",
            CopyTargetFolder = "INBOX/Kept",
        };

        var vm = ServerRuleEditorViewModel.ForEditClient(saved);

        Assert.True(vm.MarkAsRead);
        Assert.True(vm.CopyToFolder);
        Assert.Equal("INBOX/Kept", vm.CopyToFolderId);
        Assert.True(vm.MoveToFolder);
        Assert.Equal("INBOX/Digests", vm.MoveToFolderId);
        Assert.False(vm.MarkAsUnread);
        Assert.False(vm.Delete);
        Assert.True(vm.IsClientRepresentable);   // so the edit can be saved back as the client rule it is
    }

    // ── The rule itself ─────────────────────────────────────────────────────

    [Fact]
    public void AllActions_OfARuleFromBefore682_IsItsOneAction()
    {
        var old = JsonSerializer.Deserialize<MailRule>("""{"Name":"Old","Action":2,"TargetFolder":"INBOX/Sorted"}""")!;

        Assert.Equal([RuleAction.MoveToFolder], old.AllActions());
    }

    [Fact]
    public void AllActions_RunMarkingThenCopyingThenMovingOrDeleting_WhateverOrderTheyWereStoredIn()
    {
        var rule = new MailRule { Actions = [RuleAction.Delete, RuleAction.CopyToFolder, RuleAction.MarkAsUnread] };

        Assert.Equal([RuleAction.MarkAsUnread, RuleAction.CopyToFolder, RuleAction.Delete], rule.AllActions());
    }

    // ── The rules list ──────────────────────────────────────────────────────

    [Fact]
    public void RulesListRow_SaysEveryAction_InTheOrderTheyRun()
    {
        var row = UnifiedRuleRow.ForClient(new MailRule
        {
            Name = "Keep and file",
            UseSubjectCondition = true,
            SubjectContains = "digest",
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.MoveToFolder, RuleAction.CopyToFolder, RuleAction.MarkAsRead],
            TargetFolder = "INBOX/Digests",
            CopyTargetFolder = "INBOX/Kept",
        });

        Assert.EndsWith("If subject contains 'digest' → mark as read, copy to INBOX/Kept, move to INBOX/Digests", row.RowText);
    }

    [Fact]
    public void RulesListRow_OnAGraphAccount_NamesTheCopyFolder_NotItsId()
    {
        var row = UnifiedRuleRow.ForClient(
            new MailRule { Name = "Keep", Action = RuleAction.CopyToFolder, CopyTargetFolder = "AQMkADcopy" },
            folderIsOpaque: true, copyFolderDisplay: "Kept");

        Assert.Contains("copy to Kept", row.RowText);
        Assert.DoesNotContain("AQMkAD", row.RowText);
    }

    // ── Converting an account to Microsoft 365 ──────────────────────────────

    private static readonly Guid Acct = Guid.NewGuid();

    private static List<MailFolderModel> GraphFolders() =>
    [
        new() { AccountId = Acct, FullName = "id-archive", DisplayName = "Archive" },
        new() { AccountId = Acct, FullName = "id-kept", DisplayName = "Kept" },
    ];

    [Fact]
    public void Conversion_RewritesTheCopyFolder_AsWellAsTheMoveFolder()
    {
        var rule = new MailRule
        {
            Name = "Keep and file",
            AccountId = Acct,
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.CopyToFolder, RuleAction.MoveToFolder],
            TargetFolder = "Archive",
            CopyTargetFolder = "INBOX/Kept",
        };

        var report = FolderReferenceRemapper.Remap(Acct, GraphFolders(), [rule], [], new ConfigModel());

        Assert.Equal("id-archive", rule.TargetFolder);
        Assert.Equal("id-kept", rule.CopyTargetFolder);
        Assert.True(rule.IsEnabled);
        Assert.Equal(["Keep and file"], report.RemappedRules);   // one rule, reported once
    }

    [Fact]
    public void Conversion_TurnsOffACopyRule_WhoseFolderIsntThere()
    {
        var rule = new MailRule { Name = "Keep", AccountId = Acct, Action = RuleAction.CopyToFolder, CopyTargetFolder = "Gone" };

        var report = FolderReferenceRemapper.Remap(Acct, GraphFolders(), [rule], [], new ConfigModel());

        Assert.False(rule.IsEnabled);
        Assert.Equal(["Keep"], report.DisabledRules);
    }

    // ── Running a rule ──────────────────────────────────────────────────────

    /// <summary>Records what the rule engine asks the mail server to do, in order.</summary>
    private sealed class RecordingMailService : StubImapMailServiceBase
    {
        public List<string> Calls { get; } = [];

        /// <summary>Set to make the copy fail, as a deleted destination folder or a full mailbox would.</summary>
        public Exception? ThrowOnCopy { get; set; }

        public override Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            Calls.Add($"read {messageId}");
            return Task.CompletedTask;
        }

        public override Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        {
            Calls.Add($"copy {string.Join(",", messageIds)} to {destinationFolder}");
            if (ThrowOnCopy is not null) throw ThrowOnCopy;
            return Task.CompletedTask;
        }

        public override Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        {
            Calls.Add($"move {string.Join(",", messageIds)} to {destinationFolder}");
            return Task.CompletedTask;
        }

        public override Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        {
            Calls.Add($"delete {string.Join(",", messageIds)}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Records the cached rows the rule engine deletes — a message that left the folder it was in.</summary>
    private sealed class RecordingStore : StubLocalStoreService
    {
        public List<string> Deleted { get; } = [];

        /// <summary>Messages recorded as having had client rules run on them (#712).</summary>
        public List<string> RulesDone { get; } = [];

        public override Task MarkRulesAppliedAsync(Guid accountId, string folderName, IEnumerable<string> messageIds)
        {
            RulesDone.AddRange(messageIds);
            return Task.CompletedTask;
        }

        public override Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds)
        {
            Deleted.AddRange(messageIds);
            return Task.CompletedTask;
        }
    }

    /// <summary>Runs one rule over one arriving Inbox message, "Weekly digest", with id 7.</summary>
    private static async Task<(List<string> Calls, List<MailMessageSummary> StillInInbox, List<MailMessageSummary> Removed)> Run(MailRule rule, RecordingMailService? mailServer = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var server = mailServer ?? new RecordingMailService();
            var rules = new RuleService(server, new StubLocalStoreService(), dir);
            var account = Guid.NewGuid();
            rule.AccountId = account;
            rules.SaveRules([rule]);
            var incoming = new List<MailMessageSummary>
            {
                new() { MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest" },
            };

            var (_, removed) = await rules.ApplyRulesAsync(incoming, account, TestContext.Current.CancellationToken);
            return (server.Calls, incoming, removed);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ARuleWithSeveralActions_DoesEachOfThem_MovingLast()
    {
        var (calls, stillInInbox, removed) = await Run(new MailRule
        {
            Name = "Keep and file",
            SubjectContains = "digest",
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.MoveToFolder, RuleAction.CopyToFolder, RuleAction.MarkAsRead],   // stored out of order
            TargetFolder = "INBOX/Digests",
            CopyTargetFolder = "INBOX/Kept",
        });

        Assert.Equal(["read 7", "copy 7 to INBOX/Kept", "move 7 to INBOX/Digests"], calls);
        // Moved, so out of the Inbox list, and out of the running for the rules after this one.
        Assert.Empty(stillInInbox);
        Assert.Single(removed);
    }

    [Fact]
    public async Task DeletingComesAfterCopying_Too()
    {
        var (calls, _, removed) = await Run(new MailRule
        {
            Name = "Keep a copy, then bin it",
            SubjectContains = "digest",
            Action = RuleAction.Delete,
            Actions = [RuleAction.Delete, RuleAction.CopyToFolder],
            CopyTargetFolder = "INBOX/Kept",
        });

        Assert.Equal(["copy 7 to INBOX/Kept", "delete 7"], calls);
        Assert.Single(removed);
    }

    [Fact]
    public async Task ACopyRule_LeavesTheMessageInTheInbox()
    {
        var (calls, stillInInbox, removed) = await Run(new MailRule
        {
            Name = "Keep",
            SubjectContains = "digest",
            Action = RuleAction.CopyToFolder,
            CopyTargetFolder = "INBOX/Kept",
        });

        Assert.Equal(["copy 7 to INBOX/Kept"], calls);
        Assert.Single(stillInInbox);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task WhenTheCopyFails_TheRuleStopsThere_LeavingTheMessageInTheInbox()
    {
        // Otherwise the message is filed out of the Inbox with no copy kept anywhere, and nothing says so — the copy
        // is the whole point of keeping one.
        var server = new RecordingMailService { ThrowOnCopy = new IOException("the destination folder is gone") };

        var (calls, stillInInbox, removed) = await Run(new MailRule
        {
            Name = "Keep and file",
            SubjectContains = "digest",
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.CopyToFolder, RuleAction.MoveToFolder],
            TargetFolder = "INBOX/Digests",
            CopyTargetFolder = "INBOX/Kept",
        }, server);

        Assert.Equal(["copy 7 to INBOX/Kept"], calls);   // the copy was attempted; the move never ran
        Assert.Single(stillInInbox);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task ACopyThatTimesOut_IsAFailedCopy_NotACancelledSync()
    {
        // A timed-out request arrives as TaskCanceledException, which is an OperationCanceledException. Left as one it
        // would travel out past every later rule and abort the whole pass, because the callers rethrow cancellations
        // untouched. Only this rule should stop — and the test fails by throwing if it doesn't.
        var server = new RecordingMailService { ThrowOnCopy = new TaskCanceledException() };

        var (calls, stillInInbox, removed) = await Run(new MailRule
        {
            Name = "Keep and file",
            SubjectContains = "digest",
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.CopyToFolder, RuleAction.MoveToFolder],
            TargetFolder = "INBOX/Digests",
            CopyTargetFolder = "INBOX/Kept",
        }, server);

        Assert.Equal(["copy 7 to INBOX/Kept"], calls);   // the move never ran
        Assert.Single(stillInInbox);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task ACopyIntoTheFolderTheMessageIsAlreadyIn_IsSkipped()
    {
        // Client rules run on the Inbox, so a rule copying there would copy its own copy on the next sync, and again
        // on the one after. The Rules Manager refuses to save one; this is the backstop for a rule that acquires such
        // a target later, through a folder rename or a Microsoft 365 conversion.
        var (calls, stillInInbox, removed) = await Run(new MailRule
        {
            Name = "Copy to the Inbox",
            SubjectContains = "digest",
            Action = RuleAction.CopyToFolder,
            CopyTargetFolder = "INBOX",
        });

        Assert.Empty(calls);
        Assert.Single(stillInInbox);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task RunOnExistingMail_WhenTheCopyFails_KeepsTheMessageAndItsCachedRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var account = Guid.NewGuid();
            var server = new RecordingMailService { ThrowOnCopy = new IOException("the destination folder is gone") };
            var store = new RecordingStore();
            store.SeededSummaries[(account, "INBOX")] =
            [
                new MailMessageSummary { MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest" },
            ];
            var rules = new RuleService(server, store, dir);
            rules.SaveRules([new MailRule
            {
                Name = "Keep and file", AccountId = account, SubjectContains = "digest",
                Action = RuleAction.MoveToFolder,
                Actions = [RuleAction.CopyToFolder, RuleAction.MoveToFolder],
                TargetFolder = "INBOX/Digests", CopyTargetFolder = "INBOX/Kept",
            }]);

            var removed = await rules.ApplyRulesToExistingAsync(
                store, new Dictionary<Guid, string> { [account] = "INBOX" }, TestContext.Current.CancellationToken);

            Assert.Equal(["copy 7 to INBOX/Kept"], server.Calls);   // the move never ran
            Assert.Empty(removed);
            Assert.Empty(store.Deleted);                            // the cached row stays, as the message does
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RunOnExistingMail_ACopyRule_KeepsTheMessageAndItsCachedRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var account = Guid.NewGuid();
            var server = new RecordingMailService();
            var store = new RecordingStore();
            store.SeededSummaries[(account, "INBOX")] =
            [
                new MailMessageSummary { MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest" },
            ];
            var rules = new RuleService(server, store, dir);
            rules.SaveRules([new MailRule
            {
                Name = "Keep", AccountId = account, SubjectContains = "digest",
                Action = RuleAction.CopyToFolder, CopyTargetFolder = "INBOX/Kept",
            }]);

            var removed = await rules.ApplyRulesToExistingAsync(
                store, new Dictionary<Guid, string> { [account] = "INBOX" }, TestContext.Current.CancellationToken);

            Assert.Equal(["copy 7 to INBOX/Kept"], server.Calls);
            Assert.Empty(removed);          // nothing left the Inbox, so nothing counts as moved or deleted
            Assert.Empty(store.Deleted);    // and the cached row stays
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RunOnExistingMail_RecordsWhatItRanOn_SoASyncDoesNotRunTheRulesAgain() // #712
    {
        // Mail a view cached a moment ago is waiting for a sync pass. Run on Existing Mail runs the rules on it too, and
        // unless it records that, the pass runs them a second time — with Copy, a second copy.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var account = Guid.NewGuid();
            var store = new RecordingStore();
            store.SeededSummaries[(account, "INBOX")] =
            [
                new MailMessageSummary { MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest" },
                new MailMessageSummary { MessageId = "8", AccountId = account, FolderName = "INBOX", Subject = "Something else" },
            ];
            var rules = new RuleService(new RecordingMailService(), store, dir);
            rules.SaveRules([new MailRule
            {
                Name = "Keep", AccountId = account, SubjectContains = "digest",
                Action = RuleAction.CopyToFolder, CopyTargetFolder = "INBOX/Kept",
            }]);

            await rules.ApplyRulesToExistingAsync(
                store, new Dictionary<Guid, string> { [account] = "INBOX" }, TestContext.Current.CancellationToken);

            // Both: a message the rules looked at and didn't match has still had them run on it.
            Assert.Equal(["7", "8"], store.RulesDone.Order());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
