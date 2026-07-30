using System;
using System.Collections.Generic;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Deterministic coverage for QuickMail's hand-rolled type-ahead (issue #415): the prefix
/// accumulator (<see cref="TypeAheadPrefixTracker"/>) and the wrap-around matcher
/// (<see cref="TypeAheadMatcher"/>) that serve the folder tree, message list, the grouped
/// message trees, and the folder picker's tree view.
///
/// <para>Plain <c>[Fact]</c>s with a fake clock — no window, no synthesized input, no
/// dependence on elapsed time. This is the coverage that used to be attempted with real
/// keystrokes inside WPF TextSearch's reset window (removed in #414 as the least reliable
/// tests in the suite); here the reset window is exercised exactly, including its boundary.</para>
///
/// <para>These tests pin CURRENT behavior. In particular: whitespace keystrokes are rejected
/// and do not extend the window, so a prefix can never contain a space ("new y" builds "newy",
/// which does not match "New York"). Whether that should change is an open UX question on
/// issue #415 — do not "fix" it here without a decision.</para>
/// </summary>
public class TypeAheadLogicTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly object ScopeA = new();
    private static readonly object ScopeB = new();

    private static (TypeAheadPrefixTracker Tracker, FakeClock Clock) Make()
    {
        var clock = new FakeClock();
        return (new TypeAheadPrefixTracker(clock), clock);
    }

    // ── Accumulator: building a prefix ───────────────────────────────────────

    [Fact]
    public void FirstKeystroke_StartsThePrefix()
    {
        var (tracker, _) = Make();

        Assert.True(tracker.TryAppend("b", ScopeA, out var prefix));
        Assert.Equal("b", prefix);
    }

    [Fact]
    public void SecondKeystrokeWithinTheWindow_ExtendsThePrefix()
    {
        var (tracker, clock) = Make();
        tracker.TryAppend("b", ScopeA, out _);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(tracker.TryAppend("r", ScopeA, out var prefix));
        Assert.Equal("br", prefix);
    }

    [Fact]
    public void KeystrokeAtExactlyTheResetDelay_StillExtends()
    {
        // The reset condition uses strict '>', so input at exactly the delay accumulates.
        var (tracker, clock) = Make();
        tracker.TryAppend("b", ScopeA, out _);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.TryAppend("r", ScopeA, out var prefix));
        Assert.Equal("br", prefix);
    }

    [Fact]
    public void KeystrokeAfterTheResetDelay_StartsOver()
    {
        var (tracker, clock) = Make();
        tracker.TryAppend("b", ScopeA, out _);

        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
        Assert.True(tracker.TryAppend("r", ScopeA, out var prefix));
        Assert.Equal("r", prefix);
    }

    [Fact]
    public void EachKeystrokeRefreshesTheWindow()
    {
        // Three keystrokes 900ms apart span 1.8s total but never a 1s gap, so all accumulate.
        var (tracker, clock) = Make();
        tracker.TryAppend("a", ScopeA, out _);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        tracker.TryAppend("b", ScopeA, out _);
        clock.Advance(TimeSpan.FromMilliseconds(900));

        Assert.True(tracker.TryAppend("c", ScopeA, out var prefix));
        Assert.Equal("abc", prefix);
    }

    [Fact]
    public void ScopeChange_StartsOverEvenWithinTheWindow()
    {
        var (tracker, clock) = Make();
        tracker.TryAppend("b", ScopeA, out _);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(tracker.TryAppend("r", ScopeB, out var prefix));
        Assert.Equal("r", prefix);
    }

    [Fact]
    public void ReturningToTheOriginalScope_AlsoStartsOver()
    {
        var (tracker, _) = Make();
        tracker.TryAppend("b", ScopeA, out _);
        tracker.TryAppend("x", ScopeB, out _);

        Assert.True(tracker.TryAppend("r", ScopeA, out var prefix));
        Assert.Equal("r", prefix);
    }

    // ── Accumulator: rejected input ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void RejectedInput_ReturnsFalseWithEmptyPrefix(string? text)
    {
        var (tracker, _) = Make();

        Assert.False(tracker.TryAppend(text, ScopeA, out var prefix));
        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void ControlCharacter_IsRejected()
    {
        // Escape arrives through TextInput as a control character.
        var esc = ((char)27).ToString();
        var (tracker, _) = Make();

        Assert.False(tracker.TryAppend(esc, ScopeA, out var prefix));
        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void RejectedInput_DoesNotTouchTheBufferOrTheWindow()
    {
        // "a", then a rejected space at 900ms, then "b" at 1.5s: the space must not have
        // refreshed the window, so "b" starts a NEW prefix (1.5s > 1s since the last commit).
        var (tracker, clock) = Make();
        tracker.TryAppend("a", ScopeA, out _);

        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.False(tracker.TryAppend(" ", ScopeA, out _));

        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(tracker.TryAppend("b", ScopeA, out var prefix));
        Assert.Equal("b", prefix);
    }

    [Fact]
    public void WhitespaceAroundACharacter_IsTrimmed()
    {
        var (tracker, _) = Make();

        Assert.True(tracker.TryAppend(" a ", ScopeA, out var prefix));
        Assert.Equal("a", prefix);
    }

    [Fact]
    public void SpacesCannotEnterThePrefix_PinnedCurrentBehavior()
    {
        // Typing "new y" yields "newy": the space keystroke is dropped, so multi-word names
        // like "New York" can never be prefix-matched past the first word. Open UX question
        // on issue #415; this test documents the behavior, it does not endorse it.
        var (tracker, _) = Make();
        tracker.TryAppend("n", ScopeA, out _);
        tracker.TryAppend("e", ScopeA, out _);
        tracker.TryAppend("w", ScopeA, out _);
        tracker.TryAppend(" ", ScopeA, out _);

        Assert.True(tracker.TryAppend("y", ScopeA, out var prefix));
        Assert.Equal("newy", prefix);
    }

    // ── Accumulator: peek vs append ──────────────────────────────────────────

    [Fact]
    public void Peek_ComputesThePrefixWithoutRecordingIt()
    {
        var (tracker, _) = Make();

        Assert.True(tracker.TryPeek("b", ScopeA, out var first));
        Assert.Equal("b", first);

        // Nothing was recorded, so a second peek still starts from scratch.
        Assert.True(tracker.TryPeek("r", ScopeA, out var second));
        Assert.Equal("r", second);
    }

    [Fact]
    public void AppendAfterPeek_CommitsWhatWasPeeked()
    {
        // The KeyDown route peeks, and commits via TryAppend only on a match; the committed
        // prefix must equal the peeked one so the next keystroke extends what the user saw.
        var (tracker, _) = Make();
        tracker.TryAppend("b", ScopeA, out _);

        Assert.True(tracker.TryPeek("r", ScopeA, out var peeked));
        Assert.True(tracker.TryAppend("r", ScopeA, out var committed));
        Assert.Equal(peeked, committed);
        Assert.Equal("br", committed);
    }

    [Fact]
    public void PeekOnRejectedInput_LeavesStateIntact()
    {
        var (tracker, _) = Make();
        tracker.TryAppend("b", ScopeA, out _);

        Assert.False(tracker.TryPeek(" ", ScopeA, out _));
        Assert.True(tracker.TryAppend("r", ScopeA, out var prefix));
        Assert.Equal("br", prefix);
    }

    // ── Matcher ──────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> Folders =
        ["Archive", "Drafts", "Inbox", "Junk", "Notes", "Sent"];

    private static int Find(int startIndex, string prefix, IReadOnlyList<string>? items = null)
        => TypeAheadMatcher.FindNext(items ?? Folders, s => s, startIndex, prefix);

    [Fact]
    public void NoSelection_MatchesFromTheTop()
        => Assert.Equal(2, Find(-1, "i"));

    [Fact]
    public void SearchStartsAfterTheCurrentSelection()
        => Assert.Equal(3, Find(2, "j"));

    [Fact]
    public void SearchWrapsAround()
        => Assert.Equal(0, Find(4, "a"));

    [Fact]
    public void RepeatedLetter_CyclesThroughMatches()
    {
        var items = new[] { "Alpha", "Beta", "Alps", "Gamma", "Ash" };
        var first = Find(-1, "a", items);
        var second = Find(first, "a", items);
        var third = Find(second, "a", items);
        var wrapped = Find(third, "a", items);

        Assert.Equal(0, first);
        Assert.Equal(2, second);
        Assert.Equal(4, third);
        Assert.Equal(0, wrapped);
    }

    [Fact]
    public void SoleMatch_ReselectsItself()
        => Assert.Equal(2, Find(2, "i"));

    [Fact]
    public void LongerPrefix_NarrowsTheMatch()
    {
        Assert.Equal(2, Find(-1, "i"));
        Assert.Equal(2, Find(2, "in"));   // still Inbox after accumulating
        Assert.Equal(-1, Find(2, "inz")); // and a wrong letter matches nothing
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
        => Assert.Equal(2, Find(-1, "INB"));

    [Fact]
    public void NoMatch_ReturnsMinusOne()
        => Assert.Equal(-1, Find(-1, "z"));

    [Fact]
    public void EmptyItems_ReturnsMinusOne()
        => Assert.Equal(-1, Find(-1, "a", Array.Empty<string>()));

    [Fact]
    public void EmptyPrefix_ReturnsMinusOne()
        => Assert.Equal(-1, Find(-1, ""));

    [Fact]
    public void NullItemText_IsTreatedAsNoMatch()
        => Assert.Equal(1, TypeAheadMatcher.FindNext(
            new string?[] { null, "Inbox" }, s => s, -1, "i"));
}
