// The rule that decides when a focus-landing listener may be taken off — issue #637.
//
// This lived inline in MainWindow, and a deletion probe showed the whole mechanism could be removed
// with the suite still green: the view model's half was pinned, the window's half was not, and
// nothing joined them. It is a helper now so the rule itself can be stated.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class RebuildLandingTests
{
    [Fact]
    public async Task TheListenerIsArmedBeforeTheCommandRuns()
    {
        // A command that rebuilds immediately would otherwise land with nobody listening.
        var order = new List<string>();

        await RebuildLanding.RunAsync(
            arm: () => { order.Add("arm"); return () => order.Add("disarm"); },
            command: () => { order.Add("command"); return Task.CompletedTask; },
            settled: () => Task.CompletedTask);

        Assert.Equal(["arm", "command", "disarm"], order);
    }

    [Fact]
    public async Task TheListenerStaysOnUntilTheRebuildHasSettled()
    {
        // The defect this replaced: the command completing is not the rebuild landing. An
        // all-local draft delete has no network leg and returns without ever yielding, so
        // disarming when it returned tore the listener off before it could fire.
        var disarmed = false;
        var rebuild  = new TaskCompletionSource();

        var run = RebuildLanding.RunAsync(
            arm: () => () => disarmed = true,
            command: () => Task.CompletedTask,
            settled: () => rebuild.Task);

        Assert.False(run.IsCompleted);
        Assert.False(disarmed);

        rebuild.SetResult();
        await run;

        Assert.True(disarmed);
    }

    [Fact]
    public async Task ACommandThatThrows_StillTakesTheListenerOff()
    {
        // An armed listener left behind fires on the next unrelated rebuild -- a background sync
        // minutes later -- and drags keyboard focus into the tree from wherever the user has gone.
        var disarmed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RebuildLanding.RunAsync(
            arm: () => () => disarmed = true,
            command: () => throw new InvalidOperationException("boom"),
            settled: () => Task.CompletedTask));

        Assert.True(disarmed);
    }

    [Fact]
    public async Task ACommandThatRebuildsNothing_TakesTheListenerOffAtOnce()
    {
        // A move refused because the selection holds a draft that is not on the server, or a
        // delete the user answers No to. Nothing rebuilds, so the wait is already over.
        var disarmed = false;

        await RebuildLanding.RunAsync(
            arm: () => () => disarmed = true,
            command: () => Task.CompletedTask,
            settled: () => Task.CompletedTask);

        Assert.True(disarmed);
    }
}
