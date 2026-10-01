// Quiet start at sign-in (#770): when the held-back focus and startup dialogs run. The orderings
// here are the bugs two independent reviews found — an activation during a slow load being
// missed, a notification click being treated as the moment to move focus, and then that focus move
// being postponed to whatever activation came next, at a moment the user did not choose.

using System;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class QuietStartGateTests
{
    [Fact]
    public void LoadThenActivation_RunsOnActivation()
    {
        var gate = new QuietStartGate();
        Assert.Null(gate.LoadCompleted(() => { }));
        Assert.NotNull(gate.Activated());
    }

    [Fact]
    public void ActivationDuringLoad_IsNotMissed_RunsWhenLoadCompletes()
    {
        // The user selected the taskbar button while a busy machine was still loading.
        var gate = new QuietStartGate();
        Assert.Null(gate.Activated());
        Assert.True(gate.HasBeenActivated);
        Assert.NotNull(gate.LoadCompleted(() => { }));
    }

    [Fact]
    public void WorkRunsExactlyOnce()
    {
        var gate = new QuietStartGate();
        var runs = 0;
        gate.LoadCompleted(() => runs++);
        gate.Activated()?.Invoke();
        gate.Activated()?.Invoke();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void HandsBackTheWorkLoadGave()
    {
        var gate = new QuietStartGate();
        var ran = -1;
        gate.LoadCompleted(() => ran = 7);
        gate.Activated()!.Invoke();
        Assert.Equal(7, ran);
    }

    [Fact]
    public void NotificationOpeningAMessage_AfterLoad_DropsTheWork_ForGood()
    {
        // Postponing it would move focus on some later, unrelated activation.
        var gate = new QuietStartGate();
        gate.LoadCompleted(() => { });

        gate.Supersede();

        Assert.Null(gate.Activated());   // the notification's own restore
        Assert.Null(gate.Activated());   // any later Alt+Tab or dialog close
    }

    [Fact]
    public void NotificationOpeningAMessage_DuringLoad_DropsTheWorkLoadHandsInLater()
    {
        var gate = new QuietStartGate();
        gate.Supersede();

        Assert.Null(gate.LoadCompleted(() => { }));
        Assert.Null(gate.Activated());
    }

    [Fact]
    public void NotificationAfterTheWorkRan_ChangesNothing()
    {
        var gate = new QuietStartGate();
        var runs = 0;
        gate.LoadCompleted(() => runs++);
        gate.Activated()!.Invoke();

        gate.Supersede();

        Assert.Equal(1, runs);
        Assert.Null(gate.Activated());
    }
}
