// The status bar's word on offline reading (#717): how many messages have their full text, and whether
// more are coming down. The wording is a pure function; the count is a real store query.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class OfflineBodyStatusTextTests
{
    [Theory]
    [InlineData(0, 0, false, "Messages: none to download yet")]
    [InlineData(0, 12, false, "Messages: 0 of 12 downloaded")]
    [InlineData(1240, 2000, false, "Messages: 1,240 of 2,000 downloaded")]
    [InlineData(2000, 2000, false, "Messages: all 2,000 downloaded")]
    [InlineData(1, 1, false, "Messages: 1 of 1 downloaded")]
    [InlineData(120, 500, true, "Messages: downloading 120 of 500")]
    public void ItSaysWhatIsDownloaded(int downloaded, int total, bool downloading, string expected)
        => Assert.Equal(expected, MainViewModel.DescribeOfflineBodies(downloaded, total, downloading));
}

public class OfflineBodyCountStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-offcount-{Guid.NewGuid():N}");
    private readonly LocalStoreService _store;
    private readonly Guid _account = Guid.NewGuid();

    public OfflineBodyCountStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LocalStoreService(new ProfileContext(_dir));
        _store.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private MailMessageSummary Row(string id, string folder = "INBOX", int daysAgo = 0) => new()
    {
        MessageId = id, AccountId = _account, FolderName = folder, From = "Sam <sam@example.com>",
        To = "kelly@example.com", Subject = id, Date = DateTimeOffset.UtcNow.AddDays(-daysAgo),
    };

    private Task Body(string id, string folder = "INBOX")
        => _store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = id, AccountId = _account, FolderName = folder, PlainTextBody = "text",
        });

    [Fact]
    public async Task CountsOnlyTheFoldersAndDatesThePassCovers()
    {
        await _store.UpsertSummariesAsync(
        [
            Row("in-1"), Row("in-2"), Row("old", daysAgo: 40), Row("sent-1", "Sent"),
        ]);
        await Body("in-1");
        await Body("old");
        await Body("sent-1", "Sent");

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var inboxOnly = await _store.CountOfflineBodiesAsync([(_account, "INBOX")], since, TestContext.Current.CancellationToken);
        var both = await _store.CountOfflineBodiesAsync([(_account, "INBOX"), (_account, "Sent")], since, TestContext.Current.CancellationToken);
        var none = await _store.CountOfflineBodiesAsync([], since, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1), inboxOnly);
        Assert.Equal((3, 2), both);
        Assert.Equal((0, 0), none);
    }

    [Fact]
    public async Task AWiderWindowCountsOlderMailToo()
    {
        await _store.UpsertSummariesAsync([Row("new"), Row("old", daysAgo: 400)]);
        await Body("old");

        var all = await _store.CountOfflineBodiesAsync([(_account, "INBOX")], DateTimeOffset.MinValue, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1), all);
    }
}
