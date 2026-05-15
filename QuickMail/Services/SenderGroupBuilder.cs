using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Builds <see cref="SenderGroup"/> instances from a flat message list.</summary>
public static class SenderGroupBuilder
{
    /// <summary>
    /// Groups <paramref name="messages"/> by sender (From field, trimmed, case-insensitive),
    /// sorts each group newest-first, and orders groups by their newest message date descending
    /// (matching the flat message list order).
    /// </summary>
    public static IReadOnlyList<SenderGroup> Build(IEnumerable<MailMessageSummary> messages)
    {
        return messages
            .GroupBy(m => m.From.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sorted = g.OrderByDescending(m => m.Date).ToList();
                return new SenderGroup
                {
                    SenderKey = g.Key,
                    Messages  = sorted,
                };
            })
            .OrderBy(s => s.SenderKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Groups <paramref name="messages"/> by recipient (To field, trimmed, case-insensitive),
    /// sorts each group newest-first, and orders groups alphabetically.
    /// </summary>
    public static IReadOnlyList<SenderGroup> BuildByTo(IEnumerable<MailMessageSummary> messages)
    {
        return messages
            .GroupBy(m => string.IsNullOrWhiteSpace(m.To) ? "(no recipient)" : m.To.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sorted = g.OrderByDescending(m => m.Date).ToList();
                return new SenderGroup
                {
                    SenderKey = g.Key,
                    Messages  = sorted,
                };
            })
            .OrderBy(s => s.SenderKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
