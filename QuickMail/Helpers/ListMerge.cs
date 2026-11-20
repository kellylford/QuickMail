using System;
using System.Collections.Generic;

namespace QuickMail.Helpers;

/// <summary>
/// Edits a live list toward a new list of the same rows, so that rows staying in the list keep their
/// containers. Replacing an <c>ItemsSource</c>, or raising <c>Reset</c> on it, destroys every row's
/// container including the one holding keyboard focus; WPF then puts focus on the list and picks a
/// row by itself, which is how new mail moved focus to a different message.
/// Rows are compared by reference, so the caller has to have reused the instances it wants kept.
/// </summary>
public static class ListMerge
{
    /// <summary>
    /// True when <paramref name="list"/> can become <paramref name="target"/> by at most
    /// <paramref name="maxEdits"/> inserts and removals. False when the rows that stay would have to
    /// change order, which inserts and removals cannot do.
    /// </summary>
    public static bool CanApplyInPlace<T>(IReadOnlyList<T> list, IReadOnlyList<T> target, int maxEdits)
        where T : class
    {
        var inTarget = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        var inList = new HashSet<T>(list, ReferenceEqualityComparer.Instance);

        var edits = 0;
        foreach (var row in list) if (!inTarget.Contains(row)) edits++;
        foreach (var row in target) if (!inList.Contains(row)) edits++;
        if (edits > maxEdits) return false;

        var next = 0;
        foreach (var row in list)
        {
            if (!inTarget.Contains(row)) continue;
            while (next < target.Count && !inList.Contains(target[next])) next++;
            if (next >= target.Count || !ReferenceEquals(target[next], row)) return false;
            next++;
        }
        return true;
    }

    /// <summary>
    /// Makes <paramref name="list"/> equal to <paramref name="target"/> with removals and inserts only.
    /// Call <see cref="CanApplyInPlace"/> first. Returns whether anything changed.
    /// </summary>
    public static bool ApplyInPlace<T>(IList<T> list, IReadOnlyList<T> target) where T : class
    {
        var inTarget = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        var changed = false;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (inTarget.Contains(list[i])) continue;
            list.RemoveAt(i);
            changed = true;
        }
        for (var i = 0; i < target.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], target[i])) continue;
            list.Insert(i, target[i]);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// True when two grouped views show the same groups, in the same order, holding the same message
    /// instances. A rebuild that comes out this way has nothing to show, and replacing the tree's
    /// items for it would only collapse the user's place into the group row.
    /// </summary>
    public static bool SameGroups<TGroup, TRow>(
        IReadOnlyList<TGroup> current,
        IReadOnlyList<TGroup> rebuilt,
        Func<TGroup, string> key,
        Func<TGroup, IReadOnlyList<TRow>> rows)
        where TRow : class
    {
        // Empty is never "the same": something may be waiting on the tree's items being replaced after
        // a folder change, and with nothing to compare, an unraised change would leave it waiting.
        if (rebuilt.Count == 0 || current.Count != rebuilt.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(key(current[i]), key(rebuilt[i]), StringComparison.OrdinalIgnoreCase)) return false;
            var a = rows(current[i]);
            var b = rows(rebuilt[i]);
            if (a.Count != b.Count) return false;
            for (var j = 0; j < a.Count; j++)
                if (!ReferenceEquals(a[j], b[j])) return false;
        }
        return true;
    }
}
