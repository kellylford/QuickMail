using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public class LocalStoreService : ILocalStoreService
{
    private readonly string _connectionString;

    public LocalStoreService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={Path.Combine(dir, "mail.db")};Mode=ReadWriteCreate;";
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS MessageSummary (
                unique_id    INTEGER NOT NULL,
                account_id   TEXT    NOT NULL,
                folder_name  TEXT    NOT NULL,
                from_disp    TEXT    NOT NULL DEFAULT '',
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
                unique_id   INTEGER NOT NULL,
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
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN preview_text  TEXT    NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_replied    INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_forwarded  INTEGER NOT NULL DEFAULT 0;");
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
            INSERT INTO MessageSummary(unique_id, account_id, folder_name, from_disp, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded)
            VALUES($uid, $aid, $fn, $from, $subj, $dt, $read, $preview, $replied, $forwarded)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                from_disp    = excluded.from_disp,
                subject      = excluded.subject,
                date_ticks   = excluded.date_ticks,
                is_read      = excluded.is_read,
                is_replied   = excluded.is_replied,
                is_forwarded = excluded.is_forwarded,
                preview_text = CASE WHEN excluded.preview_text = '' THEN preview_text ELSE excluded.preview_text END;
            """;
        var pUid       = cmd.Parameters.Add("$uid",       SqliteType.Integer);
        var pAid       = cmd.Parameters.Add("$aid",       SqliteType.Text);
        var pFn        = cmd.Parameters.Add("$fn",        SqliteType.Text);
        var pFrom      = cmd.Parameters.Add("$from",      SqliteType.Text);
        var pSubj      = cmd.Parameters.Add("$subj",      SqliteType.Text);
        var pDt        = cmd.Parameters.Add("$dt",        SqliteType.Integer);
        var pRead      = cmd.Parameters.Add("$read",      SqliteType.Integer);
        var pPreview   = cmd.Parameters.Add("$preview",   SqliteType.Text);
        var pReplied   = cmd.Parameters.Add("$replied",   SqliteType.Integer);
        var pForwarded = cmd.Parameters.Add("$forwarded", SqliteType.Integer);

        foreach (var s in summaries)
        {
            pUid.Value       = (long)s.UniqueId;
            pAid.Value       = s.AccountId.ToString();
            pFn.Value        = s.FolderName;
            pFrom.Value      = s.From;
            pSubj.Value      = s.Subject;
            pDt.Value        = s.Date.UtcTicks;
            pRead.Value      = s.IsRead      ? 1 : 0;
            pPreview.Value   = s.Preview;
            pReplied.Value   = s.IsReplied   ? 1 : 0;
            pForwarded.Value = s.IsForwarded ? 1 : 0;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded " +
            "FROM MessageSummary ORDER BY date_ticks DESC;";
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded " +
            "FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn ORDER BY date_ticks DESC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        return await ReadSummariesAsync(cmd);
    }

    public async Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<uint> uniqueIds)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM MessageSummary WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;" +
            "DELETE FROM MessageDetail  WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pUid = cmd.Parameters.Add("$uid", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        foreach (var uid in uniqueIds)
        {
            pUid.Value = (long)uid;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task UpdateIsReadAsync(Guid accountId, string folderName, uint uniqueId, bool isRead)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET is_read=$read " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$read", isRead ? 1 : 0);
        cmd.Parameters.AddWithValue("$uid",  (long)uniqueId);
        cmd.Parameters.AddWithValue("$aid",  accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",   folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePreviewAsync(Guid accountId, string folderName, uint uniqueId, string preview)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET preview_text=$preview " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$uid",     (long)uniqueId);
        cmd.Parameters.AddWithValue("$aid",     accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",      folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpsertDetailAsync(MailMessageDetail d)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MessageDetail(unique_id, account_id, folder_name, to_addr, cc, reply_to, plain_body, html_body)
            VALUES($uid, $aid, $fn, $to, $cc, $rt, $plain, $html)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                to_addr    = excluded.to_addr,
                cc         = excluded.cc,
                reply_to   = excluded.reply_to,
                plain_body = excluded.plain_body,
                html_body  = excluded.html_body;
            """;
        cmd.Parameters.AddWithValue("$uid",   (long)d.UniqueId);
        cmd.Parameters.AddWithValue("$aid",   d.AccountId.ToString());
        cmd.Parameters.AddWithValue("$fn",    d.FolderName);
        cmd.Parameters.AddWithValue("$to",    d.To);
        cmd.Parameters.AddWithValue("$cc",    d.Cc);
        cmd.Parameters.AddWithValue("$rt",    d.ReplyTo);
        cmd.Parameters.AddWithValue("$plain", d.PlainTextBody);
        cmd.Parameters.AddWithValue("$html",  d.HtmlBody);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, uint uniqueId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.to_addr, d.cc, d.reply_to, d.plain_body, d.html_body,
                   s.from_disp, s.subject, s.date_ticks, s.is_read
            FROM MessageDetail d
            JOIN MessageSummary s USING (unique_id, account_id, folder_name)
            WHERE d.unique_id=$uid AND d.account_id=$aid AND d.folder_name=$fn;
            """;
        cmd.Parameters.AddWithValue("$uid", (long)uniqueId);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new MailMessageDetail
        {
            UniqueId      = uniqueId,
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
        };
    }

    public async Task<HashSet<uint>> GetAllUidsAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = new HashSet<uint>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add((uint)r.GetInt64(0));
        return result;
    }

    public async Task<uint> GetMaxUidAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(MAX(unique_id), 0) FROM MessageSummary " +
            "WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (uint)l : 0u;
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
                UniqueId    = (uint)r.GetInt64(0),
                AccountId   = Guid.Parse(r.GetString(1)),
                FolderName  = r.GetString(2),
                From        = r.GetString(3),
                Subject     = r.GetString(4),
                Date        = new DateTimeOffset(r.GetInt64(5), TimeSpan.Zero),
                IsRead      = r.GetInt64(6) != 0,
                Preview     = r.GetString(7),
                IsReplied   = r.GetInt64(8) != 0,
                IsForwarded = r.GetInt64(9) != 0,
            });
        }
        return list;
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
}
