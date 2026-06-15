using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QuickMail.Services.Graph;

/// <summary>Paging envelope for a Microsoft Graph collection response.</summary>
internal sealed class GraphCollection<T>
{
    [JsonPropertyName("value")] public List<T> Value { get; set; } = new();
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

internal sealed class GraphMe
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("userPrincipalName")] public string? UserPrincipalName { get; set; }
}

internal sealed class GraphMailFolder
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("parentFolderId")] public string? ParentFolderId { get; set; }
    [JsonPropertyName("totalItemCount")] public int TotalItemCount { get; set; }
    [JsonPropertyName("unreadItemCount")] public int UnreadItemCount { get; set; }
}

internal sealed class GraphEmailAddress
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }

    /// <summary>Render as an RFC822-style header value: "Name &lt;addr&gt;", or just the address.</summary>
    public string AsHeaderString()
        => string.IsNullOrEmpty(Name) || string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase)
            ? Address ?? string.Empty
            : $"{Name} <{Address}>";
}

internal sealed class GraphRecipient
{
    [JsonPropertyName("emailAddress")] public GraphEmailAddress? EmailAddress { get; set; }
}

internal sealed class GraphItemBody
{
    [JsonPropertyName("contentType")] public string? ContentType { get; set; } // "html" | "text"
    [JsonPropertyName("content")] public string? Content { get; set; }
}

internal sealed class GraphAttachment
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("isInline")] public bool IsInline { get; set; }
}

internal sealed class GraphMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("bodyPreview")] public string? BodyPreview { get; set; }
    [JsonPropertyName("body")] public GraphItemBody? Body { get; set; }
    [JsonPropertyName("from")] public GraphRecipient? From { get; set; }
    [JsonPropertyName("toRecipients")] public List<GraphRecipient>? ToRecipients { get; set; }
    [JsonPropertyName("ccRecipients")] public List<GraphRecipient>? CcRecipients { get; set; }
    [JsonPropertyName("replyTo")] public List<GraphRecipient>? ReplyTo { get; set; }
    [JsonPropertyName("internetMessageId")] public string? InternetMessageId { get; set; }
    [JsonPropertyName("receivedDateTime")] public DateTimeOffset ReceivedDateTime { get; set; }
    [JsonPropertyName("isRead")] public bool IsRead { get; set; }
    [JsonPropertyName("hasAttachments")] public bool HasAttachments { get; set; }
    [JsonPropertyName("attachments")] public List<GraphAttachment>? Attachments { get; set; }
    [JsonPropertyName("flag")] public GraphFollowUpFlag? Flag { get; set; }
}

internal sealed class GraphFollowUpFlag
{
    /// <summary>Values: "notFlagged" | "flagged" | "complete".</summary>
    [JsonPropertyName("flagStatus")] public string FlagStatus { get; set; } = "notFlagged";
}
