using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The store's record of which cached messages client rules have run on (#712). Being cached used to stand in for
/// it, but several paths cache mail without running rules — opening a folder among them — so mail one of those
/// cached first was never run through rules. These run against a real SQLite file.
/// </summary>
public class RulesWaitingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-rules-waiting-{Guid.NewGuid():N}");
    private readonly Guid _account = Guid.NewGuid();

    public RulesWaitingStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // let go of mail.db so the folder can be removed
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private LocalStoreService OpenStore()
    {
        var store = new LocalStoreService(new ProfileContext(_dir));
        store.Initialize();
        return store;
    }

    private MailMessageSummary Message(string id, string folder = "INBOX") => new()
    {
        MessageId = id, AccountId = _account, FolderName = folder,
        From = "a@b.com", To = "me@b.com", Subject = "s", Date = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AMessageNewlyCached_WaitsForRules()
    {
        var store = OpenStore();

        await store.UpsertSummariesAsync([Message("new")]);

        Assert.Equal(["new"], (await store.LoadRulesPendingSummariesAsync(_account, "INBOX")).Select(m => m.MessageId));
    }

    [Fact]
    public async Task CachingAMessageAgain_AfterItsRulesRan_DoesNotMakeItWaitAgain()
    {
        // Every new-mail sync re-caches the latest fifty messages. If that reset the record, rules would run on the
        // same mail every minute.
        var store = OpenStore();
        await store.UpsertSummariesAsync([Message("seen")]);
        await store.MarkRulesAppliedAsync(_account, "INBOX", ["seen"]);

        await store.UpsertSummariesAsync([Message("seen")]);

        Assert.Empty(await store.LoadRulesPendingSummariesAsync(_account, "INBOX"));
    }

    [Fact]
    public async Task MailCachedBeforeTheRecordExisted_CountsAsDone()
    {
        // Upgrading must never run rules over mail that was already in the mailbox. Build mail.db as the previous
        // version left it — no rules_applied column — holding one cached message, then open it with this version.
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "mail.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE MessageSummary (
                    unique_id TEXT NOT NULL, account_id TEXT NOT NULL, folder_name TEXT NOT NULL,
                    from_disp TEXT NOT NULL DEFAULT '', to_addr TEXT NOT NULL DEFAULT '', subject TEXT NOT NULL DEFAULT '',
                    date_ticks INTEGER NOT NULL, is_read INTEGER NOT NULL DEFAULT 0, preview_text TEXT NOT NULL DEFAULT '',
                    is_replied INTEGER NOT NULL DEFAULT 0, is_forwarded INTEGER NOT NULL DEFAULT 0,
                    has_attachments INTEGER NOT NULL DEFAULT 0, is_mailing_list INTEGER NOT NULL DEFAULT 0,
                    flag_id TEXT DEFAULT NULL, internet_message_id TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (unique_id, account_id, folder_name));
                PRAGMA user_version = 5;
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO MessageSummary(unique_id, account_id, folder_name, date_ticks) VALUES('old', $aid, 'INBOX', 1);";
            cmd.Parameters.AddWithValue("$aid", _account.ToString());
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = OpenStore();

        Assert.Empty(await store.LoadRulesPendingSummariesAsync(_account, "INBOX"));   // the mail already here is done…
        Assert.Equal(1, (await store.GetRulesSettledBoundaryAsync(_account, "INBOX")).MaxDateTicks);   // …and is the line
        await store.UpsertSummariesAsync([Message("arrived-after-upgrade")]);
        Assert.Equal(["arrived-after-upgrade"],                                        // …and new mail still waits
            (await store.LoadRulesPendingSummariesAsync(_account, "INBOX")).Select(m => m.MessageId));
    }

    [Fact]
    public async Task RecordingAFolderDone_LeavesOtherFoldersWaiting()
    {
        var store = OpenStore();
        await store.UpsertSummariesAsync([Message("in-archive", "Archive"), Message("in-inbox")]);

        await store.MarkFolderRulesAppliedAsync(_account, "Archive");

        Assert.Empty(await store.LoadRulesPendingSummariesAsync(_account, "Archive"));
        Assert.Equal(["in-inbox"], (await store.LoadRulesPendingSummariesAsync(_account, "INBOX")).Select(m => m.MessageId));
    }

    [Fact]
    public async Task TheSettledBoundary_IsTheNewestMailRecordedDone_NotTheNewestMailWaiting()
    {
        var store = OpenStore();
        var never = await store.GetRulesSettledBoundaryAsync(_account, "INBOX");
        Assert.Null(never.MaxNumericId);   // a folder rules have never settled has no line
        Assert.Null(never.MaxDateTicks);

        var older = Message("10");   older.Date   = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = Message("20");   newer.Date   = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var waiting = Message("99"); waiting.Date = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertSummariesAsync([older, newer, waiting]);
        await store.MarkRulesAppliedAsync(_account, "INBOX", ["10", "20"]);

        var boundary = await store.GetRulesSettledBoundaryAsync(_account, "INBOX");

        Assert.Equal(20, boundary.MaxNumericId);                    // waiting mail doesn't move the line
        Assert.Equal(newer.Date.UtcTicks, boundary.MaxDateTicks);
    }

    [Fact]
    public async Task HasAttachments_IsStoredFromTheSummary_AndALaterSummaryDoesNotClearIt()
    {
        // Rules test "has attachments" on waiting mail read back from the store, not only on mail a sync has in hand.
        var store = OpenStore();
        var first = Message("1");
        first.HasAttachments = true;          // Graph and POP3 summaries carry it
        await store.UpsertSummariesAsync([first]);
        var again = Message("1");
        again.HasAttachments = false;         // IMAP summaries don't
        await store.UpsertSummariesAsync([again]);

        Assert.True(Assert.Single(await store.LoadRulesPendingSummariesAsync(_account, "INBOX")).HasAttachments);
    }

    [Fact]
    public async Task TheSettledBoundary_NeverGoesBack_WhenSettledMailIsMovedOut()
    {
        // Someone who files everything by rule keeps an empty Inbox: each move deletes the row. If the line were worked out
        // from the rows left, it would drop, and a message a view cached next would read as older mail.
        var store = OpenStore();
        var early = Message("10"); early.Date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var late  = Message("20"); late.Date  = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertSummariesAsync([early, late]);
        await store.MarkRulesAppliedAsync(_account, "INBOX", ["10", "20"]);

        await store.DeleteSummariesAsync(_account, "INBOX", ["20"]);          // a rule moved the newest message out
        var older = Message("5"); older.Date = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertSummariesAsync([older]);
        await store.MarkRulesAppliedAsync(_account, "INBOX", ["5"]);          // settling more mail must not lower it either

        var boundary = await store.GetRulesSettledBoundaryAsync(_account, "INBOX");
        Assert.Equal(20, boundary.MaxNumericId);
        Assert.Equal(late.Date.UtcTicks, boundary.MaxDateTicks);
    }

    [Fact]
    public async Task RemovingAnAccount_RemovesItsBoundaries()
    {
        var store = OpenStore();
        await store.UpsertSummariesAsync([Message("10")]);
        await store.MarkRulesAppliedAsync(_account, "INBOX", ["10"]);

        await store.DeleteAccountDataAsync(_account);

        var boundary = await store.GetRulesSettledBoundaryAsync(_account, "INBOX");
        Assert.Null(boundary.MaxNumericId);
        Assert.Null(boundary.MaxDateTicks);
    }

    [Fact]
    public async Task WipingAnAccountsCachedMail_ClearsItsBoundaries()
    {
        // The #366 rebuild and a conversion to Microsoft 365 wipe the cache and settle it again from scratch. A line kept
        // from IMAP could sit in the future (Date headers can), and no Microsoft 365 arrival would ever reach it.
        var store = OpenStore();
        await store.UpsertSummariesAsync([Message("10")]);
        await store.MarkRulesAppliedAsync(_account, "INBOX", ["10"]);

        await store.ClearCachedMailAsync([_account]);

        var boundary = await store.GetRulesSettledBoundaryAsync(_account, "INBOX");
        Assert.Null(boundary.MaxNumericId);
        Assert.Null(boundary.MaxDateTicks);
    }

    [Fact]
    public async Task EnsuringALine_NeverReplacesOneAlreadyDrawn()
    {
        // Two passes can draw an Inbox's first line at once; the first one written stands.
        var store = OpenStore();
        await store.EnsureRulesWatermarkAsync(_account, "INBOX", 50, 500);
        await store.EnsureRulesWatermarkAsync(_account, "INBOX", 10, 100);

        var boundary = await store.GetRulesSettledBoundaryAsync(_account, "INBOX");
        Assert.Equal(50, boundary.MaxNumericId);
        Assert.Equal(500, boundary.MaxDateTicks);
    }

    [Fact]
    public async Task AnAccountHasSettledMail_OnlyOnceSomeOfItIsRecordedDone_InAnyFolder()
    {
        var store = OpenStore();
        Assert.False(await store.AccountHasRulesSettledMailAsync(_account, "local-"));

        await store.UpsertSummariesAsync([Message("1"), Message("2", "Receipts")]);
        Assert.False(await store.AccountHasRulesSettledMailAsync(_account, "local-"));   // waiting isn't settled

        await store.UpsertSummariesAsync([Message("local-abc", "Sent")]);
        await store.MarkFolderRulesAppliedAsync(_account, "Sent");
        Assert.False(await store.AccountHasRulesSettledMailAsync(_account, "local-"));   // mail the user wrote doesn't count

        await store.MarkFolderRulesAppliedAsync(_account, "Receipts");
        Assert.True(await store.AccountHasRulesSettledMailAsync(_account, "local-"));
        Assert.False(await store.AccountHasRulesSettledMailAsync(Guid.NewGuid(), "local-"));
    }
}
