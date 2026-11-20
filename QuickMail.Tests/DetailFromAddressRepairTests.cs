using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #636: a detail cached before the from_addr column existed serves the summary's
/// display-name-only From, so the same message showed a full address when opened from the server
/// and a bare name when opened from cache. Those rows repair themselves on the next open.
/// </summary>
public class DetailFromAddressRepairTests
{
    private sealed class RecordingMail : StubImapMailServiceBase
    {
        public int ForegroundFetches;
        public int BackgroundFetches;
        public Exception? FetchThrows;
        public string FreshFrom = "Kelly Ford <kelly@example.com>";

        private Task<MailMessageDetail> Fetch(Guid accountId, string folderName, string messageId)
        {
            if (FetchThrows != null) return Task.FromException<MailMessageDetail>(FetchThrows);
            return Task.FromResult(new MailMessageDetail
            {
                AccountId = accountId, FolderName = folderName, MessageId = messageId,
                From = FreshFrom, PlainTextBody = "fresh body",
            });
        }

        public override Task<MailMessageDetail> GetMessageDetailAsync(
            Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            ForegroundFetches++;
            return Fetch(accountId, folderName, messageId);
        }

        public override Task<MailMessageDetail> PrefetchMessageDetailAsync(
            Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            BackgroundFetches++;
            return Fetch(accountId, folderName, messageId);
        }
    }

    private sealed class RecordingStore : StubLocalStoreService
    {
        public MailMessageDetail? Upserted;
        public override Task UpsertDetailAsync(MailMessageDetail detail)
        {
            Upserted = detail;
            return Task.CompletedTask;
        }
    }

    private static MailMessageDetail Cached(string from) => new()
    {
        AccountId = Guid.NewGuid(), FolderName = "Inbox", MessageId = "1",
        From = from, PlainTextBody = "cached body",
    };

    [Fact]
    public async Task ANameOnlyFrom_IsRefetchedAndReCached()
    {
        var mail = new RecordingMail();
        var store = new RecordingStore();

        var result = await DetailFromAddressRepair.RepairAsync(
            Cached("Kelly Ford"), store, mail, background: false, CancellationToken.None);

        Assert.Equal("Kelly Ford <kelly@example.com>", result.From);
        Assert.Equal(1, mail.ForegroundFetches);
        Assert.Equal("Kelly Ford <kelly@example.com>", store.Upserted?.From);
    }

    [Fact]
    public async Task AFromThatAlreadyHasAnAddress_IsLeftAlone()
    {
        var mail = new RecordingMail();
        var store = new RecordingStore();

        var result = await DetailFromAddressRepair.RepairAsync(
            Cached("Kelly Ford <kelly@example.com>"), store, mail, background: false, CancellationToken.None);

        Assert.Equal("Kelly Ford <kelly@example.com>", result.From);
        Assert.Equal(0, mail.ForegroundFetches);
        Assert.Null(store.Upserted);
    }

    [Fact]
    public async Task AnEmptyFrom_IsLeftAlone()
    {
        // A detail whose summary row is gone — a deleted message whose cached body still backs a
        // calendar event — reads as an empty From. Re-fetching it would fail on every open, and
        // there was no From header to recover in the first place.
        var mail = new RecordingMail();
        var store = new RecordingStore();

        var result = await DetailFromAddressRepair.RepairAsync(
            Cached(string.Empty), store, mail, background: false, CancellationToken.None);

        Assert.Equal(string.Empty, result.From);
        Assert.Equal(0, mail.ForegroundFetches);
    }

    [Fact]
    public async Task AFailedFetch_KeepsTheCachedMessage()
    {
        // POP3's cache is the only copy, and an IMAP message may be gone from the server. A
        // name-only From is worse than a full one and far better than an empty message.
        var mail = new RecordingMail { FetchThrows = new InvalidOperationException("gone") };
        var store = new RecordingStore();

        var result = await DetailFromAddressRepair.RepairAsync(
            Cached("Kelly Ford"), store, mail, background: false, CancellationToken.None);

        Assert.Equal("Kelly Ford", result.From);
        Assert.Equal("cached body", result.PlainTextBody);
        Assert.Null(store.Upserted);
    }

    [Fact]
    public async Task TheBackgroundPath_UsesThePrefetchLease_SoPrefetchDoesNotMarkMessagesRead()
    {
        var mail = new RecordingMail();
        var store = new RecordingStore();

        await DetailFromAddressRepair.RepairAsync(
            Cached("Kelly Ford"), store, mail, background: true, CancellationToken.None);

        Assert.Equal(1, mail.BackgroundFetches);
        Assert.Equal(0, mail.ForegroundFetches);
    }
}
