using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class NavigationFocusPolicyTests
{
    [Fact]
    public void FocusNeverMoved_GoesToTheList()
        => Assert.True(NavigationFocusPolicy.ShouldFocusMessagePanel(true, true, false, false));

    [Fact]
    public void FocusAlreadyFollowedTheListIn_IsSettledThere()
        => Assert.True(NavigationFocusPolicy.ShouldFocusMessagePanel(true, true, true, true));

    [Fact]
    public void UserWentElsewhereWhileItLoaded_IsLeftThere()
        => Assert.False(NavigationFocusPolicy.ShouldFocusMessagePanel(true, true, true, false));

    [Fact]
    public void UserWentBackToTheSameTreeItem_CountsAsHavingMoved()
    {
        // Into the list with the cached messages, then back to the folder tree, which lands on the
        // item Enter was pressed on. Focus changed, and it is not in the message area: stay put.
        Assert.False(NavigationFocusPolicy.ShouldFocusMessagePanel(
            navigated: true, windowActive: true, focusChangedDuring: true, focusInMessageArea: false));
    }

    [Fact]
    public void NothingWasSelected_FocusIsLeftAlone()
        => Assert.False(NavigationFocusPolicy.ShouldFocusMessagePanel(false, true, false, false));

    [Fact]
    public void AnotherWindowIsActive_FocusIsNotTaken()
        => Assert.False(NavigationFocusPolicy.ShouldFocusMessagePanel(true, false, false, false));
}
