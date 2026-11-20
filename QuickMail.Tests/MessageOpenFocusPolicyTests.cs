using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// When a newly opened message is focused (#959, #851): only once the page on show is this render's
/// and loaded, never fewer than five tries however slow each is, and only this render's own
/// NavigationCompleted counts.
/// </summary>
public class MessageOpenFocusPolicyTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private const int Min = MessageOpenFocusPolicy.MaxUnfocusedAttempts;

    [Theory]
    [InlineData("true", FocusScriptResult.Focused)]
    [InlineData(" true ", FocusScriptResult.Focused)]
    [InlineData("false", FocusScriptResult.NotFocused)]
    [InlineData("\"stale\"", FocusScriptResult.Stale)]
    [InlineData("\"loading\"", FocusScriptResult.Loading)]
    [InlineData("\"ready\"", FocusScriptResult.Ready)]
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
    /// A stale or loading page is waited for until the budget is spent: the old loop gave up after
    /// half a second, which is what left focus outside a page still arriving.
    /// </summary>
    [Theory]
    [InlineData(FocusScriptResult.Stale)]
    [InlineData(FocusScriptResult.Loading)]
    public void NotReady_IsRetriedUntilTheBudgetRunsOut(FocusScriptResult result)
    {
        Assert.Equal(FocusDecision.Retry,
            MessageOpenFocusPolicy.Next(result, 20, 0, MessageOpenFocusPolicy.Budget - Ms(1)));
        Assert.Equal(FocusDecision.GiveUp,
            MessageOpenFocusPolicy.Next(result, 21, 0, MessageOpenFocusPolicy.Budget));
    }

    /// <summary>
    /// On a busy UI thread, which is when #959 happens, one attempt can take longer than the whole
    /// budget. It must not end the loop: there are always at least five tries.
    /// </summary>
    [Theory]
    [InlineData(FocusScriptResult.Stale)]
    [InlineData(FocusScriptResult.Loading)]
    [InlineData(FocusScriptResult.NotFocused)]
    [InlineData(FocusScriptResult.Error)]
    public void SlowFirstAttempt_DoesNotEndIt(FocusScriptResult result)
    {
        var late = MessageOpenFocusPolicy.Budget + TimeSpan.FromSeconds(5);
        var unfocused = result is FocusScriptResult.NotFocused or FocusScriptResult.Error ? 1 : 0;
        Assert.Equal(FocusDecision.Retry, MessageOpenFocusPolicy.Next(result, 0, unfocused, late));
        Assert.Equal(FocusDecision.Retry, MessageOpenFocusPolicy.Next(result, Min - 2, Min - 1, late));
        Assert.Equal(FocusDecision.GiveUp, MessageOpenFocusPolicy.Next(result, Min - 1, Min, late));
    }

    /// <summary>Focus that lands is taken, however late, rather than thrown away at the deadline.</summary>
    [Fact]
    public void FocusedAtTheDeadline_StillFocuses() =>
        Assert.Equal(FocusDecision.Focus,
            MessageOpenFocusPolicy.Next(FocusScriptResult.Focused, 40, 0, MessageOpenFocusPolicy.Budget + Ms(500)));

    /// <summary>
    /// This render's own ready page refusing focus (the window is in the background, say) gives up
    /// after five tries, as before, so the announcement after it is not held back.
    /// </summary>
    [Fact]
    public void ThisPageRefusingFocus_GivesUpAfterFiveTries()
    {
        for (var unfocused = 1; unfocused < Min; unfocused++)
            Assert.Equal(FocusDecision.Retry,
                MessageOpenFocusPolicy.Next(FocusScriptResult.NotFocused, unfocused + 3, unfocused, Ms(400)));
        Assert.Equal(FocusDecision.GiveUp,
            MessageOpenFocusPolicy.Next(FocusScriptResult.NotFocused, 9, Min, Ms(900)));
    }

    [Fact]
    public void AttemptCeiling_GivesUp() =>
        Assert.Equal(FocusDecision.GiveUp,
            MessageOpenFocusPolicy.Next(FocusScriptResult.Stale, MessageOpenFocusPolicy.MaxAttempts - 1, 0, Ms(10)));

    /// <summary>
    /// A page of this render's still loading is waited for, then focused anyway before the budget
    /// ends, rather than never.
    /// </summary>
    [Fact]
    public void WaitForLoad_UntilShortlyBeforeTheBudgetEnds()
    {
        Assert.True(MessageOpenFocusPolicy.WaitForLoad(TimeSpan.Zero));
        Assert.True(MessageOpenFocusPolicy.WaitForLoad(MessageOpenFocusPolicy.FocusAnywayAfter - Ms(1)));
        Assert.False(MessageOpenFocusPolicy.WaitForLoad(MessageOpenFocusPolicy.FocusAnywayAfter));
        Assert.True(MessageOpenFocusPolicy.FocusAnywayAfter + Ms(500) <= MessageOpenFocusPolicy.Budget);
    }

    /// <summary>The wait for NavigationCompleted is what it was before #959.</summary>
    [Fact]
    public void LoadGrace_IsFourSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(4), MessageOpenFocusPolicy.LoadGrace);

    // ── The loop, with a fake page and clock ─────────────────────────────────

    /// <summary>A page whose answers are scripted, with a clock that only moves when told.</summary>
    private sealed class FakePage
    {
        public readonly Queue<string> Readiness = new();
        public readonly Queue<string> Focus = new();
        public readonly List<string> Calls = [];
        public TimeSpan Now;
        public TimeSpan ReadinessCost;   // how long each readiness check "takes"
        public bool Current = true;

        public Task<FocusScriptResult> RunAsync() => MessageOpenFocusPolicy.RunAsync(
            () => Current,
            () => { Calls.Add("host"); return Task.CompletedTask; },
            wait =>
            {
                Calls.Add(wait ? "ready?" : "ready-now?");
                Now += ReadinessCost;
                return Task.FromResult<string?>(Readiness.Count > 0 ? Readiness.Dequeue() : "\"stale\"");
            },
            wait =>
            {
                Calls.Add(wait ? "focus" : "focus-now");
                return Task.FromResult<string?>(Focus.Count > 0 ? Focus.Dequeue() : "false");
            },
            () => Now,
            pause => { Now += pause; return Task.CompletedTask; },
            "test");
    }

    /// <summary>
    /// While the previous message's page is on show, nothing moves focus: the host's own focus step
    /// (TabInto) runs only once the page is ready, and then before the focus script, as always.
    /// </summary>
    [Fact]
    public async Task Loop_StaleThenFocused_FocusesTheHostOnlyWhenReady()
    {
        var page = new FakePage();
        page.Readiness.Enqueue("\"stale\"");
        page.Readiness.Enqueue("\"loading\"");
        page.Readiness.Enqueue("\"ready\"");
        page.Focus.Enqueue("true");

        var result = await page.RunAsync();

        Assert.Equal(FocusScriptResult.Focused, result);
        Assert.Equal(["ready?", "ready?", "ready?", "host", "focus"], page.Calls);
    }

    /// <summary>One attempt slowed past the whole budget by a busy UI thread still leaves time for focus to land.</summary>
    [Fact]
    public async Task Loop_SlowFirstAttempt_StillFocuses()
    {
        var page = new FakePage { ReadinessCost = MessageOpenFocusPolicy.Budget + TimeSpan.FromSeconds(2) };
        page.Readiness.Enqueue("\"stale\"");
        page.Readiness.Enqueue("\"ready\"");
        page.Focus.Enqueue("true");

        var result = await page.RunAsync();

        Assert.Equal(FocusScriptResult.Focused, result);
        // Past the point of waiting for the load, the page is asked whether it is merely there.
        Assert.Equal(["ready?", "ready-now?", "host", "focus-now"], page.Calls);
    }

    /// <summary>A page still loading near the end of the budget is focused anyway.</summary>
    [Fact]
    public async Task Loop_PageThatNeverFinishesLoading_IsFocusedAnyway()
    {
        var page = new FakePage();
        var waits = (int)(MessageOpenFocusPolicy.FocusAnywayAfter / MessageOpenFocusPolicy.RetryDelay);
        for (var i = 0; i < waits; i++) page.Readiness.Enqueue("\"loading\"");
        page.Readiness.Enqueue("\"ready\"");   // what the page says once not waiting for the load
        page.Focus.Enqueue("true");

        var result = await page.RunAsync();

        Assert.Equal(FocusScriptResult.Focused, result);
        Assert.Equal("ready-now?", page.Calls[^3]);
        Assert.Equal(["host", "focus-now"], page.Calls.GetRange(page.Calls.Count - 2, 2));
        Assert.DoesNotContain("focus", page.Calls);
    }

    /// <summary>A page that never becomes this render's is given up on, and focus is never moved into it.</summary>
    [Fact]
    public async Task Loop_AlwaysStale_GivesUpWithoutTouchingIt()
    {
        var page = new FakePage();

        var result = await page.RunAsync();

        Assert.Equal(FocusScriptResult.Stale, result);
        Assert.DoesNotContain("host", page.Calls);
        Assert.True(page.Now >= MessageOpenFocusPolicy.Budget);
        Assert.True(page.Calls.Count >= Min);
    }

    /// <summary>This render's page refusing focus gives up after five tries, each with the host step first.</summary>
    [Fact]
    public async Task Loop_RefusingFocus_GivesUpAfterFiveTries()
    {
        var page = new FakePage();
        for (var i = 0; i < 10; i++) page.Readiness.Enqueue("\"ready\"");

        var result = await page.RunAsync();

        Assert.Equal(FocusScriptResult.NotFocused, result);
        Assert.Equal(Min, page.Calls.FindAll(c => c == "host").Count);
        Assert.Equal(Min, page.Calls.FindAll(c => c == "focus").Count);
    }

    /// <summary>A newer render, or closing the message, stops the loop at once.</summary>
    [Fact]
    public async Task Loop_NoLongerCurrent_StopsQuietly()
    {
        var page = new FakePage { Current = false };

        await page.RunAsync();

        Assert.Empty(page.Calls);
    }

    [Fact]
    public async Task Loop_ScriptThrowing_CountsAsAFailedTry()
    {
        var calls = 0;
        var result = await MessageOpenFocusPolicy.RunAsync(
            () => true, () => Task.CompletedTask,
            _ => { calls++; throw new InvalidOperationException("gone"); },
            _ => Task.FromResult<string?>("true"),
            () => TimeSpan.Zero, _ => Task.CompletedTask, "test");

        Assert.Equal(FocusScriptResult.Error, result);
        Assert.Equal(Min, calls);
    }

    // ── Finishing, and the one-loop-per-render gate ──────────────────────────

    /// <summary>
    /// After the loop: no "Message body. Subject" when focus never reached this render's page, and no
    /// pulling focus back from wherever the user moved it during the wait.
    /// </summary>
    [Theory]
    [InlineData(FocusScriptResult.Focused, true, true)]
    [InlineData(FocusScriptResult.NotFocused, true, true)]
    [InlineData(FocusScriptResult.Loading, true, true)]
    [InlineData(FocusScriptResult.Error, true, true)]
    [InlineData(FocusScriptResult.Stale, true, false)]
    [InlineData(FocusScriptResult.Focused, false, false)]
    [InlineData(FocusScriptResult.Stale, false, false)]
    public void ShouldFinish(FocusScriptResult result, bool focusStillOnBody, bool expected) =>
        Assert.Equal(expected, MessageOpenFocusPolicy.ShouldFinish(result, focusStillOnBody));

    /// <summary>F6 back into a message still opening does not start a second loop that would announce again.</summary>
    [Fact]
    public void Gate_OneLoopPerRender()
    {
        var gate = new MessageOpenFocusGate();

        Assert.True(gate.TryBegin(3));
        Assert.True(gate.IsRunning(3));
        Assert.False(gate.TryBegin(3));   // F6 during the open
        gate.End(3);
        Assert.False(gate.IsRunning(3));
        Assert.True(gate.TryBegin(3));    // F6 later: a loop of its own
    }

    /// <summary>A newer render takes over; the older loop ending leaves the newer one marked running.</summary>
    [Fact]
    public void Gate_ANewerRender_IsNotEndedByTheOlderLoop()
    {
        var gate = new MessageOpenFocusGate();
        Assert.True(gate.TryBegin(3));
        Assert.True(gate.TryBegin(4));

        gate.End(3);

        Assert.True(gate.IsRunning(4));
        Assert.False(gate.IsRunning(3));
    }

    /// <summary>
    /// The host's GotKeyboardFocus stands aside while the loop runs (its own focus step raised it),
    /// and acts again once the loop is over, for the user's own Tab or F6.
    /// </summary>
    [Fact]
    public void Gate_GotKeyboardFocus_StandsAsideOnlyDuringTheLoop()
    {
        var gate = new MessageOpenFocusGate();
        Assert.False(gate.IsRunning(7));      // user's Tab before anything opens
        gate.TryBegin(7);
        Assert.True(gate.IsRunning(7));       // the loop's own MessageBody.Focus()
        gate.End(7);
        Assert.False(gate.IsRunning(7));      // user's Tab afterwards
    }

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
