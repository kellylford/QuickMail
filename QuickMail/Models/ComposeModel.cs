using System;
using System.Collections.Generic;

namespace QuickMail.Models;

public class ComposeModel
{
    public Guid AccountId { get; set; }
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    /// <summary>RFC 2822 Message-ID of the message being replied to.</summary>
    public string? InReplyToMessageId { get; set; }

    /// <summary>Server message id of the existing draft (null when composing new).</summary>
    public string? DraftMessageId { get; set; }

    /// <summary>Folder name of the existing draft (null when composing new).</summary>
    public string? DraftFolderName { get; set; }

    public List<AttachmentModel> Attachments { get; set; } = [];
}
