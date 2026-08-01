using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Each field row must BE a check box, and must be ONLY a check box.
///
/// <para>A field's whole purpose in this window is on/off, so it gets the real control and the
/// platform reports its role, its checked state, and every change to that state. The first attempt
/// wrapped each check box in a ListBoxItem, which carries a name of its own — so every row was
/// named twice ("Date, Date check box, unchecked"). These tests read the live automation tree
/// rather than the template, because the template looked identical in all three versions.</para>
/// </summary>
[Collection("WpfTests")]
public class RowFieldsWindowCheckBoxTests
{
    private static (RowFieldsWindow window, RowFieldsViewModel vm) MakeWindow()
    {
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());
        var window = new RowFieldsWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            // Off-screen: these tests move real keyboard focus, and a window appearing over the
            // desktop is exactly what perturbs the other focus-dependent suites (#380).
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        return (window, vm);
    }

    /// <summary>
    /// Closes the window AND drops keyboard focus. Without the second half, focus stays pointed at
    /// a check box in a window that no longer exists, and the next focus-dependent test in the
    /// WpfTests collection (RadioGroupNavigationTests) fails — it passed alone and failed in the
    /// full run until this was added.
    /// </summary>
    private static void CloseAndReleaseFocus(Window window)
    {
        window.Close();
        Keyboard.ClearFocus();
        DrainDispatcher();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new System.Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static FieldCheckList FieldList(RowFieldsWindow w)
    {
        var list = w.FindName("FieldList") as FieldCheckList;
        Assert.NotNull(list);
        return list!;
    }

    private static TextBox PreviewBox(RowFieldsWindow w)
    {
        var box = w.FindName("PreviewBox") as TextBox;
        Assert.NotNull(box);
        return box!;
    }

    private static CheckBox[] RowCheckBoxes(FieldCheckList list) =>
        Enumerable.Range(0, list.Items.Count)
            .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i))
            .OfType<DependencyObject>()
            .Select(FieldCheckList.FindCheckBox)
            .OfType<CheckBox>()
            .ToArray();

    /// <summary>
    /// THE regression this file exists for: a row must contribute exactly one element to the
    /// control view, so its name is spoken once. If a wrapper container reappears between the list
    /// and the check boxes, these children stop being check-box peers and this fails.
    /// </summary>
    [StaFact]
    public void EachRowIsExactlyOneNamedElement_NotAContainerPlusACheckBox()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var peer = UIElementAutomationPeer.CreatePeerForElement(FieldList(window));
            Assert.NotNull(peer);
            var children = peer!.GetChildren();

            Assert.Equal(vm.Fields.Count, children.Count);
            Assert.All(children, c =>
                Assert.Equal(AutomationControlType.CheckBox, c.GetAutomationControlType()));

            // …and each is named once, with the field's display text.
            Assert.Equal(
                vm.Fields.Select(f => f.DisplayName).ToArray(),
                children.Select(c => c.GetName()).ToArray());

            // Below each row there is only the check box's own content text — the structure every
            // WPF ContentControl with string content has. What must never come back is an extra
            // element ABOVE the check box, which is what the assertions on `children` above pin:
            // the list's direct children are the check boxes themselves.
            foreach (var c in children)
            {
                var descendants = c.GetChildren() ?? [];
                Assert.All(descendants, d =>
                    Assert.Equal(AutomationControlType.Text, d.GetAutomationControlType()));
            }
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void EveryFieldRowIsARealFocusableCheckBox()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var boxes = RowCheckBoxes(FieldList(window));
            Assert.Equal(vm.Fields.Count, boxes.Length);
            Assert.All(boxes, b => Assert.True(b.Focusable, "the row's check box must be the arrow stop"));
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void RowPeer_ReportsCheckBoxRoleAndToggleState()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var flag = RowCheckBoxes(FieldList(window)).First();
            var peer = UIElementAutomationPeer.CreatePeerForElement(flag);

            Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());
            Assert.Equal("Flag", peer.GetName());

            // Toggle state is the platform's to report — which is what makes a custom
            // "checked"/"not checked" announcement unnecessary, and therefore wrong.
            var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
            Assert.Equal(ToggleState.On, toggle.ToggleState);

            toggle.Toggle();
            DrainDispatcher();
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void FocusingARowSelectsIt_SoMoveUpAndDownActOnIt()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            RowCheckBoxes(FieldList(window))[3].Focus();
            DrainDispatcher();

            // Selection follows keyboard focus, because the move commands act on the selection.
            Assert.Same(vm.Fields[3], vm.SelectedField);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    // ── The list container is not a tab stop of its own (#464) ────────────────
    //
    // Tab used to stop on the list container before reaching a row. TabNavigation="Once" already
    // collapsed the rows to a single stop, so the container was the whole of the extra stop — and
    // a container is not something the user can act on. These walk focus with MoveFocus, the same
    // traversal Tab performs, so they are deterministic and not gated behind
    // QUICKMAIL_RUN_INPUT_TESTS.

    /// <summary>
    /// Tab stops from the row-type combo all the way round, plus every element the list was ASKED
    /// to give focus to. The second half matters: the list redirects focus off itself onto a row,
    /// so by the time <c>Keyboard.FocusedElement</c> is sampled a container stop has already been
    /// papered over. Watching PreviewGotKeyboardFocus catches the attempt itself.
    /// </summary>
    private static (List<IInputElement> Stops, List<object> ListFocusAttempts) WalkTabOrder(
        FieldCheckList list, ComboBox start)
    {
        var attempts = new List<object>();
        void OnPreview(object s, KeyboardFocusChangedEventArgs e)
        {
            if (ReferenceEquals(e.NewFocus, list)) attempts.Add(e.NewFocus);
        }
        // handledEventsToo, and not `+= OnPreview`: whether anything upstream marks this event
        // handled is not this test's business, and a plain instance handler silently stops running
        // if something does — reporting "nothing tried to focus the container" precisely when
        // something did. That is not hypothetical; an earlier version of the redirect handled the
        // preview event, and with `+=` this test passed with the fault reintroduced.
        list.AddHandler(UIElement.PreviewGotKeyboardFocusEvent,
                        new KeyboardFocusChangedEventHandler(OnPreview), handledEventsToo: true);

        start.Focus();
        DrainDispatcher();

        var stops = new List<IInputElement>();
        var wrapped = false;
        try
        {
            // Generous bound: the window has well under 40 focusable elements even if every check
            // box were (wrongly) its own stop, so this reaches the wrap in either state.
            for (int i = 0; i < 40; i++)
            {
                if (Keyboard.FocusedElement is not FrameworkElement current) break;
                if (stops.Count > 0 && ReferenceEquals(current, start)) { wrapped = true; break; }
                stops.Add(current);
                current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                DrainDispatcher();
            }
        }
        finally
        {
            list.RemoveHandler(UIElement.PreviewGotKeyboardFocusEvent,
                               new KeyboardFocusChangedEventHandler(OnPreview));
        }

        // Without this, a walk that stalls on one element surfaces as a baffling count assertion
        // somewhere below rather than as "focus stopped moving".
        Assert.True(wrapped, $"tab order never returned to the start; got: {Describe(stops)}");
        return (stops, attempts);
    }

    private static string Describe(IEnumerable<IInputElement> stops) =>
        string.Join(" → ", stops.Select(s => s switch
        {
            ContentControl { Content: string text } => $"{s.GetType().Name}({text})",
            FrameworkElement { Name.Length: > 0 } fe => $"{s.GetType().Name}({fe.Name})",
            _ => s.GetType().Name,
        }));

    [StaFact]
    public void TabbingThroughTheWindow_NeverStopsOnTheListItself_OnlyOnOneRow()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = FieldList(window);
            var combo = window.FindName("RowTypeList") as ComboBox;
            Assert.NotNull(combo);

            var (stops, listFocusAttempts) = WalkTabOrder(list, combo!);

            // The phantom stop, caught at the point WPF tries to make it — before the redirect
            // hides it. This is what fails if IsTabStop comes back.
            Assert.Empty(listFocusAttempts);
            Assert.DoesNotContain(list, stops);

            // …and the rows contribute exactly one stop between them, on a check box.
            var inList = stops.OfType<DependencyObject>().Where(list.IsAncestorOf).ToArray();
            Assert.Single(inList);
            Assert.IsType<CheckBox>(inList[0]);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    /// <summary>
    /// The container being a tab stop is the cause, so pin that too and not only the behaviour it
    /// produced — a future edit that re-enables it fails here, whatever else changes.
    /// </summary>
    [StaFact]
    public void TheListContainerIsNotATabStop_OnlyItsRowsAre()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            Assert.False(FieldList(window).IsTabStop, "the rows are the tab stop, not the list");
        }
        finally { CloseAndReleaseFocus(window); }
    }

    /// <summary>
    /// Out of the tab order must not mean unreachable. The "_Fields, in spoken order" label
    /// implements its access key as Target.Focus(), so Alt+F hands focus to the list itself — and
    /// the list passes it to a row, because a container the user cannot act on is not what "focus
    /// the field list" means (#464).
    /// </summary>
    [StaFact]
    public void FocusHandedToTheList_LandsOnARow_SoTheLabelsAccessKeyStillWorks()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = FieldList(window);
            var label = window.FindName("FieldsLabel") as Label;
            Assert.NotNull(label);
            Assert.Same(list, label!.Target);      // …which is where Alt+F sends focus.

            list.FocusRow(3);
            DrainDispatcher();
            // Focus goes elsewhere in the window, not nowhere: Alt+F is pressed from another
            // control, and a cleared focus is a state the window is never actually in.
            PreviewBox(window).Focus();
            DrainDispatcher();

            list.Focus();
            DrainDispatcher();

            // What is asserted is where focus ENDS UP. The list does briefly hold focus on the way
            // — see the remarks on OnGotKeyboardFocus for why cancelling the focus change instead
            // was measured to be unreliable — so asserting the absence of that transient would be
            // asserting something the implementation deliberately does not promise.
            Assert.IsType<CheckBox>(Keyboard.FocusedElement);
            // Back to the row the user was last on, the way returning to a list should behave.
            Assert.Equal(3, list.FocusedIndex());
        }
        finally { CloseAndReleaseFocus(window); }
    }

    /// <summary>
    /// The remembered row belongs to the layout that was showing. Changing the Row type swaps the
    /// field set and the view model resets its selection to the first field; if the list kept its
    /// own index, Alt+F and F6 would land on different rows — and Move Up/Down act on whichever
    /// one selection followed.
    /// </summary>
    [StaFact]
    public void ChangingTheRowType_ForgetsTheRememberedRow_SoAltFAndF6Agree()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);

            list.FocusRow(5);
            DrainDispatcher();
            Assert.Equal(5, list.FocusedIndex());

            vm.SelectedRowKind = vm.RowKinds.First(k => k != vm.SelectedRowKind);
            DrainDispatcher();
            window.UpdateLayout();

            PreviewBox(window).Focus();
            DrainDispatcher();
            list.Focus();
            DrainDispatcher();

            // The first field of the new layout — the same row the view model selected, and the
            // same one F6 into the list goes to.
            Assert.Equal(0, list.FocusedIndex());
            Assert.Same(vm.Fields[0], vm.SelectedField);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    // ── Arrow keys stop at the ends (#459) ────────────────────────────────────
    //
    // Arrow navigation between the rows is WPF's directional navigation, so the tests drive
    // MoveFocus with the same TraversalRequest the arrow keys produce — deterministic, and not
    // gated behind QUICKMAIL_RUN_INPUT_TESTS. The list is Contained, not Cycle: this is a list,
    // and arrowing past its first or last item must stop rather than wrap.

    private static bool Arrow(CheckBox from, FocusNavigationDirection direction) =>
        from.MoveFocus(new TraversalRequest(direction));

    [StaFact]
    public void DownArrow_OnTheLastField_StaysOnIt()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            var boxes = RowCheckBoxes(list);
            var last = boxes.Length - 1;

            boxes[last].Focus();
            DrainDispatcher();
            Arrow(boxes[last], FocusNavigationDirection.Down);
            DrainDispatcher();

            Assert.Equal(last, list.FocusedIndex());
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void UpArrow_OnTheFirstField_StaysOnIt()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            var boxes = RowCheckBoxes(list);

            boxes[0].Focus();
            DrainDispatcher();
            Arrow(boxes[0], FocusNavigationDirection.Up);
            DrainDispatcher();

            Assert.Equal(0, list.FocusedIndex());
        }
        finally { CloseAndReleaseFocus(window); }
    }

    /// <summary>
    /// The other half of the fix: stopping at the ends must not have stopped arrows working in
    /// between, and it must not have stranded focus outside the list either.
    /// </summary>
    [StaFact]
    public void ArrowsStillMoveBetweenAdjacentFields()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            var boxes = RowCheckBoxes(list);

            boxes[0].Focus();
            DrainDispatcher();
            Arrow(boxes[0], FocusNavigationDirection.Down);
            DrainDispatcher();
            Assert.Equal(1, list.FocusedIndex());
            Assert.Same(vm.Fields[1], vm.SelectedField);

            Arrow(boxes[1], FocusNavigationDirection.Up);
            DrainDispatcher();
            Assert.Equal(0, list.FocusedIndex());
            Assert.Same(vm.Fields[0], vm.SelectedField);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    // ── Home / End / first-letter ─────────────────────────────────────────────
    //
    // These come free inside a ListBox and had to be written by hand here. Driven through the
    // control's own methods rather than synthesized keystrokes, so they are deterministic and
    // are not gated behind QUICKMAIL_RUN_INPUT_TESTS (#415).

    [StaFact]
    public void HomeAndEnd_MoveToTheFirstAndLastField()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);

            Assert.True(list.FocusRow(list.Items.Count - 1));   // End
            DrainDispatcher();
            Assert.Equal(list.Items.Count - 1, list.FocusedIndex());
            Assert.Same(vm.Fields[^1], vm.SelectedField);

            Assert.True(list.FocusRow(0));                       // Home
            DrainDispatcher();
            Assert.Equal(0, list.FocusedIndex());
            Assert.Same(vm.Fields[0], vm.SelectedField);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void FirstLetter_JumpsToTheNextFieldStartingWithIt()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            list.FocusRow(0);
            DrainDispatcher();

            Assert.True(list.TryTypeAhead("d"));
            DrainDispatcher();

            Assert.Equal("Date", vm.Fields[list.FocusedIndex()].DisplayName);
            // Selection follows, so Move Up/Down act on what the user just landed on.
            Assert.Equal("Date", vm.SelectedField!.DisplayName);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void RepeatingTheSameLetter_CyclesThroughMatchesAndWraps()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            list.FocusRow(0);
            DrainDispatcher();

            // Message fields starting with F: "From", "Forwarded", "Flag" — in layout order
            // Flag is first, so from row 0 the first match is From.
            var seen = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 4; i++)
            {
                Assert.True(list.TryTypeAhead("f"));
                DrainDispatcher();
                seen.Add(vm.Fields[list.FocusedIndex()].DisplayName);
            }

            Assert.All(seen, s => Assert.StartsWith("F", s, System.StringComparison.Ordinal));
            // Four presses over three matches must have wrapped rather than stalled.
            Assert.Equal(seen[0], seen[3]);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void TypeAheadIgnoresSpace_SoSpaceStillTogglesTheCheckBox()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            list.FocusRow(2);
            DrainDispatcher();

            Assert.False(list.TryTypeAhead(" "));
            Assert.Equal(2, list.FocusedIndex());   // focus did not move
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void TypeAheadWithNoMatch_LeavesFocusWhereItWas()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();
            var list = FieldList(window);
            list.FocusRow(1);
            DrainDispatcher();

            Assert.False(list.TryTypeAhead("zz"));
            Assert.Equal(1, list.FocusedIndex());
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void SpaceIsLeftToTheCheckBox_NotInterceptedByTheWindow()
    {
        var (window, _) = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var box = RowCheckBoxes(FieldList(window)).First();
            box.Focus();
            DrainDispatcher();

            var args = new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            box.RaiseEvent(args);
            DrainDispatcher();

            // Unhandled by us: the check box's own Space handling toggles it and the platform
            // reports the change. Handling it here would mean re-announcing what is already said.
            Assert.False(args.Handled);
        }
        finally { CloseAndReleaseFocus(window); }
    }
}
