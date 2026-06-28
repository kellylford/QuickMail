using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AddAccountViewModelGateTests
{
    private static AddAccountViewModel Make(bool graphEnabled)
    {
        var gate = new StubFeatureGate { [FeatureFlag.GraphBackend] = graphEnabled };
        return new AddAccountViewModel(gate, new StubImapMailService(), new StubOAuthService());
    }

    [Fact]
    public void GateOff_OffersOnlyImap()
    {
        var vm = Make(graphEnabled: false);
        Assert.Single(vm.AvailableBackends);
        Assert.Equal(BackendKind.ImapSmtp, vm.AvailableBackends[0].Kind);
        Assert.False(vm.ShowBackendPicker);
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
        Assert.True(vm.IsImapBackend);
    }

    [Fact]
    public void GateOn_OffersBothBackends()
    {
        var vm = Make(graphEnabled: true);
        Assert.Equal(2, vm.AvailableBackends.Count);
        Assert.Contains(vm.AvailableBackends, b => b.Kind == BackendKind.MicrosoftGraph);
        Assert.True(vm.ShowBackendPicker);
        // Default selection is still IMAP.
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public void SelectingGraph_ForcesOAuthAndClearsImapFields()
    {
        var vm = Make(graphEnabled: true);
        vm.ImapHost = "imap.example.com";
        vm.SmtpHost = "smtp.example.com";

        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);

        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
        Assert.True(vm.IsGraphBackend);
        Assert.False(vm.IsImapBackend);
        Assert.Equal(AuthType.OAuth2Microsoft, vm.AuthType);
        Assert.Equal(string.Empty, vm.ImapHost);
        Assert.Equal(string.Empty, vm.SmtpHost);
    }

    [Fact]
    public void ToAccountModel_CarriesBackendKind()
    {
        var vm = Make(graphEnabled: true);
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.ToAccountModel().BackendKind);

        var imapVm = Make(graphEnabled: false);
        Assert.Equal(BackendKind.ImapSmtp, imapVm.ToAccountModel().BackendKind);
    }
}

public class MailServiceRouterTests
{
    /// <summary>Minimal recording backend: notes the last accountId routed to it and can raise the new-mail event.</summary>
    private sealed class RecordingMailService : IMailService
    {
        public Guid? LastAccountId { get; private set; }

        public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
        {
            LastAccountId = accountId;
            return Task.FromResult(new List<MailFolderModel>());
        }

        // Remaining members are unused by these tests.
        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
        public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
        public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task RoutesToRegisteredBackend()
    {
        var imap = new RecordingMailService();
        var graph = new RecordingMailService();
        var router = new MailServiceRouter(new IMailService[] { imap, graph });

        var graphAcct = Guid.NewGuid();
        router.RegisterAccount(graphAcct, graph);

        await router.GetFoldersAsync(graphAcct, TestContext.Current.CancellationToken);

        Assert.Equal(graphAcct, graph.LastAccountId);
        Assert.Null(imap.LastAccountId);
    }

    [Fact]
    public async Task UnregisteredAccount_FallsBackToFirstBackend()
    {
        var imap = new RecordingMailService();
        var graph = new RecordingMailService();
        var router = new MailServiceRouter(new IMailService[] { imap, graph }); // imap is the default

        var unknown = Guid.NewGuid();
        await router.GetFoldersAsync(unknown, TestContext.Current.CancellationToken);

        Assert.Equal(unknown, imap.LastAccountId);
        Assert.Null(graph.LastAccountId);
    }

    [Fact]
    public void EmptyBackends_Throws()
        => Assert.Throws<ArgumentException>(() => new MailServiceRouter(Array.Empty<IMailService>()));

}

/// <summary>Covers <see cref="ChangeNotifierRouter"/>: per-backend account partitioning and event forwarding.</summary>
public class ChangeNotifierRouterTests
{
    private sealed class RecordingChangeNotifier : IChangeNotifier
    {
        public IReadOnlyList<AccountModel>? LastWatchedAccounts { get; private set; }
        public bool StopWatchersCalled { get; private set; }
        public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) => LastWatchedAccounts = accounts;
        public void StopWatchers() => StopWatchersCalled = true;

        public event Action<Guid>? InboxNewMailDetected;
        public event Action<Guid, bool>? AccountReachabilityChanged;
        public void RaiseNewMail(Guid accountId) => InboxNewMailDetected?.Invoke(accountId);
        public void RaiseReachability(Guid accountId, bool reachable) => AccountReachabilityChanged?.Invoke(accountId, reachable);
    }

