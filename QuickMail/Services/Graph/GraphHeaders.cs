using System.Collections.Generic;

namespace QuickMail.Services.Graph;

/// <summary>Shared Microsoft Graph request headers (protocol constants, not tied to any one service).</summary>
internal static class GraphHeaders
{
    /// <summary>
    /// <c>Prefer: IdType="ImmutableId"</c> — asks Graph for immutable message ids (#366). Default ids
    /// change when a message moves between folders or from server-side mailbox operations, staling
    /// cached ids; immutable ids stay constant. Sent on every message read AND every message-targeting
    /// write, so the id type is consistent across the round-trip.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ImmutableId =
        new Dictionary<string, string> { ["Prefer"] = "IdType=\"ImmutableId\"" };
}
