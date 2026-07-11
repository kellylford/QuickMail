using System;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the anti-thrash contract behind WireUpWatchers (#215): watchers restart only when the
/// connected-account set actually changes, and state advances only after they're marked started.
/// </summary>
public class WatcherStartGateTests
{
    [Fact]
    public void RestartsOnlyWhenTheConnectedSetChanges()
    {
        var gate = new WatcherStartGate();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.True(gate.HasChanged([a]));       // first time → start
        gate.MarkStarted([a]);
        Assert.False(gate.HasChanged([a]));      // same set → no restart (anti-thrash)
        Assert.True(gate.HasChanged([a, b]));    // set grew → restart
        gate.MarkStarted([a, b]);
        Assert.False(gate.HasChanged([a, b]));   // same again → no restart
        Assert.True(gate.HasChanged([a]));       // set shrank → restart
    }

    [Fact]
    public void SetComparisonIsOrderInsensitive()
    {
        var gate = new WatcherStartGate();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        gate.MarkStarted([a, b]);
        Assert.False(gate.HasChanged([b, a])); // same members, different order → no restart
    }

    [Fact]
    public void ChangeIsReportedAgainUntilMarkStarted()
    {
        // If a change couldn't be acted on (e.g. no active background-sync token so watchers didn't
        // start and MarkStarted wasn't called), the same change must still be reported next time.
        var gate = new WatcherStartGate();
        var a = Guid.NewGuid();
        Assert.True(gate.HasChanged([a]));
        Assert.True(gate.HasChanged([a])); // still changed — not swallowed
    }
}
