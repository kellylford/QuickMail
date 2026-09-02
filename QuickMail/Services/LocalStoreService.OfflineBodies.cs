using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>The offline-bodies pass's one query (#637).</summary>
public partial class LocalStoreService
{
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
