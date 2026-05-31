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

        // unique_id column is now TEXT.
        Assert.Equal("TEXT", ColumnType(dbPath, "MessageSummary", "unique_id"));
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
        Assert.Equal(2, ReadUserVersion(dbPath));
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
        Assert.Equal(2, ReadUserVersion(dbPath));
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
}
