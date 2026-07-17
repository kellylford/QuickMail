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

/// <summary>
/// Response shape for a <c>/messages/delta</c> query. Exposes both cursors so the poll loop can
/// tell them apart: <see cref="NextLink"/> pages WITHIN a tick (transient, never persisted), while
/// <see cref="DeltaLink"/> is the cursor for the NEXT tick (persisted). See dev spec §6.12.
/// </summary>
internal sealed class GraphDeltaResponse
{
    [JsonPropertyName("value")]            public GraphMessage[]? Value { get; set; }
    [JsonPropertyName("@odata.nextLink")]  public string? NextLink { get; set; }
    [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; }
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
    [JsonPropertyName("childFolderCount")] public int ChildFolderCount { get; set; }
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

// ── Contact sync (issue #256) ────────────────────────────────────────────────

/// <summary>A saved contact from <c>/me/contacts</c>.</summary>
internal sealed class GraphContact
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("emailAddresses")] public List<GraphEmailAddress>? EmailAddresses { get; set; }
}

/// <summary>A relevance-ranked person from <c>/me/people</c> (prior recipients).</summary>
internal sealed class GraphPerson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("scoredEmailAddresses")] public List<GraphScoredEmailAddress>? ScoredEmailAddresses { get; set; }
}

internal sealed class GraphScoredEmailAddress
{
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("relevanceScore")] public double RelevanceScore { get; set; }
}

// ── Calendar sync (full-calendar spec, M4 read-down v1) ─────────────────────

/// <summary>
/// An event occurrence from <c>/me/calendarView</c>. calendarView expands recurring series
/// server-side, so each item is a concrete occurrence with its own id — no RRULE handling needed.
/// </summary>
internal sealed class GraphCalendarEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("bodyPreview")] public string? BodyPreview { get; set; }
    [JsonPropertyName("location")] public GraphLocation? Location { get; set; }
    [JsonPropertyName("organizer")] public GraphRecipient? Organizer { get; set; }
    [JsonPropertyName("start")] public GraphDateTimeTimeZone? Start { get; set; }
    [JsonPropertyName("end")] public GraphDateTimeTimeZone? End { get; set; }
    [JsonPropertyName("isAllDay")] public bool IsAllDay { get; set; }
    [JsonPropertyName("isOrganizer")] public bool IsOrganizer { get; set; }
    [JsonPropertyName("responseStatus")] public GraphResponseStatus? ResponseStatus { get; set; }
}

/// <summary>Graph's dateTimeTimeZone shape: a wall-clock string plus the zone it is expressed in.</summary>
internal sealed class GraphDateTimeTimeZone
{
    [JsonPropertyName("dateTime")] public string? DateTime { get; set; }
    [JsonPropertyName("timeZone")] public string? TimeZone { get; set; }
}

internal sealed class GraphLocation
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

internal sealed class GraphResponseStatus
{
    /// <summary>Values: "none" | "organizer" | "tentativelyAccepted" | "accepted" | "declined" | "notResponded".</summary>
    [JsonPropertyName("response")] public string? Response { get; set; }
}
