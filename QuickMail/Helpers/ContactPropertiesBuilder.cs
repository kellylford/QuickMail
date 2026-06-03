using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class ContactPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(ContactModel contact, IReadOnlyList<string> groupNames)
    {
        var details = new List<PropertyItem>
        {
            new("Display name",  NoneIfBlank(contact.DisplayName)),
            new("Email address", contact.EmailAddress),
            new("Last used",     contact.LastUsedTicks == 0
                                     ? "Never"
                                     : new DateTime(contact.LastUsedTicks, DateTimeKind.Utc)
                                           .ToLocalTime().ToString("D")),
            new("Groups",        groupNames.Count == 0
                                     ? "Not a member of any groups"
                                     : string.Join(", ", groupNames)),
        };
        return ("Contact Properties", [new("Contact", details)]);
    }

    private static string NoneIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(none)" : s;
}
