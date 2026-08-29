using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

        // Marks rows pulled from a Microsoft (Graph) calendar by GraphCalendarSyncService (M4
        // read-down v1). Graph rows are replaced wholesale per sync and must be excluded from the
        // invite harvest and orphan cleanup; account deletion still cascades by account_id.
        // Idempotent ALTER, mirroring is_all_day.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN is_graph INTEGER NOT NULL DEFAULT 0;");

        // Per-calendar tagging (multi-calendar-per-account): the specific server calendar a synced
        // row belongs to, so one account's calendars show as separate tree nodes. Empty for local /
        // invite-harvested rows. Unversioned idempotent ALTERs, like is_all_day/is_graph above.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN calendar_id   TEXT NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN calendar_name TEXT NOT NULL DEFAULT '';");

        // CalDAV resource href for a server-synced iCloud row — the real URL the event lives at on
        // the server (Apple names iPhone/web-created resources randomly, ≠ UID), so edit/delete can
        // target it instead of a reconstructed {collection}/{uid}.ics. Empty for Graph/Google/local/
        // invite rows. Unversioned idempotent ALTER, like calendar_id/calendar_name above.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN resource_url TEXT NOT NULL DEFAULT '';");

        // CalendarSource table: an account's real calendar list, as the provider reports it.
        //
        // The calendar list used to be derived with SELECT DISTINCT over CalendarEvent — the
        // calendars that happened to have synced rows. That is not the same set: a calendar with no
        // events in the sync window was invisible, so it had no tree node and could not be picked
        // as a save target, which made filing the FIRST appointment into a calendar impossible. And
        // an events-derived list has nowhere to record whether a calendar can be written to, so a
        // holidays feed looked exactly like the user's own calendar.
        //
        // Additive, no existing table touched, so no user_version bump — same reasoning as
        // CalendarEvent above. LoadCalendarSourcesAsync still falls back to the old derivation for
        // an account with no rows here, so an upgraded profile keeps its tree until the first sync.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CalendarSource (
                account_id    TEXT    NOT NULL,
                calendar_id   TEXT    NOT NULL,
                calendar_name TEXT    NOT NULL DEFAULT '',
                can_write     INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (account_id, calendar_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // Folder table (#516). The folder list used to live only in MainViewModel._cachedFolders,
        // filled by GetFoldersAsync after connect — so at launch nothing knew which folders existed,
        // let alone which were Inboxes. That made a startup folder impossible to honour before the
        // network came up, and left the folder tree showing only the virtual aggregates. Persisting
        // the list makes both work offline. Additive, no existing table touched, so no user_version
        // bump is needed (same reasoning as CalendarEvent above).
        //
        // SuppressUnreadCount is deliberately NOT a column — it is derived from Kind on the model.
        // Header rows are synthesized by RebuildFolderListFromCache and are never stored.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Folder (
                account_id            TEXT    NOT NULL,
                full_name             TEXT    NOT NULL,
                display_name          TEXT    NOT NULL DEFAULT '',
                parent_id             TEXT    DEFAULT NULL,
                kind                  INTEGER NOT NULL DEFAULT 0,
                exclude_from_all_mail INTEGER NOT NULL DEFAULT 0,
                unread_count          INTEGER NOT NULL DEFAULT 0,
                message_count         INTEGER NOT NULL DEFAULT 0,
                sort_order            INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (account_id, full_name)
            );

            CREATE TABLE IF NOT EXISTS Pop3CollectedUidl (
                account_id TEXT NOT NULL,
                uidl       TEXT NOT NULL,
                PRIMARY KEY (account_id, uidl)
            );
            """;
        cmd.ExecuteNonQuery();

        RunDataMigrations(conn);

        // Raw RFC 5322 bytes of a POP3 message, stored only when the message has attachments (#128).
        // POP3 has no equivalent of IMAP's fetch-one-body-part, so an attachment has to come back out
        // of the message we already downloaded; keeping the bytes is what makes it openable later
        // without a second full download. NULL for every IMAP and Graph row, and for POP3 messages
        // with no attachments — which is why this is a nullable column on the existing detail table
        // rather than a table of its own: DeleteSummariesAsync already drops the MessageDetail row,
        // so the bytes cascade with the message instead of leaking.
        //
        // Deliberately AFTER RunDataMigrations, unlike the ALTERs above: the 1→2 migration rebuilds
        // MessageDetail by CREATE/INSERT/DROP/RENAME against a fixed column list, so a column added
        // before it would be dropped again and stay missing for the rest of the session.
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN mime_bytes BLOB DEFAULT NULL;");

        // A draft written locally that has not reached the server's Drafts folder yet (#637). Set
        // only by LocalDraftService; 0 for every message that came from a server. Two things read
        // it: the row, which says the draft is not on the server yet, and the sync reconcile, which
        // must not treat "absent from the server listing" as "deleted on the server" for a message
        // the server has never been told about.
        //
        // AFTER RunDataMigrations for the same reason mime_bytes is: the 1→2 rebuild recreates
        // MessageSummary against a fixed column list, so a column added before it is dropped again.
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_pending_upload INTEGER NOT NULL DEFAULT 0;");

        // Why the server refused to store a draft (#637). The row keeps it, so the row itself
        // carries the news — the status bar cannot, because the sync sweep overwrites it seconds
        // later and a user with custom announcements off would never see it. A draft with this set
        // is not retried: the same refusal would repeat forever and, since the upload pass replays
        // oldest-first, it would be first every time and block every draft behind it.
        //
        // AFTER RunDataMigrations, same reason as the one above.
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN send_failed_reason TEXT DEFAULT NULL;");
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
    //   (no 5 → 6 data migration: the Graph immutable-id cache rebuild (#366) is account-scoped, so
    //    it runs at the app layer against Graph accounts only — see ClearCachedMailAsync — not as a
    //    blanket DB wipe that would also drop IMAP bodies and break IMAP calendar-invite source links.)
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
            INSERT INTO MessageSummary(unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, is_mailing_list, flag_id, internet_message_id, is_pending_upload, send_failed_reason)
            VALUES($uid, $aid, $fn, $from, $to, $subj, $dt, $read, $preview, $replied, $forwarded, $ml, $flag_id, $imid, $pending, $failed)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                from_disp       = excluded.from_disp,
                to_addr         = excluded.to_addr,
                subject         = excluded.subject,
                date_ticks      = excluded.date_ticks,
                is_read         = excluded.is_read,
                is_replied      = excluded.is_replied,
                is_forwarded    = excluded.is_forwarded,
                is_mailing_list = excluded.is_mailing_list,
                is_pending_upload = excluded.is_pending_upload,
                send_failed_reason = excluded.send_failed_reason,
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
        var pPending   = cmd.Parameters.Add("$pending",   SqliteType.Integer);
        var pFailed    = cmd.Parameters.Add("$failed",    SqliteType.Text);

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
            pPending.Value   = s.IsPendingUpload   ? 1 : 0;
            pFailed.Value    = (object?)s.SendFailedReason ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, is_pending_upload, send_failed_reason " +
            "FROM MessageSummary ORDER BY date_ticks DESC;";
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, is_pending_upload, send_failed_reason " +
            "FROM MessageSummary WHERE account_id=$aid ORDER BY date_ticks DESC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, is_pending_upload, send_failed_reason " +
            "FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn ORDER BY date_ticks DESC" +
            (limit.HasValue ? " LIMIT $limit;" : ";");
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit.Value));
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadFolderSummariesSinceAsync(
        Guid accountId, string folderName, DateTimeOffset since)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, is_pending_upload, send_failed_reason " +
            "FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn AND date_ticks>=$since ORDER BY date_ticks DESC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        // date_ticks is written as Date.UtcTicks, so the bound value has to be UtcTicks too — a
        // local-offset DateTimeOffset compared as raw Ticks is off by the offset.
        cmd.Parameters.AddWithValue("$since", since.UtcTicks);
        return await ReadSummariesAsync(cmd);
    }

    /// <summary>
    /// Records why the server refused to store a draft, and takes it out of the upload queue in
    /// the same statement — the two must never disagree (#637).
    /// </summary>
    public async Task MarkSendFailedAsync(Guid accountId, string folderName, string messageId, string reason)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET send_failed_reason=$reason " +
            "WHERE account_id=$aid AND folder_name=$fn AND unique_id=$uid;";
        cmd.Parameters.AddWithValue("$aid",    accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",     folderName);
        cmd.Parameters.AddWithValue("$uid",    messageId);
        cmd.Parameters.AddWithValue("$reason", reason);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<MailMessageSummary>> LoadPendingDraftsAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, is_pending_upload, send_failed_reason " +
            // Ascending, unlike every other summary query: this feeds the upload pass, and replaying
            // the oldest first is what makes a chain of supersedes land in the order it was written.
            // A draft the server has already refused is excluded, and that clause is what
            // stops one bad draft blocking the folder: this replays oldest-first, so a
            // permanently-refused draft would be first every time and every draft behind it
            // would never be tried. Editing and saving it clears the reason and re-arms it.
            "FROM MessageSummary WHERE account_id=$aid AND is_pending_upload=1 " +
            "  AND send_failed_reason IS NULL ORDER BY date_ticks ASC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
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

    /// <summary>
    /// Drafts held only on this computer for an account: not yet uploaded, or refused (#637).
    /// <para>Removing an account purges its store, and these cannot be downloaded again — they
    /// exist nowhere else. This is what the confirmation counts.</para>
    /// </summary>
    public async Task<int> CountUnsentMailAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM MessageSummary WHERE account_id=$aid AND " +
            "(is_pending_upload <> 0 OR send_failed_reason IS NOT NULL);";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public async Task DeleteAccountDataAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM MessageDetail     WHERE account_id = $aid;" +
            "DELETE FROM MessageSummary    WHERE account_id = $aid;" +
            "DELETE FROM CalendarEvent     WHERE account_id = $aid;" +
            "DELETE FROM Folder            WHERE account_id = $aid;" +
            "DELETE FROM Pop3CollectedUidl WHERE account_id = $aid;" +
            "DELETE FROM CalendarSource    WHERE account_id = $aid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Clears cached mail (summaries, bodies, delta cursors) for the given accounts only — used for
    /// the one-time Graph immutable-id rebuild (#366). Scoped to Graph accounts by the caller so IMAP
    /// bodies (and the IMAP calendar-invite source links that depend on them) are left intact.
    /// Calendar events are NOT touched. No-op for an empty set.
    /// </summary>
    public async Task ClearCachedMailAsync(IEnumerable<Guid> accountIds)
    {
        var ids = accountIds?.ToList() ?? [];
        if (ids.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        foreach (var id in ids)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM MessageDetail  WHERE account_id = $aid;" +
                "DELETE FROM MessageSummary WHERE account_id = $aid;" +
                "DELETE FROM DeltaToken     WHERE account_id = $aid;";
            cmd.Parameters.AddWithValue("$aid", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    // ── Folders (#516) ───────────────────────────────────────────────────────────

    public async Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolderModel> folders)
    {
        // Replace-all for this account, in one transaction: a folder deleted or renamed on the
        // server must disappear locally, and an upsert alone would leave the old row behind.
        // Other accounts are untouched, so a partial connect only refreshes what it reached.
        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM Folder WHERE account_id = $aid;";
            del.Parameters.AddWithValue("$aid", accountId.ToString());
            await del.ExecuteNonQueryAsync();
        }

        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT OR REPLACE INTO Folder
                    (account_id, full_name, display_name, parent_id, kind,
                     exclude_from_all_mail, unread_count, message_count, sort_order)
                VALUES ($aid, $fn, $dn, $pid, $kind, $excl, $unread, $total, $ord);
                """;
            var pFn    = ins.Parameters.Add("$fn",     Microsoft.Data.Sqlite.SqliteType.Text);
            var pDn    = ins.Parameters.Add("$dn",     Microsoft.Data.Sqlite.SqliteType.Text);
            var pPid   = ins.Parameters.Add("$pid",    Microsoft.Data.Sqlite.SqliteType.Text);
            var pKind  = ins.Parameters.Add("$kind",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pExcl  = ins.Parameters.Add("$excl",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pUnr   = ins.Parameters.Add("$unread", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pTot   = ins.Parameters.Add("$total",  Microsoft.Data.Sqlite.SqliteType.Integer);
            var pOrd   = ins.Parameters.Add("$ord",    Microsoft.Data.Sqlite.SqliteType.Integer);
            ins.Parameters.AddWithValue("$aid", accountId.ToString());

            var order = 0;
            foreach (var f in folders)
            {
                // Header rows are synthesized for display and carry no server folder.
                if (f.IsHeader || string.IsNullOrEmpty(f.FullName)) continue;

                pFn.Value   = f.FullName;
                pDn.Value   = f.DisplayName ?? string.Empty;
                pPid.Value  = (object?)f.ParentId ?? DBNull.Value;
                pKind.Value = (int)f.Kind;
                pExcl.Value = f.ExcludeFromAllMail ? 1 : 0;
                pUnr.Value  = f.UnreadCount;
                pTot.Value  = f.MessageCount;
                pOrd.Value  = order++;
                await ins.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
    }

    public async Task<Dictionary<Guid, List<MailFolderModel>>> LoadFoldersAsync()
    {
        var result = new Dictionary<Guid, List<MailFolderModel>>();
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, full_name, display_name, parent_id, kind,
                   exclude_from_all_mail, unread_count, message_count
            FROM Folder
            ORDER BY account_id, sort_order;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            // A row whose account_id no longer parses is unusable; skip rather than throw and
            // lose every other account's folders on one bad value.
            if (!Guid.TryParse(r.GetString(0), out var accountId)) continue;

            if (!result.TryGetValue(accountId, out var list))
                result[accountId] = list = [];

            list.Add(new MailFolderModel
            {
                AccountId          = accountId,
                FullName           = r.GetString(1),
                DisplayName        = r.GetString(2),
                ParentId           = r.IsDBNull(3) ? null : r.GetString(3),
                Kind               = (SpecialFolderKind)r.GetInt32(4),
                ExcludeFromAllMail = r.GetInt32(5) != 0,
                UnreadCount        = r.GetInt32(6),
                MessageCount       = r.GetInt32(7),
            });
        }
        return result;
    }

    public async Task PurgeFoldersForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds)
    {
        await using var conn = await OpenAsync();

        // Unlike calendar events there is no Guid.Empty "local" bucket to preserve — every folder
        // belongs to a real account, so an unrecognised id is always an orphan.
        var keep = new HashSet<string>(knownAccountIds.Select(id => id.ToString()),
                                       StringComparer.OrdinalIgnoreCase);

        var present = new List<string>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT DISTINCT account_id FROM Folder;";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) present.Add(r.GetString(0));
        }

        var orphans = present.Where(a => !keep.Contains(a)).ToList();
        if (orphans.Count == 0) return;

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM Folder WHERE account_id = $aid;";
        var p = del.CreateParameter();
        p.ParameterName = "$aid";
        del.Parameters.Add(p);
        foreach (var orphan in orphans)
        {
            p.Value = orphan;
            await del.ExecuteNonQueryAsync();
        }
        LogService.Log($"LocalStoreService: purged folders for {orphans.Count} unknown account(s).");
    }

    /// <summary>
    /// Deletes calendar events whose <c>account_id</c> is not among <paramref name="knownAccountIds"/> —
    /// orphans left behind when an account is removed and re-added (the re-added account gets a new id,
    /// so the old id's events linger and show as duplicates), or after a cache rebuild. Local events
    /// (<see cref="Guid.Empty"/>) are always kept. No-op when there are no orphans.
    /// </summary>
    public async Task PurgeCalendarEventsForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds)
    {
        await using var conn = await OpenAsync();

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Guid.Empty.ToString() };
        foreach (var id in knownAccountIds) keep.Add(id.ToString());

        var present = new List<string>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT DISTINCT account_id FROM CalendarEvent;";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) present.Add(r.GetString(0));
        }

        var orphans = present.Where(a => !keep.Contains(a)).ToList();
        if (orphans.Count == 0) return;

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM CalendarEvent WHERE account_id = $aid;";
        var p = del.CreateParameter();
        p.ParameterName = "$aid";
        del.Parameters.Add(p);
        foreach (var orphan in orphans)
        {
            p.Value = orphan;
            await del.ExecuteNonQueryAsync();
        }
        LogService.Log($"LocalStoreService: purged calendar events for {orphans.Count} unknown account(s).");
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
                   s.from_disp, s.subject, s.date_ticks, s.is_read, d.attachments_json, d.calendar_ics,
                   s.internet_message_id
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
            // Carried from the summary row so a reply composed from a cached message still threads:
            // ComposeViewModel puts this in In-Reply-To, and until now a cache-served open handed it
            // an empty string. POP3 makes that permanent rather than incidental — the cache is the
            // only copy, so there is no server fetch that would fill it in later.
            InternetMessageId = r.IsDBNull(11) ? string.Empty : r.GetString(11),
            Attachments   = attachments,
            CalendarIcs   = calendarIcs,
            CalendarInvite = string.IsNullOrWhiteSpace(calendarIcs) ? null : IcsModel.Parse(calendarIcs),
        };
    }

    /// <summary>
    /// Stores (or clears, when <paramref name="mimeBytes"/> is null) the raw message bytes on an
    /// existing MessageDetail row. Only <c>Pop3MailService</c> calls this, and only after
    /// <see cref="UpsertDetailAsync"/> has created the row — an UPDATE against a missing row is a
    /// silent no-op, which is the right outcome for a message that is no longer cached.
    /// </summary>
    public async Task StoreMimeBytesAsync(Guid accountId, string folderName, string messageId, byte[]? mimeBytes)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageDetail SET mime_bytes=$bytes " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$bytes", (object?)mimeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid",   messageId);
        cmd.Parameters.AddWithValue("$aid",   accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",    folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns the raw message bytes previously stored by <see cref="StoreMimeBytesAsync"/>, or null
    /// when none were stored (every IMAP/Graph message, and POP3 messages with no attachments).
    /// </summary>
    public async Task<byte[]?> LoadMimeBytesAsync(Guid accountId, string folderName, string messageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT mime_bytes FROM MessageDetail " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = await cmd.ExecuteScalarAsync();
        return result as byte[];
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

    public async Task<HashSet<string>> LoadPop3CollectedUidlsAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT uidl FROM Pop3CollectedUidl WHERE account_id=$aid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(r.GetString(0));
        return result;
    }

    public async Task AddPop3CollectedUidlsAsync(Guid accountId, IEnumerable<string> uidls)
    {
        var list = uidls as IReadOnlyList<string> ?? uidls.ToList();
        if (list.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR IGNORE INTO Pop3CollectedUidl (account_id, uidl) VALUES ($aid, $uidl);";
        var aid  = cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        var uidl = cmd.Parameters.AddWithValue("$uidl", string.Empty);
        foreach (var u in list)
        {
            uidl.Value = u;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task RemovePop3CollectedUidlsAsync(Guid accountId, IEnumerable<string> uidls)
    {
        var list = uidls as IReadOnlyList<string> ?? uidls.ToList();
        if (list.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM Pop3CollectedUidl WHERE account_id=$aid AND uidl=$uidl;";
        var aid  = cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        var uidl = cmd.Parameters.AddWithValue("$uidl", string.Empty);
        foreach (var u in list)
        {
            uidl.Value = u;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<Dictionary<string, bool>> LoadFolderReadStatesAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, is_read FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = new Dictionary<string, bool>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result[r.GetString(0)] = r.GetInt64(1) != 0;
        return result;
    }

    public async Task<IReadOnlyList<(string Id, DateTimeOffset Date, bool IsRead)>> LoadFolderMessageStatesAsync(
        Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, date_ticks, is_read FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = new List<(string, DateTimeOffset, bool)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add((r.GetString(0), new DateTimeOffset(r.GetInt64(1), TimeSpan.Zero), r.GetInt64(2) != 0));
        return result;
    }

    /// <summary>
    /// Which of <paramref name="messageIds"/> already exist in the folder — a bounded
    /// <c>WHERE unique_id IN (…)</c> so a live sync can dedupe its small fetched batch without
    /// scanning (and materialising a HashSet of) every id in a large cached folder.
    /// </summary>
    public async Task<HashSet<string>> GetExistingMessageIdsAsync(
        Guid accountId, string folderName, IEnumerable<string> messageIds)
    {
        var ids = messageIds as IReadOnlyList<string> ?? messageIds.ToList();
        var result = new HashSet<string>();
        if (ids.Count == 0) return result;

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => "$id" + i));
        cmd.CommandText =
            $"SELECT unique_id FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue("$id" + i, ids[i]);
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
                IsPendingUpload   = r.GetInt64(15) != 0,
                SendFailedReason  = r.IsDBNull(16) ? null : r.GetString(16),
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

    public async Task<Dictionary<string, int>> CountSummariesByFolderAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT folder_name, COUNT(*) FROM MessageSummary WHERE account_id = @id GROUP BY folder_name";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var byFolder = new Dictionary<string, int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            byFolder[reader.GetString(0)] = reader.GetInt32(1);
        return byFolder;
    }

    public async Task<Dictionary<string, (int Total, int Unread)>> CountMessagesByFolderAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT folder_name, COUNT(*), SUM(CASE WHEN is_read = 0 THEN 1 ELSE 0 END) " +
            "FROM MessageSummary WHERE account_id = @id GROUP BY folder_name";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var byFolder = new Dictionary<string, (int, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            byFolder[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        return byFolder;
    }

    public async Task<int> RefileMessagesAsync(
        Guid accountId, string fromFolder, string toFolder, IEnumerable<string> messageIds, bool copy)
    {
        var ids = messageIds as IReadOnlyList<string> ?? messageIds.ToList();
        if (ids.Count == 0 || string.Equals(fromFolder, toFolder, StringComparison.Ordinal)) return 0;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int refiled = 0;
        // Chunked for the same reason DeleteSummariesAsync is: SQLite compiles a bounded number of
        // parameters (~999), and the caller may be emptying a whole folder into Trash.
        const int chunkSize = 500;
        for (int offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            var placeholders = string.Join(',', Enumerable.Range(0, count).Select(i => $"$u{i}"));

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = copy
                // Column lists read from the live schema rather than written out here: the point of
                // moving rows in SQL is that nothing is lost in transit, and a hand-written list
                // would quietly stop carrying the next column someone adds.
                ? await CopyRowsSqlAsync(conn, placeholders)
                // UPDATE OR REPLACE, not UPDATE: an id already filed in the destination (the same
                // message copied there earlier) would otherwise collide with the primary key and
                // abort the whole transaction.
                //
                // The CalendarEvent leg retargets a harvested invite's back-link to the message's
                // new home. Without it the event still points at the folder the message left, and
                // "open the invitation" from the calendar dead-ends — the failure
                // ClearOrphanedCalendarSourceLinksAsync exists to prevent, arriving by a route it
                // cannot see (the detail row exists, just not where the event says).
                : $"""
                   UPDATE OR REPLACE MessageSummary SET folder_name=$to
                     WHERE account_id=$aid AND folder_name=$from AND unique_id IN ({placeholders});
                   UPDATE OR REPLACE MessageDetail  SET folder_name=$to
                     WHERE account_id=$aid AND folder_name=$from AND unique_id IN ({placeholders});
                   UPDATE CalendarEvent SET source_folder=$to
                     WHERE account_id=$aid AND source_folder=$from AND source_message_id IN ({placeholders});
                   """;
            cmd.Parameters.AddWithValue("$aid",  accountId.ToString());
            cmd.Parameters.AddWithValue("$from", fromFolder);
            cmd.Parameters.AddWithValue("$to",   toFolder);
            for (int i = 0; i < count; i++)
                cmd.Parameters.AddWithValue($"$u{i}", ids[offset + i]);

            // Two statements touch MessageSummary and MessageDetail; the summary is the row that
            // exists for every message, so its count is how many were actually there to re-file.
            await using (var counting = conn.CreateCommand())
            {
                counting.CommandText =
                    $"SELECT COUNT(*) FROM MessageSummary WHERE account_id=$aid AND folder_name=$from AND unique_id IN ({placeholders});";
                counting.Parameters.AddWithValue("$aid",  accountId.ToString());
                counting.Parameters.AddWithValue("$from", fromFolder);
                for (int i = 0; i < count; i++)
                    counting.Parameters.AddWithValue($"$u{i}", ids[offset + i]);
                refiled += Convert.ToInt32(await counting.ExecuteScalarAsync() ?? 0);
            }

            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return refiled;
    }

    /// <summary>
    /// <c>INSERT OR REPLACE … SELECT</c> for both message tables, with <c>folder_name</c> swapped for
    /// the destination and every other column carried across verbatim. The column names come from
    /// <c>PRAGMA table_info</c> so the copy stays complete as the schema grows — the same guarantee
    /// the row-by-row version had by re-filing loaded objects, without loading them.
    /// </summary>
    private static async Task<string> CopyRowsSqlAsync(SqliteConnection conn, string placeholders)
    {
        var sql = new StringBuilder();
        foreach (var table in new[] { "MessageSummary", "MessageDetail" })
        {
            var columns = await ColumnNamesAsync(conn, table);
            var projected = string.Join(", ", columns.Select(c => c == "folder_name" ? "$to" : c));
            sql.Append($"INSERT OR REPLACE INTO {table} ({string.Join(", ", columns)}) ")
               .Append($"SELECT {projected} FROM {table} ")
               .Append($"WHERE account_id=$aid AND folder_name=$from AND unique_id IN ({placeholders});");
        }
        return sql.ToString();
    }

    private static async Task<List<string>> ColumnNamesAsync(SqliteConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        // Table names here are compile-time literals, never user input.
        cmd.CommandText = $"PRAGMA table_info({table});";
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            names.Add(r.GetString(1));
        return names;
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
                                      is_all_day, recurrence_rule, exdates, is_graph, calendar_id, calendar_name,
                                      resource_url)
            VALUES($uid, $aid, $sum, $desc, $loc, $org, $orgn, $st, $et, $seq, $meth, $smid, $sf, $rs, $allday, $rrule, $exd, $graph, $calid, $calname, $resurl)
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
                exdates           = excluded.exdates,
                -- Preserve an existing calendar tag when the incoming row is untagged (e.g. a server
                -- write-back that targets the default calendar and carries no tag); the next full
                -- sync re-tags it. A non-empty incoming tag always wins.
                calendar_id       = CASE WHEN excluded.calendar_id   = '' THEN calendar_id   ELSE excluded.calendar_id   END,
                calendar_name     = CASE WHEN excluded.calendar_name = '' THEN calendar_name ELSE excluded.calendar_name END,
                -- Preserve a stored CalDAV resource href when the incoming row carries none (a local
                -- create/edit write-back has no href until the next read-sync captures the real one).
                resource_url      = CASE WHEN excluded.resource_url  = '' THEN resource_url  ELSE excluded.resource_url  END
            WHERE CalendarEvent.is_graph = 0 OR excluded.is_graph = 1;
            """;
        // The DO UPDATE ... WHERE guard: server-synced rows (is_graph=1) are owned by the calendar
        // sync service; the invite harvest and local authoring paths (which write is_graph=0) must
        // never overwrite them on a UID collision. The sync service's own write-back stores the
        // server's returned copy WITH is_graph=1 (excluded.is_graph=1), which must update the row —
        // without that clause a successful server edit left the local copy stale until the next
        // replace-slice sync.
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
        cmd.Parameters.AddWithValue("$graph", evt.IsGraph ? 1 : 0);
        cmd.Parameters.AddWithValue("$calid", evt.CalendarId);
        cmd.Parameters.AddWithValue("$calname", evt.CalendarName);
        cmd.Parameters.AddWithValue("$resurl", evt.ResourceUrl);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task ReplaceGraphCalendarEventsAsync(Guid accountId, IReadOnlyList<CalendarEvent> events)
    {
        // Replace-slice semantics (v1, no delta tokens): each sync deletes the account's previous
        // Graph-sourced rows and inserts the fresh window, all in one transaction, so events that
        // vanished on the server disappear locally. Harvested-invite and local rows (is_graph=0)
        // are untouched.
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM CalendarEvent WHERE account_id = $aid AND is_graph = 1;";
            del.Parameters.AddWithValue("$aid", accountId.ToString());
            await del.ExecuteNonQueryAsync();
        }

        await using (var ins = conn.CreateCommand())
        {
            // INSERT OR REPLACE: defensive against a duplicate occurrence id within one payload,
            // and against a (astronomically unlikely) collision with a harvested invite's UID —
            // the Graph copy wins for the (uid, account) key.
            ins.CommandText = """
                INSERT OR REPLACE INTO CalendarEvent(uid, account_id, summary, description, location,
                                          organizer, organizer_name, start_time_ticks, end_time_ticks,
                                          sequence, method, source_message_id, source_folder, response_status,
                                          is_all_day, recurrence_rule, exdates, is_graph, calendar_id, calendar_name,
                                          resource_url)
                VALUES($uid, $aid, $sum, $desc, $loc, $org, $orgn, $st, $et, $seq, $meth, $smid, $sf, $rs, $allday, $rrule, $exd, 1, $calid, $calname, $resurl);
                """;
            var pUid  = ins.Parameters.Add("$uid",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pAid  = ins.Parameters.Add("$aid",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pSum  = ins.Parameters.Add("$sum",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pDesc = ins.Parameters.Add("$desc", Microsoft.Data.Sqlite.SqliteType.Text);
            var pLoc  = ins.Parameters.Add("$loc",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pOrg  = ins.Parameters.Add("$org",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pOrgn = ins.Parameters.Add("$orgn", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSt   = ins.Parameters.Add("$st",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pEt   = ins.Parameters.Add("$et",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pSeq  = ins.Parameters.Add("$seq",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pMeth = ins.Parameters.Add("$meth", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSmid = ins.Parameters.Add("$smid", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSf   = ins.Parameters.Add("$sf",   Microsoft.Data.Sqlite.SqliteType.Text);
            var pRs   = ins.Parameters.Add("$rs",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pAll  = ins.Parameters.Add("$allday", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pRr   = ins.Parameters.Add("$rrule", Microsoft.Data.Sqlite.SqliteType.Text);
            var pExd  = ins.Parameters.Add("$exd",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pCid  = ins.Parameters.Add("$calid",   Microsoft.Data.Sqlite.SqliteType.Text);
            var pCn   = ins.Parameters.Add("$calname", Microsoft.Data.Sqlite.SqliteType.Text);
            var pRes  = ins.Parameters.Add("$resurl",  Microsoft.Data.Sqlite.SqliteType.Text);

            foreach (var evt in events)
            {
                pUid.Value  = evt.Uid;
                pAid.Value  = accountId.ToString();
                pSum.Value  = evt.Summary;
                pDesc.Value = evt.Description;
                pLoc.Value  = evt.Location;
                pOrg.Value  = evt.Organizer;
                pOrgn.Value = evt.OrganizerName;
                pSt.Value   = (object?)evt.StartTimeTicks ?? DBNull.Value;
                pEt.Value   = (object?)evt.EndTimeTicks ?? DBNull.Value;
                pSeq.Value  = (object?)evt.Sequence ?? DBNull.Value;
                pMeth.Value = (object?)evt.Method ?? DBNull.Value;
                pSmid.Value = evt.SourceMessageId;
                pSf.Value   = evt.SourceFolder;
                pRs.Value   = (int)evt.ResponseStatus;
                pAll.Value  = evt.IsAllDay ? 1 : 0;
                pRr.Value   = (object?)evt.RecurrenceRule ?? DBNull.Value;
                pExd.Value  = (object?)evt.ExDates ?? DBNull.Value;
                pCid.Value  = evt.CalendarId;
                pCn.Value   = evt.CalendarName;
                pRes.Value  = evt.ResourceUrl;
                await ins.ExecuteNonQueryAsync();
            }
        }

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
                   source_folder, response_status, is_all_day, recurrence_rule, exdates, is_graph,
                   calendar_id, calendar_name, resource_url
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
                IsGraph          = !r.IsDBNull(17) && r.GetInt32(17) != 0,
                CalendarId       = r.IsDBNull(18) ? string.Empty : r.GetString(18),
                CalendarName     = r.IsDBNull(19) ? string.Empty : r.GetString(19),
                ResourceUrl      = r.IsDBNull(20) ? string.Empty : r.GetString(20),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<CalendarSourceInfo>> LoadCalendarSourcesAsync()
    {
        var list = new List<CalendarSourceInfo>();
        var recorded = new HashSet<Guid>();
        await using var conn = await OpenAsync();

        // What the providers actually reported, written by the sync when it enumerates calendars.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT account_id, calendar_id, calendar_name, can_write
                FROM CalendarSource
                ORDER BY calendar_name COLLATE NOCASE;
                """;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var accountId = Guid.Parse(r.GetString(0));
                recorded.Add(accountId);
                // The empty-id row records the enumeration itself, not a calendar — see
                // ReplaceCalendarSourcesAsync.
                var calendarId = r.GetString(1);
                if (calendarId.Length == 0) continue;
                list.Add(new CalendarSourceInfo(accountId, calendarId, r.GetString(2), r.GetInt32(3) != 0));
            }
        }

        // Accounts the sync has not enumerated yet — a profile upgraded between releases, or one
        // whose first sync of the session has not landed. Fall back to the calendars its synced rows
        // are tagged with, which is the whole list this used to return, so the folder tree does not
        // empty out while waiting. Assumed writable: that is what they were treated as before.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT account_id, calendar_id, calendar_name
                FROM CalendarEvent
                WHERE is_graph = 1 AND calendar_id <> ''
                ORDER BY calendar_name COLLATE NOCASE;
                """;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var accountId = Guid.Parse(r.GetString(0));
                if (recorded.Contains(accountId)) continue;
                list.Add(new CalendarSourceInfo(accountId, r.GetString(1), r.GetString(2), CanWrite: true));
            }
        }

        // Ordered here rather than left to SQL. SQLite's NOCASE folds ASCII A-Z and nothing else, so
        // it fixes "apple after Zulu" but strands every accented name — Ärger and Ábel sort after
        // every unaccented calendar, and not even against each other. Both the picker and the folder
        // tree show this order and are read down by ear, so it has to be the order a person would
        // put them in. Sorting the combined list also stops the recorded accounts from bunching
        // ahead of the un-enumerated ones.
#pragma warning disable CA1309 // Ordinal is what this must NOT be: see the paragraph above — the
        // order is read aloud, so it has to be the one the user's own language puts names in, not
        // the one their code points fall in. This is display order, never a lookup key.
        list.Sort((a, b) => string.Compare(a.CalendarName, b.CalendarName, StringComparison.CurrentCultureIgnoreCase));
#pragma warning restore CA1309
        return list;
    }

    /// <summary>
    /// Replaces one account's recorded calendar list. Called by the sync each time it enumerates,
    /// so a calendar the user has deleted or unsubscribed from stops being offered.
    /// </summary>
    public async Task ReplaceCalendarSourcesAsync(Guid accountId, IReadOnlyList<CalendarSourceInfo> sources)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM CalendarSource WHERE account_id=$aid;";
            del.Parameters.AddWithValue("$aid", accountId.ToString());
            await del.ExecuteNonQueryAsync();
        }

        // A row with an empty calendar_id records the FACT of the enumeration, separately from
        // what it found. Without it an account whose calendar list legitimately came back empty
        // would look un-enumerated forever, and LoadCalendarSourcesAsync would fall back to the
        // calendars its stale rows are tagged with — resurrecting exactly the unsubscribed
        // calendar the per-account fallback exists to keep out, and marking it writable too.
        await using (var marker = conn.CreateCommand())
        {
            marker.CommandText = """
                INSERT OR REPLACE INTO CalendarSource (account_id, calendar_id, calendar_name, can_write)
                VALUES ($aid, '', '', 0);
                """;
            marker.Parameters.AddWithValue("$aid", accountId.ToString());
            await marker.ExecuteNonQueryAsync();
        }

        foreach (var src in sources)
        {
            if (string.IsNullOrEmpty(src.CalendarId)) continue;
            await using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT OR REPLACE INTO CalendarSource (account_id, calendar_id, calendar_name, can_write)
                VALUES ($aid, $cid, $name, $write);
                """;
            ins.Parameters.AddWithValue("$aid", accountId.ToString());
            ins.Parameters.AddWithValue("$cid", src.CalendarId);
            ins.Parameters.AddWithValue("$name", src.CalendarName ?? string.Empty);
            ins.Parameters.AddWithValue("$write", src.CanWrite ? 1 : 0);
            await ins.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>Forgets an account's calendar list (its calendar sync was switched off, or it was removed).</summary>
    public async Task DeleteCalendarSourcesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CalendarSource WHERE account_id=$aid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
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
        // is_graph = 0 guard: Graph-synced rows never reference an invite email (their source
        // fields are already empty) and are owned by the sync's replace-slice — the invite
        // harvest's cleanup must not touch them.
        cmd.CommandText = """
            UPDATE CalendarEvent
            SET source_message_id = '', source_folder = ''
            WHERE source_message_id != ''
              AND is_graph = 0
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
