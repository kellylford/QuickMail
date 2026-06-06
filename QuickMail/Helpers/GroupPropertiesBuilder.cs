using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class GroupPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(GroupModel group, IReadOnlyList<ContactModel> members)
    {
        var details = new List<PropertyItem>
        {
            new("Group name",       group.Name),
            new("Members",          $"{group.ResolvedMemberCount} of {group.MemberContactIds.Count}"),
            new("Missing contacts", group.MissingContactCount.ToString()),
            new("Last used",        group.LastUsedTicks == 0
                                        ? "Never"
                                        : new DateTime(group.LastUsedTicks, DateTimeKind.Utc)
                                              .ToLocalTime().ToString("D")),
        };

        var memberItems = members
            .Select(c => new PropertyItem(
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.EmailAddress : c.DisplayName,
                c.EmailAddress))
            .ToList();

        IReadOnlyList<PropertySection> sections = memberItems.Count > 0
            ? [new("Group", details), new("Members", memberItems)]
            : [new("Group", details)];

        return ("Group Properties", sections);
    }
}
