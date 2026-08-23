using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// One message as it appears in a grouped view: the group whose node holds it, its position
/// inside that group, and the message itself. Flattening a grouped view into a list of these
/// puts every message in the order the tree shows them, so a search for the next unread one
/// is the same scan in a tree as it is in the flat message list.
/// </summary>
/// <remarks>Collapsed groups are included — <see cref="UnreadNavigator"/> is what decides
/// where to land, and the caller expands whatever group it lands in.</remarks>
public sealed record GroupedMessageRef<TGroup>(TGroup Group, int MessageIndex, MailMessageSummary Message);

/// <summary>
/// Finds the nearest unread message above or below a starting position. Pure list logic, kept
/// out of the window so it can be tested without standing up a TreeView (issue #617).
/// </summary>
public static class UnreadNavigator
{
    /// <summary>
    /// Index of the nearest item satisfying <paramref name="isUnread"/> strictly past
    /// <paramref name="fromIndex"/> — below it when <paramref name="forward"/>, above it
    /// otherwise — or -1 when there is none. The search never wraps: reaching the end is
    /// the answer "there are no more", not "start again from the other end".
    /// </summary>
    /// <param name="fromIndex">The position to search out from. It is excluded from the
    /// search, so -1 searches the whole list forwards and <c>items.Count</c> searches the
    /// whole list backwards.</param>
    public static int FindNextUnread<T>(IReadOnlyList<T> items, Func<T, bool> isUnread, int fromIndex, bool forward)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isUnread);

        if (forward)
        {
            for (var i = Math.Max(fromIndex + 1, 0); i < items.Count; i++)
                if (isUnread(items[i])) return i;
        }
        else
        {
            for (var i = Math.Min(fromIndex - 1, items.Count - 1); i >= 0; i--)
                if (isUnread(items[i])) return i;
        }
        return -1;
    }

    /// <summary>The flat-message-list overload of <see cref="FindNextUnread{T}"/>.</summary>
    public static int FindNextUnread(IReadOnlyList<MailMessageSummary> messages, int fromIndex, bool forward) =>
        FindNextUnread(messages, m => m.IsUnread, fromIndex, forward);

    /// <summary>
    /// Every message in <paramref name="groups"/> in the order the tree shows them: each group's
    /// messages in group order, groups in collection order.
    /// </summary>
    public static List<GroupedMessageRef<TGroup>> Flatten<TGroup>(
        IEnumerable<TGroup> groups,
        Func<TGroup, IReadOnlyList<MailMessageSummary>> messagesOf)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(messagesOf);

        var flat = new List<GroupedMessageRef<TGroup>>();
        foreach (var group in groups)
        {
            var messages = messagesOf(group);
            for (var i = 0; i < messages.Count; i++)
                flat.Add(new GroupedMessageRef<TGroup>(group, i, messages[i]));
        }
        return flat;
    }

    /// <summary>Position of <paramref name="message"/> in a flattened view, or -1 when it is not in it.</summary>
    public static int IndexOfMessage<TGroup>(IReadOnlyList<GroupedMessageRef<TGroup>> flat, MailMessageSummary message)
    {
        ArgumentNullException.ThrowIfNull(flat);
        for (var i = 0; i < flat.Count; i++)
            if (ReferenceEquals(flat[i].Message, message)) return i;
        return -1;
    }

    /// <summary>
    /// Position of <paramref name="group"/>'s first message in a flattened view, or -1 when the
    /// group holds no messages or is not in the view.
    /// </summary>
    public static int IndexOfGroupStart<TGroup>(IReadOnlyList<GroupedMessageRef<TGroup>> flat, TGroup group)
    {
        ArgumentNullException.ThrowIfNull(flat);
        for (var i = 0; i < flat.Count; i++)
            if (ReferenceEquals(flat[i].Group, group)) return i;
        return -1;
    }

    /// <summary>
    /// Where a search out from the selected group header starts. A header sits above its own
    /// messages, so moving down considers the group's first message and moving up starts at the
    /// row above the header — matching what Down and Up arrow do from the same place.
    /// </summary>
    public static int SearchOriginForGroupHeader(int groupStartIndex, bool forward) =>
        forward ? groupStartIndex - 1 : groupStartIndex;

    /// <summary>
    /// Where a search starts when nothing is selected: before the first row going down, past the
    /// last row going up, so the whole view is searched either way.
    /// </summary>
    public static int SearchOriginForNoSelection(int itemCount, bool forward) => forward ? -1 : itemCount;
}
