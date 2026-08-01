using System;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Spoken preview must equal what the row would actually say, after EVERY operation — not just
/// on open. If it drifts once, it stops being usable as the thing you check before closing the
/// window, which is its whole job.
/// </summary>
public class RowFieldsPreviewConsistencyTests
{
    private static RowFieldsViewModel Make() =>
        new(new StubRowLayoutService(), new StubConfigService());

    /// <summary>What the current settings would actually produce, computed independently.</summary>
    private static string Expected(RowFieldsViewModel vm, object? sampleRow = null)
    {
        var kind = vm.SelectedRowKind.Kind;
        var values = sampleRow is not null && kind == RowKind.Message
            ? RowFieldCatalog.ValuesFor(kind, sampleRow)
            : RowFieldsViewModel.SampleValues(kind);
        return RowSpeechBuilder.Build(
            kind, values, vm.Fields.Select(f => f.ToSetting()).ToList(), vm.ShowFieldLabels);
    }

    private static void AssertConsistent(RowFieldsViewModel vm, string after)
    {
        var expected = Expected(vm);
        if (string.IsNullOrEmpty(expected))
        {
            Assert.Contains("silent", vm.Preview, StringComparison.OrdinalIgnoreCase);
            return;
        }
        Assert.Equal(expected, vm.Preview);
    }

    [Fact]
    public void PreviewTracksEveryKindOfChange()
    {
        var vm = Make();
        AssertConsistent(vm, "open");

        vm.MoveDownCommand.Execute(null);
        AssertConsistent(vm, "move down");

        vm.MoveUpCommand.Execute(null);
        AssertConsistent(vm, "move up");

        vm.Fields.First(f => f.Id == "preview").Enabled = false;
        AssertConsistent(vm, "disable a field");

        var unread = vm.Fields.First(f => f.Id == "unread");
        unread.Enabled = true;
        AssertConsistent(vm, "enable a field");

        unread.SpeakMode = SpeakMode.Always;
        AssertConsistent(vm, "change speak mode");

        vm.SelectedField = unread;
        vm.SpeakWhenTrue = true;
        AssertConsistent(vm, "change speak mode via the radio proxy");

        vm.ShowFieldLabels = true;
        AssertConsistent(vm, "turn labels on");

        vm.ShowFieldLabels = false;
        AssertConsistent(vm, "turn labels off");

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.Conversation);
        AssertConsistent(vm, "switch row type");

        vm.Fields.First(f => f.Id == "count").Enabled = false;
        AssertConsistent(vm, "disable a field on the new row type");

