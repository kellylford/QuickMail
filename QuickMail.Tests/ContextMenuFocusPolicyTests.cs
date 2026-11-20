using System.IO;
using System.Text.RegularExpressions;
using QuickMail.Helpers;
using Xunit;

using Decision = QuickMail.Helpers.ContextMenuFocusPolicy.Decision;

namespace QuickMail.Tests;

/// <summary>
/// Issue #672: Shift+F10 (and the Applications key) while reading a message moved focus out of the
/// reading pane and back to the message list, with no menu — which reads, to someone using a screen
/// reader, as the message having closed itself.
///
/// <para>
/// The cause is that <c>Keyboard.FocusedElement</c> is null in two unrelated situations: at startup
/// before any pane has been focused, and whenever Win32 focus is inside the WebView2's child HWND.
/// The window-level handlers treated the second as the first and ran a startup focus repair on
/// someone who was simply reading.
/// </para>
///
/// <para>
/// The decision is a pure function so it can be tested for real, the same carve-out
/// <see cref="ContextMenuKeys"/> got for issue #631. These are behavioural tests over every input
/// combination; the source-text guards below cover only the part that genuinely cannot be reached
/// without a live window — that the handlers still consult the policy at all.
/// </para>
/// </summary>
public class ContextMenuFocusPolicyTests
{
    // attachmentFocus, bodyFocus, messageOpen, hasWpfFocus

    /// <summary>The bug: reading the body must never be mistaken for "nothing is focused".</summary>
    [Theory]
    [InlineData(false, true, true, false)]
    // The same answer with a focused element present: whatever WPF thinks it has, the reader is in
    // the body and their position is not ours to move.
    [InlineData(false, true, true, true)]
    public void ReadingTheBody_LeavesFocusAlone(bool att, bool body, bool open, bool wpf)
        => Assert.Equal(Decision.LeaveFocusInMessageBody,
                        ContextMenuFocusPolicy.Decide(att, body, open, wpf));

    /// <summary>
    /// The startup case the repair exists for (issue #148): nothing focused, nothing open.
    /// Deleting the repair to fix #672 would trade one bug for another.
    /// </summary>
    [Fact]
    public void NothingFocusedAndNothingOpen_RepairsFocus()
        => Assert.Equal(Decision.RepairFocus,
                        ContextMenuFocusPolicy.Decide(false, false, false, false));

    /// <summary>
    /// The staleness class, and the reason <c>isMessageOpen</c> is a parameter rather than a
    /// nicety. The flag is only ever cleared by focus landing somewhere else, so a path that closes
    /// the reading pane without moving focus leaves it set. Acting on it then would suppress the
    /// repair and strand the user with no focus, no menu and no keyboard way out — worse than the
    /// bug being fixed. With no message open the flag must not be believed.
    /// </summary>
    [Fact]
    public void StaleBodyFocus_WithNoMessageOpen_StillRepairsFocus()
        => Assert.Equal(Decision.RepairFocus,
                        ContextMenuFocusPolicy.Decide(false, true, false, false));

    /// <summary>
    /// Issue #255: with the folder tree (or any real panel) focused, that panel's own menu must
    /// open. Redirecting here once replaced the folder menu with the message menu.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, true)]   // a panel focused, no message open
    [InlineData(false, false, true, true)]    // a panel focused while a message is open in the pane
    [InlineData(false, true, false, true)]    // stale flag, but a real element holds focus
    public void APanelHoldingFocus_IsLeftAlone(bool att, bool body, bool open, bool wpf)
        => Assert.Equal(Decision.LeaveAlone, ContextMenuFocusPolicy.Decide(att, body, open, wpf));

    /// <summary>
    /// Issue #631/#148: the attachment list is answered first and opens its own menu. It wins over
    /// every other input — the combinations cannot co-occur in practice, but the ordering must not
    /// depend on that.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void TheAttachmentList_WinsOverEverything(bool att, bool body, bool open, bool wpf)
        => Assert.Equal(Decision.LeaveAlone, ContextMenuFocusPolicy.Decide(att, body, open, wpf));

