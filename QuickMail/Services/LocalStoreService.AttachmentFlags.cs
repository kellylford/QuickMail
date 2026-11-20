using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

/// <summary>
/// The one-time attachment-flag pass (#931). IMAP summaries carried no attachment flag before then, so IMAP rows
/// already cached read as having none. att_checked marks the rows whose flag has been decided: every row a summary
/// writes with a known flag, and each row this pass asks the server about. A flag already set is never cleared.
/// </summary>
public partial class LocalStoreService
{
    public async Task<Dictionary<string, List<string>>> GetMessageIdsWithUncheckedAttachmentsAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // The Inbox first: it is what is on screen at startup, and on Gmail the label folders repeat its messages.
        cmd.CommandText = """
            SELECT folder_name, unique_id FROM MessageSummary
            WHERE account_id = $aid AND att_checked = 0
            ORDER BY CASE WHEN upper(folder_name) = 'INBOX' THEN 0 ELSE 1 END, folder_name, date_ticks DESC;
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        // Insertion-ordered, so the Inbox stays first.
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

    public async Task<IReadOnlyList<string>> UpdateAttachmentFlagsBatchAsync(
        Guid accountId, string folderName, IEnumerable<string> checkedIds,
        IReadOnlyDictionary<string, string> withAttachments)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var check = conn.CreateCommand();
        check.CommandText = """
            UPDATE MessageSummary SET att_checked = 1
            WHERE unique_id = $uid AND account_id = $aid AND folder_name = $fn;
            """;
        check.Parameters.AddWithValue("$aid", accountId.ToString());
        check.Parameters.AddWithValue("$fn", folderName);
        var cUid = check.Parameters.Add("$uid", SqliteType.Text);

        // Flagged only where the row's Message-ID is the one the server gave, or the row has none to check: a server
        // that renumbered a folder (a new UIDVALIDITY) would otherwise have one message's attachments flag another,
        // for good. Angle brackets are trimmed on both sides, as in the sender-address pass.
        await using var flag = conn.CreateCommand();
        flag.CommandText = """
            UPDATE MessageSummary SET has_attachments = 1
            WHERE unique_id = $uid AND account_id = $aid AND folder_name = $fn AND has_attachments = 0
              AND trim(internet_message_id, '<> ') IN ('', $imid);
            """;
        flag.Parameters.AddWithValue("$aid", accountId.ToString());
        flag.Parameters.AddWithValue("$fn", folderName);
        var fUid  = flag.Parameters.Add("$uid",  SqliteType.Text);
        var fImid = flag.Parameters.Add("$imid", SqliteType.Text);

        foreach (var id in checkedIds)
        {
            cUid.Value = id;
            await check.ExecuteNonQueryAsync();
        }

        var flagged = new List<string>();
        foreach (var (id, imid) in withAttachments)
        {
            fUid.Value  = id;
            fImid.Value = (imid ?? string.Empty).Trim('<', '>', ' ');
            if (await flag.ExecuteNonQueryAsync() > 0) flagged.Add(id);
        }
        await tx.CommitAsync();
        return flagged;
    }
}
