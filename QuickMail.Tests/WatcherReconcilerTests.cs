using System;
using System.Linq;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the fix for the regression of issue #126 ("adding a new account disconnects the others").
/// Reconcile must touch only the accounts that joined or left — an account that already has a live
/// IDLE watcher must never appear in ToStart, because restarting it drops its held connection and,
/// across all accounts at once, trips servers' per-IP connection limits.
/// </summary>
public class WatcherReconcilerTests
{
    [Fact]
    public void AddingAnAccountLeavesExistingWatchersUntouched()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // a is already watched; b just connected.
        var (toStart, toStop) = WatcherReconciler.Reconcile(currentlyWatching: [a], desired: [a, b]);

        Assert.Equal([b], toStart);   // only the new account starts
        Assert.Empty(toStop);         // the existing account is NOT restarted — the #126 guarantee
    }

    [Fact]
    public void RemovingAnAccountStopsOnlyThatWatcher()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var (toStart, toStop) = WatcherReconciler.Reconcile(currentlyWatching: [a, b], desired: [a]);

        Assert.Empty(toStart);
        Assert.Equal([b], toStop);    // only the departed account stops
    }

    [Fact]
    public void NoChangeStartsAndStopsNothing()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var (toStart, toStop) = WatcherReconciler.Reconcile(currentlyWatching: [a, b], desired: [a, b]);

        Assert.Empty(toStart);
        Assert.Empty(toStop);
    }

    [Fact]
    public void FirstStartStartsAllDesired()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var (toStart, toStop) = WatcherReconciler.Reconcile(currentlyWatching: [], desired: [a, b]);

        Assert.Equal(2, toStart.Count);
        Assert.Contains(a, toStart);
        Assert.Contains(b, toStart);
        Assert.Empty(toStop);
    }

    [Fact]
    public void SimultaneousJoinAndLeaveAreBothHandled()
    {
        var a = Guid.NewGuid();  // stays
        var b = Guid.NewGuid();  // leaves
        var c = Guid.NewGuid();  // joins

        var (toStart, toStop) = WatcherReconciler.Reconcile(currentlyWatching: [a, b], desired: [a, c]);

        Assert.Equal([c], toStart);
        Assert.Equal([b], toStop);   // a is left running
    }
}
