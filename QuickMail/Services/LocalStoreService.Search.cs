using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>A message the full-text index matched (#717): its identity, enough to find its row in a view.</summary>
public sealed record SearchHit(Guid AccountId, string FolderName, string MessageId, string InternetMessageId);

/// <summary>
/// The full-text search index (#717): an SQLite FTS5 table over every cached message's subject, sender,
/// recipients, Cc, body and attachment names.
/// <para><b>How it stays current.</b> Triggers on <c>MessageSummary</c> and <c>MessageDetail</c> mark a
/// message's <c>SearchKey</c> row pending whenever anything the index holds changes, and drop its index
/// entry when the summary goes. So every write path — sync, open, refile, the purges — keeps the index
/// honest without having to remember to, including paths written after this one. The text itself is
/// built in C# by <see cref="IndexPendingSearchAsync"/>, because an HTML-only body has to be reduced to
/// its words first and a trigger can't do that without a custom SQL function that every connection to
/// the database would then have to register.</para>
/// <para><b>Why a key table.</b> FTS5 addresses rows by integer rowid, and <c>MessageSummary</c>'s rowid
/// is not stable — a table rebuild or VACUUM may renumber it. <c>SearchKey.id</c> is an INTEGER PRIMARY
/// KEY, which SQLite never renumbers.</para>
/// <para><b>Contentless.</b> The FTS table keeps only the index, not a second copy of every body.</para>
/// </summary>
public partial class LocalStoreService
{
    /// <summary>Rows indexed per write transaction; small enough that a sync's upserts never wait long.</summary>
    internal const int SearchIndexBatchSize = 200;

    /// <summary>What a search indexes before it queries, so mail cached a moment ago is findable.</summary>
    internal const int SearchFlushBeforeQuery = 2000;

    private bool _searchIndexAvailable;
    private bool _backgroundIndexing;
    private int _indexerRunning;

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
                    ON CONFLICT(account_id, folder_name, unique_id) DO UPDATE SET pending = 1;
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
                    ON CONFLICT(account_id, folder_name, unique_id) DO UPDATE SET pending = 1;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_summary_delete AFTER DELETE ON MessageSummary BEGIN
                    DELETE FROM MessageSearch WHERE rowid = (SELECT id FROM SearchKey
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id);
                    DELETE FROM SearchKey
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_detail_insert AFTER INSERT ON MessageDetail BEGIN
                    UPDATE SearchKey SET pending = 1
                        WHERE account_id = new.account_id AND folder_name = new.folder_name AND unique_id = new.unique_id;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_detail_update AFTER UPDATE ON MessageDetail
                WHEN old.plain_body IS NOT new.plain_body OR old.html_body IS NOT new.html_body
                  OR old.cc IS NOT new.cc OR old.to_addr IS NOT new.to_addr OR old.from_addr IS NOT new.from_addr
                  OR old.attachments_json IS NOT new.attachments_json
                  OR old.account_id IS NOT new.account_id OR old.folder_name IS NOT new.folder_name
                  OR old.unique_id IS NOT new.unique_id
                BEGIN
                    UPDATE SearchKey SET pending = 1
                        WHERE account_id = new.account_id AND folder_name = new.folder_name AND unique_id = new.unique_id;
                    UPDATE SearchKey SET pending = 1
                        WHERE account_id = old.account_id AND folder_name = old.folder_name AND unique_id = old.unique_id;
                END;

                CREATE TRIGGER IF NOT EXISTS trg_search_detail_delete AFTER DELETE ON MessageDetail BEGIN
                    UPDATE SearchKey SET pending = 1
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
        if (Interlocked.CompareExchange(ref _indexerRunning, 1, 0) != 0) return;
        Task.Run(async () =>
        {
            var total = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    var n = await IndexPendingSearchAsync(SearchIndexBatchSize, CancellationToken.None);
                    total += n;
                    if (n < SearchIndexBatchSize) break;
                    // Room for a sync's upserts between batches.
                    await Task.Delay(25);
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
        });
    }

    /// <summary>
    /// Indexes up to <paramref name="maxRows"/> pending messages, newest first, in batches of
    /// <see cref="SearchIndexBatchSize"/>. Returns how many were indexed. Each batch reads and writes in
    /// one write transaction, so a message cannot change between being read and being indexed.
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
        long Id, bool HasSummary, string Subject, string Sender, string To, string Cc,
        string Body, string Attachments);

    private async Task<int> IndexBatchAsync(int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = new List<PendingRow>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = """
                SELECT k.id, s.unique_id IS NOT NULL,
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
                string S(int i) => r.IsDBNull(i) ? string.Empty : r.GetString(i);
                var plain = S(9);
                var html = S(10);
                var body = plain.Length > 0 ? plain
                         : html.Length > 0 ? HtmlStripper.ToPlainText(html, includeLinkTargets: false)
                         : S(5);
                rows.Add(new PendingRow(
                    Id: r.GetInt64(0),
                    HasSummary: r.GetInt64(1) != 0,
                    Subject: S(2),
                    Sender: JoinDistinct(S(3), S(6)),
                    To: JoinDistinct(S(4), S(7)),
                    Cc: S(8),
                    Body: body,
                    Attachments: AttachmentNames(r.IsDBNull(11) ? null : r.GetString(11))));
            }
        }
        if (rows.Count == 0) return 0;

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

        await using var settle = conn.CreateCommand();
        settle.CommandText = "UPDATE SearchKey SET pending = 0 WHERE id = $id;";
        var pSettle = settle.Parameters.Add("$id", SqliteType.Integer);

        await using var orphan = conn.CreateCommand();
        orphan.CommandText = "DELETE FROM SearchKey WHERE id = $id;";
        var pOrphan = orphan.Parameters.Add("$id", SqliteType.Integer);

        foreach (var row in rows)
        {
            pDelete.Value = row.Id;
            await delete.ExecuteNonQueryAsync(ct);
            if (!row.HasSummary)
            {
                // A key whose message has gone by a path no trigger saw (a REPLACE conflict deletes
                // without firing delete triggers). Nothing to index; drop the key.
                pOrphan.Value = row.Id;
                await orphan.ExecuteNonQueryAsync(ct);
                continue;
            }
            pId.Value = row.Id;
            pSubject.Value = row.Subject;
            pSender.Value = row.Sender;
            pTo.Value = row.To;
            pCc.Value = row.Cc;
            pBody.Value = row.Body;
            pAtt.Value = row.Attachments;
            await insert.ExecuteNonQueryAsync(ct);
            pSettle.Value = row.Id;
            await settle.ExecuteNonQueryAsync(ct);
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
    /// <see cref="SearchMatchExpression"/>), limited to <paramref name="folders"/> when given. Indexes
    /// recent pending work first, so a message cached a moment ago is found. Throws
    /// <see cref="SqliteException"/> for an expression FTS5 rejects; callers treat that as "the index has
    /// no answer" and fall back to the rows they hold.
    /// </summary>
    public async Task<List<SearchHit>> FindMessagesAsync(
        string match,
        IReadOnlyCollection<(Guid AccountId, string FolderName)>? folders,
        CancellationToken ct = default)
    {
        if (!_searchIndexAvailable) return [];
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
            WHERE MessageSearch MATCH $match{scope};
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
    /// <summary>
    /// Every cached message in <paramref name="accountIds"/> matching <paramref name="query"/>, newest first —
    /// what a Search Results folder shows (#717). The same rules as the search box: a message has the words
    /// if the index finds them or its row contains them, and loses them if either finds an excluded word.
    /// Conditions are SQL. <c>account:</c> is not applied here: account names live with the caller, which
    /// narrows <paramref name="accountIds"/> instead. Works, on the rows alone, when the index is unavailable.
    /// </summary>
    public async Task<List<MailMessageSummary>> SearchSummariesAsync(
        MessageSearchQuery query, IReadOnlyCollection<Guid> accountIds, CancellationToken ct = default)
    {
        if (accountIds.Count == 0) return [];
        if (_searchIndexAvailable)
            await IndexPendingSearchAsync(SearchFlushBeforeQuery, ct);

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        var sql = new StringBuilder(
            "SELECT s.unique_id, s.account_id, s.folder_name, s.from_disp, s.to_addr, s.subject, s.date_ticks, s.is_read, " +
            "s.preview_text, s.is_replied, s.is_forwarded, s.has_attachments, s.is_mailing_list, s.flag_id, s.internet_message_id " +
            "FROM MessageSummary s WHERE s.account_id IN (SELECT value FROM json_each($accounts))");
        cmd.Parameters.AddWithValue("$accounts", JsonSerializer.Serialize(accountIds.Select(a => a.ToString())));

        if (query.HasAttachment.HasValue) sql.Append(query.HasAttachment.Value ? " AND s.has_attachments = 1" : " AND s.has_attachments = 0");
        if (query.IsRead.HasValue)        sql.Append(query.IsRead.Value ? " AND s.is_read = 1" : " AND s.is_read = 0");
        if (query.IsFlagged.HasValue)     sql.Append(query.IsFlagged.Value ? " AND s.flag_id IS NOT NULL" : " AND s.flag_id IS NULL");
        if (query.After.HasValue)
        {
            sql.Append(" AND s.date_ticks >= $after");
            cmd.Parameters.AddWithValue("$after", new DateTimeOffset(query.After.Value).UtcTicks);
        }
        if (query.Before.HasValue)
        {
            sql.Append(" AND s.date_ticks < $before");
            cmd.Parameters.AddWithValue("$before", new DateTimeOffset(query.Before.Value).UtcTicks);
        }
        if (query.Folders.Count > 0)
        {
            var any = new List<string>();
            for (int i = 0; i < query.Folders.Count; i++)
            {
                any.Add($"s.folder_name LIKE $folder{i} ESCAPE '!' OR EXISTS (SELECT 1 FROM Folder f WHERE f.account_id = s.account_id " +
                        $"AND f.full_name = s.folder_name AND f.display_name LIKE $folder{i} ESCAPE '!')");
                cmd.Parameters.AddWithValue($"$folder{i}", LikeContains(query.Folders[i]));
            }
            sql.Append(" AND (").Append(string.Join(" OR ", any)).Append(')');
        }

        const string keyInIndex =
            "(s.account_id, s.folder_name, s.unique_id) IN (SELECT k.account_id, k.folder_name, k.unique_id " +
            "FROM MessageSearch m JOIN SearchKey k ON k.id = m.rowid WHERE MessageSearch MATCH {0})";
        var likeIndex = 0;

        var wanted = query.Terms.Where(t => !t.Negated).ToList();
        if (wanted.Count > 0)
        {
            var either = new List<string>();
            var match = _searchIndexAvailable ? SearchMatchExpression.AllOf(wanted) : null;
            if (match != null)
            {
                either.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, keyInIndex, "$wanted"));
                cmd.Parameters.AddWithValue("$wanted", match);
            }
            var rowTerms = wanted.Select(t => RowLike(cmd, t, ref likeIndex)).ToList();
            if (rowTerms.All(t => t != null))
                either.Add("(" + string.Join(" AND ", rowTerms) + ")");
            // A Cc or attachment word with no index to ask: nothing can have it.
            if (either.Count == 0) return [];
            sql.Append(" AND (").Append(string.Join(" OR ", either)).Append(')');
        }

        var unwanted = query.Terms.Where(t => t.Negated).ToList();
        if (unwanted.Count > 0)
        {
            var match = _searchIndexAvailable ? SearchMatchExpression.AnyOf(unwanted) : null;
            if (match != null)
            {
                sql.Append(" AND NOT ").Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, keyInIndex, "$unwanted"));
                cmd.Parameters.AddWithValue("$unwanted", match);
            }
            foreach (var t in unwanted)
            {
                var like = RowLike(cmd, t, ref likeIndex);
                if (like != null) sql.Append(" AND NOT ").Append(like);
            }
        }

        sql.Append(" ORDER BY s.date_ticks DESC;");
        cmd.CommandText = sql.ToString();
        return await ReadSummariesAsync(cmd);
    }

    /// <summary>The row columns a field's word can be found in without the index.</summary>
    private static string[] RowColumnsFor(SearchField field) => field switch
    {
        SearchField.Any     => ["s.from_disp", "s.to_addr", "s.subject", "s.preview_text"],
        SearchField.From    => ["s.from_disp"],
        SearchField.To      => ["s.to_addr"],
        SearchField.Subject => ["s.subject"],
        SearchField.Body    => ["s.preview_text"],
        _                   => [],
    };

    /// <summary>"The row contains this word" as SQL, or null when the row has no column that could.</summary>
    private static string? RowLike(SqliteCommand cmd, SearchTerm term, ref int index)
    {
        var columns = RowColumnsFor(term.Field);
        if (columns.Length == 0) return null;
        var name = $"$like{index++}";
        cmd.Parameters.AddWithValue(name, LikeContains(term.Text));
        return "(" + string.Join(" OR ", columns.Select(c => $"{c} LIKE {name} ESCAPE '!'")) + ")";
    }

    /// <summary>A LIKE pattern for "contains <paramref name="text"/>", with <c>!</c> as the escape character.</summary>
    private static string LikeContains(string text)
        => "%" + text.Replace("!", "!!", StringComparison.Ordinal)
                     .Replace("%", "!%", StringComparison.Ordinal)
                     .Replace("_", "!_", StringComparison.Ordinal) + "%";
}
