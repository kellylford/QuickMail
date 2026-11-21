using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>What happened when <see cref="ListMove.Apply{T}"/> was asked to move an item.</summary>
public enum ListMoveOutcome { Moved, AlreadyAtStart, AlreadyAtEnd, NothingToReorder }

/// <summary>
/// The result of a move: the outcome, and for <see cref="ListMoveOutcome.Moved"/> the visible item
/// it now sits next to and on which side, which is what the sentence the user hears names.
/// </summary>
public readonly record struct ListMoveResult<T>(ListMoveOutcome Outcome, T? Neighbour = default, bool Above = false);

/// <summary>
/// Moves one item of a list among the items the user can see (#1058), leaving the hidden ones where
/// they are: the folder tree's top level keeps a place for a section that is not showing (Favorites
/// with no favorites, Views with no saved views), and Favorites keeps a place for a favorite whose
/// folder has not loaded. Up and Down pass the visible neighbour; Start and End go to the very
/// beginning or end of the whole list.
/// </summary>
public static class ListMove
{
    public static ListMoveResult<T> Apply<T>(List<T> all, Func<T, bool> isVisible, int index, TreeMove move)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (index < 0 || index >= all.Count) throw new ArgumentOutOfRangeException(nameof(index));

        var visible = Enumerable.Range(0, all.Count).Where(i => i == index || isVisible(all[i])).ToList();
        if (visible.Count < 2) return new(ListMoveOutcome.NothingToReorder);
        var pos = visible.IndexOf(index);
        var item = all[index];

        switch (move)
        {
            case TreeMove.Up:
            case TreeMove.ToStart:
            {
                if (pos == 0) return new(ListMoveOutcome.AlreadyAtStart);
                // The neighbour is before the item, so removing the item leaves its index alone.
                var at = move == TreeMove.Up ? visible[pos - 1] : visible[0];
                var neighbour = all[at];
                all.RemoveAt(index);
                all.Insert(move == TreeMove.Up ? at : 0, item);
                return new(ListMoveOutcome.Moved, neighbour, Above: true);
            }
            default:
            {
                if (pos == visible.Count - 1) return new(ListMoveOutcome.AlreadyAtEnd);
                // The neighbour is after the item, so removing the item shifts it back by one, and
                // "just after it" is then its old index.
                var at = move == TreeMove.Down ? visible[pos + 1] : visible[^1];
                var neighbour = all[at];
                all.RemoveAt(index);
                all.Insert(move == TreeMove.Down ? at : all.Count, item);
                return new(ListMoveOutcome.Moved, neighbour, Above: false);
            }
        }
    }
}
