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

    public void Preview(string storedSound, int? volumePercent = null, string? pack = null)
    {
        Previewed.Add(storedSound);
        PreviewVolume = volumePercent;
        PreviewPack = pack;
    }
    public bool IsAudible(SoundEvent soundEvent) => Audible.Contains(soundEvent);
    public IReadOnlyList<string> CustomSoundFiles() => CustomFiles;

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
        public List<AccountModel> LoadAccounts() =>
        [
            new AccountModel { Id = Work, AccountName = "Work" },
            new AccountModel { Id = Home, AccountName = "Home" },
        ];
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    /// <summary>A VM with two accounts whose folders are loaded, each with an Archive, and two messages per account.</summary>
    private static async Task<(MainViewModel Vm, RecordingSoundService Sounds, List<MailMessageSummary> Mail)> MakeWithFoldersAsync(IMailService mail)
    {
        var store = new StubLocalStoreService();
        foreach (var id in new[] { Work, Home })
        {
            store.SeededFolders[id] =
            [
                new MailFolderModel { AccountId = id, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
                new MailFolderModel { AccountId = id, FullName = "Archive", DisplayName = "Archive", Kind = SpecialFolderKind.Archive },
            ];
        }
        var sounds = new RecordingSoundService();
        var vm = new MainViewModel(
            mail, new TwoAccounts(), new StubCredentialService(), store, new StubOAuthService(),
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
        Assert.Equal("none", Assert.Single(saved.SoundEvents).Value);
    }

    [Fact]
    public void SavingDoesNotChangeTheDictionaryTheSoundWorkerMayBeReading()
    {
        var config = new StubConfigService();
        var before = config.Load().SoundEvents;
        MakeSettings(config).SaveCommand.Execute(null);
        Assert.NotSame(before, config.Load().SoundEvents);
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
        config.Load().SoundEvents["NewMail"] = "custom:Gone.wav";
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
        vm.SelectedSoundPack = vm.SoundPacks.Single(p => p.Label == "Wood");
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
        Assert.Equal(SoundCatalog.DefaultPack, vm.SelectedSoundPack!.Id);

        vm.SelectedSoundPack = vm.SoundPacks.Single(p => p.Label == "Soft bell");
        vm.SaveCommand.Execute(null);

        Assert.Equal("softbell", config.Load().SoundPack);
        Assert.Equal("Soft bell", MakeSettings(config).SelectedSoundPack!.Label);
    }

    [Fact]
    public void SavingOnAnotherTabKeepsAStyleThisBuildDoesNotHave()
    {
        var config = new StubConfigService();
        config.Load().SoundPack = "from-the-future";
        MakeSettings(config).SaveCommand.Execute(null);
        Assert.Equal("from-the-future", config.Load().SoundPack);
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
        config.Load().SoundPack = "from-the-future";
        Assert.Equal(SoundCatalog.DefaultPack, MakeSettings(config).SelectedSoundPack!.Id);
    }

    [Fact]
    public void ChoosingAMissingFileAgainRepairsTheEntryForEveryEventUsingIt()
    {
        var config = new StubConfigService();
        config.Load().SoundEvents["NewMail"] = "custom:Gone.wav";
        config.Load().SoundEvents["MessageSent"] = "custom:Gone.wav";
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
        config.Load().SoundEvents["SomethingNew"] = "builtin:future";
        MakeSettings(config).SaveCommand.Execute(null);
        Assert.Equal("builtin:future", config.Load().SoundEvents["SomethingNew"]);
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
        vm.SoundFileRefused += reason => announced = reason;
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
