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
    /// The folder a click in the folder tree activates, or null when the click was not on a folder.
    /// Account header nodes and synthetic path segments carry no folder — the same nodes Enter skips.
    /// </summary>
    public static MailFolderModel? FolderFromClick(object? originalSource) =>
        ItemFromClick<FolderTreeNode>(originalSource)?.Folder is { IsHeader: false } folder
            ? folder
            : null;

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
