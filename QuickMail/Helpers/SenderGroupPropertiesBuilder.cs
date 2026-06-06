using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class SenderGroupPropertiesBuilder
{
    /// <param name="isToGroup">True when the group is from the To/Recipient view rather than the From/Sender view.</param>
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(SenderGroup group, bool isToGroup = false)
    {
        var role       = isToGroup ? "Recipient" : "Sender";
        var unreadCount = group.Messages.Count(m => !m.IsRead);

        var items = new List<PropertyItem>
        {
            new(role,       group.SenderKey),
            new("Messages", group.Messages.Count.ToString()),
            new("Unread",   unreadCount == 0 ? "None" : unreadCount.ToString()),
        };

        if (group.Messages.Count > 0)
        {
            items.Add(new("Latest",         group.Messages[0].Date.ToLocalTime().ToString("f")));
            items.Add(new("Newest subject", group.Messages[0].Subject));
        }

        return ($"{role} Group Properties", [new($"{role} Group", items)]);
    }
}
