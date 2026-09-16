using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>A message the full-text index matched (#717): its identity, enough to find its row in a view.</summary>
public sealed record SearchHit(Guid AccountId, string FolderName, string MessageId, string InternetMessageId);

/// <summary>
/// The full-text search index (#717): an SQLite FTS5 table over every cached message's subject, sender,
/// recipients, Cc, body and attachment names.
/// <para><b>How it stays current.</b> Triggers on <c>MessageSummary</c> and <c>MessageDetail</c> mark a
/// message's <c>SearchKey</c> row pending — and bump its generation — whenever anything the index holds
/// changes, and drop its index entry when the summary goes. So every write path — sync, open, refile, the
/// purges — keeps the index honest without having to remember to, including paths written after this one.
/// The text itself is built in C# by <see cref="IndexPendingSearchAsync"/>, because an HTML-only body has to
/// be reduced to its words first and a trigger can't do that without a custom SQL function that every
/// connection to the database would then have to register.</para>
/// <para><b>Why a key table.</b> FTS5 addresses rows by integer rowid, and <c>MessageSummary</c>'s rowid is
/// not stable — a table rebuild or VACUUM may renumber it. <c>SearchKey.id</c> is an INTEGER PRIMARY KEY,
/// which SQLite never renumbers.</para>
/// <para><b>Contentless.</b> The FTS table keeps only the index, not a second copy of every body.</para>
/// <para><b>Changing a trigger.</b> They are created with IF NOT EXISTS, so a changed body never reaches a
/// database that already has the old one: drop it by name first. And existing mail is queued for indexing
/// only when <c>MessageSearch</c> is created, so a migration that rebuilds <c>MessageSummary</c> has to
/// re-queue it (and recreate the triggers the rebuild dropped).</para>
/// </summary>
public partial class LocalStoreService
{
    /// <summary>Rows indexed per write transaction.</summary>
    internal const int SearchIndexBatchSize = 200;

    /// <summary>
    /// What a search indexes before it queries, so mail cached a moment ago is findable. Kept to one batch:
    /// while a large backlog is still being worked through in the background, a search must not wait for
    /// it — the rows answer for what the index has not reached.
    /// </summary>
    internal const int SearchFlushBeforeQuery = SearchIndexBatchSize;

    private bool _searchIndexAvailable;
    private bool _backgroundIndexing;
    private int _indexerRunning;
    private int _indexerRequested;

    public bool IsSearchIndexAvailable => _searchIndexAvailable;

    private void InitializeSearchIndex(SqliteConnection conn)
    {
        try
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MessageSearch';";
            var existed = Convert.ToInt64(cmd.ExecuteScalar()) > 0;

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS SearchKey (
                    id          INTEGER PRIMARY KEY,
                    account_id  TEXT    NOT NULL,
                    folder_name TEXT    NOT NULL,
                    unique_id   TEXT    NOT NULL,
                    pending     INTEGER NOT NULL DEFAULT 1,
                    generation  INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (account_id, folder_name, unique_id)
                );
                CREATE INDEX IF NOT EXISTS idx_searchkey_pending ON SearchKey(id) WHERE pending = 1;

                CREATE VIRTUAL TABLE IF NOT EXISTS MessageSearch USING fts5(
                    subject, sender, to_addr, cc, body, attachments,
                    content='', contentless_delete=1,
                    tokenize='unicode61 remove_diacritics 2'
                );

                CREATE TRIGGER IF NOT EXISTS trg_search_summary_insert AFTER INSERT ON MessageSummary BEGIN
                    INSERT INTO SearchKey(account_id, folder_name, unique_id)
                    VALUES (new.account_id, new.folder_name, new.unique_id)
                    ON CONFLICT(account_id, folder_name, unique_id) DO UPDATE SET pending = 1, generation = generation + 1;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_summary_update AFTER UPDATE ON MessageSummary
                WHEN old.from_disp IS NOT new.from_disp OR old.to_addr IS NOT new.to_addr
                  OR old.subject IS NOT new.subject OR old.preview_text IS NOT new.preview_text
                  OR old.account_id IS NOT new.account_id OR old.folder_name IS NOT new.folder_name
                  OR old.unique_id IS NOT new.unique_id
                BEGIN
                    DELETE FROM MessageSearch WHERE rowid = (SELECT id FROM SearchKey
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id
                          AND (old.account_id IS NOT new.account_id OR old.folder_name IS NOT new.folder_name
                               OR old.unique_id IS NOT new.unique_id));
                    DELETE FROM SearchKey
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id
                          AND (old.account_id IS NOT new.account_id OR old.folder_name IS NOT new.folder_name
                               OR old.unique_id IS NOT new.unique_id);
                    INSERT INTO SearchKey(account_id, folder_name, unique_id)
                    VALUES (new.account_id, new.folder_name, new.unique_id)
                    ON CONFLICT(account_id, folder_name, unique_id) DO UPDATE SET pending = 1, generation = generation + 1;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_summary_delete AFTER DELETE ON MessageSummary BEGIN
                    DELETE FROM MessageSearch WHERE rowid = (SELECT id FROM SearchKey
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id);
                    DELETE FROM SearchKey
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_detail_insert AFTER INSERT ON MessageDetail BEGIN
                    UPDATE SearchKey SET pending = 1, generation = generation + 1
                        WHERE account_id = new.account_id AND folder_name = new.folder_name AND unique_id = new.unique_id;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_detail_update AFTER UPDATE ON MessageDetail
                WHEN old.plain_body IS NOT new.plain_body OR old.html_body IS NOT new.html_body
                  OR old.cc IS NOT new.cc OR old.to_addr IS NOT new.to_addr OR old.from_addr IS NOT new.from_addr
                  OR old.attachments_json IS NOT new.attachments_json
                  OR old.account_id IS NOT new.account_id OR old.folder_name IS NOT new.folder_name
                  OR old.unique_id IS NOT new.unique_id
                BEGIN
                    UPDATE SearchKey SET pending = 1, generation = generation + 1
                        WHERE account_id = new.account_id AND folder_name = new.folder_name AND unique_id = new.unique_id;
                    UPDATE SearchKey SET pending = 1, generation = generation + 1
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_detail_delete AFTER DELETE ON MessageDetail BEGIN
                    UPDATE SearchKey SET pending = 1, generation = generation + 1
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id;
                END;
                """;
            cmd.ExecuteNonQuery();

            if (!existed)
            {
                // Everything already cached waits to be indexed, oldest first so the ids run newest-last and
                // the indexer — which takes the highest ids first — reaches recent mail before old.
                cmd.CommandText = """
                    INSERT OR IGNORE INTO SearchKey(account_id, folder_name, unique_id)
                    SELECT account_id, folder_name, unique_id FROM MessageSummary ORDER BY date_ticks ASC;
                    """;
                var queued = cmd.ExecuteNonQuery();
                LogService.Log($"Search index: created; {queued} cached messages queued for indexing");
            }

            tx.Commit();
            _searchIndexAvailable = true;
        }
        catch (SqliteException ex)
        {
            // The transaction rolled back, so no trigger exists to reference a missing table. Search
            // falls back to matching what the message list holds.
            _searchIndexAvailable = false;
            LogService.Log("Search index: unavailable, search matches loaded messages only", ex);
        }
    }

    /// <summary>
    /// SQL that removes an account's index entries and keys in two set-based statements, for the purges to
    /// run before they delete the account's messages. Without it every deleted message fires the triggers
    /// one row at a time — three writes each — which made removing a large account several times slower.
    /// Empty when there is no index, whose tables then do not exist.
    /// </summary>
    private string SearchIndexAccountPurgeSql => _searchIndexAvailable
        ? "DELETE FROM MessageSearch WHERE rowid IN (SELECT id FROM SearchKey WHERE account_id = $aid);" +
          "DELETE FROM SearchKey WHERE account_id = $aid;"
        : string.Empty;

    /// <summary>
    /// Starts indexing in the background whenever there is pending work: now, and after each write that
    /// can create some. Opt-in so tests that build a store on a temp profile never have a background task
    /// outliving the profile.
    /// </summary>
    public void StartBackgroundSearchIndexing()
    {
        if (!_searchIndexAvailable) return;
        _backgroundIndexing = true;
        KickSearchIndexer();
    }

    private void KickSearchIndexer()
    {
        if (!_backgroundIndexing) return;
        // Recorded before the running check, so a write that commits while a pass is finishing is seen:
        // the pass looks at this flag again after it lets go of the running flag.
        Volatile.Write(ref _indexerRequested, 1);
        if (Interlocked.CompareExchange(ref _indexerRunning, 1, 0) != 0) return;
        Task.Run(RunBackgroundIndexerAsync);
    }

    private async Task RunBackgroundIndexerAsync()
    {
        var total = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (Interlocked.Exchange(ref _indexerRequested, 0) == 1)
            {
                while (true)
                {
                    var n = await IndexPendingSearchAsync(SearchIndexBatchSize, CancellationToken.None);
                    total += n;
                    if (n < SearchIndexBatchSize) break;
                    // Room for a sync's writes between batches.
                    await Task.Delay(25);
                }
            }
            if (total >= SearchIndexBatchSize)
                LogService.Log($"Search index: indexed {total} messages in {timer.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Log("Search index: background indexing stopped", ex);
        }
        finally
        {
            Volatile.Write(ref _indexerRunning, 0);
        }
        // A kick that landed after the last check but before the running flag was released would
        // otherwise wait for the next write.
        if (Volatile.Read(ref _indexerRequested) == 1 && Interlocked.CompareExchange(ref _indexerRunning, 1, 0) == 0)
            _ = Task.Run(RunBackgroundIndexerAsync);
    }

    /// <summary>
    /// Indexes up to <paramref name="maxRows"/> pending messages, newest first, in batches of
    /// <see cref="SearchIndexBatchSize"/>. Returns how many were read for indexing.
    /// </summary>
    public async Task<int> IndexPendingSearchAsync(int maxRows, CancellationToken ct = default)
    {
        if (!_searchIndexAvailable || maxRows <= 0) return 0;
        var done = 0;
        while (done < maxRows)
        {
            ct.ThrowIfCancellationRequested();
            var n = await IndexBatchAsync(Math.Min(SearchIndexBatchSize, maxRows - done), ct);
            done += n;
            if (n == 0) break;
        }
        return done;
    }

    private sealed record PendingRow(
        long Id, long Generation, bool HasSummary, string Subject, string Sender, string To, string Cc,
        string Body, string Attachments);

    /// <summary>
    /// One batch: read the pending rows and build their text with no lock held — stripping HTML is the slow
    /// part — then write in one short transaction. A row's generation is checked as it is settled, so a
    /// message that changed in between is left pending for the next batch rather than indexed stale.
    /// </summary>
    private async Task<int> IndexBatchAsync(int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync();

        var raw = new List<(long Id, long Generation, bool HasSummary, string[] Text, string? AttachmentsJson)>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = """
                SELECT k.id, k.generation, s.unique_id IS NOT NULL,
                       s.subject, s.from_disp, s.to_addr, s.preview_text,
                       d.from_addr, d.to_addr, d.cc, d.plain_body, d.html_body, d.attachments_json
                FROM SearchKey k
                LEFT JOIN MessageSummary s
                  ON s.unique_id = k.unique_id AND s.account_id = k.account_id AND s.folder_name = k.folder_name
                LEFT JOIN MessageDetail d
                  ON d.unique_id = k.unique_id AND d.account_id = k.account_id AND d.folder_name = k.folder_name
                WHERE k.pending = 1
                ORDER BY k.id DESC
                LIMIT $limit;
                """;
            read.Parameters.AddWithValue("$limit", limit);
            await using var r = await read.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var text = new string[9];
                for (int i = 0; i < 9; i++)
                    text[i] = r.IsDBNull(i + 3) ? string.Empty : r.GetString(i + 3);
                raw.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) != 0, text, r.IsDBNull(12) ? null : r.GetString(12)));
            }
        }
        if (raw.Count == 0) return 0;

        var rows = new List<PendingRow>(raw.Count);
        foreach (var (id, generation, hasSummary, t, attJson) in raw)
        {
            try
            {
                // t: subject, from_disp, to_addr, preview_text, d.from_addr, d.to_addr, d.cc, plain_body, html_body
                var body = t[7].Length > 0 ? t[7]
                         : t[8].Length > 0 ? HtmlStripper.ToPlainText(t[8], includeLinkTargets: false)
                         : t[3];
                rows.Add(new PendingRow(id, generation, hasSummary, t[0], JoinDistinct(t[1], t[4]), JoinDistinct(t[2], t[5]),
                    t[6], body, AttachmentNames(attJson)));
            }
            catch (Exception ex)
            {
                // One message whose text can't be built must not stop every batch after it — the newest pending
                // row is always read first, so it would be retried forever. Index what the row itself has.
                LogService.Log($"Search index: could not read message text for key {id}; indexing its summary only", ex);
                rows.Add(new PendingRow(id, generation, hasSummary, t[0], t[1], t[2], string.Empty, t[3], string.Empty));
            }
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var settle = conn.CreateCommand();
        settle.CommandText = "UPDATE SearchKey SET pending = 0 WHERE id = $id AND generation = $gen AND pending = 1;";
        var pSettleId = settle.Parameters.Add("$id", SqliteType.Integer);
        var pSettleGen = settle.Parameters.Add("$gen", SqliteType.Integer);

        await using var orphan = conn.CreateCommand();
        orphan.CommandText = "DELETE FROM SearchKey WHERE id = $id AND generation = $gen;";
        var pOrphanId = orphan.Parameters.Add("$id", SqliteType.Integer);
        var pOrphanGen = orphan.Parameters.Add("$gen", SqliteType.Integer);

        await using var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM MessageSearch WHERE rowid = $id;";
        var pDelete = delete.Parameters.Add("$id", SqliteType.Integer);

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO MessageSearch(rowid, subject, sender, to_addr, cc, body, attachments)
            VALUES ($id, $subject, $sender, $to, $cc, $body, $att);
            """;
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);
        var pSubject = insert.Parameters.Add("$subject", SqliteType.Text);
        var pSender = insert.Parameters.Add("$sender", SqliteType.Text);
        var pTo = insert.Parameters.Add("$to", SqliteType.Text);
        var pCc = insert.Parameters.Add("$cc", SqliteType.Text);
        var pBody = insert.Parameters.Add("$body", SqliteType.Text);
        var pAtt = insert.Parameters.Add("$att", SqliteType.Text);

        foreach (var row in rows)
        {
            if (!row.HasSummary)
            {
                // A key whose message has gone by a path no trigger saw (a REPLACE conflict deletes without
                // firing delete triggers). Nothing to index; drop the key and anything it indexed.
                pOrphanId.Value = row.Id;
                pOrphanGen.Value = row.Generation;
                if (await orphan.ExecuteNonQueryAsync(ct) == 1)
                {
                    pDelete.Value = row.Id;
                    await delete.ExecuteNonQueryAsync(ct);
                }
                continue;
            }

            pSettleId.Value = row.Id;
            pSettleGen.Value = row.Generation;
            // Zero rows: the message changed (or went) after it was read. Leave it for the next batch.
            if (await settle.ExecuteNonQueryAsync(ct) != 1) continue;

            pDelete.Value = row.Id;
            await delete.ExecuteNonQueryAsync(ct);
            pId.Value = row.Id;
            pSubject.Value = row.Subject;
            pSender.Value = row.Sender;
            pTo.Value = row.To;
            pCc.Value = row.Cc;
            pBody.Value = row.Body;
            pAtt.Value = row.Attachments;
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private static string JoinDistinct(string a, string b)
    {
        if (b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return a;
        if (a.Length == 0) return b;
        return a + " " + b;
    }

    private static string AttachmentNames(string? json)
    {
        if (string.IsNullOrEmpty(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;
            var names = new StringBuilder();
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object
                    && a.TryGetProperty("FileName", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    if (names.Length > 0) names.Append(' ');
                    names.Append(name.GetString());
                }
            }
            return names.ToString();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The cached messages the index matches for <paramref name="match"/> (an FTS5 expression built by
    /// <see cref="SearchMatchExpression"/>), limited to <paramref name="folders"/> when given. With
    /// <paramref name="indexPendingFirst"/>, one batch of pending work is indexed first so a message cached a
    /// moment ago is found. Messages still waiting to be reindexed are left out rather than matched on text
    /// they may no longer have. Throws <see cref="SqliteException"/> for an expression FTS5 rejects; callers
    /// treat that as "the index has no answer" and fall back to the rows they hold.
    /// </summary>
    public async Task<List<SearchHit>> FindMessagesAsync(
        string match,
        IReadOnlyCollection<(Guid AccountId, string FolderName)>? folders,
        bool indexPendingFirst = true,
        CancellationToken ct = default)
    {
        if (!_searchIndexAvailable) return [];
        if (indexPendingFirst)
            await IndexPendingSearchAsync(SearchFlushBeforeQuery, ct);

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        var scope = string.Empty;
        if (folders != null)
        {
            // One JSON parameter rather than a parameter per folder: an aggregate view can span more
            // folders than SQLite binds in one statement.
            scope = " AND (k.account_id || char(31) || k.folder_name) IN (SELECT value FROM json_each($scope))";
            cmd.Parameters.AddWithValue("$scope",
                JsonSerializer.Serialize(folders.Select(f => f.AccountId.ToString() + "" + f.FolderName)));
        }
        cmd.CommandText = $"""
            SELECT k.account_id, k.folder_name, k.unique_id, s.internet_message_id
            FROM MessageSearch m
            JOIN SearchKey k ON k.id = m.rowid
            JOIN MessageSummary s
              ON s.unique_id = k.unique_id AND s.account_id = k.account_id AND s.folder_name = k.folder_name
            WHERE MessageSearch MATCH $match AND k.pending = 0{scope};
            """;
        cmd.Parameters.AddWithValue("$match", match);

        var hits = new List<SearchHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!Guid.TryParse(r.GetString(0), out var accountId)) continue;
            hits.Add(new SearchHit(accountId, r.GetString(1), r.GetString(2), r.IsDBNull(3) ? string.Empty : r.GetString(3)));
        }
        return hits;
    }
}
