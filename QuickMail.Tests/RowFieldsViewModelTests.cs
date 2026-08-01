using System;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Message List Fields chooser. It has no OK/Cancel — every mutation saves immediately, and
/// that save is also what re-speaks the rows behind the window — so "did it persist" is the
/// central assertion in almost every case here.
/// </summary>
public class RowFieldsViewModelTests
{
    private static (RowFieldsViewModel vm, StubRowLayoutService layouts, StubConfigService cfg) Make()
    {
        var layouts = new StubRowLayoutService();
        var cfg     = new StubConfigService();
        return (new RowFieldsViewModel(layouts, cfg), layouts, cfg);
    }

    private static RowFieldRow Field(RowFieldsViewModel vm, string id) =>
        vm.Fields.First(f => f.Id == id);

    // ── initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void OpensOnMessagesWithEveryFieldListedAndTheFirstSelected()
    {
        var (vm, _, _) = Make();

        Assert.Equal(RowKind.Message, vm.SelectedRowKind.Kind);
        Assert.Equal(RowFieldCatalog.For(RowKind.Message).Count, vm.Fields.Count);
        Assert.Same(vm.Fields[0], vm.SelectedField);
        Assert.False(vm.CanMoveUp);
        Assert.True(vm.CanMoveDown);
    }

    [Fact]
    public void OpeningTheWindowDoesNotWriteAnything()
    {
        // Merely looking at the chooser must not rewrite the user's file.
        var (_, layouts, _) = Make();
        Assert.Equal(0, layouts.SaveCount);
    }

    [Fact]
    public void PreviewMatchesWhatTheRowWouldActuallySpeak()
    {
        var (vm, _, _) = Make();

        var expected = RowSpeechBuilder.Build(
            RowKind.Message,
            RowFieldsViewModel.SampleValues(RowKind.Message),
            vm.Fields.Select(f => f.ToSetting()).ToList(),
            showLabels: false);

        Assert.Equal(expected, vm.Preview);
        Assert.Contains("Chris Lee", vm.Preview, StringComparison.Ordinal);
    }

    // ── moving ────────────────────────────────────────────────────────────────

    [Fact]
    public void MoveDown_ReordersPersistsAndAnnouncesThePosition()
    {
        var (vm, layouts, _) = Make();
        string? announced = null;
        vm.AnnouncementRequested += (text, cat) =>
        {
            announced = text;
            // An action outcome is a Result — users who silence background Status still hear it.
            Assert.Equal(AnnouncementCategory.Result, cat);
        };

        var moved = vm.SelectedField!;
        vm.MoveDownCommand.Execute(null);

        Assert.Equal(1, vm.Fields.IndexOf(moved));
        Assert.Same(moved, vm.SelectedField);          // selection follows the field
        Assert.Equal($"Moved down. Position 2 of {vm.Fields.Count}.", announced);
        Assert.Equal(1, layouts.SaveCount);
        Assert.Equal(moved.Id, layouts.Load().Message[1].Id);
    }

    [Fact]
    public void MoveUp_IsBlockedAtTheTopAndMoveDownAtTheBottom()
    {
        var (vm, _, _) = Make();

        vm.SelectedField = vm.Fields[0];
        Assert.False(vm.CanMoveUp);
        Assert.False(vm.MoveUpCommand.CanExecute(null));

        vm.SelectedField = vm.Fields[^1];
        Assert.False(vm.CanMoveDown);
        Assert.False(vm.MoveDownCommand.CanExecute(null));
        Assert.True(vm.CanMoveUp);
    }

    [Fact]
    public void MovingChangesTheSpokenOrderInThePreview()
    {
        var (vm, _, _) = Make();

        // "from" ahead of the status token.
        vm.SelectedField = Field(vm, "from");
        vm.MoveUpCommand.Execute(null);
        vm.MoveUpCommand.Execute(null);

        Assert.Contains("Chris Lee. unread", vm.Preview, StringComparison.Ordinal);
    }

    // ── enabling and speak modes ──────────────────────────────────────────────

    [Fact]
    public void TogglingAFieldPersistsAndDropsItFromThePreview()
    {
        var (vm, layouts, _) = Make();

        Field(vm, "preview").Enabled = false;

        Assert.DoesNotContain("Lunch tomorrow?", vm.Preview, StringComparison.Ordinal);
        Assert.False(layouts.Load().Message.First(f => f.Id == "preview").Enabled);
    }

