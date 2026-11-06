using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MailKit.Net.Smtp;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>Records what it's asked to play. The real gating is in SoundServiceTests.</summary>
internal sealed class RecordingSoundService : ISoundService
{
    public List<SoundEvent> Played { get; } = [];
    public List<string> Previewed { get; } = [];
    public HashSet<SoundEvent> Audible { get; } = [];
    public List<string> CustomFiles { get; } = [];
    public string? RefuseWith { get; set; }

    public List<bool> Background { get; } = [];
    public bool SilencesToasts { get; set; }
    public int Stops { get; private set; }
    public int? PreviewVolume { get; private set; }

    public void Play(SoundEvent soundEvent, bool background = false)
    {
        Played.Add(soundEvent);
        Background.Add(background);
    }

    public void StopAll() => Stops++;
    public List<string?> RuleSounds { get; } = [];
    public HashSet<string> RuleSoundedKeys { get; } = [];

    public void PlayRule(string? storedSound, IEnumerable<string>? messageKeys = null)
    {
        RuleSounds.Add(storedSound);
        RuleSoundedKeys.UnionWith(messageKeys ?? []);
    }

    public bool WasSoundedByRule(string messageKey) => RuleSoundedKeys.Contains(messageKey);
    public string? PreviewPack { get; private set; }

    public List<string?> RulePreviewed { get; } = [];

    public void PreviewRule(string? storedSound) => RulePreviewed.Add(storedSound);

    public void Preview(string storedSound, int? volumePercent = null, string? pack = null)
    {
        Previewed.Add(storedSound);
        PreviewVolume = volumePercent;
        PreviewPack = pack;
    }
    public bool IsAudible(SoundEvent soundEvent) => Audible.Contains(soundEvent);
    public IReadOnlyList<string> CustomSoundFiles() => CustomFiles;

