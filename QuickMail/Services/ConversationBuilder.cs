using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Builds <see cref="ConversationGroup"/> instances from a flat message list.</summary>
public static class ConversationBuilder
{
    // Matches one leading Re:/Fwd: prefix variant, case-insensitively.
    // Applied repeatedly until no more prefixes remain ("Re: Re: Fwd: X" → "X").
    private static readonly Regex _prefixRe =
        new(@"^\s*(re|fwd?)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strips all leading Re:/Fwd: chains until none remain.
    /// Examples: "Re: Meeting" → "Meeting", "Re: Re: Fwd: Meeting" → "Meeting".
    /// </summary>
    public static string NormalizeSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
        var current = subject.Trim();
        while (true)
        {
            var next = _prefixRe.Replace(current, string.Empty).TrimStart();
            if (next == current) break;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Groups <paramref name="messages"/> by normalised subject (case-insensitive) and sorts
    /// each group newest-first. Groups are returned in an unspecified order — the caller applies
    /// the desired sort.
    /// </summary>
    public static IReadOnlyList<ConversationGroup> Build(IEnumerable<MailMessageSummary> messages)
    {
        return messages
            .GroupBy(m => NormalizeSubject(m.Subject), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sorted = g.OrderByDescending(m => m.Date).ToList();
                return new ConversationGroup
                {
                    NormalizedSubject = g.Key,
                    Messages          = sorted,
                };
            })
            .ToList();
    }
}