    [Fact]
    public void UnreadOnlyWhenTrue_IsTheSupplementRequestEndToEnd()
    {
        // "I want to know about unread but not read."
        var (vm, layouts, _) = Make();

        Field(vm, "status").Enabled = false;
        var unread = Field(vm, "unread");
        unread.Enabled   = true;
        unread.SpeakMode = SpeakMode.WhenTrue;

        var saved = layouts.Load().Message;
        Assert.False(saved.First(f => f.Id == "status").Enabled);
        Assert.True(saved.First(f => f.Id == "unread").Enabled);
        Assert.Equal(SpeakMode.WhenTrue, saved.First(f => f.Id == "unread").SpeakMode);

        // The sample row is unread, so the preview says so — and never says "read".
        Assert.Contains("unread", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeakModeRadioProxiesTrackTheSelectedFieldAndAreExclusive()
    {
        var (vm, _, _) = Make();

        vm.SelectedField = Field(vm, "attachments");
        Assert.True(vm.SelectedIsState);
        Assert.True(vm.SpeakWhenTrue);
        Assert.False(vm.SpeakAlways);

        vm.SpeakAlways = true;
        Assert.True(vm.SpeakAlways);
        Assert.False(vm.SpeakWhenTrue);
        Assert.Equal(SpeakMode.Always, Field(vm, "attachments").SpeakMode);
    }

    [Fact]
    public void SelectingATextFieldHidesTheSpeakModeChoice()
    {
        var (vm, _, _) = Make();

        vm.SelectedField = Field(vm, "subject");

        Assert.False(vm.SelectedIsState);
    }

    // ── row types ─────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingRowTypeEditsThatTypesLayoutOnly()
    {
        var (vm, layouts, _) = Make();

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.Conversation);
        Assert.Equal(RowFieldCatalog.For(RowKind.Conversation).Count, vm.Fields.Count);

        Field(vm, "preview").Enabled = false;

        var saved = layouts.Load();
        Assert.False(saved.Conversation.First(f => f.Id == "preview").Enabled);
        // The message layout is untouched.
        Assert.True(saved.Message.First(f => f.Id == "preview").Enabled);
    }

    [Fact]
    public void EditsSurviveSwitchingAwayAndBack()
    {
        var (vm, _, _) = Make();

        Field(vm, "date").Enabled = false;
        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.SenderGroup);
        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.Message);

        Assert.False(Field(vm, "date").Enabled);
    }

    [Fact]
    public void SwitchingRowTypeDoesNotSaveByItself()
    {
        var (vm, layouts, _) = Make();

        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.SenderGroup);

        Assert.Equal(0, layouts.SaveCount);
    }

    // ── labels ────────────────────────────────────────────────────────────────

    [Fact]
    public void ShowFieldLabels_PersistsToConfigAndReachesThePreview()
    {
        var (vm, _, cfg) = Make();

        vm.ShowFieldLabels = true;

        Assert.True(cfg.Load().MessageListShowFieldLabels);
        Assert.Contains("From: Chris Lee", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowFieldLabels_IsReadBackWhenTheWindowReopens()
    {
        var layouts = new StubRowLayoutService();
        var cfg     = new StubConfigService();
        new RowFieldsViewModel(layouts, cfg).ShowFieldLabels = true;

        Assert.True(new RowFieldsViewModel(layouts, cfg).ShowFieldLabels);
    }

    // ── reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_RestoresDefaultsForTheCurrentRowTypeOnly()
    {
        var (vm, layouts, _) = Make();

        // Disturb both layouts.
        Field(vm, "subject").Enabled = false;
        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == RowKind.Conversation);
        Field(vm, "subject").Enabled = false;

        vm.ResetDefaultsCommand.Execute(null);

        var saved = layouts.Load();
        Assert.True(saved.Conversation.First(f => f.Id == "subject").Enabled);
        // Messages was not the selected row type, so it keeps the user's change.
        Assert.False(saved.Message.First(f => f.Id == "subject").Enabled);
    }

    [Fact]
    public void Reset_AnnouncesWhichRowTypeWasReset()
    {
        var (vm, _, _) = Make();
        string? announced = null;
        vm.AnnouncementRequested += (text, cat) =>
        {
            announced = text;
            Assert.Equal(AnnouncementCategory.Result, cat);
        };

        vm.ResetDefaultsCommand.Execute(null);

        Assert.Equal("Messages fields reset to defaults.", announced);
    }

    // ── accessibility of the list itself ──────────────────────────────────────

    [Fact]
    public void RowsAndRowTypesSpeakTheirDisplayText()
    {
        // Selector items take their accessible name from ToString(), not DisplayMemberPath.
        var (vm, _, _) = Make();

        Assert.Equal("Subject", Field(vm, "subject").ToString());
        Assert.Equal("Messages", vm.RowKinds[0].ToString());
        Assert.Equal("Conversation groups", vm.RowKinds[1].ToString());
        Assert.Equal("Sender and recipient groups", vm.RowKinds[2].ToString());
    }

    [Fact]
    public void EverythingOff_PreviewSaysTheRowWouldBeSilent()
    {
        var (vm, _, _) = Make();

        foreach (var f in vm.Fields) f.Enabled = false;

        Assert.Contains("silent", vm.Preview, StringComparison.OrdinalIgnoreCase);
    }
}
