using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Reconnect Account (#615): one disconnected account, reconnected on request. Startup and the
/// automatic reconnects never show a sign-in window (#206), so this command is the one place a
/// lapsed sign-in is put right without restarting — and the one place that may show that window.
/// A shared mailbox reads through its parent's sign-in, so reconnecting it signs the parent in and
/// connects the parent first.
/// </summary>
public class ReconnectAccountTests
{
    /// <summary>Records which accounts were connected, in order.</summary>
    private sealed class RecordingMailService : StubImapMailServiceBase
    {
        public List<Guid> Connected { get; } = [];
        /// <summary>When set, ConnectAsync records the attempt and then throws it.</summary>
        public Exception? ThrowOnConnect { get; set; }
        public int Attempts { get; private set; }
        public override Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
        {
            Attempts++;
            if (ThrowOnConnect is { } ex) return Task.FromException(ex);
            Connected.Add(account.Id);
            return Task.CompletedTask;
        }
    }

    private static (MainViewModel vm, RecordingMailService mail, StubOAuthService oauth, StubCommandRegistry registry) MakeVm()
    {
        var mail     = new RecordingMailService();
        var oauth    = new StubOAuthService();
        var registry = new StubCommandRegistry();
        var vm = new MainViewModel(mail, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), oauth, new StubSyncService(),
            new StubConfigService(), registry, new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        return (vm, mail, oauth, registry);
    }

    private static AccountModel Microsoft(string name, string username) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = username,
        AuthType = AuthType.OAuth2Microsoft, BackendKind = BackendKind.MicrosoftGraph,
    };

    private static AccountModel Shared(AccountModel parent, string name) => new()
    {
        // As AddSharedMailboxViewModel builds one: its own address, the parent's backend.
        Id = Guid.NewGuid(), AccountName = name, Username = "support@example.com",
        AuthType = AuthType.OAuth2Microsoft, BackendKind = parent.BackendKind,
        IsShared = true, ParentAccountId = parent.Id, SharedAddress = "support@example.com",
    };

    [Fact]
    public async Task NoAccountSelected_SaysSo()
    {
        var (vm, mail, _, _) = MakeVm();

        await vm.ReconnectAccountCommand.ExecuteAsync(null);

        Assert.Equal("Select an account to reconnect.", vm.StatusText);
        Assert.Empty(mail.Connected);
    }

    [Fact]
    public async Task ConnectedAccount_IsLeftAlone()
    {
        var (vm, mail, _, _) = MakeVm();
        var work = Microsoft("Work", "me@example.com");
        work.IsConnected = true;
        vm.Accounts.Add(work);

        await vm.ReconnectAccountCommand.ExecuteAsync(work);

        Assert.Equal("Work is already connected.", vm.StatusText);
        Assert.Empty(mail.Connected);
    }

    [Fact]
    public async Task SignInStillValid_ConnectsWithoutASignInWindow()
    {
        var (vm, mail, oauth, _) = MakeVm();
        var work = Microsoft("Work", "me@example.com");
        vm.Accounts.Add(work);

        await vm.ReconnectAccountCommand.ExecuteAsync(work);

        Assert.True(work.IsConnected);
        Assert.Equal([work.Id], mail.Connected);
        Assert.Empty(oauth.InteractiveSignIns);
        Assert.Equal("Work reconnected.", vm.StatusText);
    }

    [Fact]
    public async Task SignInLapsed_ShowsTheSignInWindow_ThenConnects()
    {
        var (vm, mail, oauth, _) = MakeVm();
        var work = Microsoft("Work", "me@example.com");
        vm.Accounts.Add(work);
        oauth.ThrowOnEnsureSilent = new InteractiveSignInRequiredException("lapsed");
        oauth.SignInUsername = "me@example.com";

        await vm.ReconnectAccountCommand.ExecuteAsync(work);

        Assert.Equal([work], oauth.InteractiveSignIns);
        Assert.True(work.IsConnected);
        Assert.Equal([work.Id], mail.Connected);
    }

    [Fact]
    public async Task SignedInAsSomeoneElse_DoesNotConnect()
    {
        // #202: an admin signing in to approve consent must not have their sign-in stand in for the
        // account's own.
        var (vm, mail, oauth, _) = MakeVm();
        var work = Microsoft("Work", "me@example.com");
        vm.Accounts.Add(work);
        oauth.ThrowOnEnsureSilent = new InteractiveSignInRequiredException("lapsed");
        oauth.SignInUsername = "admin@example.com";

        await vm.ReconnectAccountCommand.ExecuteAsync(work);

        Assert.False(work.IsConnected);
        Assert.Equal(0, mail.Attempts);
        Assert.Equal(1, oauth.SilentChecks);   // the connect's own silent check never ran: the guard stopped it
        Assert.StartsWith("Signed in as admin@example.com, not me@example.com.", vm.StatusText);
    }

    [Fact]
    public async Task SharedMailbox_SignsInAndConnectsTheParentFirst()
    {
        var (vm, mail, oauth, _) = MakeVm();
        var parent = Microsoft("Work", "me@example.com");
        var shared = Shared(parent, "Support");
        vm.Accounts.Add(parent);
        vm.Accounts.Add(shared);
        oauth.ThrowOnEnsureSilent = new InteractiveSignInRequiredException("lapsed");
        oauth.SignInUsername = "me@example.com";

        await vm.ReconnectAccountCommand.ExecuteAsync(shared);

        Assert.Equal([parent], oauth.InteractiveSignIns);   // the parent's sign-in, not the shared mailbox's
        Assert.Equal([parent.Id, shared.Id], mail.Connected);
        Assert.True(parent.IsConnected);
        Assert.True(shared.IsConnected);
        Assert.Equal("Support reconnected.", vm.StatusText);
    }

    [Fact]
    public async Task SharedMailbox_ParentAlreadyConnected_ConnectsOnlyTheSharedMailbox()
    {
        var (vm, mail, _, _) = MakeVm();
        var parent = Microsoft("Work", "me@example.com");
        parent.IsConnected = true;
        var shared = Shared(parent, "Support");
        vm.Accounts.Add(parent);
        vm.Accounts.Add(shared);

        await vm.ReconnectAccountCommand.ExecuteAsync(shared);

        Assert.Equal([shared.Id], mail.Connected);
        Assert.True(shared.IsConnected);
    }

    [Fact]
    public async Task SharedMailbox_ParentGone_SaysSo()
    {
        var (vm, mail, _, _) = MakeVm();
        var orphan = Shared(Microsoft("Removed", "gone@example.com"), "Support");
        vm.Accounts.Add(orphan);

        await vm.ReconnectAccountCommand.ExecuteAsync(orphan);

        Assert.Empty(mail.Connected);
        Assert.Equal("Could not reconnect Support: the account it belongs to is no longer in QuickMail.", vm.StatusText);
    }

    [Fact]
    public async Task Google_SignInLapsed_ShowsTheSignInWindow()
    {
        // The router's EnsureSilentTokenAsync is a no-op for Google, so a lapsed Google sign-in has to
        // be found by asking for a token; Google reports it as an ordinary exception.
        var (vm, mail, oauth, _) = MakeVm();
        var gmail = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Gmail", Username = "me@gmail.com",
            AuthType = AuthType.OAuth2Google, BackendKind = BackendKind.ImapSmtp,
        };
        vm.Accounts.Add(gmail);
        oauth.ThrowOnEnsureSilent = new InvalidOperationException("No Google credentials stored for me@gmail.com.");
        oauth.SignInUsername = "me@gmail.com";

        await vm.ReconnectAccountCommand.ExecuteAsync(gmail);

        Assert.Equal([gmail], oauth.InteractiveSignIns);
        Assert.True(gmail.IsConnected);
    }

    [Fact]
    public async Task PasswordAccount_WithNoSavedPassword_SaysWhereToEnterIt()
    {
        var (vm, mail, _, _) = MakeVm();
        var home = new AccountModel { Id = Guid.NewGuid(), AccountName = "Home", Username = "me@home.example", AuthType = AuthType.Password };
        vm.Accounts.Add(home);

        await vm.ReconnectAccountCommand.ExecuteAsync(home);

        Assert.Equal(0, mail.Attempts);
        Assert.Equal("Could not reconnect Home. It has no saved password. Enter it in Manage Accounts.", vm.StatusText);
    }

    [Fact]
    public async Task ServerUnreachable_TriesOnce_AndSaysSo()
    {
        // Startup retries three times with backoff; a reconnect the user asked for answers at once.
        var (vm, mail, _, _) = MakeVm();
        var work = Microsoft("Work", "me@example.com");
        vm.Accounts.Add(work);
        mail.ThrowOnConnect = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);

        await vm.ReconnectAccountCommand.ExecuteAsync(work);

        Assert.Equal(1, mail.Attempts);
        Assert.False(work.IsConnected);
        Assert.Equal("Could not reconnect Work. The server could not be reached.", vm.StatusText);
    }

    [Fact]
    public async Task SharedMailbox_WithAnImapParent_IsNotConnected()
    {
        // Nothing else connects these: a shared mailbox is only opened through Graph.
        var (vm, mail, _, _) = MakeVm();
        var parent = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Work", Username = "me@example.com",
            AuthType = AuthType.OAuth2Microsoft, BackendKind = BackendKind.ImapSmtp,
        };
        var shared = Shared(parent, "Support");
        vm.Accounts.Add(parent);
        vm.Accounts.Add(shared);

        await vm.ReconnectAccountCommand.ExecuteAsync(shared);

        Assert.Equal(0, mail.Attempts);
        Assert.StartsWith("Could not reconnect Support: a shared mailbox can only be opened", vm.StatusText);
    }

    [Fact]
    public async Task NoParameter_UsesTheSelectedAccount()
    {
        // The Command Palette passes nothing; it acts on the account selected in the account list.
        var (vm, mail, _, _) = MakeVm();
        var work = Microsoft("Work", "me@example.com");
        vm.Accounts.Add(work);
        vm.SelectedAccount = work;

        await vm.ReconnectAccountCommand.ExecuteAsync(null);

        Assert.Equal([work.Id], mail.Connected);
    }

    [Fact]
    public void Registered_InAccountCategory_WithNoDefaultKey()
    {
        var (_, _, _, registry) = MakeVm();
        var cmd = registry.FindById("account.reconnect");

        Assert.NotNull(cmd);
        Assert.Equal("Account", cmd!.Category);
        Assert.Equal("Reconnect Account", cmd.Title);
        Assert.Equal(Key.None, cmd.DefaultKey);
        // Always available, so a key the user binds to it always answers (see ConnectedAccount_IsLeftAlone).
        Assert.Null(cmd.IsAvailable);
    }
}
