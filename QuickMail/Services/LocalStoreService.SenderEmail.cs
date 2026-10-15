using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>
/// The one-time sender-address backfill (#803). Rows cached before from_email existed have it empty; these fill it
/// in place — never deleting a row or touching anything but the sender — first from what the database already
/// holds, then, for IMAP rows with no cached body, from addresses the sync fetches from the server.
/// Every write is guarded by <c>from_email = ''</c>, so running any of this twice changes nothing.
/// </summary>
public partial class LocalStoreService
{
    public async Task<int> BackfillSenderEmailsLocallyAsync(IReadOnlyCollection<Guid> graphAccountIds)
    {
        var graph = graphAccountIds.Select(g => g.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var conn = await OpenAsync();
        // Generation of the row's search entry when the index has already settled it, else null (pending, or no
        // index at all: without FTS5 there is no SearchKey table to join).
        var settledGeneration = _searchIndexAvailable
            ? "(SELECT k.generation FROM SearchKey k WHERE k.unique_id = s.unique_id AND k.account_id = s.account_id AND k.folder_name = s.folder_name AND k.pending = 0)"
            : "NULL";
        var updates = new List<(string Uid, string Aid, string Fn, string? From, string FromEmail, long? Generation)>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = $"""
                SELECT s.unique_id, s.account_id, s.folder_name, s.from_disp, d.from_addr, {settledGeneration}
                FROM MessageSummary s
                LEFT JOIN MessageDetail d USING (unique_id, account_id, folder_name)
                WHERE s.from_email = '' AND s.from_disp <> '';
                """;
            await using var r = await read.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var aid = r.GetString(1);
                var result = SenderAddressBackfill.For(
                    r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), graph.Contains(aid));
                if (result is { } found)
                    updates.Add((r.GetString(0), aid, r.GetString(2), found.From, found.FromEmail,
                                 r.IsDBNull(5) ? null : r.GetInt64(5)));
            }
        }
        if (updates.Count == 0) return 0;

        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE MessageSummary SET from_email = $email, from_disp = COALESCE($from, from_disp)
            WHERE unique_id = $uid AND account_id = $aid AND folder_name = $fn AND from_email = '';
            """;
        var pEmail = cmd.Parameters.Add("$email", SqliteType.Text);
        var pFrom  = cmd.Parameters.Add("$from",  SqliteType.Text);
        var pUid   = cmd.Parameters.Add("$uid",   SqliteType.Text);
        var pAid   = cmd.Parameters.Add("$aid",   SqliteType.Text);
        var pFn    = cmd.Parameters.Add("$fn",    SqliteType.Text);

        // Every address filled here was already in the row's search text: the old Graph text "Name <address>",
        // the cached body's sender line, or a Graph display text that is the address. The update trigger marks
        // the row for reindexing anyway, which on a large cache is a long background job that drops these rows
        // from index results until it is done, for words the index already has. So where the index had settled
        // the row and nothing but this update has touched it since (its generation moved by exactly one), it is
        // settled again (review of #806). The server half's addresses are new words and are reindexed as usual.
        await using var settle = conn.CreateCommand();
        settle.CommandText = """
            UPDATE SearchKey SET pending = 0
            WHERE unique_id = $uid AND account_id = $aid AND folder_name = $fn AND generation = $gen AND pending = 1;
            """;
        var sUid = settle.Parameters.Add("$uid", SqliteType.Text);
        var sAid = settle.Parameters.Add("$aid", SqliteType.Text);
        var sFn  = settle.Parameters.Add("$fn",  SqliteType.Text);
        var sGen = settle.Parameters.Add("$gen", SqliteType.Integer);

        var filled = 0;
        var leftPending = false;
        foreach (var (uid, aid, fn, from, email, generation) in updates)
        {
            pEmail.Value = email;
            pFrom.Value  = (object?)from ?? DBNull.Value;
            pUid.Value   = uid;
            pAid.Value   = aid;
            pFn.Value    = fn;
            if (await cmd.ExecuteNonQueryAsync() == 0) continue;
            filled++;
            if (generation is { } gen)
            {
                (sUid.Value, sAid.Value, sFn.Value, sGen.Value) = (uid, aid, fn, gen + 1);
                if (await settle.ExecuteNonQueryAsync() == 1) continue;
            }
            leftPending = true;
        }
        await tx.CommitAsync();
        if (leftPending) KickSearchIndexer();
        return filled;
    }

    public async Task<Dictionary<string, List<string>>> GetMessageIdsMissingSenderEmailAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT folder_name, unique_id FROM MessageSummary
            WHERE account_id = $aid AND from_email = '' AND from_disp <> ''
            ORDER BY folder_name, date_ticks DESC;
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        var byFolder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var folder = r.GetString(0);
            if (!byFolder.TryGetValue(folder, out var ids)) byFolder[folder] = ids = [];
            ids.Add(r.GetString(1));
        }
        return byFolder;
    }

    public async Task<int> UpdateSenderEmailsBatchAsync(
        Guid accountId, string folderName, IEnumerable<(string MessageId, string FromEmail, string InternetMessageId)> updates)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        // A server can renumber a folder (a new UIDVALIDITY) without the cache knowing, and then the stored id names
        // another message. So the address is written only where the row's Message-ID is the one the server gave,
        // or the row has none to check (review of #806). Angle brackets are trimmed on both sides: IMAP rows hold
        // the id without them and Graph rows with them.
        cmd.CommandText = """
            UPDATE MessageSummary SET from_email = $email
            WHERE unique_id = $uid AND account_id = $aid AND folder_name = $fn AND from_email = ''
              AND trim(internet_message_id, '<> ') IN ('', $imid);
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn", folderName);
        var pUid   = cmd.Parameters.Add("$uid",   SqliteType.Text);
        var pEmail = cmd.Parameters.Add("$email", SqliteType.Text);
        var pImid  = cmd.Parameters.Add("$imid",  SqliteType.Text);
        var written = 0;
        foreach (var (id, email, imid) in updates)
        {
            if (string.IsNullOrEmpty(email)) continue;
            pUid.Value   = id;
            pEmail.Value = email;
            pImid.Value  = (imid ?? string.Empty).Trim('<', '>', ' ');
            written += await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        if (written > 0) KickSearchIndexer();
        return written;
    }
}
