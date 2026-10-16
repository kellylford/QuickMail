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
        var unreadCount = group.UnreadCount;

        // A sender group is keyed on the name, so it can hold mail from several addresses under that name: a
        // forger's among them. Each is listed as "Name <address>", as a single message's Properties shows its sender
        // (review of #806). A recipient group's key is already the To text, addresses and all.
        var who = isToGroup ? group.SenderKey : string.Join("; ", SenderList.Of(group.Messages));
        if (who.Length == 0) who = group.SenderKey;

        var items = new List<PropertyItem>
        {
            new(role,       who),
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
