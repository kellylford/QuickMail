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
    //   (no 3 → 4 data migration needed — flag_id column added via RunMigration; default NULL is correct)
    // Add new migrations as: if (version < 4) { ...; }
    private const int CurrentSchemaVersion = 3;

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
                    PRIMARY KEY (unique_id, account_id, folder_name)
                );
                INSERT INTO MessageSummary_v2
                SELECT CAST(unique_id AS TEXT), account_id, folder_name, from_disp, to_addr,
                       subject, date_ticks, is_read, preview_text, is_replied, is_forwarded,
                       has_attachments, is_mailing_list, flag_id
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
                    PRIMARY KEY (unique_id, account_id, folder_name)
                );
                INSERT INTO MessageDetail_v2
                SELECT CAST(unique_id AS TEXT), account_id, folder_name, to_addr, cc,
                       reply_to, plain_body, html_body, attachments_json
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
            INSERT INTO MessageSummary(unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, is_mailing_list, flag_id)
            VALUES($uid, $aid, $fn, $from, $to, $subj, $dt, $read, $preview, $replied, $forwarded, $ml, $flag_id)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                from_disp       = excluded.from_disp,
                to_addr         = excluded.to_addr,
                subject         = excluded.subject,
                date_ticks      = excluded.date_ticks,
                is_read         = excluded.is_read,
                is_replied      = excluded.is_replied,
                is_forwarded    = excluded.is_forwarded,
                is_mailing_list = excluded.is_mailing_list,
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
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id " +
            "FROM MessageSummary ORDER BY date_ticks DESC;";
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id " +
            "FROM MessageSummary WHERE account_id=$aid ORDER BY date_ticks DESC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id " +
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
            cmd.CommandText =
                $"DELETE FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageDetail  WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});";
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
            "DELETE FROM MessageSummary WHERE account_id = $aid;";
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

    public async Task UpsertDetailAsync(MailMessageDetail d)
    {
        var attJson = d.Attachments.Count > 0
            ? JsonSerializer.Serialize(d.Attachments.Select(a => new { a.FileName, a.ContentType, a.FileSize, a.PartSpecifier }))
            : null;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MessageDetail(unique_id, account_id, folder_name, to_addr, cc, reply_to, plain_body, html_body, attachments_json)
            VALUES($uid, $aid, $fn, $to, $cc, $rt, $plain, $html, $attjson)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                to_addr          = excluded.to_addr,
                cc               = excluded.cc,
                reply_to         = excluded.reply_to,
                plain_body       = excluded.plain_body,
                html_body        = excluded.html_body,
                attachments_json = excluded.attachments_json;
            """;
        cmd.Parameters.AddWithValue("$uid",    d.MessageId);
        cmd.Parameters.AddWithValue("$aid",    d.AccountId.ToString());
        cmd.Parameters.AddWithValue("$fn",     d.FolderName);
        cmd.Parameters.AddWithValue("$to",     d.To);
        cmd.Parameters.AddWithValue("$cc",     d.Cc);
        cmd.Parameters.AddWithValue("$rt",     d.ReplyTo);
        cmd.Parameters.AddWithValue("$plain",  d.PlainTextBody);
        cmd.Parameters.AddWithValue("$html",   d.HtmlBody);
        cmd.Parameters.AddWithValue("$attjson", (object?)attJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        // Update the summary's has_attachments flag
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText =
            "UPDATE MessageSummary SET has_attachments=$ha " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd2.Parameters.AddWithValue("$ha",  d.Attachments.Count > 0 ? 1 : 0);
        cmd2.Parameters.AddWithValue("$uid", d.MessageId);
        cmd2.Parameters.AddWithValue("$aid", d.AccountId.ToString());
        cmd2.Parameters.AddWithValue("$fn",  d.FolderName);
        await cmd2.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    public async Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.to_addr, d.cc, d.reply_to, d.plain_body, d.html_body,
                   s.from_disp, s.subject, s.date_ticks, s.is_read, d.attachments_json
            FROM MessageDetail d
            JOIN MessageSummary s USING (unique_id, account_id, folder_name)
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
            From          = r.GetString(5),
            Subject       = r.GetString(6),
            Date          = new DateTimeOffset(r.GetInt64(7), TimeSpan.Zero),
            IsRead        = r.GetInt64(8) != 0,
            Attachments   = attachments,
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
}
