// Quiet start at sign-in (#770): when the held-back focus and startup dialogs run. The orderings
// here are the two bugs an independent review found in the first version — an activation during a
// slow load being missed, and a notification click being treated as the moment to show dialogs.

using System;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class QuietStartGateTests
{
    private static Action Work(Action<int> record, int id) => () => record(id);

    [Fact]
    public void LoadThenActivation_RunsOnActivation()
    {
        var gate = new QuietStartGate();
        Assert.Null(gate.LoadCompleted(() => { }));
        Assert.NotNull(gate.Activated(opensMessageFromNotification: false));
    }

    [Fact]
    public void ActivationDuringLoad_IsNotMissed_RunsWhenLoadCompletes()
    {
        // The user selected the taskbar button while a busy machine was still loading.
        var gate = new QuietStartGate();
        Assert.Null(gate.Activated(opensMessageFromNotification: false));
        Assert.True(gate.HasBeenActivated);
        Assert.NotNull(gate.LoadCompleted(() => { }));
    }

    [Fact]
    public void WorkRunsExactlyOnce()
    {
        var gate = new QuietStartGate();
        var runs = 0;
        gate.LoadCompleted(() => runs++);
        gate.Activated(false)?.Invoke();
        gate.Activated(false)?.Invoke();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void NotificationOpeningAMessage_DoesNotCount_TheNextActivationDoes()
    {
        var gate = new QuietStartGate();
        gate.LoadCompleted(() => { });

        Assert.Null(gate.Activated(opensMessageFromNotification: true));
        Assert.False(gate.HasBeenActivated);

        Assert.NotNull(gate.Activated(opensMessageFromNotification: false));
    }

    [Fact]
    public void NotificationDuringLoad_DoesNotCount_EitherWay()
    {
        var gate = new QuietStartGate();
        Assert.Null(gate.Activated(opensMessageFromNotification: true));
        Assert.Null(gate.LoadCompleted(() => { }));
        Assert.NotNull(gate.Activated(opensMessageFromNotification: false));
    }

    [Fact]
    public void HandsBackTheWorkLoadGave()
    {
        var gate = new QuietStartGate();
        var ran = -1;
        gate.LoadCompleted(Work(i => ran = i, 7));
        gate.Activated(false)!.Invoke();
        Assert.Equal(7, ran);
    }
}
