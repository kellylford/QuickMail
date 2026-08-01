// Regression tests for issue #441: arrowing through a settings radio group moved focus but did
// not select the focused option — the user had to press Space, which is not how radio groups
// behave anywhere else on Windows.
//
// RadioGroupNavigation.SelectionFollowsFocus makes arrows select. The tests feed real
// KeyEventArgs through the attached PreviewKeyDown handler rather than synthesizing keystrokes,
// so they are deterministic and do not need QUICKMAIL_RUN_INPUT_TESTS.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class RadioGroupNavigationTests
{
    private static (StackPanel panel, RadioButton[] buttons) BuildGroup(bool followFocus = true)
    {
        var panel = new StackPanel();
        var buttons = new RadioButton[3];
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i] = new RadioButton { GroupName = "Mode", Content = $"Option {i}" };
            panel.Children.Add(buttons[i]);
            // A description under each option, like the settings dialog has — the walk must
            // skip non-radio siblings.
            panel.Children.Add(new TextBlock { Text = $"Explains option {i}" });
        }
        buttons[0].IsChecked = true;
        RadioGroupNavigation.SetSelectionFollowsFocus(panel, followFocus);
        return (panel, buttons);
    }

    private static void PressKey(StackPanel panel, RadioButton source, Key key)
    {
        var args = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            new HwndSourceStub(),
            timestamp: 0,
            key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
            Source      = source,
        };
        panel.RaiseEvent(args);
    }

    // KeyEventArgs needs a PresentationSource; the tests never show a window, so a stub suffices.
    private sealed class HwndSourceStub : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    [StaFact]
    public void DownArrow_ChecksNextOption()
    {
        var (panel, buttons) = BuildGroup();

        PressKey(panel, buttons[0], Key.Down);

        Assert.True(buttons[1].IsChecked);
        Assert.False(buttons[0].IsChecked);
    }

    [StaFact]
    public void RightArrow_ChecksNextOption()
    {
        var (panel, buttons) = BuildGroup();

        PressKey(panel, buttons[0], Key.Right);

        Assert.True(buttons[1].IsChecked);
    }

    [StaFact]
    public void UpArrow_FromFirstOption_WrapsToLast()
    {
        // The groups use DirectionalNavigation="Cycle"; selection must wrap the same way.
        var (panel, buttons) = BuildGroup();

        PressKey(panel, buttons[0], Key.Up);

        Assert.True(buttons[2].IsChecked);
    }

    [StaFact]
    public void DownArrow_FromLastOption_WrapsToFirst()
    {
        var (panel, buttons) = BuildGroup();
        buttons[2].IsChecked = true;

        PressKey(panel, buttons[2], Key.Down);

        Assert.True(buttons[0].IsChecked);
    }

    [StaFact]
    public void DisabledOption_IsSkipped()
    {
        var (panel, buttons) = BuildGroup();
        buttons[1].IsEnabled = false;

        PressKey(panel, buttons[0], Key.Down);

        Assert.True(buttons[2].IsChecked);
        Assert.False(buttons[1].IsChecked);
    }

    [StaFact]
    public void OtherGroupInSamePanel_IsNotTouched()
    {
        var (panel, buttons) = BuildGroup();
        var other = new RadioButton { GroupName = "SomethingElse", Content = "Unrelated" };
        panel.Children.Add(other);

        PressKey(panel, buttons[0], Key.Down);

        Assert.True(buttons[1].IsChecked);
        Assert.False(other.IsChecked);
    }

    [StaFact]
    public void NonArrowKey_IsIgnored()
    {
        // Tab leaves the group; it must never change the selection on the way out.
        var (panel, buttons) = BuildGroup();

        PressKey(panel, buttons[0], Key.Tab);

        Assert.True(buttons[0].IsChecked);
        Assert.False(buttons[1].IsChecked);
    }

    [StaFact]
    public void WithoutTheBehaviour_ArrowsDoNotSelect()
    {
        // Guards the opt-in: groups that must not select on focus (e.g. the FlagManager colour
        // swatches, whose buttons run a command on check) are unaffected.
        var (panel, buttons) = BuildGroup(followFocus: false);

        PressKey(panel, buttons[0], Key.Down);

        Assert.True(buttons[0].IsChecked);
        Assert.False(buttons[1].IsChecked);
    }

    [StaFact]
    public void TheOptionIsCheckedBeforeItIsFocused()
    {
        // Order matters: the focus that follows must land on a button that is already selected,
        // so the state the platform reports for it is the new one. Nothing else announces.
        var (panel, buttons) = BuildGroup();
        bool? checkedBeforeFocus = null;
        buttons[1].Checked      += (_, _) => checkedBeforeFocus ??= true;
        buttons[1].GotFocus     += (_, _) => checkedBeforeFocus ??= false;

        PressKey(panel, buttons[0], Key.Down);

        Assert.True(checkedBeforeFocus, "the button was focused before it was checked");
    }
}
