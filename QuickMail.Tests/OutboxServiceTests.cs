using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The drain is what turns "queued" into "sent" once the network is back (#637). These run against
/// the real <see cref="LocalStoreService"/> on a temp profile with the send and mail services
/// stubbed, so what is asserted is the service's own bookkeeping: which rows it touches, what
/// state it leaves them in, and what it tells the rest of the app afterwards.
/// </summary>
public class OutboxServiceTests
{
    private static LocalStoreService NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailOutboxSvc-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();
        return store;
    }

    private sealed class ListAccountService(params AccountModel[] accounts) : IAccountService
    {
        private readonly List<AccountModel> _accounts = [.. accounts];
        public List<AccountModel> LoadAccounts() => [.. _accounts];
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private sealed class PasswordCredentialService(string? password) : ICredentialService
    {
        public void SavePassword(Guid accountId, string password) { }
        public string? GetPassword(Guid accountId) => password;
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    /// <summary>Records draft and sent-folder traffic, with knobs to make each leg fail.</summary>
    private sealed class RecordingMailService : StubImapMailServiceBase
    {
        public string? DraftsFolder { get; set; } = "Drafts";
        public Exception? AppendDraftFailure { get; set; }
        public List<(ComposeModel Draft, string? Replaced)> AppendedDrafts { get; } = [];
        public List<ComposeModel> AppendedToSent { get; } = [];
        public List<(string Folder, string Id)> Trashed { get; } = [];
        public Exception? SentAppendFailure { get; set; }

        public override Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult(DraftsFolder);

        public override Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default)
        {
            if (AppendDraftFailure != null) return Task.FromException<string>(AppendDraftFailure);
            AppendedDrafts.Add((draft, replaceMessageId));
            return Task.FromResult("77");
        }

        public override Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
        {
            if (SentAppendFailure != null) return Task.FromException(SentAppendFailure);
            AppendedToSent.Add(sent);
            return Task.CompletedTask;
        }

        public override Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            Trashed.Add((folderName, messageId));
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public LocalStoreService Store { get; } = NewStore();
        public RecordingMailService Mail { get; } = new();
        public StubSmtpService Smtp { get; } = new();
        public StubConnectivityService Connectivity { get; } = new();
        public AccountModel Account { get; } = new() { AccountName = "Work", Username = "kelly@example.com", AuthType = AuthType.Password };
        public List<OutboxFlushResult> Completed { get; } = [];
        public int ChangedCount { get; private set; }

        public OutboxService Build(string? password = "pw", bool onlineMode = false, bool withConnectivity = true)
        {
            var svc = new OutboxService(Store, Mail, Smtp, new ListAccountService(Account),
                new PasswordCredentialService(password), withConnectivity ? Connectivity : null, onlineMode);
            svc.FlushCompleted += r => Completed.Add(r);
            svc.Changed += () => ChangedCount++;
            return svc;
        }

        public ComposeModel Compose(string subject = "Hello") => new()
        {
            AccountId = Account.Id,
            To = "to@example.com",
            Subject = subject,
            Body = "body",
            DraftMessageId = "4242",
            DraftFolderName = "Drafts",
        };
    }

    private static SocketException Unreachable() => new((int)SocketError.HostUnreachable);

    private static SmtpCommandException Rejected()
        => new(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 no such user");

    [Fact]
    public async Task EnqueueTwiceWithTheSameIdKeepsOneRow()
    {
        var f = new Fixture();
        using var svc = f.Build();

        var id = await svc.EnqueueDraftAsync(f.Compose("first"), f.Account.Id, existingId: null);
        var again = await svc.EnqueueDraftAsync(f.Compose("second"), f.Account.Id, existingId: id);

        Assert.Equal(id, again);
        var one = Assert.Single(await svc.ListAsync());
        Assert.Equal("second", one.Subject);
        Assert.Equal(OutboxKind.Draft, one.Kind);
        Assert.Equal(2, f.ChangedCount);
    }

    [Fact]
    public async Task EnqueueSendOverADraftRowTurnsItIntoASend()
    {
        var f = new Fixture();
        using var svc = f.Build();

        var id = await svc.EnqueueDraftAsync(f.Compose(), f.Account.Id, null);
        await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, id);

        var one = Assert.Single(await svc.ListAsync());
        Assert.Equal(OutboxKind.Send, one.Kind);
        Assert.Equal(OutboxState.Pending, one.State);
        Assert.Equal("4242", one.ReplaceDraftId);
    }

    [Fact]
    public async Task FlushSendsFilesInSentTrashesTheServerDraftAndRemovesTheRow()
    {
        var f = new Fixture();
        using var svc = f.Build();
        await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var result = await svc.FlushAsync();

        Assert.Equal(new OutboxFlushResult(1, 0, 0, 0), result);
        var sent = Assert.Single(f.Smtp.Sent);
        Assert.Equal("Hello", sent.Compose.Subject);
        Assert.Equal(f.Account.Id, sent.Account.Id);
        Assert.Single(f.Mail.AppendedToSent);
        Assert.Equal(("Drafts", "4242"), Assert.Single(f.Mail.Trashed));
        Assert.Empty(await svc.ListAsync());
        Assert.Equal(result, Assert.Single(f.Completed));
    }

    [Fact]
    public async Task FlushUploadsADraftReplacingTheServerCopy()
    {
        var f = new Fixture();
        using var svc = f.Build();
        await svc.EnqueueDraftAsync(f.Compose(), f.Account.Id, null);

        var result = await svc.FlushAsync();

        Assert.Equal(new OutboxFlushResult(0, 1, 0, 0), result);
        var (draft, replaced) = Assert.Single(f.Mail.AppendedDrafts);
        Assert.Equal("4242", replaced);
        Assert.Equal("Drafts", draft.DraftFolderName);
        Assert.Empty(f.Smtp.Sent);
        Assert.Empty(await svc.ListAsync());
    }

    [Fact]
    public async Task ATransportFailureLeavesTheRowPendingWithBackoff()
    {
        var f = new Fixture();
        using var svc = f.Build();
        f.Smtp.SendFailure = Unreachable();
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var result = await svc.FlushAsync();

        Assert.Equal(new OutboxFlushResult(0, 0, 0, 0, Deferred: 1), result);
        Assert.Empty(f.Completed);
        var row = await svc.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal(OutboxState.Pending, row.State);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.NextAttemptUtc);
        Assert.True(row.NextAttemptUtc > DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Contains("Waiting to send", row.StateDisplay, StringComparison.Ordinal);

        // Inside the backoff window the row is not retried, even though the network is "back".
        f.Smtp.SendFailure = null;
        var second = await svc.FlushAsync();
        Assert.Equal(1, second.Skipped);
        Assert.Empty(f.Smtp.Sent);

        // Send Outbox Now ignores the backoff.
        var forced = await svc.FlushAsync(force: true);
        Assert.Equal(1, forced.Sent);
        Assert.Single(f.Smtp.Sent);
    }

    [Fact]
    public async Task AServerRejectionParksTheRowAsFailedUntilForced()
    {
        var f = new Fixture();
        using var svc = f.Build();
        f.Smtp.SendFailure = Rejected();
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var result = await svc.FlushAsync();

        Assert.Equal(new OutboxFlushResult(0, 0, 1, 0), result);
        Assert.Equal(result, Assert.Single(f.Completed));
        var row = await svc.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal(OutboxState.Failed, row.State);
        Assert.StartsWith("Failed: ", row.StateDisplay, StringComparison.Ordinal);
        Assert.Contains("550", row.LastError, StringComparison.Ordinal);

        // A plain drain leaves it alone; the user has to ask.
        f.Smtp.SendFailure = null;
        Assert.Equal(0, (await svc.FlushAsync()).Sent);
        Assert.Equal(1, (await svc.FlushAsync(force: true)).Sent);
        Assert.Empty(await svc.ListAsync());
    }

    [Fact]
    public async Task NoDraftsFolderIsAPermanentFailureWithAReasonTheUserCanRead()
    {
        var f = new Fixture();
        using var svc = f.Build();
        f.Mail.DraftsFolder = null;
        var compose = f.Compose();
        compose.DraftFolderName = null;
        var id = await svc.EnqueueDraftAsync(compose, f.Account.Id, null);

        var result = await svc.FlushAsync();

        Assert.Equal(1, result.Failed);
        var row = await svc.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal(OutboxState.Failed, row.State);
        Assert.Equal("No Drafts folder found on this account.", row.LastError);
    }

    [Fact]
    public async Task MissingPasswordIsAPermanentFailure()
    {
        var f = new Fixture();
        using var svc = f.Build(password: null);
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        await svc.FlushAsync();

        var row = await svc.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal(OutboxState.Failed, row.State);
        Assert.Equal("No password stored for this account.", row.LastError);
        Assert.Empty(f.Smtp.Sent);
    }

    [Fact]
    public async Task AKnownOfflineAccountIsSkippedUnlessForced()
    {
        var f = new Fixture();
        using var svc = f.Build();
        f.Connectivity.SetAccount(f.Account.Id, false);
        await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var result = await svc.FlushAsync();
        Assert.Equal(1, result.Skipped);
        Assert.Empty(f.Smtp.Sent);

        var forced = await svc.FlushAsync(force: true);
        Assert.Equal(1, forced.Sent);
    }

    [Fact]
    public async Task SentAppendFailureDoesNotFailTheRow()
    {
        var f = new Fixture();
        using var svc = f.Build();
        f.Mail.SentAppendFailure = new InvalidOperationException("no Sent folder");
        await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var result = await svc.FlushAsync();

        Assert.Equal(1, result.Sent);
        Assert.Empty(await svc.ListAsync());
    }

    [Fact]
    public async Task ItemsDrainOldestFirst()
    {
        var f = new Fixture();
        using var svc = f.Build();
        var first = await svc.EnqueueSendAsync(f.Compose("first"), f.Account.Id, null);
        var row = await f.Store.LoadOutboxItemAsync(first);
        row!.CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        await f.Store.UpsertOutboxItemAsync(row, f.Compose("first"));
        await svc.EnqueueSendAsync(f.Compose("second"), f.Account.Id, null);

        await svc.FlushAsync();

        Assert.Equal(["first", "second"], f.Smtp.Sent.Select(s => s.Compose.Subject));
    }

    [Fact]
    public async Task FlushAccountOnlyTouchesThatAccount()
    {
        var f = new Fixture();
        using var svc = f.Build();
        var other = Guid.NewGuid();
        await svc.EnqueueSendAsync(f.Compose("mine"), f.Account.Id, null);
        var otherCompose = f.Compose("theirs");
        otherCompose.AccountId = other;
        await svc.EnqueueSendAsync(otherCompose, other, null);

        var result = await svc.FlushAccountAsync(f.Account.Id);

        Assert.Equal(1, result.Sent);
        Assert.Equal("mine", Assert.Single(f.Smtp.Sent).Compose.Subject);
        Assert.Equal("theirs", Assert.Single(await svc.ListAsync()).Subject);
    }

    [Fact]
    public async Task RemoveReportsWhetherTheRowExisted()
    {
        var f = new Fixture();
        using var svc = f.Build();
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        Assert.True(await svc.RemoveAsync(id));
        Assert.False(await svc.RemoveAsync(id));
        Assert.Equal(0, await svc.CountAsync());
    }

    [Fact]
    public async Task ConcurrentFlushIsSkippedNotQueued()
    {
        var f = new Fixture();
        using var svc = f.Build();
        var gate = new TaskCompletionSource();
        var slowSmtp = new GatedSmtp(gate.Task);
        using var slow = new OutboxService(f.Store, f.Mail, slowSmtp, new ListAccountService(f.Account),
            new PasswordCredentialService("pw"), null, onlineMode: false);
        await slow.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var first = slow.FlushAsync();
        await slowSmtp.Started.Task;
        var second = await slow.FlushAsync();
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Sent);

        gate.SetResult();
        Assert.Equal(1, (await first).Sent);
    }

    private sealed class GatedSmtp(Task gate) : ISendMailService
    {
        public TaskCompletionSource Started { get; } = new();
        public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await gate;
        }
        public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password, string organizerEmail, CancellationToken ct = default) => Task.CompletedTask;
        public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task OnlineModeNeverTouchesTheStore()
    {
        var f = new Fixture();
        using var svc = new OutboxService(new ThrowingStore(), f.Mail, f.Smtp, new ListAccountService(f.Account),
            new PasswordCredentialService("pw"), null, onlineMode: true);

        Assert.False(svc.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EnqueueDraftAsync(f.Compose(), f.Account.Id, null));
        Assert.Empty(await svc.ListAsync());
        Assert.Equal(0, await svc.CountAsync());
        Assert.Equal(OutboxFlushResult.Nothing, await svc.FlushAsync());
        Assert.Null(await svc.LoadComposeAsync("outbox-x"));
    }

    /// <summary>What every ILocalStoreService call does in --online mode: throw.</summary>
    private sealed class ThrowingStore : StubLocalStoreService
    {
        public override Task UpsertOutboxItemAsync(OutboxItem item, ComposeModel compose) => throw new InvalidOperationException("no store");
        public override Task<List<OutboxItem>> LoadOutboxItemsAsync() => throw new InvalidOperationException("no store");
        public override Task<OutboxItem?> LoadOutboxItemAsync(string id) => throw new InvalidOperationException("no store");
        public override Task<ComposeModel?> LoadOutboxComposeAsync(string id) => throw new InvalidOperationException("no store");
        public override Task<int> CountOutboxItemsAsync() => throw new InvalidOperationException("no store");
    }

    [Fact]
    public async Task ComingBackOnlineDrainsWithoutBeingAsked()
    {
        var f = new Fixture();
        using var svc = f.Build();
        await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        f.Connectivity.RaiseOnlineChanged(true);

        // The reconnect drain is debounced by a couple of seconds so several accounts returning
        // together start one drain, not one each.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (f.Smtp.Sent.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Single(f.Smtp.Sent);
        Assert.Single(f.Completed);
    }

    [Fact]
    public void DisposeUnsubscribesFromConnectivity()
    {
        var f = new Fixture();
        var svc = f.Build();
        svc.Dispose();
        // Raising after dispose must not throw and must not start work on a disposed service.
        f.Connectivity.RaiseOnlineChanged(true);
        Assert.Equal(0, f.Connectivity.OnlineChangedSubscribers);
    }

    [Fact]
    public async Task ARowLeftSendingByACrashIsGivenBackToTheQueue()
    {
        var f = new Fixture();
        using var svc = f.Build();
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);
        // The previous process died mid-send: the row says Sending and nobody will finish it.
        await f.Store.UpdateOutboxStateAsync(id, OutboxState.Sending, 1, null, null);

        var result = await svc.FlushAsync();

        Assert.Equal(1, result.Sent);
        Assert.Empty(await svc.ListAsync());
    }

    [Fact]
    public async Task ARowHeldByAComposeWindowIsNeverDrainedEvenWhenForced()
    {
        var f = new Fixture();
        using var svc = f.Build();
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);
        svc.Hold(id);

        Assert.Equal(1, (await svc.FlushAsync()).Skipped);
        Assert.Equal(1, (await svc.FlushAsync(force: true)).Skipped);
        Assert.Empty(f.Smtp.Sent);

        svc.Release(id);
        Assert.Equal(1, (await svc.FlushAsync()).Sent);
    }

    [Fact]
    public async Task AForcedDrainThatCannotReachTheServerLeavesTheScheduleAlone()
    {
        var f = new Fixture();
        using var svc = f.Build();
        f.Smtp.SendFailure = Unreachable();
        var id = await svc.EnqueueSendAsync(f.Compose(), f.Account.Id, null);

        var forced = await svc.FlushAsync(force: true);

        Assert.Equal(1, forced.Deferred);
        Assert.Equal(0, forced.Skipped);
        var row = await svc.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal(OutboxState.Pending, row.State);
        Assert.Equal(0, row.Attempts);          // Send Outbox Now is not a strike against the row
        Assert.Null(row.NextAttemptUtc);
    }

    [Fact]
    public async Task ARowRewrittenWhileItWasBeingSentIsKeptForTheNextDrain()
    {
        var f = new Fixture();
        var gate = new TaskCompletionSource();
        var slowSmtp = new GatedSmtp(gate.Task);
        using var svc = new OutboxService(f.Store, f.Mail, slowSmtp, new ListAccountService(f.Account),
            new PasswordCredentialService("pw"), null, onlineMode: false);
        var id = await svc.EnqueueSendAsync(f.Compose("first wording"), f.Account.Id, null);

        var drain = svc.FlushAsync();
        await slowSmtp.Started.Task;
        // The user saved a newer version into the same row while the old one was on the wire.
        await svc.EnqueueDraftAsync(f.Compose("second wording"), f.Account.Id, id);
        gate.SetResult();
        var result = await drain;

        Assert.Equal(1, result.Sent);
        var kept = Assert.Single(await svc.ListAsync());
        Assert.Equal("second wording", kept.Subject);
        Assert.Equal(OutboxState.Pending, kept.State);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(9, 32)]
    public void BackoffDoublesAndCaps(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), OutboxService.Backoff(attempts));
    }
}
