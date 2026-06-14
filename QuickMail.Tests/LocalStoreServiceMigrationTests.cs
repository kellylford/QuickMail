using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Covers the v1 → v2 schema migration (PR 2): unique_id INTEGER → TEXT, the pre-migration
/// mail.db backup, the DeltaToken table, and the numeric high-water-mark query.
/// </summary>
public class LocalStoreServiceMigrationTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"QuickMailMigTests-{Guid.NewGuid():N}");

    /// <summary>Builds a pre-v2 database: INTEGER unique_id columns, user_version = 1, seeded rows.</summary>
    private static void SeedV1Database(string dbPath, Guid accountId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE MessageSummary (
                unique_id    INTEGER NOT NULL,
                account_id   TEXT    NOT NULL,
                folder_name  TEXT    NOT NULL,
                from_disp    TEXT    NOT NULL DEFAULT '',
                to_addr      TEXT    NOT NULL DEFAULT '',
                subject      TEXT    NOT NULL DEFAULT '',
                date_ticks   INTEGER NOT NULL,
                is_read      INTEGER NOT NULL DEFAULT 0,
                preview_text TEXT    NOT NULL DEFAULT '',
                is_replied   INTEGER NOT NULL DEFAULT 0,
                is_forwarded INTEGER NOT NULL DEFAULT 0,
                has_attachments INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (unique_id, account_id, folder_name)
            );
            CREATE TABLE MessageDetail (
                unique_id   INTEGER NOT NULL,
                account_id  TEXT    NOT NULL,
                folder_name TEXT    NOT NULL,
                to_addr     TEXT    NOT NULL DEFAULT '',
                cc          TEXT    NOT NULL DEFAULT '',
                reply_to    TEXT    NOT NULL DEFAULT '',
                plain_body  TEXT    NOT NULL DEFAULT '',
                html_body   TEXT    NOT NULL DEFAULT '',
                attachments_json TEXT DEFAULT NULL,
                PRIMARY KEY (unique_id, account_id, folder_name)
            );
            """;
        cmd.ExecuteNonQuery();

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO MessageSummary(unique_id, account_id, folder_name, from_disp, subject, date_ticks, is_read)
                VALUES (5, $aid, 'Inbox', 'a@x.com', 'five', $ticks, 0),
                       (42, $aid, 'Inbox', 'b@x.com', 'forty-two', $ticks, 1);
                """;
            insert.Parameters.AddWithValue("$aid", accountId.ToString());
            insert.Parameters.AddWithValue("$ticks", DateTimeOffset.UtcNow.UtcTicks);
            insert.ExecuteNonQuery();
        }

        // A matching detail row for unique_id 42 — the migration rebuilds MessageDetail too,
        // so this covers that table's INTEGER -> TEXT conversion.
        using (var insertDetail = conn.CreateCommand())
        {
            insertDetail.CommandText = """
                INSERT INTO MessageDetail(unique_id, account_id, folder_name, to_addr, plain_body)
                VALUES (42, $aid, 'Inbox', 'recipient@x.com', 'body forty-two');
                """;
            insertDetail.Parameters.AddWithValue("$aid", accountId.ToString());
            insertDetail.ExecuteNonQuery();
        }

        using (var ver = conn.CreateCommand())
        {
            ver.CommandText = "PRAGMA user_version = 1;";
            ver.ExecuteNonQuery();
        }
    }

    private static int ReadUserVersion(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool TableExists(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() != null;
    }

    private static string ColumnType(string dbPath, string table, string column)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name=$c;";
        cmd.Parameters.AddWithValue("$c", column);
        return (string?)cmd.ExecuteScalar() ?? "";
    }

    [Fact]
    public async Task Migration_FromV1_PreservesRowsAsTextIds()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "mail.db");
        var accountId = Guid.NewGuid();
        SeedV1Database(dbPath, accountId);

        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        var loaded = await store.LoadAllSummariesAsync();
        Assert.Equal(2, loaded.Count);
        // CAST(unique_id AS TEXT) yields the plain decimal string — no zero padding.
        Assert.Contains(loaded, m => m.MessageId == "5"  && m.Subject == "five");
        Assert.Contains(loaded, m => m.MessageId == "42" && m.Subject == "forty-two");

        // The MessageDetail row survives the rebuild and is keyed by the text id.
        var detail = await store.LoadDetailAsync(accountId, "Inbox", "42");
        Assert.NotNull(detail);
        Assert.Equal("42", detail!.MessageId);
        Assert.Equal("recipient@x.com", detail.To);
        Assert.Equal("body forty-two", detail.PlainTextBody);

        // unique_id column is now TEXT on both tables.
        Assert.Equal("TEXT", ColumnType(dbPath, "MessageSummary", "unique_id"));
        Assert.Equal("TEXT", ColumnType(dbPath, "MessageDetail",  "unique_id"));
    }

    [Fact]
    public void Migration_FromV1_CreatesBackupAndBumpsVersionAndAddsDeltaToken()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "mail.db");
        SeedV1Database(dbPath, Guid.NewGuid());

        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        Assert.True(File.Exists(dbPath + ".pre-v2"), "pre-v2 backup should be created");
        Assert.Equal(3, ReadUserVersion(dbPath));
        Assert.True(TableExists(dbPath, "DeltaToken"), "DeltaToken table should exist after migration");
    }

    [Fact]
    public async Task Migration_FromV1_GetMaxMessageKey_IsNumericMax()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "mail.db");
        var accountId = Guid.NewGuid();
        SeedV1Database(dbPath, accountId);

        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        // 42 > 5 numerically; a lexicographic MAX would wrongly return "5".
        Assert.Equal("42", await store.GetMaxMessageKeyAsync(accountId, "Inbox"));
    }

    [Fact]
    public void FreshDatabase_InitializesAtVersion2_WithNoBackup()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "mail.db");

        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        Assert.False(File.Exists(dbPath + ".pre-v2"), "fresh DB must not produce a backup");
        Assert.Equal(3, ReadUserVersion(dbPath));
        Assert.True(TableExists(dbPath, "DeltaToken"));
        Assert.Equal("TEXT", ColumnType(dbPath, "MessageSummary", "unique_id"));
    }

    [Fact]
    public async Task FreshDatabase_RoundTripsStringMessageId()
    {
        var dir = NewTempDir();
        var accountId = Guid.NewGuid();
        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        await store.UpsertSummariesAsync([new QuickMail.Models.MailMessageSummary
        {
            MessageId  = "1001",
            AccountId  = accountId,
            FolderName = "Inbox",
            Subject    = "hello",
            Date       = DateTimeOffset.UtcNow,
        }]);

        var loaded = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.Single(loaded);
        Assert.Equal("1001", loaded[0].MessageId);
        Assert.Equal("1001", await store.GetMaxMessageKeyAsync(accountId, "Inbox"));
    }

    // ── Phase 5: IMAP ↔ local flag reconciliation (§9.3) ─────────────────────

    private static QuickMail.Models.MailMessageSummary MakeSummary(Guid accountId, string id, bool serverFlagged = false, string? flagId = null) =>
        new()
        {
            MessageId      = id,
            AccountId      = accountId,
            FolderName     = "Inbox",
            Subject        = id,
            Date           = DateTimeOffset.UtcNow,
            IsServerFlagged = serverFlagged,
            FlagId         = flagId,
        };

    [Fact]
    public async Task ExternallyFlagged_NewMessage_GetsBuiltInFlagOnUpsert()
    {
        var dir       = NewTempDir();
        var accountId = Guid.NewGuid();
        var store     = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        // Simulate a message that arrives from IMAP with \Flagged set (flagged in Outlook)
        // and no local flag_id yet (new message, first sync).
        var msg = MakeSummary(accountId, "1", serverFlagged: true);
        await store.UpsertSummariesAsync([msg]);

        var loaded = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.Equal(QuickMail.Models.FlagDefinition.BuiltInFlagId.ToString(), loaded[0].FlagId);
    }

    [Fact]
    public async Task ExternallyUnflagged_ExistingMessage_ClearsFlagOnUpsert()
    {
        var dir       = NewTempDir();
        var accountId = Guid.NewGuid();
        var store     = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        // First sync: message arrives server-flagged → gets built-in flag_id.
        await store.UpsertSummariesAsync([MakeSummary(accountId, "1", serverFlagged: true)]);

        // Second sync: server no longer reports \Flagged (externally unflagged).
        await store.UpsertSummariesAsync([MakeSummary(accountId, "1", serverFlagged: false)]);

        var loaded = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        Assert.Null(loaded[0].FlagId);
    }

    [Fact]
    public async Task LocalNamedFlag_PreservedWhenServerFlaggedOnUpsert()
    {
        var dir       = NewTempDir();
        var accountId = Guid.NewGuid();
        var customId  = Guid.NewGuid().ToString();
        var store     = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();

        // User sets a named flag ("Urgent") locally — stored via UpdateFlagIdAsync.
        await store.UpsertSummariesAsync([MakeSummary(accountId, "1", serverFlagged: true)]);
        await store.UpdateFlagIdAsync(accountId, "Inbox", "1", customId);

        // Next sync: server still reports \Flagged (which it would, since we set it).
        await store.UpsertSummariesAsync([MakeSummary(accountId, "1", serverFlagged: true)]);

        var loaded = await store.LoadFolderSummariesAsync(accountId, "Inbox");
        // Local "Urgent" flag must be preserved — not overwritten with the built-in default.
        Assert.Equal(customId, loaded[0].FlagId);
    }
}