    /// <summary>
    /// A message open in the reading pane but nothing focused — reachable at launch when the last
    /// session left a message open. Still the startup case, so the repair must run. Without this,
    /// widening the LeaveAlone condition to include an open message passes every other test here
    /// while re-breaking issue #148.
    /// </summary>
    [Fact]
    public void AMessageOpenButNothingFocused_StillRepairsFocus()
        => Assert.Equal(Decision.RepairFocus,
                        ContextMenuFocusPolicy.Decide(false, false, true, false));

    /// <summary>
    /// Exhaustive: all 16 inputs are decided, and only the intended two produce
    /// <see cref="Decision.LeaveFocusInMessageBody"/>. Guards against a future condition that
    /// widens the suppression — the failure mode here is silence, which nobody reports.
    /// </summary>
    [Fact]
    public void OnlyReadingWithAMessageOpen_EverSuppresses()
    {
        for (var i = 0; i < 16; i++)
        {
            bool att = (i & 1) != 0, body = (i & 2) != 0, open = (i & 4) != 0, wpf = (i & 8) != 0;
            var expected = !att && body && open;
            Assert.Equal(expected,
                ContextMenuFocusPolicy.Decide(att, body, open, wpf)
                    == Decision.LeaveFocusInMessageBody);
        }
    }

    // ── The window wiring, which no unit test can reach ──────────────────────────────────────────

