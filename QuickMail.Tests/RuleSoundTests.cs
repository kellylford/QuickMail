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
        Assert.Equal(["builtin:arrived"], sounds.Previewed);
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
        public List<string> Calls { get; } = [];
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

    private static async Task<RecordingSoundService> Run(MailRule rule, Mail? mail = null, int messages = 1)
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

    [Fact]
    public async Task FiftyMatchesPlayOnce()
    {
        var sounds = await Run(Rule(), messages: 50);
        Assert.Single(sounds.RuleSounds);
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
                new MailMessageSummary { MessageId = "7", AccountId = account, FolderName = "INBOX", Subject = "Weekly digest" },
            ];
            var sounds = new RecordingSoundService();
            var rules = new RuleService(new Mail(), store, dir) { Sounds = sounds };
            var rule = Rule();
            rule.AccountId = account;
            rules.SaveRules([rule]);

            await rules.ApplyRulesToExistingAsync(
                store, new Dictionary<Guid, string> { [account] = "INBOX" }, TestContext.Current.CancellationToken);

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
    public void AnActionFromANewerQuickMailLoadsAndIsIgnoredWhenRun()
    {
        // What an older build does with PlaySound, pinned for the next action added after it.
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
    [InlineData(null, "play sound Matched")]
    [InlineData("rubbish", "play sound Matched")]
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
        public List<string> Played { get; } = [];

        public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
        {
            lock (Played) Played.Add(clip.Key);
        }

        public void Check(string filePath) { }
    }

    [Fact]
    public async Task AMessageARuleSoundedForDoesNotAlsoSoundAsNewMail()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var config = new StubConfigService();
        config.Load().PlayEventSounds = true;
        var output = new CountingOutput();
        using var service = new SoundService(config, folder, output, openBuiltIn: (_, _) => new MemoryStream());

        service.PlayRule("builtin:reminder");
        service.Play(SoundEvent.NewMail);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        lock (output.Played) Assert.Equal(["builtin:reminder"], output.Played);
    }
}
