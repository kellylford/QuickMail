using System.Windows.Input;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

// Covers bare-key bindings (issue #330): Delete/Backspace/Insert/F-keys must be capturable and must
// round-trip through the gesture string, while ordinary text and dialog-navigation keys must not.
public class GestureHelperTests
{
    [Theory]
    [InlineData(Key.Delete, true)]
    [InlineData(Key.Back, true)]
    [InlineData(Key.Insert, true)]
    [InlineData(Key.F1, true)]
    [InlineData(Key.F5, true)]
    [InlineData(Key.F24, true)]
    [InlineData(Key.A, false)]
    [InlineData(Key.D5, false)]
    [InlineData(Key.Space, false)]
    [InlineData(Key.Enter, false)]
    [InlineData(Key.Escape, false)]
    [InlineData(Key.Tab, false)]
    public void IsBindableBareKey_AllowsOnlyTheIntendedKeys(Key key, bool expected)
        => Assert.Equal(expected, GestureHelper.IsBindableBareKey(key));

    [Theory]
    [InlineData(Key.Delete, "Delete")]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.Insert, "Insert")]
    [InlineData(Key.F3, "F3")]
    public void BareKey_RoundTripsThroughFormatAndTryParse(Key key, string expectedGesture)
    {
        var gesture = GestureHelper.Format(key, ModifierKeys.None);
        Assert.Equal(expectedGesture, gesture);

        Assert.True(GestureHelper.TryParse(gesture, out var parsedKey, out var parsedMods));
        Assert.Equal(key, parsedKey);
        Assert.Equal(ModifierKeys.None, parsedMods);
    }

    [Theory]
    [InlineData("A")]        // bare letter — not allowlisted
    [InlineData("Space")]    // bare space
    [InlineData("Enter")]    // dialog-navigation key
    public void TryParse_RejectsBareNonAllowlistedKeys(string gesture)
        => Assert.False(GestureHelper.TryParse(gesture, out _, out _));

    [Fact]
    public void TryParse_StillParsesModifierCombos()
    {
        Assert.True(GestureHelper.TryParse("Ctrl+Shift+Delete", out var key, out var mods));
        Assert.Equal(Key.Delete, key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, mods);
    }
}
