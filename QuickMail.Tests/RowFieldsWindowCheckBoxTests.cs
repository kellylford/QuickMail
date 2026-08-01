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
/// Each field row must BE a check box, not a list item that happens to contain one: a field's whole
/// purpose in this window is on/off, so it gets the real control and the platform reports its role,
/// its checked state, and every change to that state. These read the live automation tree rather
/// than the template, because the template looked identical when the rows were not check boxes.
/// </summary>
[Collection("WpfTests")]
public class RowFieldsWindowCheckBoxTests
{
    private static RowFieldsWindow MakeWindow() =>
        new(new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService()))
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new System.Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static CheckBox[] RowCheckBoxes(ListBox list) =>
        list.Items.Cast<object>()
            .Select((_, i) => list.ItemContainerGenerator.ContainerFromIndex(i))
            .OfType<DependencyObject>()
            .Select(FindCheckBox)
            .OfType<CheckBox>()
            .ToArray();

    private static CheckBox? FindCheckBox(DependencyObject root)
    {
        if (root is CheckBox cb) return cb;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindCheckBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } f) return f;
        }
        return null;
    }

    [StaFact]
    public void EveryFieldRowIsARealFocusableCheckBox()
    {
        var window = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = window.FindName("FieldList") as ListBox;
            Assert.NotNull(list);

            var boxes = RowCheckBoxes(list!);
            Assert.Equal(list!.Items.Count, boxes.Length);
            Assert.All(boxes, b => Assert.True(b.Focusable, "the row's check box must be the arrow stop"));

            // The containers must NOT also be focusable, or every row would be two stops.
            var containers = Enumerable.Range(0, list.Items.Count)
                .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i))
                .OfType<ListBoxItem>()
                .ToArray();
            Assert.Equal(list.Items.Count, containers.Length);
            Assert.All(containers, c => Assert.False(c.Focusable));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void RowPeer_ReportsCheckBoxRoleAndToggleState()
    {
        var window = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = window.FindName("FieldList") as ListBox;
            var flag = RowCheckBoxes(list!).First();

            var peer = UIElementAutomationPeer.CreatePeerForElement(flag);
            Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());
            Assert.Equal("Flag", peer.GetName());

            // Toggle state is the platform's to report — this is what makes a custom
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
        var window = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = window.FindName("FieldList") as ListBox;
            var boxes = RowCheckBoxes(list!);

            boxes[3].Focus();
            DrainDispatcher();

            // Selection follows keyboard focus, because the move commands act on the selection.
            Assert.Same(list!.Items[3], list.SelectedItem);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void SpaceIsLeftToTheCheckBox_NotInterceptedByTheWindow()
    {
        var window = MakeWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var list = window.FindName("FieldList") as ListBox;
            var box = RowCheckBoxes(list!).First();
            box.Focus();
            DrainDispatcher();

            var args = new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            box.RaiseEvent(args);
            DrainDispatcher();

            // Unhandled by us: the check box's own Space handling does the toggling, and the
            // platform reports it. Handling it here would mean re-announcing what is already said.
            Assert.False(args.Handled);
        }
        finally { window.Close(); }
    }
}
