using System;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// When a newly opened message is focused (#959, #851): only once the page on show is this render's,
/// for as long as the budget allows, and only this render's own NavigationCompleted counts.
/// </summary>
public class MessageOpenFocusPolicyTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData("true", FocusScriptResult.Focused)]
    [InlineData(" true ", FocusScriptResult.Focused)]
    [InlineData("false", FocusScriptResult.NotFocused)]
    [InlineData("\"stale\"", FocusScriptResult.Stale)]
    [InlineData("null", FocusScriptResult.Error)]
    [InlineData("", FocusScriptResult.Error)]
    [InlineData(null, FocusScriptResult.Error)]
    [InlineData("\"true\"", FocusScriptResult.Error)]
    public void Parse_ReadsTheScriptsAnswer(string? json, FocusScriptResult expected) =>
        Assert.Equal(expected, MessageOpenFocusPolicy.Parse(json));

    /// <summary>The previous page is still on show for a moment; then the new one takes focus.</summary>
    [Fact]
    public void StaleThenTrue_RetriesThenFocuses()
    {
        Assert.Equal(FocusDecision.Retry, MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, 0, 0, Ms(100)));
        Assert.Equal(FocusDecision.Retry, MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, 1, 0, Ms(200)));
        Assert.Equal(FocusDecision.Focus, MessageOpenFocusPolicy.Next(FocusScriptResult.Focused, 2, 0, Ms(300)));
    }

    /// <summary>
    /// A stale page is waited for well past the five attempts the old loop allowed: that loop gave up
    /// after half a second, which is what left focus outside a page still arriving.
    /// </summary>
    [Fact]
    public void Stale_IsRetriedUntilTheBudgetRunsOut()
    {
        Assert.Equal(FocusDecision.Retry, MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, 20, 0, Ms(2500)));
        Assert.Equal(FocusDecision.Retry,
            MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, 20, 0, MessageOpenFocusPolicy.Budget - Ms(1)));
        Assert.Equal(FocusDecision.GiveUp,
            MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, 21, 0, MessageOpenFocusPolicy.Budget));
    }

    [Fact]
    public void Timeout_GivesUpWhateverTheResult()
    {
        var over = MessageOpenFocusPolicy.Budget + Ms(1);
        Assert.Equal(FocusDecision.GiveUp, MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, 3, 0, over));
        Assert.Equal(FocusDecision.GiveUp, MessageOpenFocusPolicy.Next(FocusScriptResult.NotFocused, 3, 1, over));
        Assert.Equal(FocusDecision.GiveUp, MessageOpenFocusPolicy.Next(FocusScriptResult.Error, 3, 1, over));
    }

    /// <summary>Focus that lands is taken, however late, rather than thrown away at the deadline.</summary>
    [Fact]
    public void FocusedAtTheDeadline_StillFocuses() =>
        Assert.Equal(FocusDecision.Focus,
            MessageOpenFocusPolicy.Next(FocusScriptResult.Focused, 40, 0, MessageOpenFocusPolicy.Budget + Ms(500)));

    /// <summary>
    /// This render's own page refusing focus (the window is in the background, say) gives up after
    /// five tries, as before, so the announcement after it is not held back for the whole budget.
    /// </summary>
    [Fact]
    public void ThisPageRefusingFocus_GivesUpAfterFiveTries()
    {
        for (var unfocused = 1; unfocused < MessageOpenFocusPolicy.MaxUnfocusedAttempts; unfocused++)
            Assert.Equal(FocusDecision.Retry,
                MessageOpenFocusPolicy.Next(FocusScriptResult.NotFocused, unfocused + 3, unfocused, Ms(400)));
        Assert.Equal(FocusDecision.GiveUp,
            MessageOpenFocusPolicy.Next(FocusScriptResult.NotFocused, 9, MessageOpenFocusPolicy.MaxUnfocusedAttempts, Ms(900)));
    }

    [Fact]
    public void AttemptCeiling_GivesUp() =>
        Assert.Equal(FocusDecision.GiveUp,
            MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, MessageOpenFocusPolicy.MaxAttempts - 1, 0, Ms(10)));

    /// <summary>The grace before focusing a page still loading is inside the budget, leaving time to retry after it.</summary>
    [Fact]
    public void LoadGrace_LeavesTimeToRetry() =>
        Assert.True(MessageOpenFocusPolicy.LoadGrace + TimeSpan.FromSeconds(1) <= MessageOpenFocusPolicy.Budget);

    // ── NavigationMatcher ────────────────────────────────────────────────────

    /// <summary>
    /// The navigation this one stopped completes after it was started: that completion is not ours,
    /// and must not release the wait for the new message.
    /// </summary>
    [Fact]
    public void Completion_WithTheWrongId_IsNotOurs()
    {
        var matcher = new NavigationMatcher();
        matcher.Started(8, "data:text/html;charset=utf-8;base64,AAAA");

        Assert.False(matcher.IsOurs(7));   // the earlier, stopped navigation
        Assert.True(matcher.IsOurs(8));
    }

    [Fact]
    public void Completion_BeforeOurNavigationStarted_IsNotOurs()
    {
        var matcher = new NavigationMatcher();
        Assert.Null(matcher.NavigationId);
        Assert.False(matcher.IsOurs(7));
    }

    /// <summary>Only the first document navigation after the call is taken; a later one is not.</summary>
    [Fact]
    public void OnlyTheFirstDocumentNavigation_IsTaken()
    {
        var matcher = new NavigationMatcher();
        matcher.Started(8, "about:blank");
        matcher.Started(9, "data:text/html,later");

        Assert.Equal(8UL, matcher.NavigationId);
        Assert.False(matcher.IsOurs(9));
    }

    /// <summary>A link the user follows starts a navigation too; it is never mistaken for the message's own.</summary>
    [Fact]
    public void ALinkNavigation_IsNotTaken()
    {
        var matcher = new NavigationMatcher();
        matcher.Started(5, "https://example.com/");
        matcher.Started(6, "data:text/html;base64,AAAA");

        Assert.Equal(6UL, matcher.NavigationId);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("about:blank", true)]
    [InlineData("DATA:text/html,x", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("quickmail:x", false)]
    public void IsDocumentNavigation(string? uri, bool expected) =>
        Assert.Equal(expected, NavigationMatcher.IsDocumentNavigation(uri));
}
