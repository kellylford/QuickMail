using System;

namespace QuickMail.Models;

/// <summary>
/// A standing subscription to one conversation, persisted in <c>watches.json</c>.
/// <para>Unlike a flag — which marks a message you already have — a watch is a predicate evaluated
/// at fetch time, so replies that have not arrived yet are already members. That is why the entry
/// stores a matching key rather than a list of message ids.</para>
/// </summary>
public class WatchedConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The matching key: <c>ConversationBuilder.NormalizeSubject</c> of the subject at watch time,
    /// compared case-insensitively. Never empty — <c>WatchService</c> refuses a blank key, because
    /// an empty key would match every blank-subject message in every account.
    /// </summary>
    public string NormalizedSubject { get; set; } = string.Empty;

    /// <summary>
    /// The full original subject as it appeared on the watched message, for display and
    /// announcements. Never used for matching — the prefix-stripped
    /// <see cref="NormalizedSubject"/> is.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
