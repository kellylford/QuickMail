using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Unread count and Unread of total fields on conversation and sender group rows (#800).
/// Has unread only says whether any message in a group is unread; these say how many, so the user
/// can tell one new reply from a whole thread they have not read.
/// </summary>
public class UnreadCountRowFieldTests
{
    private static readonly RowKind[] GroupKinds = [RowKind.Conversation, RowKind.SenderGroup];

    private static MailMessageSummary Msg(string id, bool isRead) => new()
    {
        MessageId  = id,
        AccountId  = Guid.Empty,
        FolderName = "INBOX",
        From       = "Chris Lee",
        Subject    = "Budget review",
        Date       = DateTimeOffset.Now,
        IsRead     = isRead,
    };

    private static List<MailMessageSummary> Messages(int unread, int total) =>
        Enumerable.Range(0, total).Select(i => Msg(i.ToString(System.Globalization.CultureInfo.InvariantCulture), i >= unread)).ToList();

    private static ConversationGroup Conversation(List<MailMessageSummary> msgs) =>
        new() { NormalizedSubject = "budget review", Messages = msgs };

    private static SenderGroup Sender(List<MailMessageSummary> msgs) =>
        new() { SenderKey = "Chris Lee", Messages = msgs };

    private static object Group(RowKind kind, List<MailMessageSummary> msgs) =>
        kind == RowKind.Conversation ? Conversation(msgs) : Sender(msgs);

    /// <summary>A layout with only the named fields on, in that order.</summary>
    private static List<RowFieldSetting> Only(RowKind kind, params string[] ids)
    {
        var layout = RowFieldCatalog.DefaultLayout(kind);
        foreach (var f in layout) f.Enabled = false;
        return ids.Select(id => { var f = layout.First(s => s.Id == id); f.Enabled = true; return f; })
                  .Concat(layout.Where(s => !ids.Contains(s.Id)))
                  .ToList();
    }

    private static string Speak(RowKind kind, object row, List<RowFieldSetting> layout, bool labels = false) =>
        RowSpeechBuilder.Build(kind, RowFieldCatalog.ValuesFor(kind, row), layout, labels);

    // ── what the fields say ───────────────────────────────────────────────────

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void UnreadCount_SaysHowManyAreUnread(RowKind kind)
    {
        var row = Group(kind, Messages(unread: 2, total: 5));
        Assert.Equal("2 unread.", Speak(kind, row, Only(kind, "unreadcount")));
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void UnreadOfTotal_SaysHowManyOutOfHowMany(RowKind kind)
    {
        var row = Group(kind, Messages(unread: 2, total: 5));
        Assert.Equal("2 of 5 unread.", Speak(kind, row, Only(kind, "unreadoftotal")));
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void BothFieldsAreSilentWhenEverythingIsRead(RowKind kind)
    {
        // Like Has unread: an all-read group stays as short as it was, rather than gaining
        // "0 unread" on every row.
        var row = Group(kind, Messages(unread: 0, total: 5));
        Assert.Equal("5 messages.", Speak(kind, row, Only(kind, "count", "unreadcount", "unreadoftotal")));
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void FieldLabelsDoNotPrefixTheCounts(RowKind kind)
    {
        // Self-describing, like "3 messages": "Unread: 2 unread" would be noise.
        var row = Group(kind, Messages(unread: 1, total: 4));
        Assert.Equal("1 unread. 1 of 4 unread.",
            Speak(kind, row, Only(kind, "unreadcount", "unreadoftotal"), labels: true));
    }

    [Fact]
    public void TheCustomersExampleReads()
    {
        // The form #800 asked for: the unread count next to the total, then the subject.
        var row = Conversation(Messages(unread: 6, total: 8));
        Assert.Equal("6 unread. 8 messages. Budget review.",
            Speak(RowKind.Conversation, row, Only(RowKind.Conversation, "unreadcount", "count", "subject")));
    }

    // ── nobody's row changes until they ask ───────────────────────────────────

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void NewFieldsAreOffInTheDefaultLayout(RowKind kind)
    {
        var layout = RowFieldCatalog.DefaultLayout(kind);
        Assert.False(layout.Single(f => f.Id == "unreadcount").Enabled);
        Assert.False(layout.Single(f => f.Id == "unreadoftotal").Enabled);
    }

    [Fact]
    public void ASavedLayoutGainsTheNewFieldsTurnedOff()
    {
        // A rowlayout.json saved before #800 has no entry for these ids. Reconcile must add them
        // (so the chooser lists them) and must not switch them on.
        var saved = RowFieldCatalog.DefaultLayouts();
        saved.Conversation.RemoveAll(f => f.Id is "unreadcount" or "unreadoftotal");
        saved.SenderGroup.RemoveAll(f => f.Id is "unreadcount" or "unreadoftotal");

        saved.Reconcile();

        foreach (var kind in GroupKinds)
        {
            var layout = saved.For(kind);
            Assert.False(layout.Single(f => f.Id == "unreadcount").Enabled);
            Assert.False(layout.Single(f => f.Id == "unreadoftotal").Enabled);
        }
    }

    // ── the row keeps up as messages are read ─────────────────────────────────

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void ReadingAMessageRefreshesTheCounts(RowKind kind)
    {
        // The row's spoken name is a binding on these properties, so it only changes if they raise
        // PropertyChanged when a message in the group is read.
        var msgs = Messages(unread: 2, total: 3);
        var row = Group(kind, msgs);
        var raised = new List<string?>();
        ((System.ComponentModel.INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        msgs[0].IsRead = true;

        Assert.Contains("UnreadCount", raised);
        Assert.Contains("UnreadOfTotal", raised);
        Assert.Equal("1 unread. 1 of 3 unread.",
            Speak(kind, row, Only(kind, "unreadcount", "unreadoftotal")));
    }

    // ── the chooser ───────────────────────────────────────────────────────────

    private static (RowFieldsViewModel vm, Func<string, RowFieldRow> Field) Chooser(RowKind kind)
    {
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());
        vm.SelectedRowKind = vm.RowKinds.First(k => k.Kind == kind);
        return (vm, id => vm.Fields.First(f => f.Id == id));
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void TheChooserListsBothFields(RowKind kind)
    {
        var (vm, _) = Chooser(kind);
        Assert.Contains(vm.Fields, f => f.Id == "unreadcount" && f.DisplayName == "Unread count");
        Assert.Contains(vm.Fields, f => f.Id == "unreadoftotal" && f.DisplayName == "Unread of total");
    }

    [Fact]
    public void TurningOnACountWithHasUnreadOn_SaysItIsSaidTwice()
    {
        var (vm, field) = Chooser(RowKind.Conversation);   // Has unread is on by default
        vm.SelectedField = field("unreadcount");

        field("unreadcount").Enabled = true;

        Assert.Contains("Has unread is also on", vm.SelectedFieldNote, StringComparison.Ordinal);
    }

    [Fact]
    public void HasUnreadExplainsTheDoubling_OnlyWhileACountIsOn()
    {
        var (vm, field) = Chooser(RowKind.SenderGroup);
        vm.SelectedField = field("hasunread");
        Assert.Equal(string.Empty, vm.SelectedFieldNote);

        field("unreadoftotal").Enabled = true;
        Assert.Contains("already says there is unread mail", vm.SelectedFieldNote, StringComparison.Ordinal);

        field("unreadoftotal").Enabled = false;
        Assert.Equal(string.Empty, vm.SelectedFieldNote);
    }

    [Fact]
    public void BothCountsOn_EachSaysTheOtherRepeatsIt()
    {
        var (vm, field) = Chooser(RowKind.Conversation);
        field("hasunread").Enabled = false;
        field("unreadcount").Enabled = true;
        field("unreadoftotal").Enabled = true;

        vm.SelectedField = field("unreadcount");
        Assert.Contains("Unread of total is also on", vm.SelectedFieldNote, StringComparison.Ordinal);

        vm.SelectedField = field("unreadoftotal");
        Assert.Contains("Unread count is also on", vm.SelectedFieldNote, StringComparison.Ordinal);
    }

    // ── through the WPF converter ─────────────────────────────────────────────

    private static string Convert(RowKind kind, object?[] fieldValues, params string[] on)
    {
        var layouts = RowFieldCatalog.DefaultLayouts();
        var layout  = layouts.For(kind);
        foreach (var f in layout) f.Enabled = on.Contains(f.Id);
        var values  = fieldValues.Append(new RowSpeechSettings(layouts, false)).ToArray();
        return (string)new QuickMail.Views.RowSpeechConverter(kind).Convert(
            values!, typeof(string), parameter: null!, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void TheConverterPassesTheBoundCountsThrough(RowKind kind)
    {
        // The real binding path: a boxed int and a boxed UnreadTally in the last two field slots.
        var values = RowFieldCatalog.ValuesFor(kind, Group(kind, Messages(unread: 3, total: 7)));
        Assert.Equal("3 unread. 3 of 7 unread.", Convert(kind, values, "unreadcount", "unreadoftotal"));
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void UnsetCountsAreSilentRatherThanStringified(RowKind kind)
    {
        // WPF hands UnsetValue through while a template is being set up.
        var values = RowFieldCatalog.ValuesFor(kind, Group(kind, Messages(unread: 3, total: 7)));
        values[^2] = System.Windows.DependencyProperty.UnsetValue;
        values[^1] = System.Windows.DependencyProperty.UnsetValue;

        var name = Convert(kind, values, "count", "unreadcount", "unreadoftotal");

        Assert.Equal("7 messages.", name);
    }

    [Fact]
    public void ACountTurnedOff_GetsNoNoteAboutDoubling()
    {
        // An off field cannot double anything, whatever else is on.
        var (vm, field) = Chooser(RowKind.Conversation);
        field("unreadoftotal").Enabled = true;
        vm.SelectedField = field("unreadcount");

        Assert.Equal(string.Empty, vm.SelectedFieldNote);
    }

    [Fact]
    public void AllThreeOn_EachFieldStillHasANote()
    {
        var (vm, field) = Chooser(RowKind.Conversation);
        field("unreadcount").Enabled = true;
        field("unreadoftotal").Enabled = true;

        foreach (var id in new[] { "hasunread", "unreadcount", "unreadoftotal" })
        {
            vm.SelectedField = field(id);
            Assert.NotEqual(string.Empty, vm.SelectedFieldNote);
        }
    }

    [Fact]
    public void ACountOnItsOwnHasNoNote()
    {
        var (vm, field) = Chooser(RowKind.Conversation);
        field("hasunread").Enabled = false;
        field("unreadcount").Enabled = true;

        vm.SelectedField = field("unreadcount");

        Assert.Equal(string.Empty, vm.SelectedFieldNote);
    }
}
