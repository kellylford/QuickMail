namespace QuickMail.Helpers;

/// <summary>
/// Decides whether a keyboard navigation to a folder should put focus in the message panel once it
/// has finished — Enter in the folder tree, Go to Folder, Go to Startup Folder.
///
/// <para>
/// The navigation is awaited to the end, and for All Mail and the other combined folders the end is
/// every account's server check. Focus has normally gone into the list long before that: the
/// window's Messages handler moves it there as soon as the cached list is shown. Refocusing
/// unconditionally at the end pulled back a user who had moved on in the meantime — to another
/// pane, into a message, or back to the folder tree.
/// </para>
///
/// <para>
/// "Moved" has to mean any focus change during the navigation, not a difference between where
/// focus started and where it ended. Going back to the folder tree lands on the open folder's item,
/// which is the very element Enter was pressed on, so a before/after comparison reads that as
/// focus never having moved.
/// </para>
///
/// <para>
/// A pure function for the same reason as <see cref="ContextMenuFocusPolicy"/>: the real path needs
/// a live window and a slow server, and the decision itself is worth pinning exactly.
/// </para>
/// </summary>
public static class NavigationFocusPolicy
{
    /// <param name="navigated">False when the command went nowhere (nothing to select yet).</param>
    /// <param name="windowActive">The main window is still the active one.</param>
    /// <param name="focusChangedDuring">Keyboard focus changed at any point while it ran.</param>
    /// <param name="focusInMessageArea">Focus is now in the message list or a group tree.</param>
    public static bool ShouldFocusMessagePanel(
        bool navigated, bool windowActive, bool focusChangedDuring, bool focusInMessageArea) =>
        navigated && windowActive && (!focusChangedDuring || focusInMessageArea);
}
