// The rule that decides when a focus-landing listener may be taken off — issue #637.
//
// This lived inline in MainWindow, and a deletion probe showed the whole mechanism could be removed
// with the suite still green: the view model's half was pinned, the window's half was not, and
// nothing joined them. It is a helper now so the rule itself can be stated — including the taking of
// the mark, which a later probe showed was the half still living untested in the window.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class RebuildLandingTests
{
    private static readonly object Mark = new();

    private static Task RunAsync(Func<Action> arm, Func<Task> command,
                                 Func<object, Task>? settledSince = null,
                                 Func<object>? mark = null)
        => RebuildLanding.RunAsync(mark ?? (() => Mark),
                                   settledSince ?? (_ => Task.CompletedTask), arm, command);

    [Fact]
    public async Task TheMarkIsTakenBeforeTheListenerIsArmed()
    {
        // Half the rule, and the half that was still in the window when a probe found the wiring
        // uncovered. A mark taken after arming describes a moment the listener was already live
        // for, so a rebuild in that gap counts as "somebody else's" when it may be the command's.
        var order = new List<string>();

        await RebuildLanding.RunAsync(
            mark: () => { order.Add("mark"); return Mark; },
            settledSince: _ => { order.Add("wait"); return Task.CompletedTask; },
            arm: () => { order.Add("arm"); return () => order.Add("disarm"); },
            command: () => { order.Add("command"); return Task.CompletedTask; });

        Assert.Equal(["mark", "arm", "command", "wait", "disarm"], order);
    }

    [Fact]
    public async Task TheWaitIsAskedAboutTheMarkThatWasTaken()
    {
        // Not about "whatever is in flight now": that is what let a refused command block on a
        // background sync's rebuild and its listener land on it.
        object? askedAbout = null;
        var taken = new object();

        await RebuildLanding.RunAsync(
            mark: () => taken,
            settledSince: m => { askedAbout = m; return Task.CompletedTask; },
            arm: () => () => { },
            command: () => Task.CompletedTask);

        Assert.Same(taken, askedAbout);
    }

    [Fact]
    public async Task TheListenerStaysOnUntilTheRebuildHasSettled()
    {
        // The command completing is not the rebuild landing. An all-local draft delete has no
        // network leg and returns without ever yielding, so disarming when it returned tore the
        // listener off before it could fire.
        var disarmed = false;
        var rebuild  = new TaskCompletionSource();

        var run = RunAsync(
            arm: () => () => disarmed = true,
            command: () => Task.CompletedTask,
            settledSince: _ => rebuild.Task);

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            arm: () => () => disarmed = true,
            command: () => throw new InvalidOperationException("boom")));

        Assert.True(disarmed);
    }

    [Fact]
    public async Task ACommandThatRebuildsNothing_TakesTheListenerOffAtOnce()
    {
        // A move refused because the selection holds a draft that is not on the server, or a
        // delete the user answers No to.
        //
        // The wait is the REAL one from the view model rather than a hard-coded completed task:
        // written the lazy way, this test passed while a refused command sat waiting on whatever
        // rebuild a background sync happened to have in flight.
        var disarmed = false;
        var vm = LandingVm();

        await RebuildLanding.RunAsync(
            vm.GroupRebuildMark, vm.GroupRebuildSettledSince,
            arm: () => () => disarmed = true,
            command: () => Task.CompletedTask);

        Assert.True(disarmed);
    }

    [Fact]
    public void ARefusedCommand_DoesNotWaitOnSomebodyElsesRebuild()
    {
        // Measured behaviour before this: the wait returned the last rebuild scheduled by ANYONE.
        var vm = LandingVm();
        vm.Messages.Add(Row());
        vm.ViewMode = ViewMode.Conversations;          // somebody else's rebuild, still in flight

        var mark = vm.GroupRebuildMark();              // taken AFTER it was scheduled

        Assert.True(vm.GroupRebuildSettledSince(mark).IsCompleted);
    }

    [Fact]
    public void ARebuildScheduledAfterTheMark_IsStillWaitedFor()
    {
        // The other half: the mark must not make the wait useless.
        var vm = LandingVm();
        var mark = vm.GroupRebuildMark();
        vm.Messages.Add(Row());
        vm.ViewMode = ViewMode.Conversations;

        Assert.False(vm.GroupRebuildSettledSince(mark).IsCompleted);
    }

    private static MailMessageSummary Row() => new()
    {
        MessageId = "1", AccountId = Guid.NewGuid(), FolderName = "INBOX", Subject = "One",
    };

    private static MainViewModel LandingVm() => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService(), uiDispatcher: new HoldsEverything());

    /// <summary>Never runs what it is given, so a scheduled rebuild stays in flight.</summary>
    private sealed class HoldsEverything : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) { }
    }
}
