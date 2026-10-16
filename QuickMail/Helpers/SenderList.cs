using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// The senders of a set of messages, as the Properties windows list them: each one "Name &lt;address&gt;", the way a
/// single message's Properties shows its sender (review of #806). One entry per distinct name and address, so
/// two people sharing a name, or a forger using a real sender's name, are listed separately rather than hidden
/// under one name.
/// </summary>
internal static class SenderList
{
    /// <summary>
    /// Distinct senders, in message order. Two kinds of entry say less than another one beside them, and are left
    /// out when it is there: a name alone, from a message whose address is not known yet (cached before the
    /// sender-address catch-up reached it), beside that name with its address; and an address alone, from a message
    /// sent with no display name, beside that address with a name. Otherwise one sender who wrote once with a name
    /// and once without is listed twice.
    /// </summary>
    internal static List<string> Of(IEnumerable<MailMessageSummary> messages)
    {
        var senders = messages
            .Select(m => (Name: m.From?.Trim() ?? string.Empty, Email: m.FromEmail?.Trim() ?? string.Empty,
                          Full: m.FromWithAddress.Trim()))
            .Where(s => s.Full.Length > 0)
            .ToList();
        // Entries that carry both a name and an address: Full is then neither the name alone nor the address alone.
        var both = senders
            .Where(s => !Same(s.Full, s.Name) && !Same(s.Full, s.Email))
            .ToList();
        var namesWithAnAddress = both.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addressesWithAName = both.Select(s => s.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return senders
            .Where(s => !(Same(s.Full, s.Name) && namesWithAnAddress.Contains(s.Name)))
            // The bare address, whether or not the catch-up has filled the row's address in yet. An entry with a name
            // never reads as a bare address, so only address-only entries can match.
            .Where(s => !addressesWithAName.Contains(s.Full))
            .Select(s => s.Full)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>How many senders a Properties value names before it says how many more there are.</summary>
    internal const int MaxListed = 10;

    /// <summary>
    /// <paramref name="senders"/> as one Properties value: the first <see cref="MaxListed"/>, then how many more. A
    /// long thread, or a group whose sender uses a new address on every message, would otherwise be one line read
    /// out as hundreds of addresses (review of #806).
    /// </summary>
    internal static string Describe(IReadOnlyList<string> senders) =>
        senders.Count <= MaxListed
            ? string.Join("; ", senders)
            : $"{string.Join("; ", senders.Take(MaxListed))}; and {senders.Count - MaxListed} more";
}
