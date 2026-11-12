using System.Windows.Input;

namespace QuickMail.Helpers;

/// <summary>Which end of a list a key press found the selection already at (issue #923).</summary>
public enum ListEdge
{
    /// <summary>The key moves the selection, or isn't a move at all.</summary>
    None,

    /// <summary>Up or Home with the first item already selected.</summary>
    Top,

    /// <summary>Down or End with the last item already selected.</summary>
    Bottom,
}

/// <summary>
/// Decides whether a key press in the message list ran into an end of it (issue #923). At the top,
/// Up and Home leave the selection where it is, and so does Down or End at the bottom; nothing
/// changes, so the platform reports nothing and the person can't tell they are at the end. This is
/// what tells the window to cue it: a sound, or a "Top of list" / "Bottom of list" hint.
/// <para>
/// Pure apart from one remembered flag, so it can be tested without a window. The window passes the
/// position of the focused row among the rows a person can arrow through (for a tree, the visible
/// rows in order: each group, then its messages when it is expanded).
/// </para>
/// <para>
/// Every separate press at the edge cues again. A key held down cues once, on the first press that
/// meets the edge, and not on each auto-repeat after it, which would be a stream of sounds or
/// announcements over the screen reader's speech.
/// </para>
/// </summary>
public sealed class ListEdgeCue
{
    private bool _cuedThisHold;

    /// <summary>The edge a key press met, ignoring key repeat: Top, Bottom or None.</summary>
    /// <param name="key">The key pressed.</param>
    /// <param name="modifiers">The modifiers held. Only a plain key counts: Shift extends a selection,
    /// and Ctrl moves focus without selecting, so neither is a move of the selection.</param>
    /// <param name="index">Where the focused row is among the rows, or -1 for none.</param>
    /// <param name="count">How many rows there are.</param>
    public static ListEdge At(Key key, ModifierKeys modifiers, int index, int count)
    {
        if (modifiers != ModifierKeys.None || count <= 0 || index < 0 || index >= count) return ListEdge.None;
        return key switch
        {
            Key.Up or Key.Home when index == 0 => ListEdge.Top,
            Key.Down or Key.End when index == count - 1 => ListEdge.Bottom,
            _ => ListEdge.None,
        };
    }

    /// <summary>
    /// The edge to cue for this key press, or None. Call it for every key press the list sees, so a
    /// press that moves (or any other key) ends a hold.
    /// </summary>
    /// <param name="isRepeat">The press is the keyboard's auto-repeat of a key held down.</param>
    public ListEdge Next(Key key, ModifierKeys modifiers, int index, int count, bool isRepeat)
    {
        var edge = At(key, modifiers, index, count);
        if (edge == ListEdge.None)
        {
            _cuedThisHold = false;
            return ListEdge.None;
        }
        if (isRepeat && _cuedThisHold) return ListEdge.None;
        _cuedThisHold = true;
        return edge;
    }
}