    public string? CustomSoundPath(string fileName) =>
        CustomFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase) ? Path.Combine(Path.GetTempPath(), fileName) : null;

    public List<string> Removed { get; } = [];
    public string? RemoveRefusal { get; set; }

    public bool TryRemoveCustomSound(string fileName, out string refusal)
    {
        refusal = RemoveRefusal ?? string.Empty;
        if (RemoveRefusal is not null) return false;
        Removed.Add(fileName);
        CustomFiles.RemoveAll(f => f.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public bool TryAddCustomSound(string sourcePath, out string fileName, out string refusal)
    {
        fileName = RefuseWith is null ? Path.GetFileName(sourcePath) : string.Empty;
        refusal = RefuseWith ?? string.Empty;
        return RefuseWith is null;
    }

    public void Dispose() { }
}

public class SoundEventsTests
{
    private static readonly Guid Work = Guid.Parse("dddddddd-1111-1111-1111-111111111111");

    // ── Sending ─────────────────────────────────────────────────────────────────

    private static (ComposeViewModel Vm, StubSmtpService Smtp, RecordingSoundService Sounds) MakeCompose()
    {
        var smtp = new StubSmtpService();
        var sounds = new RecordingSoundService();
        var vm = new ComposeViewModel(smtp, new StubAccountService(), new StubCredentialService(),
            new StubImapMailService(), new StubTemplateService(),
            outbox: new StubOutboxService(), connectivity: new StubConnectivityService(), sounds: sounds)
        {
            SenderAccount = new AccountModel { Id = Work, Username = "kelly@example.com", AuthType = AuthType.OAuth2Google },
            To = "someone@example.com",
            Subject = "Lunch",
            Body = "Friday?",
        };
        return (vm, smtp, sounds);
    }

    [Fact]
    public async Task ASentMessagePlaysTheSentSoundOnly()
    {
        var (vm, _, sounds) = MakeCompose();
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.MessageSent], sounds.Played);
    }

    [Fact]
    public async Task AMessageThatCouldNotReachTheServerPlaysQueuedNotSent()
    {
        var (vm, smtp, sounds) = MakeCompose();
        smtp.SendFailure = new SocketException((int)SocketError.HostUnreachable);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.MessageQueued], sounds.Played);
    }

    [Fact]
    public async Task ARefusedMessagePlaysFailed()
    {
        var (vm, smtp, sounds) = MakeCompose();
        smtp.SendFailure = new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 no such user");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.SendFailed], sounds.Played);
    }

    [Fact]
    public async Task AMessageMissingARecipientPlaysNothing()
    {
        var (vm, _, sounds) = MakeCompose();
        vm.To = string.Empty;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(sounds.Played);
    }

    // ── Arriving mail ───────────────────────────────────────────────────────────

    private static MailMessageSummary Msg(string id, string subject = "Hello", bool read = false, int daysAgo = 0) => new()
    {
        MessageId = id, AccountId = Work, FolderName = "INBOX", From = "someone@example.com",
        Subject = subject, Date = DateTimeOffset.Now.AddDays(-daysAgo), IsRead = read,
    };

    private static AccountModel Account(bool shared = false, bool optIn = false) => new()
    {
        Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft,
        IsShared = shared, NotifyOnNewMail = optIn,
    };

    private sealed class Mail
    {
        public RecordingSoundService Sounds { get; } = new();
        public StubWatchService Watch { get; } = new();
        public ToastRecorder Toasts { get; } = new();
        public StubConfigService Config { get; } = new();
        public StubOutboxService Outbox { get; } = new();
        public StubConnectivityService Connectivity { get; } = new();
        public MainViewModel Vm { get; }

        public Mail(bool withSounds = true) =>
            Vm = new MainViewModel(
                new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
                new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), Config,
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
                notificationService: Toasts, watchService: Watch, outboxService: Outbox, connectivity: Connectivity,
                soundService: withSounds ? Sounds : null);
    }

    private sealed class ToastRecorder : INotificationService
    {
        public List<(string Kind, bool Silent)> Shown { get; } = [];
        public bool IsSupported => true;
#pragma warning disable CS0067 // never raised by this fake
        public event Action<NotificationActivation>? Activated;
#pragma warning restore CS0067
        public void ShowNewMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages, bool silent = false) => Shown.Add(("new", silent));
        public void ShowWatchedMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages, bool silent = false) => Shown.Add(("watched", silent));
        public void ShowInfo(string title, string message, bool silent = false) => Shown.Add(("info", silent));
    }

    [Fact]
    public void NewInboxMailPlaysNewMailOnce()
    {
        var m = new Mail();
        var incoming = new[] { Msg("1") };
        m.Vm.MaybePlayMailSounds(Account(), incoming, isInbox: true);
        m.Vm.MaybePlayMailSounds(Account(), incoming, isInbox: true);
        Assert.Equal([SoundEvent.NewMail], m.Sounds.Played);
    }

    [Fact]
    public void AWatchedReplyPlaysTheWatchedSoundInAnyFolder()
    {
        var m = MailWithAudible(SoundEvent.WatchedMail);
        m.Vm.MaybePlayMailSounds(Account(), [Msg("1", "Re: Budget Review")], isInbox: false);
        Assert.Equal([SoundEvent.WatchedMail], m.Sounds.Played);
    }

    [Fact]
    public void OrdinaryMailOutsideTheInboxPlaysNothing()
    {
        var m = new Mail();
        m.Vm.MaybePlayMailSounds(Account(), [Msg("1")], isInbox: false);
        Assert.Empty(m.Sounds.Played);
    }

    [Fact]
    public void MailElsewhereDoesNotUseUpTheSameIdInTheInbox()
    {
        // IMAP ids are per folder, so the key set must not be fed mail that won't sound.
        var m = new Mail();
        m.Vm.MaybePlayMailSounds(Account(), [Msg("5")], isInbox: false);
        m.Vm.MaybePlayMailSounds(Account(), [Msg("5")], isInbox: true);
        Assert.Equal([SoundEvent.NewMail], m.Sounds.Played);
    }

    [Theory]
    [InlineData(true, 0, false)]    // Already read.
    [InlineData(false, 3, false)]   // From before this launch.
    [InlineData(false, 0, true)]
    public void OnlyMailThatIsNewCounts(bool read, int daysAgo, bool sounds)
    {
        var m = new Mail();
        m.Vm.MaybePlayMailSounds(Account(), [Msg("1", read: read, daysAgo: daysAgo)], isInbox: true);
        Assert.Equal(sounds, m.Sounds.Played.Count == 1);
    }

    [Fact]
    public void ACatchUpBacklogIsSilentAndDoesNotSoundLater()
    {
        var m = new Mail();
        var backlog = Enumerable.Range(1, 6).Select(i => Msg(i.ToString())).ToArray();
        m.Vm.MaybePlayMailSounds(Account(), backlog, isInbox: true);
        m.Vm.MaybePlayMailSounds(Account(), backlog, isInbox: true);
        Assert.Empty(m.Sounds.Played);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void ASharedMailboxNeedsItsOptIn(bool sharedWithoutOptIn, int expected)
    {
        var m = new Mail();
        m.Vm.MaybePlayMailSounds(Account(shared: sharedWithoutOptIn), [Msg("1")], isInbox: true);
        Assert.Equal(expected, m.Sounds.Played.Count);
    }

    [Fact]
    public void ASharedMailboxThatOptedInPlays()
    {
        var m = new Mail();
        m.Vm.MaybePlayMailSounds(Account(shared: true, optIn: true), [Msg("1")], isInbox: true);
        Assert.Equal([SoundEvent.NewMail], m.Sounds.Played);
    }

    [Fact]
    public void MailARuleSoundedForDoesNotAlsoPlayNewMail()
    {
        var m = new Mail();
        m.Sounds.RuleSoundedKeys.Add(SoundCatalog.MessageKey(Work, "1"));
        m.Vm.MaybePlayMailSounds(Account(), [Msg("1")], isInbox: true);
        Assert.Empty(m.Sounds.Played);
    }

    [Fact]
    public void OtherMailInTheSameBatchStillPlaysNewMail()
    {
        var m = new Mail();
        m.Sounds.RuleSoundedKeys.Add(SoundCatalog.MessageKey(Work, "1"));
        m.Vm.MaybePlayMailSounds(Account(), [Msg("1"), Msg("2")], isInbox: true);
        Assert.Equal([SoundEvent.NewMail], m.Sounds.Played);
    }

    [Fact]
    public void ClosingSettingsWithoutAPreviewLeavesOtherSoundsAlone()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);

        vm.StopSounds();
        Assert.Equal(0, sounds.Stops);

        vm.PreviewSoundCommand.Execute(null);
        vm.StopSounds();
        Assert.Equal(1, sounds.Stops);
    }

    [Fact]
    public void SoundsDoNotUseUpAToast()
    {
        // The whole reason for the second key set: the toast still fires after the sound has seen the mail.
        var m = new Mail();
        m.Config.Load().NotifyOnNewMail = true;
        var incoming = new[] { Msg("1") };
        m.Vm.MaybePlayMailSounds(Account(), incoming, isInbox: true);
        m.Vm.MaybeNotifyNewMail(Account(), incoming);
        Assert.Equal("new", Assert.Single(m.Toasts.Shown).Kind);
    }

    [Fact]
    public void SoundsPlayWithToastsTurnedOff()
    {
        var m = new Mail();
        m.Config.Load().NotifyOnNewMail = false;
        m.Vm.MaybePlayMailSounds(Account(), [Msg("1")], isInbox: true);
        Assert.Equal([SoundEvent.NewMail], m.Sounds.Played);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryToastFollowsTheSilentSetting(bool silent)
    {
        var m = new Mail();
        m.Config.Load().NotifyOnNewMail = true;
        m.Config.Load().NotifyOnWatchedConversation = true;
        m.Sounds.SilencesToasts = silent;
        m.Watch.Watch("Budget Review");

        var watched = new[] { Msg("1", "Re: Budget Review") };
        m.Vm.MaybeNotifyWatchedMail(Account(), watched);
        m.Vm.MaybeNotifyNewMail(Account(), [Msg("2")]);

        Assert.Equal([("watched", silent), ("new", silent)], m.Toasts.Shown);
    }

    /// <summary>The sound and both toasts on one batch, with what's audible as the settings would make it.</summary>
    private static Mail MailWithAudible(params SoundEvent[] audible)
    {
        var m = new Mail();
        foreach (var e in audible) m.Sounds.Audible.Add(e);
        m.Watch.Watch("Budget Review");
        return m;
    }

    private static MailMessageSummary[] Batch(int watched, int others) =>
    [
        .. Enumerable.Range(1, watched).Select(i => Msg($"w{i}", "Re: Budget Review")),
        .. Enumerable.Range(1, others).Select(i => Msg($"o{i}")),
    ];

    [Fact]
    public void AWatchedReplyIsHeardEvenInABacklogOfOrdinaryMail()
    {
        // 2 watched and 5 others: the toast paths see 2 and 5 and show both, so a sound has to play.
        var m = MailWithAudible(SoundEvent.WatchedMail, SoundEvent.NewMail);
        m.Vm.MaybePlayMailSounds(Account(), Batch(2, 5), isInbox: true);
        Assert.Equal([SoundEvent.WatchedMail], m.Sounds.Played);
    }

    [Fact]
    public void WithTheWatchedSoundOffABatchStillSoundsAsNewMail()
    {
        var m = MailWithAudible(SoundEvent.NewMail);
        m.Vm.MaybePlayMailSounds(Account(), Batch(1, 1), isInbox: true);
        Assert.Equal([SoundEvent.NewMail], m.Sounds.Played);
    }

    [Fact]
    public void AWatchedBacklogOverTheLimitIsSilent()
    {
        var m = MailWithAudible(SoundEvent.WatchedMail, SoundEvent.NewMail);
        m.Vm.MaybePlayMailSounds(Account(), Batch(6, 0), isInbox: true);
        Assert.Empty(m.Sounds.Played);
    }

    [Fact]
    public void WithTheWatchedSoundOffAWatchedReplyElsewhereStaysSilent()
    {
        var m = MailWithAudible(SoundEvent.NewMail);
        m.Vm.MaybePlayMailSounds(Account(), Batch(1, 0), isInbox: false);
        Assert.Empty(m.Sounds.Played);
    }

    [Fact]
    public void WithoutASoundServiceAToastKeepsItsOwnSound()
    {
        var m = new Mail(withSounds: false);
        m.Config.Load().NotifyOnNewMail = true;
        m.Vm.MaybeNotifyNewMail(Account(), [Msg("1")]);
        Assert.False(Assert.Single(m.Toasts.Shown).Silent);
    }

    // ── The Outbox and the connection ───────────────────────────────────────────

    [Theory]
    [InlineData(2, 0, new[] { SoundEvent.MessageSent })]
    [InlineData(0, 1, new[] { SoundEvent.SendFailed })]
    [InlineData(1, 1, new[] { SoundEvent.MessageSent, SoundEvent.SendFailed })]
    [InlineData(0, 0, new SoundEvent[0])]
    public void AnOutboxDrainPlaysEachOutcomeOnceHoweverManyItCarried(int sent, int failed, SoundEvent[] expected)
    {
        var m = new Mail();
        m.Outbox.RaiseFlushCompleted(new OutboxFlushResult(sent, 1, failed, 0, FailedSends: failed));
        Assert.Equal(expected, m.Sounds.Played);
        Assert.All(m.Sounds.Background, Assert.True);   // Nobody just did anything: it went out on its own.
    }

    [Fact]
    public void ADraftThatFailedToUploadIsNotASendFailure()
    {
        var m = new Mail();
        m.Outbox.RaiseFlushCompleted(new OutboxFlushResult(0, 0, 1, 0, FailedSends: 0));
        Assert.Empty(m.Sounds.Played);
    }

    [Fact]
    public void AConnectionLostAndRestoredPlayOnceEach()
    {
        var m = new Mail();
        m.Connectivity.RaiseOnlineChanged(false);
        m.Connectivity.RaiseOnlineChanged(false);
        m.Connectivity.RaiseOnlineChanged(true);
        m.Connectivity.RaiseOnlineChanged(true);
        Assert.Equal([SoundEvent.ConnectionLost, SoundEvent.ConnectionRestored], m.Sounds.Played);
    }

    [Fact]
    public void BeingOnlineFromTheStartPlaysNothing()
    {
        var m = new Mail();
        m.Connectivity.RaiseOnlineChanged(true);
        Assert.Empty(m.Sounds.Played);
    }

    // ── Acting on messages ──────────────────────────────────────────────────────

    private sealed class FailableMail : StubImapMailServiceBase
    {
        public bool Fail { get; set; }

        public override Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds,
                                               string destinationFolder, System.Threading.CancellationToken ct = default)
            => Fail ? Task.FromException(new InvalidOperationException("server said no")) : Task.CompletedTask;
    }

    private static (MainViewModel Vm, FailableMail Mail, RecordingSoundService Sounds) MakeForMove()
    {
        var mail = new FailableMail();
        var sounds = new RecordingSoundService();
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService(), soundService: sounds);
        return (vm, mail, sounds);
    }

    private static MailFolderModel Archive() => new() { AccountId = Work, FullName = "Archive", DisplayName = "Archive" };

    private sealed class FailableTrashMail : StubImapMailServiceBase
    {
        public override Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, System.Threading.CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("server said no"));
    }

    private static MainViewModel MakeForDelete(IMailService mail, RecordingSoundService sounds) => new(
        mail, new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
        new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
        new StubViewService(), new StubRuleService(), new StubSmtpService(), soundService: sounds);

    private static readonly Guid Home = Guid.Parse("dddddddd-2222-2222-2222-222222222222");

    private sealed class TwoAccounts : IAccountService
    {
        public BackendKind? HomeBackend { get; init; }

        public List<AccountModel> LoadAccounts()
        {
            var home = new AccountModel { Id = Home, AccountName = "Home" };
            if (HomeBackend is { } kind) home.BackendKind = kind;
            return [new AccountModel { Id = Work, AccountName = "Work" }, home];
        }

        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    /// <summary>A VM with two accounts whose folders are loaded, each with an Archive, and two messages per account.</summary>
    private static async Task<(MainViewModel Vm, RecordingSoundService Sounds, List<MailMessageSummary> Mail)> MakeWithFoldersAsync(IMailService mail, BackendKind? homeBackend = null)
    {
        var store = new StubLocalStoreService();
        foreach (var id in new[] { Work, Home })
        {
            store.SeededFolders[id] =
            [
                new MailFolderModel { AccountId = id, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
                new MailFolderModel { AccountId = id, FullName = "Archive", DisplayName = "Archive", Kind = SpecialFolderKind.Archive },
                new MailFolderModel { AccountId = id, FullName = "Trash", DisplayName = "Trash", Kind = SpecialFolderKind.Trash, MessageCount = 5 },
            ];
        }
        var sounds = new RecordingSoundService();
        var vm = new MainViewModel(
            mail, new TwoAccounts { HomeBackend = homeBackend }, new StubCredentialService(), store, new StubOAuthService(),
            new StubSyncService(), new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), soundService: sounds);
        vm.LoadAccountList();
        await vm.InitialLoadAsync();

        vm.Messages.Clear();
        List<MailMessageSummary> messages =
        [
            new() { MessageId = "1", AccountId = Work, FolderName = "INBOX", Subject = "One" },
            new() { MessageId = "2", AccountId = Work, FolderName = "INBOX", Subject = "Two" },
            new() { MessageId = "3", AccountId = Home, FolderName = "INBOX", Subject = "Three" },
            new() { MessageId = "4", AccountId = Home, FolderName = "INBOX", Subject = "Four" },
        ];
        foreach (var m in messages) vm.Messages.Add(m);
        return (vm, sounds, messages);
    }

    private sealed class FailingFilingMail : StubImapMailServiceBase
    {
        public override Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds,
                                               string destinationFolder, System.Threading.CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("server said no"));
    }

    private sealed class TrashMail : StubImapMailServiceBase
    {
        /// <summary>Accounts whose empty fails, and how. Any other account deletes <see cref="Deleted"/>.</summary>
        public Dictionary<Guid, Exception> Failures { get; } = [];

        /// <summary>What the check afterward finds, per account. Absent means the check itself can't run.</summary>
        public Dictionary<Guid, int> Remaining { get; } = [];

        public int Deleted { get; set; } = 3;
        public List<Guid> Attempted { get; } = [];

        /// <summary>When set, the empty waits for this, so a test can do something while it runs.</summary>
        public Task? Hold { get; set; }

        public override async Task<int> EmptyTrashAsync(Guid accountId, System.Threading.CancellationToken ct = default)
        {
            Attempted.Add(accountId);
            if (Hold is not null) await Hold;
            return Failures.TryGetValue(accountId, out var failure) ? throw failure : Deleted;
        }

        public override Task<int> CountTrashMessagesAsync(Guid accountId, System.Threading.CancellationToken ct = default)
            => Remaining.TryGetValue(accountId, out var left)
                ? Task.FromResult(left)
                : Task.FromException<int>(new InvalidOperationException("can't connect"));
    }

    private static readonly InvalidOperationException ServerSaidNo = new("server said no");

    /// <summary>Puts a VM in All Trash, with one trash message per account, in the flat list and behind it.</summary>
    private static async Task<(MailMessageSummary Work, MailMessageSummary Home)> ShowAllTrashAsync(MainViewModel vm)
    {
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllTrashFolder);
        var work = new MailMessageSummary { MessageId = "1", AccountId = Work, FolderName = "Trash", Subject = "Work's" };
        var home = new MailMessageSummary { MessageId = "2", AccountId = Home, FolderName = "Trash", Subject = "Home's" };
        ShowInList(vm, work, home);
        return (work, home);
    }

    private static void ShowInList(MainViewModel vm, params MailMessageSummary[] rows)
    {
        vm.Messages.Clear();
        foreach (var m in rows) vm.Messages.Add(m);
        typeof(MainViewModel)
            .GetField("_rawMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, rows.ToList());
    }

    [Fact]
    public async Task EmptyingTrashPlaysOnceAcrossAccounts()
    {
        var mail = new TrashMail();
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        await vm.EmptyTrashCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.TrashEmptied], sounds.Played);
        Assert.Equal([Work, Home], mail.Attempted);
        Assert.Equal("6 messages deleted from trash.", vm.StatusText);
    }

    [Fact]
    public async Task EmptyingATrashWithNothingInItPlaysNothing()
    {
        var (vm, sounds, _) = await MakeWithFoldersAsync(new TrashMail { Deleted = 0 });
        await vm.EmptyTrashCommand.ExecuteAsync(null);
        Assert.Empty(sounds.Played);
        Assert.Equal("Trash was already empty.", vm.StatusText);
    }

    [Fact]
    public async Task WhenTheCheckCannotRunAFailureStaysAFailure()
    {
        // The network is down: the empty fails, and so does the check. Nothing was deleted.
        var mail = new TrashMail();
        mail.Failures[Work] = ServerSaidNo;
        mail.Failures[Home] = ServerSaidNo;
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
        Assert.Equal("Could not empty Work: server said no. Could not empty Home: server said no.", vm.StatusText);
    }

    [Fact]
    public async Task ASecondAccountFailingIsNotHiddenByTheFirstBeingEmpty()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Work] = 0;
        mail.Remaining[Home] = 2;
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
        Assert.Equal("Emptied trash for Work. Could not empty Home: server said no.", vm.StatusText);
    }

    [Fact]
    public async Task AFirstAccountFailingDoesNotStopTheSecond()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = ServerSaidNo;
        mail.Remaining[Work] = 2;
        mail.Remaining[Home] = 0;
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal([Work, Home], mail.Attempted);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
        Assert.Equal("Emptied trash for Home. Could not empty Work: server said no.", vm.StatusText);
    }

    [Fact]
    public async Task InAllTrashOnlyTheEmptiedAccountsMessagesGo()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (_, home) = await ShowAllTrashAsync(vm);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Same(home, Assert.Single(vm.Messages));
        Assert.Same(home, Assert.Single(vm.LoadedMessages));
    }

    [Fact]
    public async Task ATimeoutOnOneAccountIsThatAccountsFailureAlone()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = new OperationCanceledException();
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        var (_, home) = await ShowAllTrashAsync(vm);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal([Work, Home], mail.Attempted);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
        Assert.Equal("Emptied trash for Home. Could not empty Work: it timed out.", vm.StatusText);
        Assert.Equal(Work, Assert.Single(vm.Messages).AccountId);
    }

    [Fact]
    public async Task AFailureIsReadOutInPlainWords()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = new AccountNotConnectedException(Work);
        mail.Failures[Home] = new AccountNotConnectedException(Home);
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var announced = new List<(string Text, AnnouncementCategory Category)>();
        vm.AnnouncementRequested += (_, a) => announced.Add(a);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        const string Expected = "Could not empty Work: could not reach the server. Could not empty Home: could not reach the server.";
        Assert.Equal(Expected, vm.StatusText);
        Assert.Contains((Expected, AnnouncementCategory.Result), announced);
    }

    [Fact]
    public async Task EveryOutcomeIsAnnounced()
    {
        var (vm, _, _) = await MakeWithFoldersAsync(new TrashMail());
        var announced = new List<(string Text, AnnouncementCategory Category)>();
        vm.AnnouncementRequested += (_, a) => announced.Add(a);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Contains(("6 messages deleted from trash.", AnnouncementCategory.Result), announced);
    }

    [Fact]
    public async Task AnOpenMessageFromAnEmptiedAccountCloses()
    {
        // Open and selected can differ: arrowing to another row doesn't change what's open.
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (_, home) = await ShowAllTrashAsync(vm);
        vm.SelectedMessage = home;
        vm.MessageDetail = new MailMessageDetail { MessageId = "1", AccountId = Work, FolderName = "Trash" };
        vm.IsMessageOpen = true;

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Null(vm.MessageDetail);
        Assert.False(vm.IsMessageOpen);
        Assert.Same(home, vm.SelectedMessage);
    }

    [Fact]
    public async Task AnOpenMessageFromAFailedAccountStaysOpen()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (_, home) = await ShowAllTrashAsync(vm);
        vm.SelectedMessage = home;
        vm.MessageDetail = new MailMessageDetail { MessageId = "2", AccountId = Home, FolderName = "Trash" };
        vm.IsMessageOpen = true;

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.NotNull(vm.MessageDetail);
        Assert.True(vm.IsMessageOpen);
    }

    [Fact]
    public async Task FocusLandsOnASurvivorBeforeTheFocusedRowGoes()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (work, home) = await ShowAllTrashAsync(vm);
        vm.SelectedMessage = work;
        var rowsWhenLanding = -1;
        MailMessageSummary? selectedWhenLanding = null;
        vm.MessageListFocusNowRequested += () =>
        {
            rowsWhenLanding = vm.Messages.Count;
            selectedWhenLanding = vm.SelectedMessage;
            return true;
        };
        var queued = false;
        vm.MessageListFocusRequested += () => queued = true;

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal(2, rowsWhenLanding);
        Assert.Same(home, selectedWhenLanding);
        Assert.False(queued);
    }

    [Fact]
    public async Task ASelectionThatIsNotLeavingKeepsItsPlace()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (work, home) = await ShowAllTrashAsync(vm);
        var other = new MailMessageSummary { MessageId = "3", AccountId = Home, FolderName = "Trash", Subject = "Home's other" };
        ShowInList(vm, work, home, other);
        vm.SelectedMessage = other;
        var focusMoved = false;
        vm.MessageListFocusNowRequested += () => { focusMoved = true; return true; };
        vm.MessageListFocusRequested += () => focusMoved = true;

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Same(other, vm.SelectedMessage);
        Assert.False(focusMoved);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task WhenEveryRowIsLeavingFocusMovesToTheListFirst()
    {
        var (vm, _, _) = await MakeWithFoldersAsync(new TrashMail());
        var (work, _) = await ShowAllTrashAsync(vm);
        vm.SelectedMessage = work;
        var rowsWhenParking = -1;
        MailMessageSummary? selectedWhenParking = work;
        vm.MessageListFocusNowRequested += () =>
        {
            rowsWhenParking = vm.Messages.Count;
            selectedWhenParking = vm.SelectedMessage;
            return true;
        };

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal(2, rowsWhenParking);
        Assert.Null(selectedWhenParking);
        Assert.Empty(vm.Messages);
        Assert.Empty(vm.LoadedMessages);
    }

    [Fact]
    public async Task AnOpenMessageFromAFailedAccountStaysOpenWhenTheSelectionIsLeaving()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (work, _) = await ShowAllTrashAsync(vm);
        vm.SelectedMessage = work;
        vm.MessageDetail = new MailMessageDetail { MessageId = "2", AccountId = Home, FolderName = "Trash" };
        vm.IsMessageOpen = true;

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.NotNull(vm.MessageDetail);
        Assert.True(vm.IsMessageOpen);
    }

    [Fact]
    public async Task InAGroupViewALeavingSelectionIsClearedNotReplaced()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var (work, _) = await ShowAllTrashAsync(vm);
        vm.ViewMode = ViewMode.From;
        vm.SelectedMessage = work;

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedMessage);
    }

    [Theory]
    [InlineData(false, "Empty trash failed: server said no.")]
    [InlineData(true, "Empty trash timed out.")]
    public async Task OneAccountsFailureIsWordedWithoutNamingIt(bool timeout, string expected)
    {
        var mail = new TrashMail();
        mail.Failures[Work] = timeout ? new OperationCanceledException() : ServerSaidNo;
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        vm.SelectedAccount = vm.Accounts.Single(a => a.Id == Work);
        vm.SelectedFolder = new MailFolderModel { AccountId = Work, FullName = "Archive", DisplayName = "Archive", Kind = SpecialFolderKind.Archive };

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal([Work], mail.Attempted);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
        Assert.Equal(expected, vm.StatusText);
    }

    [Fact]
    public async Task MovingAwayWhileItRunsLeavesTheNewViewAlone()
    {
        var hold = new TaskCompletionSource();
        var mail = new TrashMail { Hold = hold.Task };
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        await ShowAllTrashAsync(vm);

        var running = vm.EmptyTrashCommand.ExecuteAsync(null);
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllInboxesFolder);
        var inbox = new MailMessageSummary { MessageId = "9", AccountId = Work, FolderName = "INBOX", Subject = "Not trash" };
        ShowInList(vm, inbox);
        hold.SetResult();
        await running;

        Assert.Same(inbox, Assert.Single(vm.Messages));
        Assert.Same(inbox, Assert.Single(vm.LoadedMessages));
    }

    [Fact]
    public async Task AnAccountWithNoTrashFolderKeepsWhatIsListed()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = new TrashFolderNotFoundException();
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        var (_, home) = await ShowAllTrashAsync(vm);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Same(home, Assert.Single(vm.Messages));
        Assert.Equal([SoundEvent.TrashEmptied], sounds.Played);
        Assert.Equal("3 messages deleted from trash. No trash folder found for Home.", vm.StatusText);
    }

    [Fact]
    public async Task NoTrashFolderAnywhereSaysSoAndPlaysNothing()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = new TrashFolderNotFoundException();
        mail.Failures[Home] = new TrashFolderNotFoundException();
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        await ShowAllTrashAsync(vm);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Empty(sounds.Played);
        Assert.Equal("No trash folder found.", vm.StatusText);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task OnlyEmptiedAccountsLoseTheirRememberedTrashCount()
    {
        var mail = new TrashMail();
        mail.Failures[Home] = ServerSaidNo;
        mail.Remaining[Home] = 2;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        var prompts = new List<string>();
        vm.ConfirmationRequested = (prompt, _) => { prompts.Add(prompt); return true; };

        await vm.EmptyTrashCommand.ExecuteAsync(null);
        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Contains("10 messages", prompts[0], StringComparison.Ordinal);
        Assert.Contains("5 messages", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenEveryAccountFailsTheTrashListStays()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = ServerSaidNo;
        mail.Failures[Home] = ServerSaidNo;
        var (vm, _, _) = await MakeWithFoldersAsync(mail);
        await ShowAllTrashAsync(vm);

        await vm.EmptyTrashCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(2, vm.LoadedMessages.Count);
    }

    [Fact]
    public async Task AnImapAccountWithNoConnectionDoesNotReportAnEmptyTrash()
    {
        using var service = new ImapMailService(new StubOAuthService());
        await Assert.ThrowsAsync<AccountNotConnectedException>(
            () => service.CountTrashMessagesAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnImapServerThatCannotBeReachedDoesNotReportAnEmptyTrash()
    {
        // The pool outlives a refused connection, so this is the path a network failure really takes.
        var ct = TestContext.Current.CancellationToken;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            Username = "someone@example.com",
            AuthType = AuthType.Password,
            ImapHost = "127.0.0.1",
            ImapPort = port,
            ImapUseSsl = false,
        };
        using var service = new ImapMailService(new StubOAuthService());
        await Assert.ThrowsAnyAsync<Exception>(() => service.ConnectAsync(account, "pw", ct));
        Assert.True(service.IsConnected(account.Id));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.CountTrashMessagesAsync(account.Id, ct));
        Assert.True(ConnectionFailure.IsConnectionFailure(thrown), thrown.ToString());
    }

    [Fact]
    public async Task DecliningToEmptyTrashPlaysNothing()
    {
        var (vm, sounds, _) = await MakeWithFoldersAsync(new TrashMail());
        vm.ConfirmationRequested = (_, _) => false;
        await vm.EmptyTrashCommand.ExecuteAsync(null);
        Assert.Empty(sounds.Played);
    }

    [Fact]
    public async Task AFailedEmptyTrashPlaysActionFailed()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = ServerSaidNo;
        mail.Remaining[Work] = 2;
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        await vm.EmptyTrashCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
    }

    [Fact]
    public async Task ATimedOutEmptyTrashPlaysActionFailed()
    {
        var mail = new TrashMail();
        mail.Failures[Work] = new OperationCanceledException();
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        await vm.EmptyTrashCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
        Assert.Equal("Emptied trash for Home. Could not empty Work: it timed out.", vm.StatusText);
    }

    [Fact]
    public async Task AnEmptyTrashThatFailedButReallyWorkedPlaysEmptied()
    {
        // The connection dropped on the server's reply: the check afterward finds the trash empty.
        var mail = new TrashMail { Deleted = 0 };
        mail.Failures[Work] = new InvalidOperationException("connection dropped");
        mail.Remaining[Work] = 0;
        var (vm, sounds, _) = await MakeWithFoldersAsync(mail);
        await vm.EmptyTrashCommand.ExecuteAsync(null);
        Assert.Equal([SoundEvent.TrashEmptied], sounds.Played);
        Assert.Equal("Trash emptied.", vm.StatusText);
    }

    [Fact]
    public async Task ABulkDeleteAcrossAccountsPlaysOnce()
    {
        var (vm, sounds, mail) = await MakeWithFoldersAsync(new StubImapMailService());
        await vm.DeleteMessagesAsync(mail);
        Assert.Equal([SoundEvent.MessageDeleted], sounds.Played);
    }

    [Fact]
    public async Task ABulkArchiveAcrossAccountsPlaysOnce()
    {
        var (vm, sounds, mail) = await MakeWithFoldersAsync(new StubImapMailService());
        await vm.ArchiveMessagesAsync(mail);
        Assert.Equal([SoundEvent.MessageArchived], sounds.Played);
    }

    [Fact]
    public async Task AFailedArchivePlaysActionFailedAndNotArchived()
    {
        var (vm, sounds, mail) = await MakeWithFoldersAsync(new FailingFilingMail());
        await vm.ArchiveMessagesAsync(mail);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
    }

    [Fact]
    public async Task ABulkMovePlaysOnce()
    {
        var (vm, sounds, mail) = await MakeWithFoldersAsync(new StubImapMailService());
        await vm.MoveSelectedMessagesToFolderAsync(mail.Take(2).ToList(), Archive());
        Assert.Equal([SoundEvent.MessageMoved], sounds.Played);
    }

    [Fact]
    public async Task ADeletedMessagePlaysDeleted()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeForDelete(new StubImapMailService(), sounds);
        var message = Msg("1");
        vm.Messages.Add(message);
        await vm.DeleteMessagesAsync([message]);
        Assert.Equal([SoundEvent.MessageDeleted], sounds.Played);
    }

    [Fact]
    public async Task AFailedDeletePlaysActionFailedAndNotDeleted()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeForDelete(new FailableTrashMail(), sounds);
        var message = Msg("1");
        vm.Messages.Add(message);
        await vm.DeleteMessagesAsync([message]);
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
    }

    [Fact]
    public async Task AMovedMessagePlaysMoved()
    {
        var (vm, _, sounds) = MakeForMove();
        await vm.MoveSelectedMessagesToFolderAsync([Msg("1")], Archive());
        Assert.Equal([SoundEvent.MessageMoved], sounds.Played);
    }

    [Fact]
    public async Task AFailedMovePlaysActionFailedAndNotMoved()
    {
        var (vm, mail, sounds) = MakeForMove();
        mail.Fail = true;
        await vm.MoveSelectedMessagesToFolderAsync([Msg("1")], Archive());
        Assert.Equal([SoundEvent.ActionFailed], sounds.Played);
    }

    // ── The Sounds tab ──────────────────────────────────────────────────────────

    private static SettingsViewModel MakeSettings(StubConfigService config, RecordingSoundService? sounds = null) =>
        new(config, new StubCommandRegistry(), sounds: sounds);

    [Fact]
    public void EveryEventIsListedWithWhatItPlays()
    {
        var vm = MakeSettings(new StubConfigService());

        Assert.Equal(SoundCatalog.Events.Count, vm.SoundEventRows.Count);
        Assert.Equal(vm.SoundEventRows[0], vm.SelectedSoundEvent);
        Assert.Equal("Message sent: Sent", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.MessageSent).AccessibleName);
        Assert.Equal("Message deleted: Removed", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.MessageDeleted).AccessibleName);
    }

    [Fact]
    public void ChoosingASoundChangesTheSelectedRowOnly()
    {
        var vm = MakeSettings(new StubConfigService());
        var row = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail);
        vm.SelectedSoundEvent = row;
        Assert.Equal("Arrived", vm.SelectedSoundChoice!.Label);

        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == "Reminder");

        Assert.Equal("New mail: Reminder", row.AccessibleName);
        Assert.Equal("Message sent: Sent", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.MessageSent).AccessibleName);
    }

    [Fact]
    public void MovingToAnotherEventShowsItsSoundAndDoesNotChangeIt()
    {
        var vm = MakeSettings(new StubConfigService());
        var first = vm.SelectedSoundEvent!.Choice;
        vm.SelectedSoundEvent = vm.SoundEventRows[1];
        Assert.Equal(vm.SoundEventRows[1].Choice, vm.SelectedSoundChoice);
        Assert.Equal(first, vm.SoundEventRows[0].Choice);
    }

    [Fact]
    public void SavingRecordsOnlyWhatDiffersFromTheDefaults()
    {
        var config = new StubConfigService();
        var vm = MakeSettings(config);
        vm.PlayEventSounds = true;
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == "None");
        vm.SelectedSoundVolume = vm.SoundVolumeOptions.Single(o => o.Percent == 50);

        vm.SaveCommand.Execute(null);

        var saved = config.Load();
        Assert.True(saved.PlayEventSounds);
        Assert.Equal(50, saved.SoundVolume);
        Assert.Equal("none", Assert.Single(saved.ActiveSoundEvents()).Value);
    }

    [Fact]
    public void SavingDoesNotChangeTheDictionaryTheSoundWorkerMayBeReading()
    {
        var config = new StubConfigService();
        var before = config.Load().SoundSchemes;
        MakeSettings(config).SaveCommand.Execute(null);
        Assert.NotSame(before, config.Load().SoundSchemes);
    }

    [Fact]
    public void AVolumeSetByHandSurvivesOpeningAndSavingSettings()
    {
        var config = new StubConfigService();
        config.Load().SoundVolume = 37;
        var vm = MakeSettings(config);
        Assert.Equal("37%", vm.SelectedSoundVolume!.Label);
        vm.SaveCommand.Execute(null);
        Assert.Equal(37, config.Load().SoundVolume);
    }

    [Fact]
    public void ASoundWhoseFileHasGoneStaysListedAsMissing()
    {
        var config = new StubConfigService();
        config.Load().ActiveSoundEvents()["NewMail"] = "custom:Gone.wav";
        var vm = MakeSettings(config);
        Assert.Equal("New mail: Gone.wav (missing)", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail).AccessibleName);
    }

    [Fact]
    public void PreviewPlaysTheChosenSoundAtTheVolumeShowing()
    {
        var sounds = new RecordingSoundService();
        var vm = MakeSettings(new StubConfigService(), sounds);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Label == "Reminder");
        vm.SelectedSoundVolume = vm.SoundVolumeOptions.Single(o => o.Percent == 20);
        vm.SelectedSoundScheme = vm.SoundSchemes.Single(p => p.Name == "Wood");
        vm.PreviewSoundCommand.Execute(null);
        Assert.Equal(["builtin:reminder"], sounds.Previewed);
        Assert.Equal(20, sounds.PreviewVolume);
        Assert.Equal("wood", sounds.PreviewPack);
    }

    [Fact]
    public void TheStyleStartsOnTheDefaultAndIsSaved()
    {
        var config = new StubConfigService();
        var vm = MakeSettings(config);
        Assert.Equal(SoundCatalog.DefaultPack, vm.SelectedSoundScheme!.Id);

        vm.SelectedSoundScheme = vm.SoundSchemes.Single(p => p.Name == "Soft bell");
        vm.SaveCommand.Execute(null);

        Assert.Equal("softbell", config.Load().SoundSchemeId);
        Assert.Equal("Soft bell", MakeSettings(config).SelectedSoundScheme!.Name);
    }

    [Fact]
    public void SavingOnAnotherTabKeepsAStyleThisBuildDoesNotHave()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemeId = "from-the-future";
        MakeSettings(config).SaveCommand.Execute(null);
        Assert.Equal("from-the-future", config.Load().SoundSchemeId);
    }

    [Fact]
    public void AToastForMailARuleSoundedForLeavesOutTheWindowsSound()
    {
        // Event sounds off, a rule plays a sound, toasts on: one sound, not the rule's and Windows' too.
        var m = new Mail();
        m.Config.Load().NotifyOnNewMail = true;
        m.Sounds.RuleSoundedKeys.Add(SoundCatalog.MessageKey(Work, "1"));
        m.Vm.MaybeNotifyNewMail(Account(), [Msg("1")]);
        Assert.True(Assert.Single(m.Toasts.Shown).Silent);
    }

    [Fact]
    public void AToastForOtherMailKeepsTheWindowsSoundWithEventSoundsOff()
    {
        var m = new Mail();
        m.Config.Load().NotifyOnNewMail = true;
        m.Sounds.RuleSoundedKeys.Add(SoundCatalog.MessageKey(Work, "1"));
        m.Vm.MaybeNotifyNewMail(Account(), [Msg("2")]);
        Assert.False(Assert.Single(m.Toasts.Shown).Silent);
    }

    [Fact]
    public void AStyleThisBuildDoesNotHaveShowsAsTheDefault()
    {
        var config = new StubConfigService();
        config.Load().SoundSchemeId = "from-the-future";
        Assert.Equal(SoundCatalog.DefaultPack, MakeSettings(config).SelectedSoundScheme!.Id);
    }

    [Fact]
    public void ChoosingAMissingFileAgainRepairsTheEntryForEveryEventUsingIt()
    {
        var config = new StubConfigService();
        config.Load().ActiveSoundEvents()["NewMail"] = "custom:Gone.wav";
        config.Load().ActiveSoundEvents()["MessageSent"] = "custom:Gone.wav";
        var vm = MakeSettings(config, new RecordingSoundService());
        vm.PickSoundFileRequested = () => @"C:\Music\Gone.wav";

        vm.ChooseSoundFileCommand.Execute(null);

        Assert.Equal("New mail: Gone.wav", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail).AccessibleName);
        Assert.Equal("Message sent: Gone.wav", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.MessageSent).AccessibleName);
        Assert.DoesNotContain(vm.SoundChoices, c => c.Label.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingKeepsAnEntryFromANewerQuickMail()
    {
        var config = new StubConfigService();
        config.Load().ActiveSoundEvents()["SomethingNew"] = "builtin:future";
        MakeSettings(config).SaveCommand.Execute(null);
        Assert.Equal("builtin:future", config.Load().ActiveSoundEvents()["SomethingNew"]);
    }

    [Fact]
    public void AChosenFileBecomesTheEventsSound()
    {
        var vm = MakeSettings(new StubConfigService(), new RecordingSoundService());
        vm.PickSoundFileRequested = () => @"C:\Music\My Chime.wav";

        vm.ChooseSoundFileCommand.Execute(null);

        Assert.Equal("custom:My Chime.wav", vm.SelectedSoundEvent!.Choice.Stored);
        Assert.Contains(vm.SoundChoices, c => c.Label == "My Chime.wav");
        Assert.False(vm.HasSoundFileMessage);
    }

    [Fact]
    public void CancellingTheFileDialogChangesNothing()
    {
        var vm = MakeSettings(new StubConfigService(), new RecordingSoundService());
        var before = vm.SelectedSoundEvent!.Choice;
        vm.PickSoundFileRequested = () => null;

        vm.ChooseSoundFileCommand.Execute(null);

        Assert.Equal(before, vm.SelectedSoundEvent.Choice);
        Assert.False(vm.HasSoundFileMessage);
    }

    [Fact]
    public void ARefusedFileSaysWhyAndChangesNothing()
    {
        var sounds = new RecordingSoundService { RefuseWith = "That file could not be played." };
        var vm = MakeSettings(new StubConfigService(), sounds);
        var before = vm.SelectedSoundEvent!.Choice;
        string? announced = null;
        vm.SoundResultAnnounced += reason => announced = reason;
        vm.PickSoundFileRequested = () => @"C:\Music\junk.wav";

        vm.ChooseSoundFileCommand.Execute(null);

        Assert.Equal(before, vm.SelectedSoundEvent.Choice);
        Assert.Equal("That file could not be played.", vm.SoundFileMessage);
        Assert.Equal(vm.SoundFileMessage, announced);
    }

}