    [Fact]
    public void StartWatchers_FansFullListToEveryNotifier()
    {
        var imap = new RecordingChangeNotifier();
        var graph = new RecordingChangeNotifier();
        var router = new ChangeNotifierRouter(new IChangeNotifier[] { imap, graph });

        var imapAcct  = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp };
        var graphAcct = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph };

        router.StartWatchers(new[] { imapAcct, graphAcct }, TestContext.Current.CancellationToken);

        // The router fans the full list to every notifier; each notifier filters to its own accounts.
        Assert.Equal(new[] { imapAcct.Id, graphAcct.Id }, imap.LastWatchedAccounts!.Select(a => a.Id));
        Assert.Equal(new[] { imapAcct.Id, graphAcct.Id }, graph.LastWatchedAccounts!.Select(a => a.Id));
    }

    [Fact]
    public void ForwardsInboxNewMailFromAnyNotifier()
    {
        var imap = new RecordingChangeNotifier();
        var graph = new RecordingChangeNotifier();
        var router = new ChangeNotifierRouter(new IChangeNotifier[] { imap, graph });

        var seen = new List<Guid>();
        router.InboxNewMailDetected += seen.Add;

        var fromImap = Guid.NewGuid();
        var fromGraph = Guid.NewGuid();
        imap.RaiseNewMail(fromImap);
        graph.RaiseNewMail(fromGraph);

        Assert.Equal(new[] { fromImap, fromGraph }, seen);
    }

    [Fact]
    public void ForwardsReachabilityFromAnyNotifier()
    {
        var imap = new RecordingChangeNotifier();
        var graph = new RecordingChangeNotifier();
        var router = new ChangeNotifierRouter(new IChangeNotifier[] { imap, graph });

        var seen = new List<(Guid Id, bool Reachable)>();
        router.AccountReachabilityChanged += (id, r) => seen.Add((id, r));

        var acct = Guid.NewGuid();
        imap.RaiseReachability(acct, false);

        Assert.Equal(new[] { (acct, false) }, seen);
    }

    [Fact]
    public void Dispose_StopsForwarding()
    {
        var imap = new RecordingChangeNotifier();
        var router = new ChangeNotifierRouter(new IChangeNotifier[] { imap });

        var seen = new List<Guid>();
        router.InboxNewMailDetected += seen.Add;
        router.Dispose();

        imap.RaiseNewMail(Guid.NewGuid());

        Assert.Empty(seen);
    }

    [Fact]
    public void Dispose_CallsStopWatchersOnEveryNotifier()
    {
        var imap = new RecordingChangeNotifier();
        var graph = new RecordingChangeNotifier();
        var router = new ChangeNotifierRouter(new IChangeNotifier[] { imap, graph });

        router.Dispose();

        // Disposing the router must tear down every notifier's watcher tasks, not just unsubscribe.
        Assert.True(imap.StopWatchersCalled);
        Assert.True(graph.StopWatchersCalled);
    }
}

public class ConfigFeatureGateTests
{
    private static ConfigModel ConfigWith(string key, string value)
    {
        var c = new ConfigModel();
        c.Features[key] = value;
        return c;
    }

    [Fact]
    public void Default_GraphBackendOff()
        => Assert.False(new ConfigFeatureGate(new ConfigModel(), Array.Empty<string>())
            .IsEnabled(FeatureFlag.GraphBackend));

    [Fact]
    public void Config_EnablesFlag()
        => Assert.True(new ConfigFeatureGate(ConfigWith("GraphBackend", "true"), Array.Empty<string>())
            .IsEnabled(FeatureFlag.GraphBackend));

    [Fact]
    public void Config_IsCaseInsensitive()
        => Assert.True(new ConfigFeatureGate(ConfigWith("graphbackend", "true"), Array.Empty<string>())
            .IsEnabled(FeatureFlag.GraphBackend));

    [Fact]
    public void CliEnable_OverridesDefault()
        => Assert.True(new ConfigFeatureGate(new ConfigModel(), new[] { "GraphBackend" })
            .IsEnabled(FeatureFlag.GraphBackend));

    [Fact]
    public void CliDisable_OverridesConfigEnabled()
        => Assert.False(new ConfigFeatureGate(ConfigWith("GraphBackend", "true"), Array.Empty<string>(), new[] { "GraphBackend" })
            .IsEnabled(FeatureFlag.GraphBackend));

    [Fact]
    public void CliDisable_WinsOverCliEnable()
        => Assert.False(new ConfigFeatureGate(new ConfigModel(), new[] { "GraphBackend" }, new[] { "GraphBackend" })
            .IsEnabled(FeatureFlag.GraphBackend));
}
