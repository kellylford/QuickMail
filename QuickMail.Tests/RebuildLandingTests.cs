// The rule that decides when a focus-landing listener may be taken off — issue #637.
//
// This lived inline in MainWindow, and a deletion probe showed the whole mechanism could be removed
// with the suite still green: the view model's half was pinned, the window's half was not, and
// nothing joined them. It is a helper now so the rule itself can be stated.

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
        //
        // The settled callback is the REAL one from the view model rather than a hard-coded
        // completed task: written the lazy way, this test passed while a refused command sat
        // waiting on whatever rebuild a background sync happened to have in flight, and the
        // listener it had armed fired on that -- focus dragged into the tree after a command the
        // user had just declined.
        var disarmed = false;
        var vm = LandingVm();
        var mark = vm.GroupRebuildMark();

        await RebuildLanding.RunAsync(
            arm: () => () => disarmed = true,
            command: () => Task.CompletedTask,
            settled: () => vm.GroupRebuildSettledSince(mark));

        Assert.True(disarmed);
    }

    [Fact]
    public async Task ARefusedCommand_DoesNotWaitOnSomebodyElsesRebuild()
    {
        // Measured behaviour before this: the wait returned the last rebuild scheduled by ANYONE,
        // so a command that rebuilt nothing still blocked on a background sync's rebuild and its
        // listener landed on it.
        var vm = LandingVm();
        vm.Messages.Add(new MailMessageSummary
        {
            MessageId = "1", AccountId = Guid.NewGuid(), FolderName = "INBOX", Subject = "One",
        });
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
        vm.Messages.Add(new MailMessageSummary
        {
            MessageId = "1", AccountId = Guid.NewGuid(), FolderName = "INBOX", Subject = "One",
        });
        vm.ViewMode = ViewMode.Conversations;

        Assert.NotSame(mark, vm.GroupRebuildMark());
    }

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