[Collection("WpfTests")]
public class SoundsTabAccessibilityTests
{
    private static (SettingsDialog Dialog, SettingsViewModel Vm) MakeDialog()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        return (new SettingsDialog(vm), vm);
    }

    [StaFact]
    public void TypingMovesToAMatchingEvent()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            Assert.True(dialog.TrySoundEventTypeAhead("n"));
            Assert.Equal(SoundEvent.NewMail, vm.SelectedSoundEvent!.Info.Event);
        }
        finally { dialog.Close(); }
    }

    // Regression guard for #418's bug in this list: text that isn't taken here reaches the window and
    // fires an access key without Alt, so a stray "s" would run Save and close Settings.
    [StaFact]
    public void AnUnmatchedLetterIsTakenByTheListAndFiresNothing()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            var list = dialog.FindName("SoundEventList") as ListBox;
            Assert.NotNull(list);
            Assert.False(list.IsTextSearchEnabled);
            var before = vm.SelectedSoundEvent;

            var args = new System.Windows.Input.TextCompositionEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, list, "z"))
            {
                RoutedEvent = UIElement.PreviewTextInputEvent,
            };
            list.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.Equal(before, vm.SelectedSoundEvent);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void TheEventRowsNameIsOnTheContainerAndTheListsHaveLabels()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        var window = new SettingsDialog(vm) { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var list = window.FindName("SoundEventList") as ListBox;
            Assert.NotNull(list);
            DependencyObject? parent = list;
            while (parent is not null and not TabItem) parent = LogicalTreeHelper.GetParent(parent);
            var tab = parent as TabItem;
            Assert.NotNull(tab);
            tab.IsSelected = true;
            window.UpdateLayout();
            list.UpdateLayout();

            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Assert.NotNull(row);
            Assert.Equal(vm.SoundEventRows[0].AccessibleName, AutomationProperties.GetName(row));

            foreach (var name in new[] { "SoundEventList", "SoundVolumeCombo", "SoundStyleCombo", "SoundChoiceCombo" })
            {
                var control = window.FindName(name) as FrameworkElement;
                Assert.NotNull(control);
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(control);
                Assert.False(string.IsNullOrWhiteSpace(peer.GetName()), $"{name} has no accessible name.");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
