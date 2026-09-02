using System;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>What kind of composition is being performed — drives the window/tab title prefix.</summary>
public enum ComposeKind
{
    NewMessage,
    Reply,
    ReplyAll,
    Forward,
    EditDraft,
    NewDraft,
    EditTemplate,
}

public class ComposeModel
{
    /// <summary>What kind of composition this is; used to determine the window title prefix.</summary>
    public ComposeKind Kind { get; set; } = ComposeKind.NewMessage;

    public Guid AccountId { get; set; }
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>The editing mode this message was composed in.</summary>
    public ComposeMode Mode { get; set; } = ComposeMode.PlainText;

    /// <summary>
    /// Complete HTML document for the text/html part. When non-empty the message
    /// is sent as multipart/alternative with <see cref="Body"/> as the text/plain part;
    /// when empty the message is text/plain only (existing behavior).
    /// </summary>
    public string? HtmlBody { get; set; }
    /// <summary>RFC 2822 Message-ID of the message being replied to.</summary>
    public string? InReplyToMessageId { get; set; }

    /// <summary>Server message id of the existing draft (null when composing new).</summary>
    public string? DraftMessageId { get; set; }

    /// <summary>Folder name of the existing draft (null when composing new).</summary>
    public string? DraftFolderName { get; set; }

    /// <summary>
    /// Id of the local Outbox row this compose was opened from, or that it last saved into (null when
    /// nothing is queued). A save or send replaces that row instead of queuing another (issue #637).
    /// </summary>
    public string? OutboxId { get; set; }

    public List<AttachmentModel> Attachments { get; set; } = [];

    /// <summary>A shallow copy with no attachments — what the Outbox stores as JSON, keeping the bytes in their own rows.</summary>
    public ComposeModel WithoutAttachments() => new()
    {
        Kind = Kind,
        AccountId = AccountId,
        To = To,
        Cc = Cc,
        Bcc = Bcc,
        Subject = Subject,
        Body = Body,
        Mode = Mode,
        HtmlBody = HtmlBody,
        InReplyToMessageId = InReplyToMessageId,
        DraftMessageId = DraftMessageId,
        DraftFolderName = DraftFolderName,
        OutboxId = OutboxId,
        Attachments = [],
    };
}
