using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class ConversationPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(ConversationGroup group)
    {
        var unreadCount = group.Messages.Count(m => !m.IsRead);

        var participants = group.Messages
            .Select(m => m.From)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<PropertyItem>
        {
            new("Subject",      group.Subject),
            new("Messages",     group.Messages.Count.ToString()),
            new("Unread",       unreadCount == 0 ? "None" : unreadCount.ToString()),
            new("Participants", participants.Count > 0
                                    ? string.Join("; ", participants)
                                    : "(none)"),
        };

        if (group.Messages.Count > 1)
        {
            var newest = group.Messages.Max(m => m.Date).ToLocalTime().ToString("f");
            var oldest = group.Messages.Min(m => m.Date).ToLocalTime().ToString("f");
            items.Add(new("Newest", newest));
            items.Add(new("Oldest", oldest));
        }
        else if (group.Messages.Count == 1)
        {
            items.Add(new("Date", group.Messages[0].Date.ToLocalTime().ToString("f")));
        }

        return ("Conversation Properties", [new("Conversation", items)]);
    }
}