        vm.ResetDefaultsCommand.Execute(null);
        AssertConsistent(vm, "reset to defaults");

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.SenderGroup);
        AssertConsistent(vm, "switch to the third row type");

        foreach (var f in vm.Fields) f.Enabled = false;
        AssertConsistent(vm, "disable everything");
    }

    [Fact]
    public void PreviewIsNotStaleAfterSwitchingRowTypeAndBack()
    {
        var vm = Make();

        vm.Fields.First(f => f.Id == "date").Enabled = false;
        var messagePreview = vm.Preview;

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.SenderGroup);
        Assert.NotEqual(messagePreview, vm.Preview);

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.Message);
        Assert.Equal(messagePreview, vm.Preview);
    }

    /// <summary>
    /// Both "Status (combined)" and "Unread" say the word "unread". With both enabled the row says
    /// it twice — which is what a user gets by turning Unread on without noticing Status is already
    /// on. The preview has to show that honestly, so it is visible before the window closes.
    /// </summary>
    [Fact]
    public void OverlappingStatusFields_AreShownDoubledRatherThanHidden()
    {
        var vm = Make();
        vm.Fields.First(f => f.Id == "unread").Enabled = true;

        Assert.Equal(2, vm.Preview.Split("unread", StringSplitOptions.None).Length - 1);
    }

    // ── previewing the row the user is actually standing on ───────────────────

    [Fact]
    public void PreviewUsesTheSelectedMessage_NotASyntheticSample()
    {
        // The reported problem: the synthetic sample is flagged, has an attachment and a source
        // folder, so it said things the user's real rows never said.
        var real = new MailMessageSummary
        {
            From = "Angela", Subject = "Updates", Preview = "Sneak peek",
            Date = DateTimeOffset.Now, IsRead = true,   // read, unflagged, no attachment, no folder
        };
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService(), real);

        Assert.Equal($"read. Angela. Updates. Sneak peek. {real.DateDisplay}.", vm.Preview);
        Assert.DoesNotContain("Follow up", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("attachments", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewStaysConsistentWithARealMessageThroughEveryChange()
    {
        var real = new MailMessageSummary
        {
            From = "Angela", Subject = "Updates", Preview = "Sneak peek",
            Date = DateTimeOffset.Now, IsRead = true,
        };
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService(), real);

        Assert.Equal(Expected(vm, real), vm.Preview);

        vm.MoveDownCommand.Execute(null);
        Assert.Equal(Expected(vm, real), vm.Preview);

        vm.Fields.First(f => f.Id == "status").Enabled = false;
        vm.Fields.First(f => f.Id == "unread").Enabled = true;
        Assert.Equal(Expected(vm, real), vm.Preview);
        // The message is read and Unread is "only when true", so nothing is said about status.
        Assert.DoesNotContain("read", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewFallsBackToTheSampleForARowTypeTheSelectionIsNot()
    {
        var real = new MailMessageSummary { From = "Angela", Subject = "Updates", Date = DateTimeOffset.Now };
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService(), real);

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.Conversation);

        // A message cannot preview a conversation-group row; the sample carries it.
        Assert.Contains("messages", vm.Preview, StringComparison.Ordinal);
    }

    // ── the two cues that answer "I moved it and nothing changed" ─────────────

    [Fact]
    public void MovingAFieldThatIsOff_SaysSoInTheAnnouncement()
    {
        var vm = Make();
        string? announced = null;
        vm.AnnouncementRequested += (text, _) => announced = text;

        // "unread" ships disabled — exactly the field that was moved in the bug report.
        vm.SelectedField = vm.Fields.First(f => f.Id == "unread");
        vm.MoveUpCommand.Execute(null);

        Assert.EndsWith("Not spoken.", announced!, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingAFieldThatIsOn_DoesNotSayNotSpoken()
    {
        var vm = Make();
        string? announced = null;
        vm.AnnouncementRequested += (text, _) => announced = text;

        vm.SelectedField = vm.Fields.First(f => f.Id == "subject");
        vm.MoveUpCommand.Execute(null);

        Assert.DoesNotContain("Not spoken", announced!, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingUnreadWhileStatusIsOn_ExplainsTheOverlap()
    {
        var vm = Make();

        vm.SelectedField = vm.Fields.First(f => f.Id == "unread");

        Assert.Contains("Status (combined) is also on", vm.SelectedFieldNote, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningStatusOff_ClearsTheOverlapNote()
    {
        var vm = Make();
        vm.SelectedField = vm.Fields.First(f => f.Id == "unread");
        Assert.NotEqual(string.Empty, vm.SelectedFieldNote);

        vm.Fields.First(f => f.Id == "status").Enabled = false;

        Assert.Equal(string.Empty, vm.SelectedFieldNote);
    }

    [Fact]
    public void SelectingStatus_SaysWhatItActuallySpeaks()
    {
        var vm = Make();

        vm.SelectedField = vm.Fields.First(f => f.Id == "status");

        Assert.Contains("replied, forwarded, unread, or read", vm.SelectedFieldNote, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextFieldsHaveNoNote()
    {
        var vm = Make();

        vm.SelectedField = vm.Fields.First(f => f.Id == "subject");

        Assert.Equal(string.Empty, vm.SelectedFieldNote);
    }
}
