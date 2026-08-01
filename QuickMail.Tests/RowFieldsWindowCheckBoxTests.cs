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
        };
        return (window, vm);
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
        finally { window.Close(); }
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
        finally { window.Close(); }
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
        finally { window.Close(); }
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
        finally { window.Close(); }
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
        finally { window.Close(); }
    }
}
