// The four signals the user asked for, on channels that reach him — issue #637.
//
// He runs QuickMail with most custom announcements off, so a signal delivered only through
// AccessibilityHelper.Announce is, for him, silence. Each of these therefore has to land somewhere
// durable: the status bar, or a focusable field. These pin the durable half, which is the half that
// kept being left out.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftOutcomesReachTheUserTests
{
    private static readonly Guid AccountId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static MainViewModel Vm() => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService());

    private static MailMessageSummary Row(string id, string? reason = null) => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = $"Message {id}", IsRead = true,
        IsPendingUpload = id.StartsWith("local-", StringComparison.Ordinal),
        SendFailedReason = reason,
    };

    // ── Move and copy refuse BEFORE the folder picker ────────────────────────

    [Fact]
    public void AMoveIsRefusedBeforeThePickerOpens()
    {
        var vm = Vm();

        Assert.True(vm.RefuseIfAnyHeldOnlyHere([Row("local-1"), Row("41")], "move"));
        Assert.Contains("have not reached the server", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedDraftIsNotBlamedOnTheServer()
    {
        // The reason may be QuickMail's own — an unreadable saved copy — so "fix what the server
        // objected to" is both wrong and unfollowable.
        var vm = Vm();

        Assert.True(vm.RefuseIfAnyHeldOnlyHere(
            [Row("local-1", "Its saved copy on this computer could not be read, so there was nothing to upload.")],
            "move"));
        Assert.DoesNotContain("your server", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryMessagesAreNotRefused()
    {
        var vm = Vm();
        Assert.False(vm.RefuseIfAnyHeldOnlyHere([Row("41"), Row("42")], "move"));
    }

    // ── Auto-save failing means the message is nowhere ───────────────────────

    [Fact]
    public async Task WhenTheLocalStoreRefusesTheWrite_TheComposeWindowSaysSoDurably()
    {
        // This catch now means the LOCAL store refused, so the message exists nowhere at all. It
        // was announced as Status and written to a TextBlock with no focus stop — nothing that
        // reaches a user with announcements off.
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            new RecordingMailService { AppendDraftThrows = true },
            new ThrowingDraftService(), new StubTemplateService())
        {
            SenderAccount = new AccountModel { Id = AccountId, Username = "samuel@interfree.ca" },
            To = "someone@example.com",
            Subject = "Airport thoughts",
            Body = "Boarding soon.",
        };

        await vm.AutoSaveAsync();

        Assert.Contains("not saved anywhere", vm.DeliveryNotice, StringComparison.Ordinal);
    }

    private sealed class ThrowingDraftService : ILocalDraftService
    {
        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft, string folderName,
            string? previousMessageId, System.Threading.CancellationToken ct = default)
            => throw new InvalidOperationException("database is locked");
        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, System.Threading.CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(null);
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id) => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason) => Task.CompletedTask;
        public Task DiscardAsync(Guid a, string f, string id) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid a)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id) => Task.FromResult(string.Empty);
    }
}
