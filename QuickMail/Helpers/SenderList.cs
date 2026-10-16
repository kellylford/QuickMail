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
    /// Distinct senders, in message order. A message whose address is not known yet (cached before the
    /// sender-address catch-up reached it) gives its name alone, and that name is left out when another message
    /// under the same name has its address, which says more.
    /// </summary>
    internal static List<string> Of(IEnumerable<MailMessageSummary> messages)
    {
        var senders = messages
            .Select(m => (Name: m.From?.Trim() ?? string.Empty, Full: m.FromWithAddress.Trim()))
            .Where(s => s.Full.Length > 0)
            .ToList();
        var namesWithAnAddress = senders
            .Where(s => !string.Equals(s.Full, s.Name, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return senders
            .Where(s => !(string.Equals(s.Full, s.Name, StringComparison.OrdinalIgnoreCase)
                          && namesWithAnAddress.Contains(s.Name)))
            .Select(s => s.Full)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
