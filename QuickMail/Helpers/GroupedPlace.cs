using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Where the user was in a grouped message tree (Conversations, From, To), and where that is once the
/// tree's groups have been replaced. Group objects do not survive a rebuild, so a place is found again by
/// the message, then by the group's key, then by position.
/// </summary>
public static class GroupedPlace
{
    public static IReadOnlyList<MailMessageSummary>? MessagesOf(object? item) => item switch
    {
        ConversationGroup c => c.Messages,
        SenderGroup s       => s.Messages,
        _                   => null,
    };

    public static string? KeyOf(object? item) => item switch
    {
        ConversationGroup c => c.NormalizedSubject,
        SenderGroup s       => s.SenderKey,
        _                   => null,
    };

    /// <summary>The group's position, or its parent group's when <paramref name="selected"/> is a message.</summary>
    public static int IndexOf(IReadOnlyList<object> groups, object? selected)
    {
        if (selected is MailMessageSummary message)
        {
            for (var i = 0; i < groups.Count; i++)
                if (MessagesOf(groups[i])?.Contains(message) == true) return i;
            return -1;
        }
        for (var i = 0; i < groups.Count; i++)
            if (ReferenceEquals(groups[i], selected)) return i;
        return -1;
    }

    /// <summary>
    /// Finds a place again in rebuilt groups.
    /// </summary>
    /// <param name="groups">The tree's groups now.</param>
    /// <param name="item">The group or message the user was on; null if unknown.</param>
    /// <param name="index">Where that sat, for when nothing better is left.</param>
    /// <param name="messageOpen">
    /// A message is open in the reading pane. A selected group is what Delete and Archive act on, whatever
    /// has focus, so here only the user's own message or group will do: falling back to some other group
    /// would point those commands at mail the user cannot see.
    /// </param>
    /// <param name="keyOfMessage">The key of the group a message would belong to in this tree.</param>
    /// <returns>The group to select, and the message within it if there is one; (null, null) to leave
    /// the tree's selection alone.</returns>
    public static (object? Group, MailMessageSummary? Message) Find(
        IReadOnlyList<object> groups,
        object? item,
        int index,
        bool messageOpen,
        Func<MailMessageSummary, string?> keyOfMessage)
    {
        if (groups.Count == 0) return (null, null);

        if (item is MailMessageSummary message)
        {
            var home = groups.FirstOrDefault(g => MessagesOf(g)?.Contains(message) == true);
            if (home != null) return (home, message);
            if (messageOpen) return (null, null);
        }

        // A message that has gone falls to its own conversation or sender if that is still here.
        var key = KeyOf(item) ?? (item is MailMessageSummary gone ? keyOfMessage(gone) : null);
        if (key != null)
        {
            var same = groups.FirstOrDefault(g => string.Equals(KeyOf(g), key, StringComparison.OrdinalIgnoreCase));
            if (same != null) return (same, null);
        }

        if (messageOpen) return (null, null);
        return (groups[Math.Clamp(index, 0, groups.Count - 1)], null);
    }
}
