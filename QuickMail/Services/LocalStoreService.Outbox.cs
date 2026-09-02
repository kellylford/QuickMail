using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>The Outbox tables (#637): see the DDL comment in <see cref="Initialize"/>.</summary>
public partial class LocalStoreService
{
    // Enum names rather than numbers, so renumbering ComposeMode/ComposeKind cannot silently
    // change what a queued message means; nulls dropped so the JSON stays small.
    private static readonly JsonSerializerOptions OutboxJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string OutboxItemColumns =
        "id, account_id, kind, state, created_ticks, updated_ticks, attempts, last_error, " +
        "next_attempt_ticks, replace_draft_id, draft_folder_name, subject, to_addr, cc, bcc, has_attachments";

    public async Task UpsertOutboxItemAsync(OutboxItem item, ComposeModel compose)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(compose);
        if (string.IsNullOrEmpty(item.Id))
            throw new ArgumentException("An Outbox item needs an id.", nameof(item));

        // Only attachments whose bytes are in memory can be sent later; an unloaded one (a forward
        // whose download never completed) would be dropped by MimeMessageBuilder anyway.
        var loaded = compose.Attachments.Where(a => a.IsLoaded).ToList();
        item.HasAttachments = loaded.Count > 0;
        item.UpdatedUtc = DateTimeOffset.UtcNow;
        if (item.CreatedUtc == default) item.CreatedUtc = item.UpdatedUtc;

        var json = JsonSerializer.Serialize(compose.WithoutAttachments(), OutboxJson);

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO Outbox({OutboxItemColumns}, compose_json)
                VALUES ($id, $aid, $kind, $state, $created, $updated, $attempts, $err,
                        $next, $replace, $dfolder, $subject, $to, $cc, $bcc, $hasAtt, $json)
                ON CONFLICT(id) DO UPDATE SET
                    account_id = excluded.account_id,
                    kind = excluded.kind,
                    state = excluded.state,
                    updated_ticks = excluded.updated_ticks,
                    attempts = excluded.attempts,
                    last_error = excluded.last_error,
                    next_attempt_ticks = excluded.next_attempt_ticks,
                    replace_draft_id = excluded.replace_draft_id,
                    draft_folder_name = excluded.draft_folder_name,
                    subject = excluded.subject,
                    to_addr = excluded.to_addr,
                    cc = excluded.cc,
                    bcc = excluded.bcc,
                    has_attachments = excluded.has_attachments,
                    compose_json = excluded.compose_json;
                """;
            cmd.Parameters.AddWithValue("$id",       item.Id);
            cmd.Parameters.AddWithValue("$aid",      item.AccountId.ToString());
            cmd.Parameters.AddWithValue("$kind",     (int)item.Kind);
            cmd.Parameters.AddWithValue("$state",    (int)item.State);
            cmd.Parameters.AddWithValue("$created",  item.CreatedUtc.UtcTicks);
            cmd.Parameters.AddWithValue("$updated",  item.UpdatedUtc.UtcTicks);
            cmd.Parameters.AddWithValue("$attempts", item.Attempts);
            cmd.Parameters.AddWithValue("$err",      (object?)item.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$next",     item.NextAttemptUtc.HasValue ? item.NextAttemptUtc.Value.UtcTicks : DBNull.Value);
            cmd.Parameters.AddWithValue("$replace",  (object?)item.ReplaceDraftId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dfolder",  (object?)item.DraftFolderName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subject",  item.Subject ?? string.Empty);
            cmd.Parameters.AddWithValue("$to",       item.To ?? string.Empty);
            cmd.Parameters.AddWithValue("$cc",       item.Cc ?? string.Empty);
            cmd.Parameters.AddWithValue("$bcc",      item.Bcc ?? string.Empty);
            cmd.Parameters.AddWithValue("$hasAtt",   item.HasAttachments ? 1 : 0);
            cmd.Parameters.AddWithValue("$json",     json);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM OutboxAttachment WHERE outbox_id = $id;";
            del.Parameters.AddWithValue("$id", item.Id);
            await del.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < loaded.Count; i++)
        {
            var att = loaded[i];
            await using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO OutboxAttachment(outbox_id, ordinal, file_name, content_type, size, content) " +
                "VALUES ($id, $ord, $name, $type, $size, $bytes);";
            ins.Parameters.AddWithValue("$id",    item.Id);
            ins.Parameters.AddWithValue("$ord",   i);
            ins.Parameters.AddWithValue("$name",  att.FileName ?? string.Empty);
            ins.Parameters.AddWithValue("$type",  att.ContentType ?? "application/octet-stream");
            ins.Parameters.AddWithValue("$size",  att.FileSize > 0 ? att.FileSize : att.Content!.LongLength);
            ins.Parameters.AddWithValue("$bytes", att.Content!);
            await ins.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<List<OutboxItem>> LoadOutboxItemsAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {OutboxItemColumns} FROM Outbox ORDER BY created_ticks DESC;";
        var list = new List<OutboxItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ReadOutboxItem(r));
        return list;
    }

    public async Task<OutboxItem?> LoadOutboxItemAsync(string id)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {OutboxItemColumns} FROM Outbox WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadOutboxItem(r) : null;
    }

    public async Task<ComposeModel?> LoadOutboxComposeAsync(string id)
    {
        await using var conn = await OpenAsync();

        string? json;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT compose_json FROM Outbox WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            json = await cmd.ExecuteScalarAsync() as string;
        }
        if (json == null) return null;

        var compose = JsonSerializer.Deserialize<ComposeModel>(json, OutboxJson);
        if (compose == null) return null;
        compose.OutboxId = id;
        compose.Attachments = [];

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT file_name, content_type, size, content FROM OutboxAttachment " +
                "WHERE outbox_id = $id ORDER BY ordinal;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                compose.Attachments.Add(new AttachmentModel
                {
                    FileName    = r.GetString(0),
                    ContentType = r.GetString(1),
                    FileSize    = r.GetInt64(2),
                    Content     = (byte[])r[3],
                });
            }
        }
        return compose;
    }

    public async Task UpdateOutboxStateAsync(
        string id, OutboxState state, int attempts, string? lastError, DateTimeOffset? nextAttemptUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE Outbox SET state = $state, attempts = $attempts, last_error = $err, " +
            "next_attempt_ticks = $next, updated_ticks = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$state",    (int)state);
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue("$err",      (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next",     nextAttemptUtc.HasValue ? nextAttemptUtc.Value.UtcTicks : DBNull.Value);
        cmd.Parameters.AddWithValue("$updated",  DateTimeOffset.UtcNow.UtcTicks);
        cmd.Parameters.AddWithValue("$id",       id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteOutboxItemAsync(string id)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM OutboxAttachment WHERE outbox_id = $id;" +
            "DELETE FROM Outbox WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task<int> CountOutboxItemsAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Outbox;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static OutboxItem ReadOutboxItem(SqliteDataReader r) => new()
    {
        Id              = r.GetString(0),
        AccountId       = Guid.Parse(r.GetString(1)),
        Kind            = (OutboxKind)r.GetInt32(2),
        State           = (OutboxState)r.GetInt32(3),
        CreatedUtc      = new DateTimeOffset(r.GetInt64(4), TimeSpan.Zero),
        UpdatedUtc      = new DateTimeOffset(r.GetInt64(5), TimeSpan.Zero),
        Attempts        = r.GetInt32(6),
        LastError       = r.IsDBNull(7) ? null : r.GetString(7),
        NextAttemptUtc  = r.IsDBNull(8) ? null : new DateTimeOffset(r.GetInt64(8), TimeSpan.Zero),
        ReplaceDraftId  = r.IsDBNull(9) ? null : r.GetString(9),
        DraftFolderName = r.IsDBNull(10) ? null : r.GetString(10),
        Subject         = r.GetString(11),
        To              = r.GetString(12),
        Cc              = r.GetString(13),
        Bcc             = r.GetString(14),
        HasAttachments  = r.GetInt32(15) != 0,
    };
}
