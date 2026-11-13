// Tests for TextEditingGestures (#978): which keys a registry window must leave to the focused
// text box or page when the command bound to them is unavailable.

using System.Windows.Input;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class TextEditingGesturesTests
{
    [Theory]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.X, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.A, ModifierKeys.Control)]
    [InlineData(Key.Insert, ModifierKeys.Control)]
    [InlineData(Key.Insert, ModifierKeys.Shift)]
    [InlineData(Key.Delete, ModifierKeys.Shift)]
    public void Clipboard_and_select_all_gestures_pass_through(Key key, ModifierKeys mods)
    {
        Assert.True(TextEditingGestures.IsTextEditingGesture(key, mods));
        Assert.False(TextEditingGestures.SwallowWhenUnavailable(key, mods));
    }

    [Theory]
    [InlineData(Key.Left, ModifierKeys.Alt)]       // previous message: must not fall through to the page
    [InlineData(Key.Right, ModifierKeys.Alt)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.C, ModifierKeys.Alt)]
    [InlineData(Key.C, ModifierKeys.None)]
    [InlineData(Key.Delete, ModifierKeys.None)]
    [InlineData(Key.Insert, ModifierKeys.None)]
    [InlineData(Key.R, ModifierKeys.Control)]
    public void Other_gestures_are_still_swallowed(Key key, ModifierKeys mods)
    {
        Assert.False(TextEditingGestures.IsTextEditingGesture(key, mods));
        Assert.True(TextEditingGestures.SwallowWhenUnavailable(key, mods));
    }
}
