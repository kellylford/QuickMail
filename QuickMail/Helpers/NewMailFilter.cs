using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Pure logic for deciding which just-synced inbox messages are "genuinely new" and should
/// raise a new-mail notification. Kept separate from <c>MainViewModel</c> so the rules are
/// unit-testable without a live VM. See docs/planning/notifications-pm-dev-spec.md.
/// </summary>
internal static class NewMailFilter
{
    // Separator that cannot appear in a Guid string or an IMAP message id, so the composite
    // key is unambiguous.
    private const string Sep = "|";

    /// <summary>Per-session identity for a message across repeated IDLE fires.</summary>
    public static string Key(MailMessageSummary m) => m.AccountId + Sep + m.MessageId;

    /// <summary>
    /// Returns the genuinely-new messages from <paramref name="incoming"/>: unread, dated at or
    /// after <paramref name="thresholdUtc"/> (so the startup backlog is excluded), and whose key
    /// is not already in <paramref name="alreadyNotified"/>. Survivors' keys are added to
    /// <paramref name="alreadyNotified"/> so a later call for the same message returns nothing.
    /// </summary>
    public static List<MailMessageSummary> SelectNew(
        IEnumerable<MailMessageSummary> incoming,
        DateTimeOffset thresholdUtc,
        ISet<string> alreadyNotified)
    {
        var result = new List<MailMessageSummary>();
        foreach (var m in incoming)
        {
            if (m.IsRead) continue;
            if (m.Date < thresholdUtc) continue;
            if (!alreadyNotified.Add(Key(m))) continue;
            result.Add(m);
        }
        return result;
    }
}