    /// <summary>
    /// Both gesture paths must consult the policy. Shift+F10 arrives twice over — once as a key in
    /// <c>PreviewKeyDown</c>, and again as WM_CONTEXTMENU in the window hook — and the Applications
    /// key arrives ONLY as WM_CONTEXTMENU, on key up (see <see cref="ContextMenuKeys"/>). Wiring
    /// one and not the other leaves the unwired path free to move focus, which is how the second
    /// site was missed the first time.
    /// </summary>
    [Theory]
    [InlineData("if (key == Key.F10 && modifiers == ModifierKeys.Shift)")]
    [InlineData("if (msg == WM_CONTEXTMENU)")]
    public void BothGesturePaths_ConsultThePolicy(string blockStart)
    {
        var source = MainWindowSource();
        var start  = source.IndexOf(blockStart, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"the block starting \"{blockStart}\" is gone");

        // Bounded to this block: unbounded, the search finds the OTHER site and passes on its
        // wiring instead of this one's.
        var end = source.IndexOf("\n        }", start, System.StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of \"{blockStart}\"");

        var block = source[start..end];

        // The argument LIST, in order, with whitespace collapsed so the assertion survives
        // reformatting but not reordering. Asserting the identifiers separately does not pin
        // position: swapping the first two arguments leaves every one of them present, and makes
        // reading the body decide LeaveAlone — issues #148 and #672 restored together. Inverting
        // the focus test hands a focused folder tree in as "nothing focused", so the repair yanks
        // focus to the message list: issue #255, also with every identifier still present.
        var normalised = Regex.Replace(block, @"\s+", " ");
        Assert.Contains(
            "ContextMenuFocusPolicy.Decide( ReadingPaneAttachmentList.IsKeyboardFocusWithin, "
            + "_messageBodyHasFocus, _vm.IsMessageOpen, focused != null)",
            normalised, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag is tracked, not derived — deriving it from <c>Keyboard.FocusedElement</c> at call
    /// time is the bug itself. <c>handledEventsToo</c> carries the clearing half: focus landing on a
    /// control that marks the event handled must still reset the flag, and it is exactly the edit
    /// that leaves every other assertion here green while restoring #672 in full.
    /// </summary>
    [Fact]
    public void MessageBodyFocus_IsTrackedWithHandledEventsToo()
    {
        var source = MainWindowSource();

        Assert.Contains("private bool _messageBodyHasFocus;", source, System.StringComparison.Ordinal);
        Assert.Contains("UIElement.GotKeyboardFocusEvent", source, System.StringComparison.Ordinal);
        Assert.Contains("_messageBodyHasFocus = ReferenceEquals(e.NewFocus, MessageBody)",
                        source, System.StringComparison.Ordinal);
        // Scoped to this AddHandler: searching the whole file passes the moment any other
        // handler registers with handledEventsToo, which is the failure mode two assertions in
        // this file already had to be tightened out of.
        var reg = source[source.IndexOf("AddHandler(UIElement.GotKeyboardFocusEvent",
                                        System.StringComparison.Ordinal)..];
        reg = reg[..reg.IndexOf(";", System.StringComparison.Ordinal)];
        Assert.Contains("handledEventsToo: true", reg, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The hook swallows only the keyboard-raised message. It sits on the top-level HWND, so a
    /// right-click anywhere in the window arrives there too, and <c>lParam</c> is what tells them
    /// apart (-1 for the keyboard, screen coordinates for the mouse).
    /// </summary>
    [Fact]
    public void TheHook_SwallowsOnlyTheKeyboardGesture()
    {
        var source = MainWindowSource();
        var start  = source.IndexOf("if (msg == WM_CONTEXTMENU)", System.StringComparison.Ordinal);
        Assert.True(start >= 0);

        var block = source[start..source.IndexOf("\n        }", start, System.StringComparison.Ordinal)];
        Assert.Contains("(long)lParam == -1", block, System.StringComparison.Ordinal);

        // The suppression itself must be gated on it. Asserting only that the expression exists
        // somewhere in the block passes while the swallow ignores it — which is exactly what a
        // mutation run caught this assertion doing.
        Assert.Contains("keyboardRaised && decision == ContextMenuFocusPolicy.Decision.LeaveFocusInMessageBody",
                        block, System.StringComparison.Ordinal);

        // Scoped to the suppression branch itself. The #148 recovery below also sets handled,
        // so an unscoped assertion matches that one instead and passes while this branch hands
        // the gesture straight back to DefWindowProc — the Win32 system menu, issue #148 again.
        // A mutation run caught the unscoped version doing exactly that.
        var swallow = block[block.IndexOf(
            "if (keyboardRaised && decision == ContextMenuFocusPolicy.Decision.LeaveFocusInMessageBody)",
            System.StringComparison.Ordinal)..];
        swallow = swallow[..swallow.IndexOf("\n            }", System.StringComparison.Ordinal)];

        Assert.Contains("handled = true;",      swallow, System.StringComparison.Ordinal);
        Assert.Contains("return IntPtr.Zero;",  swallow, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #673: F6 out of the message body cycled from the wrong pane, and focus was not
    /// restored to the reading pane after a view-mode change, because both sites asked
    /// <c>MessageBody.IsKeyboardFocusWithin</c> — false in exactly the state they were asking
    /// about, since Win32 focus is inside the WebView2's child HWND and WPF has no focused element.
    ///
    /// Both now go through one predicate. They asked the same wrong question in two places and were
    /// fixed in neither for months; a shared predicate is what stops them drifting apart again.
    /// </summary>
    [Fact]
    public void TheReadingPaneIsRecognisedAsFocused_AtEverySiteThatAsks()
    {
        var source = MainWindowSource();

        Assert.Contains("private bool IsMessageBodyFocused =>", source, System.StringComparison.Ordinal);
        Assert.Contains("_messageBodyHasFocus && _vm.IsMessageOpen", source,
                        System.StringComparison.Ordinal);

        // Neither site may go back to asking the property on its own.
        var pane = source[source.IndexOf("private int GetFocusedPaneIndex", System.StringComparison.Ordinal)..];
        pane = pane[..pane.IndexOf("\n    }", System.StringComparison.Ordinal)];
        Assert.Contains("IsMessageBodyFocused", pane, System.StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBody.IsKeyboardFocusWithin", pane, System.StringComparison.Ordinal);

        var restore = source[source.IndexOf("ShouldRestoreMessagePanelFocusAfterViewModeChange() =>",
                                            System.StringComparison.Ordinal)..];
        restore = restore[..restore.IndexOf(");", System.StringComparison.Ordinal)];
        Assert.Contains("IsMessageBodyFocused", restore, System.StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBody.IsKeyboardFocusWithin", restore, System.StringComparison.Ordinal);
    }

    private static string MainWindowSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs"));

    private static string RepoRoot()
    {
        // AppContext.BaseDirectory rather than the current directory: runner-independent.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
