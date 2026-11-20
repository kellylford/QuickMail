using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>The "Play a sound" rule action (#912), and #129's sound for chosen senders.</summary>
public class RuleSoundTests
{
    // ── The editor ──────────────────────────────────────────────────────────────

    private static ServerRuleEditorViewModel Named()
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = "Sound for Pat";
        return vm;
    }

    [Fact]
    public void ARuleThatOnlyPlaysASoundIsValidAndNeedsNoCondition()
    {
        var vm = Named();
        vm.PlaySound = true;
        Assert.True(vm.Validate());
    }

    [Fact]
    public void ABoxLeftUncheckedIsNotAnAction()
    {
        var vm = Named();
        Assert.False(vm.Validate());
    }

    [Fact]
    public void PlayingASoundMakesARuleClientSideOnAnAccountWithServerRules()
    {
        var vm = Named();
        vm.PlaySound = true;
        Assert.Equal(RuleRunsWhere.Client, vm.Classify(accountSupportsServerRules: true).Kind);
    }

    [Fact]
    public void ASoundWithAServerOnlyOptionIsRefusedAndNamed()
    {
        var vm = Named();
        vm.PlaySound = true;
        vm.ForwardTo = "someone@example.com";

        var result = vm.Classify(accountSupportsServerRules: true);

        Assert.True(result.IsConflict);
        Assert.Contains("Play a sound", result.ConflictError, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewRuleStartsOnTheDefaultRuleSound()
    {
        var vm = Named();
        vm.PlaySound = true;
        Assert.Equal("builtin:" + SoundCatalog.DefaultRuleSound, vm.ToClientRule(Guid.NewGuid()).Sound);
    }

    [Fact]
    public void TheChosenSoundIsSaved()
    {
        var vm = Named();
        vm.PlaySound = true;
        vm.SelectedRuleSound = vm.RuleSoundChoices.Single(c => c.Label == "Reminder");

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal("builtin:reminder", rule.Sound);
        Assert.True(rule.Does(RuleAction.PlaySound));
    }

    [Fact]
    public void AnUncheckedBoxLeavesNoSoundOnTheRule()
    {
        var vm = Named();
        vm.MarkAsRead = true;
        vm.SelectedRuleSound = vm.RuleSoundChoices.Single(c => c.Label == "Reminder");
        Assert.Null(vm.ToClientRule(Guid.NewGuid()).Sound);
    }

    [Fact]
    public void AnOlderQuickMailReadingOnlyTheMainActionNeverRunsTheSoundInsteadOfTheRealOne()
    {
        var vm = Named();
        vm.PlaySound = true;
        vm.MarkAsRead = true;
        var rule = vm.ToClientRule(Guid.NewGuid());
        Assert.Equal(RuleAction.MarkAsRead, rule.Action);
        Assert.Contains(RuleAction.PlaySound, rule.Actions!);

        var filing = Named();
        filing.UseSubjectContains = true;
        filing.SubjectContains = "digest";
        filing.PlaySound = true;
        filing.MoveToFolder = true;
        filing.MoveToFolderId = "INBOX/Digests";
        Assert.Equal(RuleAction.MoveToFolder, filing.ToClientRule(Guid.NewGuid()).Action);
    }

    [Fact]
    public void ARuleThatOnlyPlaysASoundDoesNothingInAnOlderQuickMailRatherThanMarkingRead()
    {
        var vm = Named();
        vm.PlaySound = true;
        var rule = vm.ToClientRule(Guid.NewGuid());
        Assert.Equal(RuleAction.PlaySound, rule.Action);
        Assert.Null(rule.Actions);
    }

    private static MailRule SavedRule(string? sound) => new()
    {
        Name = "Sound", AccountId = Guid.NewGuid(), Action = RuleAction.PlaySound, Sound = sound,
    };

    [Fact]
    public void EditingARuleShowsItsSound()
    {
        var vm = ServerRuleEditorViewModel.ForEditClient(SavedRule("builtin:reminder"));
        Assert.True(vm.PlaySound);
        Assert.True(vm.IsPlaySoundSelected);
        Assert.Equal("Reminder", vm.SelectedRuleSound!.Label);
    }

    [Fact]
    public void ASavedRuleOpenedAndSavedAgainKeepsItsSound()
    {
        var original = SavedRule("custom:Chime.wav");
        var vm = ServerRuleEditorViewModel.ForEditClient(original);
        vm.AttachSounds(new RecordingSoundService { CustomFiles = { "Chime.wav" } });
        Assert.Equal("Chime.wav", vm.SelectedRuleSound!.Label);
        Assert.Equal("custom:Chime.wav", vm.ToClientRule(original.AccountId!.Value).Sound);
    }

    [Fact]
    public void ACustomSoundWhoseFileHasGoneIsShownAsMissingAndKept()
    {
        var original = SavedRule("custom:Gone.wav");
        var vm = ServerRuleEditorViewModel.ForEditClient(original);
        vm.AttachSounds(new RecordingSoundService());
        Assert.Equal("Gone.wav (missing)", vm.SelectedRuleSound!.Label);
        Assert.Equal("custom:Gone.wav", vm.ToClientRule(original.AccountId!.Value).Sound);
    }

    [Fact]
    public void AServerRuleCannotTakeASound()
    {
        var vm = ServerRuleEditorViewModel.ForEdit(new ServerRuleModel { DisplayName = "On the server" });
        Assert.False(vm.CanPlaySound);
        vm.PlaySound = true;
        Assert.False(vm.IsPlaySoundSelected);
    }

    [Fact]
    public void PreviewPlaysTheSelectedSound()
    {
        var sounds = new RecordingSoundService();
        var vm = Named();
        vm.AttachSounds(sounds);
        vm.SelectedRuleSound = vm.RuleSoundChoices.Single(c => c.Label == "Arrived");
        vm.PreviewRuleSoundCommand.Execute(null);
        Assert.Equal(["builtin:arrived"], sounds.RulePreviewed);
    }

    [Fact]
    public void TheDefaultSoundComesFirstAndIsWhatANewRuleHas()
    {
        var vm = Named();
        Assert.Equal("Default sound", vm.RuleSoundChoices[0].Label);
        Assert.Equal("builtin:matched", vm.RuleSoundChoices[0].Stored);
        Assert.Equal(vm.RuleSoundChoices[0], vm.SelectedRuleSound);
        Assert.DoesNotContain(vm.RuleSoundChoices, c => c.Label == "Matched");
    }

    [Fact]
    public void ChoosingAFileSaysWhetherItWorkedSoTheViewCanMoveFocus()
    {
        var vm = Named();
        vm.AttachSounds(new RecordingSoundService());
        vm.PickSoundFileRequested = () => @"C:\Music\Ding.wav";
        Assert.True(vm.TryChooseRuleSoundFile());
        vm.PickSoundFileRequested = () => null;
        Assert.False(vm.TryChooseRuleSoundFile());
        vm.AttachSounds(new RecordingSoundService { RefuseWith = "No." });
        vm.PickSoundFileRequested = () => @"C:\Music\Junk.wav";
        Assert.False(vm.TryChooseRuleSoundFile());
    }

    [Fact]
    public void AChosenFileBecomesTheRulesSound()
    {
        var vm = Named();
        vm.AttachSounds(new RecordingSoundService());
        vm.PlaySound = true;
        vm.PickSoundFileRequested = () => @"C:\Music\Pat.wav";

        vm.ChooseRuleSoundFileCommand.Execute(null);

        Assert.Equal("custom:Pat.wav", vm.ToClientRule(Guid.NewGuid()).Sound);
    }

    [Fact]
    public void ARefusedFileSaysWhyAndLeavesTheSoundAlone()
    {
        var vm = Named();
        vm.AttachSounds(new RecordingSoundService { RefuseWith = "That file could not be played." });
        var before = vm.SelectedRuleSound;
        string? announced = null;
        vm.AnnouncementRequested += (text, _) => announced = text;
        vm.PickSoundFileRequested = () => @"C:\Music\junk.wav";

        vm.ChooseRuleSoundFileCommand.Execute(null);

        Assert.Equal(before, vm.SelectedRuleSound);
        Assert.Equal("That file could not be played.", vm.RuleSoundMessage);
        Assert.Equal(vm.RuleSoundMessage, announced);
    }

    // ── Running a rule ──────────────────────────────────────────────────────────

    private sealed class Mail : StubImapMailServiceBase
    {
        public Exception? ThrowOnCopy { get; set; }
        public Exception? ThrowOnMove { get; set; }
        public Exception? ThrowOnDelete { get; set; }

        public override Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        {
            if (ThrowOnCopy is not null) throw ThrowOnCopy;
            return Task.CompletedTask;
        }

        public override Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default)
        {
            if (ThrowOnMove is not null) throw ThrowOnMove;
            return Task.CompletedTask;
        }

        public override Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        {
            if (ThrowOnDelete is not null) throw ThrowOnDelete;
            return Task.CompletedTask;
        }
    }

    private static async Task<RecordingSoundService> Run(MailRule rule, Mail? mail = null, int messages = 1, int ageHours = -1)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var sounds = new RecordingSoundService();
            var rules = new RuleService(mail ?? new Mail(), new StubLocalStoreService(), dir) { Sounds = sounds };
            var account = Guid.NewGuid();
            rule.AccountId = account;
            rules.SaveRules([rule]);
            var incoming = Enumerable.Range(1, messages).Select(i => new MailMessageSummary
            {
                MessageId = i.ToString(), AccountId = account, FolderName = "INBOX", Subject = "Weekly digest",
                Date = DateTimeOffset.UtcNow.AddHours(-ageHours),   // -1 is a little ahead of the rule service starting.
            }).ToList();

            await rules.ApplyRulesAsync(incoming, account, TestContext.Current.CancellationToken);
            return sounds;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static MailRule Rule(string subject = "digest", string? sound = "builtin:reminder", params RuleAction[] actions) => new()
    {
        Name = "Digest", SubjectContains = subject, UseSubjectCondition = true, Sound = sound,
        Action = actions.Length == 0 ? RuleAction.PlaySound : actions[^1],
        Actions = actions.Length == 0 ? null : [.. actions, RuleAction.PlaySound],
        TargetFolder = "INBOX/Digests", CopyTargetFolder = "INBOX/Kept",
    };

    [Fact]
    public async Task AMatchingRulePlaysItsSound()
    {
        var sounds = await Run(Rule());
        Assert.Equal(["builtin:reminder"], sounds.RuleSounds);
    }

    [Fact]
    public async Task ARuleThatDoesNotMatchPlaysNothing()
    {
        var sounds = await Run(Rule(subject: "invoice"));
        Assert.Empty(sounds.RuleSounds);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 0)]    // A catch-up backlog, as for the new-mail sound.
    [InlineData(50, 0)]
    public async Task ABatchPlaysOnceUnlessItIsABacklog(int messages, int sounds)
    {
        var played = await Run(Rule(), messages: messages);
        Assert.Equal(sounds, played.RuleSounds.Count);
    }

    [Fact]
    public async Task MailFromBeforeQuickMailStartedPlaysNothing()
    {
        var played = await Run(Rule(), ageHours: 3);
        Assert.Empty(played.RuleSounds);
    }

    [Fact]
    public async Task ARuleSaysWhichMessagesItSoundedFor()
    {
        var played = await Run(Rule(), messages: 2);
        Assert.Equal(2, played.RuleSoundedKeys.Count);
    }

    [Fact]
    public async Task AnActionFromANewerQuickMailIsIgnoredWhenRun()
    {
        // What an older build does with PlaySound, pinned for the next action added after it.
        var rule = new MailRule
        {
            Name = "Future", SubjectContains = "digest", UseSubjectCondition = true,
            Action = (RuleAction)99, Actions = [(RuleAction)99, RuleAction.PlaySound],
        };
        var played = await Run(rule);
        Assert.Single(played.RuleSounds);
    }

    [Fact]
    public void TheWiringInTheAppIsThere()
    {
        // These lines make the feature work and nothing else would notice them gone.
        var source = RepoDir("QuickMail");
        Assert.Contains("ruleService.Sounds = _soundService", File.ReadAllText(Path.Combine(source, "App.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("sounds: _vm.Sounds);", File.ReadAllText(Path.Combine(source, "Views", "MainWindow.xaml.cs")), StringComparison.Ordinal);
        var rulesVm = File.ReadAllText(Path.Combine(source, "ViewModels", "UnifiedRulesViewModel.cs"));
        Assert.Equal(2, rulesVm.Split("editor.AttachSounds(_sounds)").Length - 1);   // The new-rule path and the edit path.

        var xaml = File.ReadAllText(Path.Combine(source, "Views", "ServerRuleEditorWindow.xaml"));
        Assert.Contains("IsEnabled=\"{Binding CanPlaySound}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(4, xaml.Split("IsEnabled=\"{Binding IsPlaySoundSelected}\"").Length - 1);   // Label, list and two buttons.
    }

    private static string RepoDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, name, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, name);
    }

    [Fact]
    public async Task AMoveThatWorkedPlaysTheSound()
    {
        var sounds = await Run(Rule(actions: RuleAction.MoveToFolder));
        Assert.Single(sounds.RuleSounds);
    }

    [Fact]
    public async Task AFailedMoveStaysSilent()
    {
        var sounds = await Run(Rule(actions: RuleAction.MoveToFolder),
            new Mail { ThrowOnMove = new NotSupportedException("That folder has been deleted") });
        Assert.Empty(sounds.RuleSounds);
    }

    [Fact]
    public async Task AFailedDeleteStaysSilent()
    {
        var sounds = await Run(Rule(actions: RuleAction.Delete),
            new Mail { ThrowOnDelete = new IOException("connection lost") });
        Assert.Empty(sounds.RuleSounds);
    }

    [Fact]
    public async Task AFailedCopyStaysSilent()
    {
        var sounds = await Run(Rule(actions: RuleAction.CopyToFolder),
            new Mail { ThrowOnCopy = new IOException("connection lost") });
        Assert.Empty(sounds.RuleSounds);
    }

    [Fact]
    public void TheSoundIsTheLastThingARuleDoes()
    {
        var rule = new MailRule
        {
            Action = RuleAction.PlaySound,
            Actions = [RuleAction.PlaySound, RuleAction.MoveToFolder, RuleAction.CopyToFolder, RuleAction.MarkAsRead],
        };
        Assert.Equal(RuleAction.PlaySound, rule.AllActions()[^1]);
    }

    [Fact]
    public async Task RunOnExistingMailIsSilent()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var account = Guid.NewGuid();
            var store = new StubLocalStoreService();
            store.SeededSummaries[(account, "INBOX")] =
            [
                new MailMessageSummary
                {
                    MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest",
                    Date = DateTimeOffset.UtcNow.AddHours(1),   // New enough that only the pass itself keeps it quiet.
                },
            ];
            var sounds = new RecordingSoundService();
            var rules = new RuleService(new Mail(), store, dir) { Sounds = sounds };
            var rule = Rule(actions: RuleAction.MoveToFolder);
            rule.AccountId = account;
            rules.SaveRules([rule]);

            var removed = (await rules.ApplyRulesToExistingAsync(
                store, new Dictionary<Guid, string> { [account] = "INBOX" }, TestContext.Current.CancellationToken)).Removed;

            Assert.Single(removed);   // The rule did run.
            Assert.Empty(sounds.RuleSounds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WithNoSoundServiceARuleStillDoesEverythingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var rules = new RuleService(new Mail(), new StubLocalStoreService(), dir);
            var account = Guid.NewGuid();
            var rule = Rule(actions: RuleAction.MoveToFolder);
            rule.AccountId = account;
            rules.SaveRules([rule]);
            var incoming = new List<MailMessageSummary>
            {
                new() { MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest" },
            };

            await rules.ApplyRulesAsync(incoming, account, TestContext.Current.CancellationToken);

            Assert.Empty(incoming);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── What's stored, and how it reads ─────────────────────────────────────────

    [Fact]
    public void TheActionsStoredNumberIsOnTheEnd()
    {
        // Stored by number, so an existing action must never change: rules.json on disk depends on it.
        Assert.Equal(0, (int)RuleAction.MarkAsRead);
        Assert.Equal(4, (int)RuleAction.CopyToFolder);
        Assert.Equal(5, (int)RuleAction.PlaySound);
    }

    [Fact]
    public void ARuleWithNoSoundWritesNoSoundField() =>
        Assert.DoesNotContain("\"Sound\"", JsonSerializer.Serialize(new MailRule { Name = "Plain" }), StringComparison.Ordinal);

    [Fact]
    public void AnActionFromANewerQuickMailLoads()
    {
        var rule = JsonSerializer.Deserialize<MailRule>("""{"Name":"Future","Action":99,"Actions":[0,99]}""");
        Assert.NotNull(rule);
        Assert.Contains((RuleAction)99, rule.AllActions());
    }

    [Fact]
    public void CloningARuleForAnAccountKeepsItsSound()
    {
        var clone = typeof(RuleService).GetMethod("CloneForAccount", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(clone);
        var copy = clone.Invoke(null, [new MailRule { Name = "All", Action = RuleAction.PlaySound, Sound = "builtin:reminder" }, Guid.NewGuid()]) as MailRule;
        Assert.Equal("builtin:reminder", copy!.Sound);
    }

    [Theory]
    [InlineData("builtin:reminder", "play sound Reminder")]
    [InlineData("custom:Pat.wav", "play sound Pat.wav")]
    [InlineData("builtin:wood/matched", "play sound Matched (Wood)")]
    [InlineData("builtin:matched", "play the default sound")]
    [InlineData(null, "play the default sound")]
    [InlineData("rubbish", "play the default sound")]
    [InlineData("none", "play the default sound")]
    public void TheRuleListSaysWhichSound(string? sound, string expected)
    {
        var row = UnifiedRuleRow.ForClient(new MailRule
        {
            Name = "Pat", AccountId = Guid.NewGuid(), Action = RuleAction.PlaySound, Sound = sound,
        });
        Assert.Contains(expected, row.RowText, StringComparison.Ordinal);
    }

    // ── A rule sound and new mail together, with the real flood rules ───────────

    private sealed class CountingOutput : ISoundOutput
    {
        private readonly List<string> _played = [];

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            lock (_played) _played.Add(clip.Key);
        }

        public List<string> Snapshot()
        {
            lock (_played) return [.. _played];
        }

        /// <summary>Waits for a first sound to arrive, then long enough for a wrongly queued second one to follow it.</summary>
        public async Task WaitUntilQuietAsync()
        {
            for (var i = 0; i < 500 && Snapshot().Count == 0; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        public void Check(string filePath) { }
    }

    [Fact]
    public async Task AMessageARuleSoundedForDoesNotAlsoSoundAsNewMailHoweverLongSyncTakes()
    {
        // The real rule engine, sound service and new-mail check, with the clock well past the 3-second
        // hold: the case of a sound-only rule leaving the message in the batch, and a slow reconcile.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var config = new StubConfigService();
            config.Load().PlayEventSounds = true;
            var now = DateTime.UtcNow;
            var output = new CountingOutput();
            using var service = new SoundService(config, dir, output, openBuiltIn: (_, _) => new MemoryStream(), utcNow: () => now);
            var vm = new MainViewModel(
                new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
                new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), config,
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
                soundService: service);
            var rules = new RuleService(new Mail(), new StubLocalStoreService(), dir) { Sounds = service };
            var account = new AccountModel { Id = Guid.NewGuid(), AccountName = "Work", Username = "work@example.com" };
            var rule = Rule();
            rule.AccountId = account.Id;
            rules.SaveRules([rule]);
            List<MailMessageSummary> incoming =
            [
                new() { MessageId = "7", AccountId = account.Id, FolderName = "INBOX", Subject = "Weekly digest", Date = DateTimeOffset.UtcNow.AddHours(1) },
            ];

            await rules.ApplyRulesAsync(incoming, account.Id, TestContext.Current.CancellationToken);
            Assert.Single(incoming);   // A sound-only rule leaves the message where it is.

            now += TimeSpan.FromMinutes(1);
            vm.MaybePlayMailSounds(account, incoming, isInbox: true);

            await output.WaitUntilQuietAsync();
            Assert.Equal(["builtin:reminder"], output.Snapshot());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ANewRuleInTheRulesWindowCanSeeCustomSounds()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), AccountName = "Work", Username = "work@example.com" };
        var sounds = new RecordingSoundService { CustomFiles = { "Pat.wav" } };
        var vm = new UnifiedRulesViewModel(new StubRuleService(), serverRules: null, [account],
            preferredAccountId: account.Id, sounds: sounds);
        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        vm.NewRuleCommand.Execute(null);

        Assert.NotNull(editor);
        Assert.Contains(editor.RuleSoundChoices, c => c.Label == "Pat.wav");
    }

    [Fact]
    public void ClosingTheEditorStopsOnlyItsOwnPreview()
    {
        var sounds = new RecordingSoundService();
        var vm = Named();
        vm.AttachSounds(sounds);

        vm.StopSounds();
        Assert.Equal(0, sounds.Stops);

        vm.PreviewRuleSoundCommand.Execute(null);
        vm.StopSounds();
        Assert.Equal(1, sounds.Stops);
    }

    [Fact]
    public void ARefusalIsClearedOnceTheSoundOrTheBoxChanges()
    {
        var vm = Named();
        vm.AttachSounds(new RecordingSoundService { RefuseWith = "No." });
        vm.PickSoundFileRequested = () => @"C:\Music\junk.wav";
        vm.ChooseRuleSoundFileCommand.Execute(null);
        Assert.True(vm.HasRuleSoundMessage);

        vm.SelectedRuleSound = vm.RuleSoundChoices.Single(c => c.Label == "Reminder");

        Assert.False(vm.HasRuleSoundMessage);
    }
}
