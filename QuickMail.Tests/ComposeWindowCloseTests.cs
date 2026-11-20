using System.Threading.Tasks;
using System.Windows;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #637: a compose window with unsaved changes used to refuse to close whenever the server
/// save failed — the user on airport wifi was left holding a window they could not close without
/// losing the message. The close decision now reads <see cref="ComposeViewModel.LastSaveOutcome"/>:
/// a draft kept on this computer lets the window close; only a draft that went nowhere keeps it open.
/// </summary>
[Collection("WpfTests")]
public class ComposeWindowCloseTests
{
    /// <summary>Fails like an unreachable server, after yielding like one: a real save never completes
    /// synchronously, and the close flow depends on that — its continuation runs after the Closing
    /// event has returned, so its own Close() is a fresh call rather than a re-entrant one WPF ignores.</summary>
    private sealed class YieldingUnreachableMail : StubImapMailServiceBase
    {
        public override async Task<string?> FindDraftsFolderNameAsync(System.Guid accountId, System.Threading.CancellationToken ct = default)
        {
            await Task.Yield();
            return "Drafts";
        }
        public override async Task<string> AppendDraftAsync(System.Guid accountId, ComposeModel draft, string? replaceMessageId, System.Threading.CancellationToken ct = default)
        {
            await Task.Yield();
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);
        }
    }

    /// <summary>Pumps the dispatcher until the queued continuations have run.</summary>
    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static (ComposeWindow Window, ComposeViewModel Vm, StubOutboxService Outbox) NewWindow(bool outboxAvailable)
    {
        // Without this the continuations after the yield land on the thread pool, where the
        // window's Close() is a cross-thread call swallowed by the async void handler.
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var mail = new YieldingUnreachableMail();
        var outbox = new StubOutboxService { IsAvailable = outboxAvailable };
        var vm = new ComposeViewModel(
            new StubSmtpService(),
            new StubAccountService(),
            new StubCredentialService(),
            mail,
            new StubTemplateService(),
            outbox: outbox);
        vm.SenderAccount = new AccountModel { Username = "kelly@example.com", AuthType = AuthType.OAuth2Google };
        vm.To = "someone@example.com";   // dirty, so the close prompt runs
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ConfirmSaveOnClose = () => Task.FromResult<bool?>(true),   // the user chose Save
        };
        return (window, vm, outbox);
    }

    [StaFact]
    public void ADraftKeptOnThisComputerLetsTheWindowClose()
    {
        var (window, vm, outbox) = NewWindow(outboxAvailable: true);
        var closed = 0;
        window.Closed += (_, _) => closed++;

        window.Close();
        DoEvents();

        Assert.Equal(DraftSaveOutcome.SavedLocally, vm.LastSaveOutcome);
        Assert.Single(outbox.Enqueued);
        Assert.Equal(1, closed);
    }

    [StaFact]
    public void ADraftThatWentNowhereKeepsTheWindowOpen()
    {
        var (window, vm, outbox) = NewWindow(outboxAvailable: false);
        var closed = 0;
        window.Closed += (_, _) => closed++;
        try
        {
            window.Close();
            DoEvents();

            Assert.Equal(DraftSaveOutcome.Failed, vm.LastSaveOutcome);
            Assert.Empty(outbox.Enqueued);
            Assert.Equal(0, closed);
        }
        finally
        {
            // Discard so the test leaves no zombie window holding the process open.
            window.ConfirmSaveOnClose = () => Task.FromResult<bool?>(false);
            window.Close();
            DoEvents();
        }
    }
}
