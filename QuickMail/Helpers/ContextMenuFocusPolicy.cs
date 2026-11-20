namespace QuickMail.Helpers;

/// <summary>
/// Decides what the window-level context-menu gesture handlers should do about focus (issue #672).
///
/// <para>
/// <c>Keyboard.FocusedElement</c> being null means two unrelated things, and telling them apart is
/// the whole problem. It is null at startup, before any pane has been focused — the case
/// <see cref="Decision.RepairFocus"/> exists for (issue #148). It is <em>also</em> null whenever
/// Win32 focus sits inside the reading pane's WebView2, because focus has left the WPF tree
/// entirely for a child HWND. Treating the second as the first ran the startup repair on someone
/// who was simply reading, moving focus to the message list — the reported symptom, which reads as
/// the message having closed itself.
/// </para>
///
/// <para>
/// Carved out as a pure function for the same reason <see cref="ContextMenuKeys"/> was (issue
/// #631): the real path needs a WebView2 holding Win32 focus and a live window message, which no
/// unit test can stand up, while the decision itself is three booleans and is worth pinning
/// exactly. The handlers keep the parts that genuinely need a window — reading the focus state,
/// and acting on the answer.
/// </para>
/// </summary>
public static class ContextMenuFocusPolicy
{
    /// <summary>What a window-level context-menu gesture handler should do.</summary>
    public enum Decision
    {
        /// <summary>
        /// Do nothing and let the gesture take its ordinary course. The attachment list's own
        /// <c>ContextMenu</c> opens this way, and so does every pane that already holds real WPF
        /// focus — routing <c>ContextMenuOpening</c> from the focused element (issue #255: the
        /// folder tree must keep its own menu rather than be handed the message menu).
        /// </summary>
        LeaveAlone,

        /// <summary>
        /// The user is reading the message body. Do not move focus. For the WM_CONTEXTMENU hook
        /// this also means swallowing the message: with no WPF focus, <c>DefWindowProc</c> would
        /// answer with the Win32 system menu (issue #148's symptom).
        /// </summary>
        LeaveFocusInMessageBody,

        /// <summary>
        /// Nothing holds focus and no message is open — genuinely the startup case. Park focus on
        /// the active panel so <c>ContextMenuOpening</c> has an element to route from.
        /// </summary>
        RepairFocus,
    }

    /// <param name="attachmentListHasFocus">
    /// The reading pane's attachment list holds focus. It opens its own menu and needs no help,
    /// so it is answered first — before the message-body test, which cannot be true at the same
    /// time but must not be relied on for that.
    /// </param>
    /// <param name="messageBodyHasFocus">
    /// The reading pane's WebView2 last received keyboard focus. Tracked by the window rather than
    /// derived at call time, because the focus state this describes is exactly the one WPF reports
    /// as "nothing is focused".
    /// </param>
    /// <param name="isMessageOpen">
    /// A message is open in the reading pane. Required alongside
    /// <paramref name="messageBodyHasFocus"/>: the flag is only ever cleared by focus landing
    /// somewhere else, so a path that closes the reading pane without moving focus leaves it
    /// stale-true. Acting on a stale flag would suppress <see cref="Decision.RepairFocus"/> and
    /// strand the user with no focus, no menu and no keyboard way out — worse than the bug this
    /// policy exists to fix. A stale flag cannot outlive the open message, so the pairing removes
    /// the whole class.
    /// </param>
    /// <param name="hasWpfFocus">
    /// WPF reports a focused element (<c>Keyboard.FocusedElement != null</c>).
    /// </param>
    public static Decision Decide(bool attachmentListHasFocus, bool messageBodyHasFocus,
                                  bool isMessageOpen, bool hasWpfFocus)
    {
        if (attachmentListHasFocus)               return Decision.LeaveAlone;
        if (messageBodyHasFocus && isMessageOpen) return Decision.LeaveFocusInMessageBody;
        return hasWpfFocus ? Decision.LeaveAlone : Decision.RepairFocus;
    }
}
