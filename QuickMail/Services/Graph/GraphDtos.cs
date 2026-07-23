using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickMail.Services.Graph;

/// <summary>
/// A server-side Inbox rule (<c>GET/POST/PATCH/DELETE /me/mailFolders/inbox/messageRules</c>).
/// <para>
/// <b>conditions / actions / exceptions are kept as raw <see cref="JsonElement"/> deliberately.</b>
/// Graph <c>PATCH</c> <i>replaces</i> these complex objects wholesale rather than merging individual
/// predicates, so a model that only understands a subset would silently drop predicates the user set
/// in Outlook. Retaining the raw JSON lets us (a) decide whether a rule is fully representable before
/// allowing an edit, and (b) render the complete rule to the user even when it isn't editable.
/// See <c>docs/planning/server-rules-pm-dev-spec.md</c> §16.
/// </para>
/// </summary>
internal sealed class GraphMessageRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("isReadOnly")] public bool IsReadOnly { get; set; }
    [JsonPropertyName("hasError")] public bool HasError { get; set; }
    [JsonPropertyName("conditions")] public JsonElement? Conditions { get; set; }
    [JsonPropertyName("actions")] public JsonElement? Actions { get; set; }
    [JsonPropertyName("exceptions")] public JsonElement? Exceptions { get; set; }
}

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

/// <summary>One calendar from <c>/me/calendars</c> — its id and display name (multi-calendar sync).</summary>
internal sealed class GraphCalendar
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
}

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

/// <summary>Request body for <c>POST /me/events</c> (create push, single events only in v1).</summary>
internal sealed class GraphCreateEventBody
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphItemBody? Body { get; set; }
    [JsonPropertyName("start")] public GraphDateTimeTimeZone? Start { get; set; }
    [JsonPropertyName("end")] public GraphDateTimeTimeZone? End { get; set; }
    [JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphLocation? Location { get; set; }
    [JsonPropertyName("isAllDay")] public bool IsAllDay { get; set; }
}
