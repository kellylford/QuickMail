using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Resolves which row a mouse click landed on, for the lists and trees that activate on a click
/// as well as on Enter.
///
/// <para>Why this exists: QuickMail is keyboard-first, and every list activates on Enter through a
/// handler that reads <c>SelectedItem</c>. The mouse cannot borrow that path. A left-click moves the
/// selection and nothing more — the folder tree deliberately has no <c>SelectedItemChanged</c>
/// handler (arrowing through folders must not fetch every folder it passes over), so clicking a
/// folder highlighted the row and left the message list showing the folder the user came from, and
/// the next F6 into the tree snapped the highlight back to the folder that was really open. Clicking
/// a folder was, in effect, a no-op.</para>
///
/// <para>Reading <c>SelectedItem</c> from the mouse handler instead would work for a plain click and
/// be wrong everywhere else: the expander chevron and the flag hit area do not move the selection,
/// and neither does empty space below the last row, so a click there would activate whatever was
/// selected beforehand — the flat message list did exactly that, re-opening the selected message
/// when the user clicked below the list. The row is therefore resolved from the element that was
/// actually clicked.</para>
/// </summary>
internal static class MouseActivation
{
    /// <summary>
    /// The data item of the row containing <paramref name="originalSource"/> (a mouse event's
    /// <see cref="RoutedEventArgs.OriginalSource"/>), or null when the click landed on row chrome
    /// that owns its own action, on empty space below the rows, or on a row whose item is not a
    /// <typeparamref name="TItem"/> (a group header in one of the grouped trees, say).
    /// </summary>
    public static TItem? ItemFromClick<TItem>(object? originalSource) where TItem : class
    {
        var node = originalSource as DependencyObject;
        while (node != null)
        {
            switch (node)
            {
                // Chrome that acts on its own: the expander chevron, the tab strip's close button,
                // a scroll bar or its thumb. A click there is not an activation of the row.
                case ButtonBase or ScrollBar or Thumb:
                    return null;

                // The row. ListViewItem derives from ListBoxItem, so both arrive here.
                case TreeViewItem or ListBoxItem:
                    return (node as FrameworkElement)?.DataContext as TItem;

                // Reached the list itself without crossing a row: empty space below the items.
                case ItemsControl:
                    return null;
            }

            node = ParentOf(node);
        }

        return null;
    }

    /// <summary>
    /// True when the modifiers make a click a selection gesture rather than an activation. The
    /// message list is Extended-selection, so Ctrl+click and Shift+click build a multi-message
    /// selection — every message added to one used to be opened as well, which in Window mode meant
    /// a window per message on the way to a multi-message delete.
    /// </summary>
    public static bool ExtendsSelection(ModifierKeys modifiers) =>
        (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

    /// <summary>
    /// The row item a click landed on, whatever its type. What a press and its release are compared
    /// on, so an activation can require both halves of the click to land on the same row.
    /// </summary>
    public static object? RowFromClick(object? originalSource) => ItemFromClick<object>(originalSource);

    /// <summary>
    /// The folder this row activates, or null when the row is not one: account header nodes and
    /// synthetic path segments carry no folder, and a folder marked as a header is one
    /// SelectFolderAsync returns straight back out of — the same rows Enter skips.
    /// </summary>
    public static MailFolderModel? ActivatableFolder(object? row) =>
        row is FolderTreeNode { Folder: { IsHeader: false } folder } ? folder : null;

    /// <summary>
    /// The message this row opens: its own message, or the one message of a single-message group.
    /// A group row is not inert — Enter on a one-message conversation opens that message rather than
    /// expanding a branch with nothing in it, and in Conversations view most rows are that shape, so
    /// a click that only understood message rows left most of the view doing nothing.
    /// </summary>
    public static MailMessageSummary? ActivatableMessage(object? row) => row switch
    {
        MailMessageSummary message                 => message,
        ConversationGroup { Messages.Count: 1 } cg => cg.Messages[0],
        SenderGroup       { Messages.Count: 1 } sg => sg.Messages[0],
        _                                          => null,
    };

    // The visual tree is the one that matters — a click's OriginalSource is the rendered element —
    // but it is not continuous: a Run inside a TextBlock is a content element with no visual parent,
    // and VisualTreeHelper.GetParent throws on anything that is not a Visual. Step over those
    // through the logical/host link so a click on message subject text still finds its row.
    private static DependencyObject? ParentOf(DependencyObject node) => node switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement fce                     => fce.Parent ?? fce.TemplatedParent,
        _                                               => null,
    };
}

/// <summary>
/// Pairs a press with its release for one list, so activating a row takes both halves of a click
/// landing on it.
///
/// <para>An unpaired button-up handler has three faults, all of which the first pass of this change
/// shipped. A double-click delivers two button-ups, so every handler ran twice: two standalone
/// windows for one message in Window mode, and on the account list a second connect that cancelled
/// the first and left "Connection cancelled." on the status line of an account that had just
/// connected. A press on one row released over another activated the row under the release while
/// the list highlighted the row under the press — the same "selection says one thing, list shows
/// another" split this whole change exists to remove. And a drag across the message list to select
/// several messages ended by activating the last row, which collapsed the multi-selection back to
/// one: Delete then deleted one message where the user had selected five.</para>
/// </summary>
internal sealed class RowClickTracker
{
    private object? _pressed;

    /// <summary>
    /// Records the row a press landed on. The second and later clicks of a multi-click record
    /// nothing, which is what holds a double-click to a single activation — ClickCount is dependable
    /// on the press, where WPF's own TreeViewItem reads it to toggle expansion.
    /// </summary>
    public void Press(object? row, int clickCount) => _pressed = clickCount == 1 ? row : null;

    /// <summary>
    /// The row this release activates: the pressed row, and only when the release landed on it too.
    /// Clears the press either way, so one press can never activate twice.
    /// </summary>
    public object? Release(object? row)
    {
        var pressed = _pressed;
        _pressed = null;
        return pressed is not null && ReferenceEquals(pressed, row) ? pressed : null;
    }
}
