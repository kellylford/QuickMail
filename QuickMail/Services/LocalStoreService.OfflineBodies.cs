using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>The offline-bodies pass's one query (#637).</summary>
public partial class LocalStoreService
{
    /// <summary>
    /// How many messages in <paramref name="folders"/> dated <paramref name="since"/> or later are cached, and
    /// how many of those have their full text (#717) — what the status bar reports about offline reading.
    /// </summary>
    public async Task<(int Total, int Downloaded)> CountOfflineBodiesAsync(
        IReadOnlyCollection<(Guid AccountId, string FolderName)> folders, DateTimeOffset since,
        System.Threading.CancellationToken ct = default)
    {
        if (folders.Count == 0) return (0, 0);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), SUM(CASE WHEN d.unique_id IS NULL THEN 0 ELSE 1 END)
            FROM MessageSummary s
            LEFT JOIN MessageDetail d
              ON d.unique_id = s.unique_id AND d.account_id = s.account_id AND d.folder_name = s.folder_name
            WHERE s.date_ticks >= $since
              AND (s.account_id || char(31) || s.folder_name) IN (SELECT value FROM json_each($scope));
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcTicks);
        cmd.Parameters.AddWithValue("$scope", System.Text.Json.JsonSerializer.Serialize(
            folders.Select(f => f.AccountId.ToString() + "\u001f" + f.FolderName)));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (0, 0);
        return (r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1));
    }

    public async Task<List<string>> GetMessageIdsMissingDetailAsync(Guid accountId, string folderName, DateTimeOffset since, int limit)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Both tables share the composite primary key, so the join is index-backed. Newest first:
        // if the pass is cut short, the mail most likely to be read is what got cached.
        cmd.CommandText = """
            SELECT s.unique_id
            FROM MessageSummary s
            LEFT JOIN MessageDetail d
              ON d.unique_id = s.unique_id AND d.account_id = s.account_id AND d.folder_name = s.folder_name
            WHERE s.account_id = $aid AND s.folder_name = $fn AND s.date_ticks >= $since AND d.unique_id IS NULL
            ORDER BY s.date_ticks DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$aid",   accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",    folderName);
        cmd.Parameters.AddWithValue("$since", since.UtcTicks);
        cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

        var ids = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            ids.Add(r.GetString(0));
        return ids;
    }
}
