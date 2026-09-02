using System;

namespace QuickMail.Models;

/// <summary>What a queued Outbox row is waiting to do.</summary>
public enum OutboxKind
{
    /// <summary>Upload to the account's Drafts folder.</summary>
    Draft = 0,
    /// <summary>Send, then file in Sent and trash any server draft it replaces.</summary>
    Send = 1,
}

/// <summary>Where a queued Outbox row is in its life.</summary>
public enum OutboxState
{
    /// <summary>Waiting for a connection (or for its backoff to elapse).</summary>
    Pending = 0,
    /// <summary>A drain is working on it right now.</summary>
    Sending = 1,
    /// <summary>The server answered and refused; only a manual retry or a reopen-and-send moves it.</summary>
    Failed = 2,
}

/// <summary>
/// One row of the local Outbox: a message or draft written on this computer while the server could
/// not be reached (issue #637). The compose model itself lives in the store as JSON plus attachment
/// blobs; this is the listing shape, denormalised so the Outbox folder can be drawn without parsing.
/// </summary>
public sealed class OutboxItem
{
    public const string IdPrefix = "outbox-";

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N");

    public string Id { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public OutboxKind Kind { get; set; }
    public OutboxState State { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Earliest time the next automatic attempt may run; null means eligible now.</summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }
    /// <summary>Server draft this row replaces on upload, or trashes after a send. Null when none.</summary>
    public string? ReplaceDraftId { get; set; }
    public string? DraftFolderName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public bool HasAttachments { get; set; }

    /// <summary>What the Outbox folder shows for this row's state.</summary>
    public string StateDisplay => (Kind, State) switch
    {
        (_, OutboxState.Failed)             => string.IsNullOrWhiteSpace(LastError) ? "Failed" : $"Failed: {LastError}",
        (OutboxKind.Send, OutboxState.Sending)  => "Sending…",
        (OutboxKind.Draft, OutboxState.Sending) => "Uploading draft…",
        (OutboxKind.Send, _)                => "Waiting to send",
        (OutboxKind.Draft, _)               => "Waiting to upload draft",
        _                                   => "Waiting",
    };

    public override string ToString() => $"{StateDisplay}: {Subject}";
}
