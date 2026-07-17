using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public class LocalStoreService : ILocalStoreService
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public LocalStoreService(ProfileContext profile)
    {
        _dbPath = Path.Combine(profile.ProfileDir, "mail.db");
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;";
    }

    public void Initialize()
    {
        // Pre-migration backup: if we're upgrading from a pre-v2 (INTEGER unique_id) database,
        // copy mail.db to mail.db.pre-v2 before touching it. The v2 migration is one-way, so the
        // backup is the safety net. Preserved indefinitely; the user can delete it manually.
        if (File.Exists(_dbPath))
        {
            using var probe = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;");
            probe.Open();
            if (GetUserVersion(probe) < 2)
            {
                var backupPath = _dbPath + ".pre-v2";
                File.Copy(_dbPath, backupPath, overwrite: true);
                LogService.Log($"LocalStoreService: backed up mail.db to {backupPath} before v2 migration");
            }
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS MessageSummary (
                unique_id    TEXT    NOT NULL,
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
                PRIMARY KEY (unique_id, account_id, folder_name)
            );
            CREATE INDEX IF NOT EXISTS idx_summary_date
                ON MessageSummary(date_ticks DESC);

            CREATE TABLE IF NOT EXISTS MessageDetail (
                unique_id   TEXT    NOT NULL,
                account_id  TEXT    NOT NULL,
                folder_name TEXT    NOT NULL,
                to_addr     TEXT    NOT NULL DEFAULT '',
                cc          TEXT    NOT NULL DEFAULT '',
                reply_to    TEXT    NOT NULL DEFAULT '',
                plain_body  TEXT    NOT NULL DEFAULT '',
                html_body   TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (unique_id, account_id, folder_name)
            );
            """;
        cmd.ExecuteNonQuery();

        // Migration: add columns added after initial release.
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN to_addr        TEXT    NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN preview_text  TEXT    NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_replied    INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_forwarded  INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN has_attachments INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_mailing_list INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN attachments_json TEXT DEFAULT NULL;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN flag_id TEXT DEFAULT NULL;");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN calendar_ics TEXT DEFAULT NULL;");
        // Stable RFC 5322 Message-ID for collapsing duplicate copies across folders (issue #220).
        // Adds the column for DBs already past the v1→v2 rebuild; fresh/v1 DBs get it from the
        // rebuild's schema below. No index: deduplication runs in memory (MessageDeduplicator), so
        // nothing queries this column — an index would only add upsert write cost.
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN internet_message_id TEXT NOT NULL DEFAULT '';");

        // CalendarEvent table (schema v4). Additive — no existing table touched.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CalendarEvent (
                uid              TEXT    NOT NULL,
                account_id       TEXT    NOT NULL,
                summary          TEXT    NOT NULL DEFAULT '',
                description      TEXT    NOT NULL DEFAULT '',
                location         TEXT    NOT NULL DEFAULT '',
                organizer        TEXT    NOT NULL DEFAULT '',
                organizer_name   TEXT    NOT NULL DEFAULT '',
                start_time_ticks INTEGER DEFAULT NULL,
                end_time_ticks   INTEGER DEFAULT NULL,
                sequence         TEXT    DEFAULT NULL,
                method           TEXT    DEFAULT NULL,
                source_message_id TEXT   NOT NULL DEFAULT '',
                source_folder    TEXT    NOT NULL DEFAULT '',
                response_status  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (uid, account_id)
            );
            CREATE INDEX IF NOT EXISTS idx_calendar_start
                ON CalendarEvent(start_time_ticks);
            """;
        cmd.ExecuteNonQuery();

        // All-day flag for locally-authored appointments. Idempotent ALTER (RunMigration ignores
        // "duplicate column" on databases that already have it).
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN is_all_day INTEGER NOT NULL DEFAULT 0;");

        // RRULE string for repeating appointments (null for one-offs). Idempotent ALTER.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN recurrence_rule TEXT DEFAULT NULL;");

        // Excluded occurrence starts for a recurring master ("delete just this one"). Idempotent ALTER.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN exdates TEXT DEFAULT NULL;");

        RunDataMigrations(conn);
    }

    // SQLite's PRAGMA user_version stores a single integer per database. We use it as a
    // gate so data migrations run exactly once instead of on every startup — the to_addr
    // backfill in particular used to scan the whole MessageSummary table every launch.
    //
    // Migration numbering:
    //   0 → 1   to_addr backfill from MessageDetail
    //   1 → 2   unique_id INTEGER → TEXT (string MessageId); add DeltaToken table
    //   2 → 3   is_mailing_list backfill from to_addr patterns
    //   (no 3 → 4 data migration needed — CalendarEvent table + calendar_ics column
    //    added via CREATE TABLE IF NOT EXISTS / RunMigration; defaults are correct.
    //    Harvesting from existing calendar_ics rows happens on demand via CalendarService.RefreshAsync.)
    //   4 → 5   clear MessageSummary so the next sync backfills internet_message_id (issue #220
    //           duplicate-collapse). The Message-ID can't be reconstructed from cached rows, and
    //           the cache repopulates automatically on the next launch's sync. MessageDetail (bodies)
    //           is left intact — same key, still valid.
    // Add new migrations as: if (version < 5) { ...; }
    private const int CurrentSchemaVersion = 5;

    private static void RunDataMigrations(SqliteConnection conn)
    {
        var version = GetUserVersion(conn);
        if (version >= CurrentSchemaVersion) return;

        if (version < 1)
        {
            using var backfillCmd = conn.CreateCommand();
            backfillCmd.CommandText = """
                UPDATE MessageSummary
                SET to_addr = COALESCE((
                    SELECT d.to_addr
                    FROM MessageDetail d
                    WHERE d.unique_id = MessageSummary.unique_id
                      AND d.account_id = MessageSummary.account_id
                      AND d.folder_name = MessageSummary.folder_name
                ), to_addr)
                WHERE to_addr = '';
                """;
            backfillCmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            // Convert unique_id from INTEGER to TEXT for both tables, and add the DeltaToken
            // table used by the Graph backend. Rebuild-and-rename is the standard SQLite idiom
            // for a column type change. Wrapped in a transaction: on failure the rollback leaves
            // the v1 schema intact (the mail.db.pre-v2 backup is the second safety net).
            using var tx = conn.BeginTransaction();
            using var migrateCmd = conn.CreateCommand();
            migrateCmd.Transaction = tx;
            migrateCmd.CommandText = """
                CREATE TABLE MessageSummary_v2 (
                    unique_id    TEXT    NOT NULL,
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
                    is_mailing_list INTEGER NOT NULL DEFAULT 0,
                    flag_id      TEXT    DEFAULT NULL,
                    internet_message_id TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (unique_id, account_id, folder_name)
                );
                INSERT INTO MessageSummary_v2
                SELECT CAST(unique_id AS TEXT), account_id, folder_name, from_disp, to_addr,
                       subject, date_ticks, is_read, preview_text, is_replied, is_forwarded,
                       has_attachments, is_mailing_list, flag_id, internet_message_id
                FROM MessageSummary;
                DROP TABLE MessageSummary;
                ALTER TABLE MessageSummary_v2 RENAME TO MessageSummary;
                CREATE INDEX idx_summary_date ON MessageSummary(date_ticks DESC);

                CREATE TABLE MessageDetail_v2 (
                    unique_id   TEXT NOT NULL,
                    account_id  TEXT NOT NULL,
                    folder_name TEXT NOT NULL,
                    to_addr     TEXT NOT NULL DEFAULT '',
                    cc          TEXT NOT NULL DEFAULT '',
                    reply_to    TEXT NOT NULL DEFAULT '',
                    plain_body  TEXT NOT NULL DEFAULT '',
                    html_body   TEXT NOT NULL DEFAULT '',
                    attachments_json TEXT DEFAULT NULL,
                    calendar_ics TEXT DEFAULT NULL,
                    PRIMARY KEY (unique_id, account_id, folder_name)
                );
                INSERT INTO MessageDetail_v2
                SELECT CAST(unique_id AS TEXT), account_id, folder_name, to_addr, cc,
                       reply_to, plain_body, html_body, attachments_json, calendar_ics
                FROM MessageDetail;
                DROP TABLE MessageDetail;
                ALTER TABLE MessageDetail_v2 RENAME TO MessageDetail;

                CREATE TABLE DeltaToken (
                    account_id   TEXT NOT NULL,
                    folder_id    TEXT NOT NULL,
                    delta_token  TEXT NOT NULL,
                    updated_utc  INTEGER NOT NULL,
                    PRIMARY KEY (account_id, folder_id)
                );
                """;
            migrateCmd.ExecuteNonQuery();
            tx.Commit();
        }

        if (version < 3)
        {
            // Best-effort backfill: flag rows whose to_addr contains a recognisable
            // mailing-list domain. The IMAP List-Id header detection handles newly
            // synced messages; this covers rows already in the DB.
            using var mlCmd = conn.CreateCommand();
            mlCmd.CommandText = """
                UPDATE MessageSummary
                SET is_mailing_list = 1
                WHERE is_mailing_list = 0
                  AND (
                        to_addr LIKE '%.groups.io%'
                     OR to_addr LIKE '%freelists.org%'
                     OR to_addr LIKE '%@listserv.%'
                     OR to_addr LIKE '%@mailman.%'
                     OR to_addr LIKE '%yahoogroups.com%'
                     OR to_addr LIKE '%googlegroups.com%'
                  );
                """;
            mlCmd.ExecuteNonQuery();
        }

        if (version < 5)
        {
            // Purge cached summaries so the next sync repopulates internet_message_id, which
            // aggregate views need to collapse Gmail's per-folder duplicate copies (issue #220).
            using var clearCmd = conn.CreateCommand();
            clearCmd.CommandText = "DELETE FROM MessageSummary;";
            clearCmd.ExecuteNonQuery();
        }

        SetUserVersion(conn, CurrentSchemaVersion);
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA does not accept bound parameters; format the integer directly (safe — int).
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void RunMigration(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch { /* column already exists — safe to ignore */ }
    }

    public async Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MessageSummary(unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, is_mailing_list, flag_id, internet_message_id)
            VALUES($uid, $aid, $fn, $from, $to, $subj, $dt, $read, $preview, $replied, $forwarded, $ml, $flag_id, $imid)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                from_disp       = excluded.from_disp,
                to_addr         = excluded.to_addr,
                subject         = excluded.subject,
                date_ticks      = excluded.date_ticks,
                is_read         = excluded.is_read,
                is_replied      = excluded.is_replied,
                is_forwarded    = excluded.is_forwarded,
                is_mailing_list = excluded.is_mailing_list,
                internet_message_id = CASE WHEN excluded.internet_message_id = '' THEN internet_message_id ELSE excluded.internet_message_id END,
                preview_text    = CASE WHEN excluded.preview_text = '' THEN preview_text ELSE excluded.preview_text END,
                flag_id         = CASE
                    WHEN excluded.flag_id IS NULL THEN NULL
                    WHEN flag_id IS NULL           THEN excluded.flag_id
                    ELSE                                flag_id
                    END;
            """;
            // flag_id reconciliation on DO UPDATE (§9.3):
            //   server unflagged (excluded.flag_id NULL)  → clear any local flag (external unflag)
            //   server flagged, no local flag             → apply built-in default flag id
            //   server flagged, local named flag present  → preserve the user's named flag
        var pUid       = cmd.Parameters.Add("$uid",       SqliteType.Text);
        var pAid       = cmd.Parameters.Add("$aid",       SqliteType.Text);
        var pFn        = cmd.Parameters.Add("$fn",        SqliteType.Text);
        var pFrom      = cmd.Parameters.Add("$from",      SqliteType.Text);
        var pTo        = cmd.Parameters.Add("$to",        SqliteType.Text);
        var pSubj      = cmd.Parameters.Add("$subj",      SqliteType.Text);
        var pDt        = cmd.Parameters.Add("$dt",        SqliteType.Integer);
        var pRead      = cmd.Parameters.Add("$read",      SqliteType.Integer);
        var pPreview   = cmd.Parameters.Add("$preview",   SqliteType.Text);
        var pReplied   = cmd.Parameters.Add("$replied",   SqliteType.Integer);
        var pForwarded = cmd.Parameters.Add("$forwarded", SqliteType.Integer);
        var pMl        = cmd.Parameters.Add("$ml",        SqliteType.Integer);
        var pFlagId    = cmd.Parameters.Add("$flag_id",   SqliteType.Text);
        var pImid      = cmd.Parameters.Add("$imid",      SqliteType.Text);

        foreach (var s in summaries)
        {
            pUid.Value       = s.MessageId;
            pAid.Value       = s.AccountId.ToString();
            pFn.Value        = s.FolderName;
            pFrom.Value      = s.From;
            pTo.Value        = s.To;
            pSubj.Value      = s.Subject;
            pDt.Value        = s.Date.UtcTicks;
            pRead.Value      = s.IsRead          ? 1 : 0;
            pPreview.Value   = s.Preview;
            pReplied.Value   = s.IsReplied        ? 1 : 0;
            pForwarded.Value = s.IsForwarded      ? 1 : 0;
            pMl.Value        = s.IsMailingList    ? 1 : 0;
            pFlagId.Value    = s.IsServerFlagged
                ? (object)FlagDefinition.BuiltInFlagId.ToString()
                : DBNull.Value;
            pImid.Value      = s.InternetMessageId ?? string.Empty;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id " +
            "FROM MessageSummary ORDER BY date_ticks DESC;";
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id " +
            "FROM MessageSummary WHERE account_id=$aid ORDER BY date_ticks DESC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id " +
            "FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn ORDER BY date_ticks DESC" +
            (limit.HasValue ? " LIMIT $limit;" : ";");
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit.Value));
        return await ReadSummariesAsync(cmd);
    }

    public async Task<bool> HasSummariesMissingRecipientsAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM MessageSummary WHERE to_addr = '' LIMIT 1);";
        var result = await cmd.ExecuteScalarAsync();
        return result is long value && value != 0;
    }

    public async Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds)
    {
        // Chunk so the IN list stays well under SQLite's compiled-parameter limit (~999).
        // Two round-trips per chunk regardless of size beats 2N round-trips for the old loop.
        const int chunkSize = 500;
        var ids = messageIds as IList<string> ?? messageIds.ToList();
        if (ids.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        for (int offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            // Build "$u0,$u1,..." once for this chunk.
            var placeholders = string.Join(',', Enumerable.Range(0, count).Select(i => $"$u{i}"));
            await using var cmd = conn.CreateCommand();
            // Also clear source links in CalendarEvent for any deleted message that was
            // an invite source.  Cleared rather than deleted because the event itself
            // (the user's acceptance, the meeting time) should stay visible in the
            // calendar even after the original invite email is purged from the cache.
            cmd.CommandText =
                $"DELETE FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageDetail  WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});" +
                $"UPDATE CalendarEvent SET source_message_id='', source_folder='' WHERE account_id=$aid AND source_folder=$fn AND source_message_id IN ({placeholders});";
            cmd.Parameters.AddWithValue("$aid", accountId.ToString());
            cmd.Parameters.AddWithValue("$fn",  folderName);
            for (int i = 0; i < count; i++)
                cmd.Parameters.AddWithValue($"$u{i}", ids[offset + i]);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task DeleteAccountDataAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM MessageDetail  WHERE account_id = $aid;" +
            "DELETE FROM MessageSummary WHERE account_id = $aid;" +
            "DELETE FROM CalendarEvent  WHERE account_id = $aid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET is_read=$read " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$read", isRead ? 1 : 0);
        cmd.Parameters.AddWithValue("$uid",  messageId);
        cmd.Parameters.AddWithValue("$aid",  accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",   folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        cmd.CommandText =
            "UPDATE MessageSummary SET is_read=$read " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pRead = cmd.Parameters.Add("$read", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pUid  = cmd.Parameters.Add("$uid",  Microsoft.Data.Sqlite.SqliteType.Text);
        var pAid  = cmd.Parameters.Add("$aid",  Microsoft.Data.Sqlite.SqliteType.Text);
        var pFn   = cmd.Parameters.Add("$fn",   Microsoft.Data.Sqlite.SqliteType.Text);
        pRead.Value = isRead ? 1 : 0;
        foreach (var (accountId, folderName, messageId) in items)
        {
            pUid.Value = messageId;
            pAid.Value = accountId.ToString();
            pFn.Value  = folderName;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET flag_id=$fid " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$fid", (object?)flagId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateFlagIdBatchAsync(
        IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items,
        string? flagId)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        cmd.CommandText =
            "UPDATE MessageSummary SET flag_id=$fid " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pFid = cmd.Parameters.Add("$fid", SqliteType.Text);
        var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
        var pAid = cmd.Parameters.Add("$aid", SqliteType.Text);
        var pFn  = cmd.Parameters.Add("$fn",  SqliteType.Text);
        pFid.Value = (object?)flagId ?? DBNull.Value;
        foreach (var (accountId, folderName, messageId) in items)
        {
            pUid.Value = messageId;
            pAid.Value = accountId.ToString();
            pFn.Value  = folderName;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET preview_text=$preview " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$uid",     messageId);
        cmd.Parameters.AddWithValue("$aid",     accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",      folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePreviewsBatchAsync(
        Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates)
    {
        var list = updates as IList<(string, string)> ?? updates.ToList();
        if (list.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET preview_text=$preview " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pPrev = cmd.Parameters.Add("$preview", SqliteType.Text);
        var pUid  = cmd.Parameters.Add("$uid",     SqliteType.Text);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);

        foreach (var (messageId, preview) in list)
        {
            pPrev.Value = preview;
            pUid.Value  = messageId;
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task UpsertDetailAsync(MailMessageDetail detail)
    {
        var attJson = detail.Attachments.Count > 0
            ? JsonSerializer.Serialize(detail.Attachments.Select(a => new { a.FileName, a.ContentType, a.FileSize, a.PartSpecifier }))
            : null;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MessageDetail(unique_id, account_id, folder_name, to_addr, cc, reply_to, plain_body, html_body, attachments_json, calendar_ics)
            VALUES($uid, $aid, $fn, $to, $cc, $rt, $plain, $html, $attjson, $ics)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                to_addr          = excluded.to_addr,
                cc               = excluded.cc,
                reply_to         = excluded.reply_to,
                plain_body       = excluded.plain_body,
                html_body        = excluded.html_body,
                attachments_json = excluded.attachments_json,
                calendar_ics     = excluded.calendar_ics;
            """;
        cmd.Parameters.AddWithValue("$uid",    detail.MessageId);
        cmd.Parameters.AddWithValue("$aid",    detail.AccountId.ToString());
        cmd.Parameters.AddWithValue("$fn",     detail.FolderName);
        cmd.Parameters.AddWithValue("$to",     detail.To);
        cmd.Parameters.AddWithValue("$cc",     detail.Cc);
        cmd.Parameters.AddWithValue("$rt",     detail.ReplyTo);
        cmd.Parameters.AddWithValue("$plain",  detail.PlainTextBody);
        cmd.Parameters.AddWithValue("$html",   detail.HtmlBody);
        cmd.Parameters.AddWithValue("$attjson", (object?)attJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ics",    (object?)detail.CalendarIcs ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        // Update the summary's has_attachments flag
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText =
            "UPDATE MessageSummary SET has_attachments=$ha " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd2.Parameters.AddWithValue("$ha",  detail.Attachments.Count > 0 ? 1 : 0);
        cmd2.Parameters.AddWithValue("$uid", detail.MessageId);
        cmd2.Parameters.AddWithValue("$aid", detail.AccountId.ToString());
        cmd2.Parameters.AddWithValue("$fn",  detail.FolderName);
        await cmd2.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    public async Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // LEFT JOIN so the detail can be loaded even when the MessageSummary row is
        // missing (e.g. the message was purged from the sync range or deleted from the
        // server, but the cached body + calendar ICS remain). Without this, opening a
        // calendar event's source invite fails with "message not found" because the
        // INNER JOIN returns nothing.
        cmd.CommandText = """
            SELECT d.to_addr, d.cc, d.reply_to, d.plain_body, d.html_body,
                   s.from_disp, s.subject, s.date_ticks, s.is_read, d.attachments_json, d.calendar_ics
            FROM MessageDetail d
            LEFT JOIN MessageSummary s USING (unique_id, account_id, folder_name)
            WHERE d.unique_id=$uid AND d.account_id=$aid AND d.folder_name=$fn;
            """;
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        List<AttachmentModel> attachments = [];
        if (!r.IsDBNull(9))
        {
            var json = r.GetString(9);
            try
            {
                var metas = JsonSerializer.Deserialize<List<AttachmentMeta>>(json);
                if (metas != null)
                    attachments = metas.Select(m => new AttachmentModel
                    {
                        FileName      = m.FileName,
                        ContentType   = m.ContentType,
                        FileSize      = m.FileSize,
                        PartSpecifier = m.PartSpecifier,
                    }).ToList();
            }
            catch { /* corrupt json — ignore */ }
        }

        var calendarIcs = r.IsDBNull(10) ? string.Empty : r.GetString(10);

        return new MailMessageDetail
        {
            MessageId     = messageId,
            AccountId     = accountId,
            FolderName    = folderName,
            To            = r.GetString(0),
            Cc            = r.GetString(1),
            ReplyTo       = r.GetString(2),
            PlainTextBody = r.GetString(3),
            HtmlBody      = r.GetString(4),
            From          = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            Subject       = r.IsDBNull(6) ? "(no subject)" : r.GetString(6),
            Date          = r.IsDBNull(7) ? DateTimeOffset.MinValue : new DateTimeOffset(r.GetInt64(7), TimeSpan.Zero),
            IsRead        = !r.IsDBNull(8) && r.GetInt64(8) != 0,
            Attachments   = attachments,
            CalendarIcs   = calendarIcs,
            CalendarInvite = string.IsNullOrWhiteSpace(calendarIcs) ? null : IcsModel.Parse(calendarIcs),
        };
    }

    public async Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = new HashSet<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(r.GetString(0));
        return result;
    }

    public async Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // IMAP stores plain decimal UID strings; compute the numeric high-water mark via CAST so
        // "9" < "10" sorts correctly (lexicographic MAX would not). Graph rows are non-numeric and
        // CAST to 0 — harmless, since Graph never reads this value.
        cmd.CommandText =
            "SELECT COALESCE(MAX(CAST(unique_id AS INTEGER)), 0) FROM MessageSummary " +
            "WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = await cmd.ExecuteScalarAsync();
        var max = result is long l ? l : 0L;
        return max.ToString(CultureInfo.InvariantCulture);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<List<MailMessageSummary>> ReadSummariesAsync(SqliteCommand cmd)
    {
        var list = new List<MailMessageSummary>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new MailMessageSummary
            {
                MessageId   = r.GetString(0),
                AccountId   = Guid.Parse(r.GetString(1)),
                FolderName  = r.GetString(2),
                From        = r.GetString(3),
                To          = r.GetString(4),
                Subject     = r.GetString(5),
                Date        = new DateTimeOffset(r.GetInt64(6), TimeSpan.Zero),
                IsRead      = r.GetInt64(7) != 0,
                Preview     = r.GetString(8),
                IsReplied      = r.GetInt64(9) != 0,
                IsForwarded    = r.GetInt64(10) != 0,
                HasAttachments = r.GetInt64(11) != 0,
                IsMailingList  = r.GetInt64(12) != 0,
                FlagId         = r.IsDBNull(13) ? null : r.GetString(13),
                InternetMessageId = r.IsDBNull(14) ? string.Empty : r.GetString(14),
            });
        }
        return list;
    }

    public async Task<int> CountSummariesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MessageSummary WHERE account_id = @id";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
    }

    public async Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(date_ticks) FROM MessageSummary WHERE account_id = @id";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return null;
        var ticks = Convert.ToInt64(result);
        return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync();
        return c;
    }

    /// <summary>Lightweight DTO for serializing attachment metadata to JSON (no Content bytes).</summary>
    private sealed record AttachmentMeta(
        string  FileName,
        string  ContentType,
        long    FileSize,
        string? PartSpecifier);

    // ── Calendar events ──────────────────────────────────────────────────────────

    public async Task UpsertCalendarEventAsync(CalendarEvent evt)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CalendarEvent(uid, account_id, summary, description, location,
                                      organizer, organizer_name, start_time_ticks, end_time_ticks,
                                      sequence, method, source_message_id, source_folder, response_status,
                                      is_all_day, recurrence_rule, exdates)
            VALUES($uid, $aid, $sum, $desc, $loc, $org, $orgn, $st, $et, $seq, $meth, $smid, $sf, $rs, $allday, $rrule, $exd)
            ON CONFLICT(uid, account_id) DO UPDATE SET
                summary           = excluded.summary,
                description       = excluded.description,
                location          = excluded.location,
                organizer         = excluded.organizer,
                organizer_name    = excluded.organizer_name,
                start_time_ticks  = excluded.start_time_ticks,
                end_time_ticks    = excluded.end_time_ticks,
                sequence          = excluded.sequence,
                method            = excluded.method,
                source_message_id = excluded.source_message_id,
                source_folder     = excluded.source_folder,
                is_all_day        = excluded.is_all_day,
                recurrence_rule   = excluded.recurrence_rule,
                exdates           = excluded.exdates;
            """;
        cmd.Parameters.AddWithValue("$uid",  evt.Uid);
        cmd.Parameters.AddWithValue("$aid",  evt.AccountId.ToString());
        cmd.Parameters.AddWithValue("$sum",  evt.Summary);
        cmd.Parameters.AddWithValue("$desc", evt.Description);
        cmd.Parameters.AddWithValue("$loc",  evt.Location);
        cmd.Parameters.AddWithValue("$org",  evt.Organizer);
        cmd.Parameters.AddWithValue("$orgn", evt.OrganizerName);
        cmd.Parameters.AddWithValue("$st",   (object?)evt.StartTimeTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$et",   (object?)evt.EndTimeTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seq",  (object?)evt.Sequence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meth", (object?)evt.Method ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smid", evt.SourceMessageId);
        cmd.Parameters.AddWithValue("$sf",   evt.SourceFolder);
        cmd.Parameters.AddWithValue("$rs",   (int)evt.ResponseStatus);
        cmd.Parameters.AddWithValue("$allday", evt.IsAllDay ? 1 : 0);
        cmd.Parameters.AddWithValue("$rrule", (object?)evt.RecurrenceRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exd",  (object?)evt.ExDates ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task<List<CalendarEvent>> LoadCalendarEventsAsync()
    {
        var list = new List<CalendarEvent>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT uid, account_id, summary, description, location, organizer, organizer_name,
                   start_time_ticks, end_time_ticks, sequence, method, source_message_id,
                   source_folder, response_status, is_all_day, recurrence_rule, exdates
            FROM CalendarEvent
            ORDER BY start_time_ticks IS NULL, start_time_ticks ASC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new CalendarEvent
            {
                Uid              = r.GetString(0),
                AccountId        = Guid.Parse(r.GetString(1)),
                Summary          = r.GetString(2),
                Description      = r.GetString(3),
                Location         = r.GetString(4),
                Organizer        = r.GetString(5),
                OrganizerName    = r.GetString(6),
                StartTimeTicks   = r.IsDBNull(7) ? null : r.GetInt64(7),
                EndTimeTicks     = r.IsDBNull(8) ? null : r.GetInt64(8),
                Sequence         = r.IsDBNull(9) ? null : r.GetString(9),
                Method           = r.IsDBNull(10) ? null : r.GetString(10),
                SourceMessageId  = r.GetString(11),
                SourceFolder     = r.GetString(12),
                ResponseStatus   = (CalendarResponseStatus)r.GetInt32(13),
                IsAllDay         = !r.IsDBNull(14) && r.GetInt32(14) != 0,
                RecurrenceRule   = r.IsDBNull(15) ? null : r.GetString(15),
                ExDates          = r.IsDBNull(16) ? null : r.GetString(16),
            });
        }
        return list;
    }

    public async Task UpdateCalendarResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE CalendarEvent SET response_status=$rs WHERE uid=$uid AND account_id=$aid;";
        cmd.Parameters.AddWithValue("$rs",  (int)status);
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteCalendarEventAsync(string uid, Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM CalendarEvent WHERE uid=$uid AND account_id=$aid;";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<(Guid AccountId, string FolderName, string MessageId, string IcsText)>> LoadAllCalendarIcsAsync()
    {
        var list = new List<(Guid, string, string, string)>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, folder_name, unique_id, calendar_ics
            FROM MessageDetail
            WHERE calendar_ics IS NOT NULL AND calendar_ics != '';
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((
                Guid.Parse(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3)));
        }
        return list;
    }

    public async Task ClearOrphanedCalendarSourceLinksAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Clear source_message_id and source_folder on any CalendarEvent whose
        // source MessageDetail row no longer exists.  This happens when the local
        // message cache purges old messages (sync window rolls forward or the user
        // deletes a message) but the CalendarEvent outlives it.  After clearing,
        // OpenSourceMessage silently no-ops for these events instead of failing with
        // "Message UID N not found".
        cmd.CommandText = """
            UPDATE CalendarEvent
            SET source_message_id = '', source_folder = ''
            WHERE source_message_id != ''
              AND NOT EXISTS (
                  SELECT 1 FROM MessageDetail md
                  WHERE md.unique_id   = CalendarEvent.source_message_id
                    AND md.account_id  = CalendarEvent.account_id
                    AND md.folder_name = CalendarEvent.source_folder
              );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Graph delta cursors (PR 7b) ──────────────────────────────────────────────

    public async Task<string?> GetDeltaTokenAsync(Guid accountId, string folderId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT delta_token FROM DeltaToken WHERE account_id=$aid AND folder_id=$fid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fid", folderId);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SetDeltaTokenAsync(Guid accountId, string folderId, string deltaToken)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DeltaToken(account_id, folder_id, delta_token, updated_utc)
            VALUES($aid, $fid, $token, $now)
            ON CONFLICT(account_id, folder_id) DO UPDATE SET
                delta_token = excluded.delta_token,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$aid",   accountId.ToString());
        cmd.Parameters.AddWithValue("$fid",   folderId);
        cmd.Parameters.AddWithValue("$token", deltaToken);
        cmd.Parameters.AddWithValue("$now",   DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }
}
