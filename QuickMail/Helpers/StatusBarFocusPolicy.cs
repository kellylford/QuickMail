namespace QuickMail.Helpers;

/// <summary>
/// Whether focus has to be moved off a status bar region that just hid itself (review of #806).
///
/// <para>
/// The sender-address region disappears when its update ends, quite possibly while someone is sitting on it
/// reading the count. Left alone, focus stays on an element nobody can see or reach, until the next key press
/// finds its way out. So the window moves focus to a neighbouring region. It must do that only for someone who
/// was on the region: anyone anywhere else in the window has to be left exactly where they are.
/// </para>
///
/// <para>
/// "On the region" covers two things. Keyboard focus is the ordinary case. Logical focus is the case where
/// QuickMail is not the active window, so no element has keyboard focus at all, but the window remembers the
/// region as the place to return to. Coming back would then land on the hidden region. A pure function, like
/// <see cref="ContextMenuFocusPolicy"/>, because the real path needs a shown, active window that unit tests
/// cannot stand up.
/// </para>
/// </summary>
public static class StatusBarFocusPolicy
{
    /// <param name="nowVisible">Whether the region is visible after the change.</param>
    /// <param name="hasKeyboardFocus">Whether the region holds keyboard focus. WPF moves keyboard focus off a
    /// hidden element only after its visibility change has been reported, so this still says where it was.</param>
    /// <param name="hasLogicalFocus">Whether the region is the window's remembered focus, which is what counts
    /// while another application is in front.</param>
    public static bool ShouldMoveFocusOffHiddenRegion(bool nowVisible, bool hasKeyboardFocus, bool hasLogicalFocus)
        => !nowVisible && (hasKeyboardFocus || hasLogicalFocus);
}
