using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the composed spoken string of a list row. This is the whole accessibility surface of
/// the message list, so it is pinned here rather than left to visual inspection: the default layout
/// must reproduce what the app has always said, and every reorder/enable/mode option must do
/// exactly what the chooser promises.
/// </summary>
public class RowSpeechBuilderTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Bound values in catalog order for a message row. One entry per catalog field — see
    /// <see cref="SampleValuesCoverEveryCatalogField"/>, which pins that.
    /// </summary>
    private static object?[] MessageValues(
        string flag = "", string status = "unread", bool attachments = false,
        string from = "Chris Lee", string subject = "Budget review",
        string preview = "Lunch tomorrow?", string date = "2:14P",
        bool isUnread = true, bool replied = false, bool forwarded = false,
        string to = "", string folder = "", bool mailingList = false, bool watched = false)
        => [flag, status, attachments, from, subject, preview, date,
            isUnread, replied, forwarded, to, folder, mailingList, watched];

    private static object?[] ConversationValues(
        string subject = "Budget review", int count = 3, string sender = "Chris Lee",
        string? flag = null, bool hasUnread = false,
        string preview = "Lunch tomorrow?", string date = "2:14P")
        => [subject, count, sender, flag, hasUnread, preview, date];

    private static object?[] SenderGroupValues(
        string sender = "Chris Lee", int count = 3, string? flag = null, bool hasUnread = false,
        string preview = "Lunch tomorrow?", string date = "2:14P", string newestSubject = "Budget review")
        => [sender, count, flag, hasUnread, preview, date, newestSubject];

    private static List<RowFieldSetting> Layout(RowKind kind) => RowFieldCatalog.DefaultLayout(kind);

    private static RowFieldSetting Field(List<RowFieldSetting> layout, string id) =>
        layout.First(f => f.Id == id);

    private static void Move(List<RowFieldSetting> layout, string id, int delta)
    {
        var idx = layout.FindIndex(f => f.Id == id);
        var item = layout[idx];
        layout.RemoveAt(idx);
        layout.Insert(idx + delta, item);
    }

    // ── the migration guarantee ───────────────────────────────────────────────

    [Fact]
    public void DefaultLayout_ReproducesTheHistoricalMessageString()
    {
        // What MessageAccessibleNameConverter emitted for a flagged, unread message with an
        // attachment: "{flag}. {status}. attachments. {from}. {subject}. {preview}. {date}."
        // The word now comes from the separate "unread" field rather than "status", in the same
        // position — an unread row is unchanged by the #558 default change.
        var text = RowSpeechBuilder.Build(
            RowKind.Message,
            MessageValues(flag: "Follow up", isUnread: true, attachments: true),
            Layout(RowKind.Message));

        Assert.Equal(
            "Follow up. unread. attachments. Chris Lee. Budget review. Lunch tomorrow?. 2:14P.",
            text);
    }

    [Fact]
    public void DefaultLayout_SaysNothingAboutReadStateOnAReadMessage()
    {
        // #558: the app used to say "read" on every read row, and the field that said it had no
        // speak mode to turn that off. Silence on read is now the default.
        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(isUnread: false), Layout(RowKind.Message));

        Assert.Equal("Chris Lee. Budget review. Lunch tomorrow?. 2:14P.", text);
        Assert.DoesNotContain("read", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultLayout_SaysUnreadAndRepliedTogether_WhichTheCombinedFieldCouldNot()
    {
        // status's priority chain returns "replied" first and never reaches the unread test, so a
        // replied-but-unread message used to lose its unread state entirely.
        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(isUnread: true, replied: true), Layout(RowKind.Message));

        Assert.StartsWith("unread. replied.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultLayout_OmitsFlagAndAttachmentsWhenAbsent()
    {
        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(), Layout(RowKind.Message));

        Assert.Equal("unread. Chris Lee. Budget review. Lunch tomorrow?. 2:14P.", text);
    }

    [Fact]
    public void TheOldSingleWordIsStillAvailableByTurningStatusBackOn()
    {
        // The default changed; the capability did not. Anyone who preferred one word restores it.
        var layout = Layout(RowKind.Message);
        Field(layout, "status").Enabled  = true;
        Field(layout, "unread").Enabled  = false;
        Field(layout, "replied").Enabled = false;
        // Off-by-default fields are appended, so status starts at the tail; put it back in the
        // slot it used to hold, behind flag.
        Move(layout, "status", 1 - layout.FindIndex(f => f.Id == "status"));

        Assert.Equal(
            "read. Chris Lee. Budget review. Lunch tomorrow?. 2:14P.",
            RowSpeechBuilder.Build(
                RowKind.Message, MessageValues(status: "read", isUnread: false), layout));
    }

    [Fact]
    public void DefaultLayout_SpeaksSourceFolderLastInAggregateViews()
    {
        // #423: FolderDisplayName is stamped only in aggregate views, and was appended after the
        // date. That placement must survive the move to layouts.
        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(folder: "Work — Archive"), Layout(RowKind.Message));

        Assert.EndsWith("2:14P. Work — Archive.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultLayout_ReproducesTheHistoricalConversationAndSenderStrings()
    {
        Assert.Equal(
            "Budget review. 3 messages. Chris Lee. Follow up. Has unread. Lunch tomorrow?. 2:14P.",
            RowSpeechBuilder.Build(
                RowKind.Conversation,
                ConversationValues(flag: "Follow up", hasUnread: true),
                Layout(RowKind.Conversation)));

        Assert.Equal(
            "Chris Lee. 3 messages. Follow up. Has unread. Lunch tomorrow?. 2:14P.",
            RowSpeechBuilder.Build(
                RowKind.SenderGroup,
                SenderGroupValues(flag: "Follow up", hasUnread: true),
                Layout(RowKind.SenderGroup)));
    }

    [Fact]
    public void SingleMessageGroup_SaysMessageNotMessages()
    {
        var text = RowSpeechBuilder.Build(
            RowKind.SenderGroup, SenderGroupValues(count: 1), Layout(RowKind.SenderGroup));

        Assert.Contains("1 message.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1 messages", text, StringComparison.Ordinal);
    }

    // ── the one intentional behaviour change ──────────────────────────────────

    [Fact]
    public void EmptyFieldsAreSkippedEntirely_NoDeadBeats()
    {
        // The old converter emitted ". . " for a message with no preview. Nothing empty should
        // produce a separator now.
        var text = RowSpeechBuilder.Build(
            RowKind.Message,
            MessageValues(preview: "", subject: ""),
            Layout(RowKind.Message));

        Assert.Equal("unread. Chris Lee. 2:14P.", text);
        Assert.DoesNotContain(". .", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhitespaceOnlyValuesCountAsEmpty()
    {
        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(preview: "   "), Layout(RowKind.Message));

        Assert.DoesNotContain(". .", text, StringComparison.Ordinal);
        Assert.Equal("unread. Chris Lee. Budget review. 2:14P.", text);
    }

    // ── reordering and enabling ───────────────────────────────────────────────

    [Fact]
    public void ReorderingChangesTheSpokenOrder()
    {
        var layout = Layout(RowKind.Message);
        Move(layout, "subject", -2);   // subject ahead of from

        var text = RowSpeechBuilder.Build(RowKind.Message, MessageValues(), layout);

        Assert.StartsWith("unread. Budget review. Chris Lee.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentsCanBeMovedToTheEnd()
    {
        // The supplement request: "I want attachments last."
        var layout = Layout(RowKind.Message);
        layout.Remove(Field(layout, "attachments"));
        layout.Add(new RowFieldSetting { Id = "attachments", Enabled = true, SpeakMode = SpeakMode.WhenTrue });

        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(attachments: true), layout);

        Assert.EndsWith("2:14P. attachments.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledFieldDisappears()
    {
        var layout = Layout(RowKind.Message);
        Field(layout, "preview").Enabled = false;

        var text = RowSpeechBuilder.Build(RowKind.Message, MessageValues(), layout);

        Assert.DoesNotContain("Lunch tomorrow?", text, StringComparison.Ordinal);
        Assert.Equal("unread. Chris Lee. Budget review. 2:14P.", text);
    }

    [Fact]
    public void EverythingDisabledYieldsEmptyRatherThanStrayPunctuation()
    {
        var layout = Layout(RowKind.Message);
        foreach (var f in layout) f.Enabled = false;

        Assert.Equal(string.Empty, RowSpeechBuilder.Build(RowKind.Message, MessageValues(), layout));
    }

    // ── the status supplement: individual state fields with a speak mode ───────

    [Fact]
    public void UnreadWhenTrue_SpeaksOnUnreadAndIsSilentOnRead()
    {
        // "I want to know about unread but not read." Since #558 this is the shipped default; the
        // explicit setup stays so the behaviour is pinned independently of what the default is.
        var layout = Layout(RowKind.Message);
        Field(layout, "status").Enabled = false;
        var unread = Field(layout, "unread");
        unread.Enabled   = true;
        unread.SpeakMode = SpeakMode.WhenTrue;

        var onUnread = RowSpeechBuilder.Build(RowKind.Message, MessageValues(isUnread: true), layout);
        var onRead   = RowSpeechBuilder.Build(RowKind.Message, MessageValues(isUnread: false), layout);

        Assert.Contains("unread", onUnread, StringComparison.Ordinal);
        Assert.DoesNotContain("unread", onRead, StringComparison.Ordinal);
        Assert.DoesNotContain("read", onRead, StringComparison.Ordinal);
        Assert.Equal("Chris Lee. Budget review. Lunch tomorrow?. 2:14P.", onRead);
    }

    [Fact]
    public void UnreadAlways_SpeaksBothWords()
    {
        var layout = Layout(RowKind.Message);
        Field(layout, "status").Enabled = false;
        var unread = Field(layout, "unread");
        unread.Enabled   = true;
        unread.SpeakMode = SpeakMode.Always;

        // Unread leads the shipped order, so Always is heard at the front of the row.
        Assert.StartsWith("unread. Chris Lee.",
            RowSpeechBuilder.Build(RowKind.Message, MessageValues(isUnread: true), layout),
            StringComparison.Ordinal);
        Assert.StartsWith("read. Chris Lee.",
            RowSpeechBuilder.Build(RowKind.Message, MessageValues(isUnread: false), layout),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StateFieldWithNoFalseWordStaysSilentEvenInAlwaysMode()
    {
        // Attachments has no "no attachments" word — Always must not invent one.
        var layout = Layout(RowKind.Message);
        Field(layout, "attachments").SpeakMode = SpeakMode.Always;

        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(attachments: false), layout);

        Assert.DoesNotContain("attachment", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepliedAndForwardedCanBeSpokenSeparatelyFromTheCombinedStatus()
    {
        var layout = Layout(RowKind.Message);
        Field(layout, "status").Enabled   = false;
        Field(layout, "replied").Enabled  = true;
        Field(layout, "unread").Enabled   = true;

        // A replied message that is still unread says both — the combined field could only say one.
        var text = RowSpeechBuilder.Build(
            RowKind.Message, MessageValues(replied: true, isUnread: true), layout);

        Assert.Contains("unread", text, StringComparison.Ordinal);
        Assert.Contains("replied", text, StringComparison.Ordinal);
    }

    // ── labels ────────────────────────────────────────────────────────────────

    [Fact]
    public void LabelsPrefixTextFieldsOnly()
    {
        var text = RowSpeechBuilder.Build(
            RowKind.Message,
            MessageValues(attachments: true),
            Layout(RowKind.Message),
            showLabels: true);

        Assert.Contains("From: Chris Lee", text, StringComparison.Ordinal);
        Assert.Contains("Subject: Budget review", text, StringComparison.Ordinal);
        // State words say what they are; labelling them would produce "Attachments: attachments".
        Assert.Contains(". attachments. ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Attachments: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelsDoNotLabelCounts()
    {
        var text = RowSpeechBuilder.Build(
            RowKind.Conversation, ConversationValues(), Layout(RowKind.Conversation), showLabels: true);

        Assert.Contains("3 messages", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Count: ", text, StringComparison.Ordinal);
    }

    // ── robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void NullOrEmptyLayoutFallsBackToDefaultsRatherThanSilence()
    {
        var fromNull  = RowSpeechBuilder.Build(RowKind.Message, MessageValues(), null);
        var fromEmpty = RowSpeechBuilder.Build(RowKind.Message, MessageValues(), []);

        Assert.Equal(fromNull, fromEmpty);
        Assert.Contains("Chris Lee", fromNull, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortValueArrayIsToleratedRatherThanThrowing()
    {
        // Bindings are still being wired up: the row should say what it can, not crash. Only
        // flag/status/attachments have arrived, and the unread slot has not — so a state field
        // reading as absent must stay silent rather than being treated as false-and-spoken.
        var text = RowSpeechBuilder.Build(
            RowKind.Message, ["", "unread", true], Layout(RowKind.Message));

        Assert.Equal("attachments.", text);
    }

    [Fact]
    public void UnknownFieldIdInALayoutIsIgnored()
    {
        var layout = Layout(RowKind.Message);
        layout.Insert(0, new RowFieldSetting { Id = "not-a-field", Enabled = true });

        var text = RowSpeechBuilder.Build(RowKind.Message, MessageValues(), layout);

        Assert.StartsWith("unread.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleValuesCoverEveryCatalogField()
    {
        // Build tolerates a short array by treating the tail as absent, so a helper that falls
        // behind the catalog does not fail loudly — it just makes the newest fields untestable.
        Assert.Equal(RowFieldCatalog.For(RowKind.Message).Count, MessageValues().Length);
        Assert.Equal(RowFieldCatalog.For(RowKind.Conversation).Count, ConversationValues().Length);
        Assert.Equal(RowFieldCatalog.For(RowKind.SenderGroup).Count, SenderGroupValues().Length);
    }

    [Fact]
    public void EveryCatalogFieldAppearsExactlyOnceInItsDefaultLayout()
    {
        foreach (var kind in new[] { RowKind.Message, RowKind.Conversation, RowKind.SenderGroup })
        {
            var layout = RowFieldCatalog.DefaultLayout(kind);
            var catalogIds = RowFieldCatalog.For(kind).Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal);
            var layoutIds  = layout.Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal);

            Assert.Equal(catalogIds, layoutIds);
            Assert.Equal(layout.Count, layout.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void StateFieldsDeclareATrueWordAndTextFieldsDoNot()
    {
        foreach (var kind in new[] { RowKind.Message, RowKind.Conversation, RowKind.SenderGroup })
        foreach (var def in RowFieldCatalog.For(kind))
        {
            if (def.IsState)
                Assert.False(string.IsNullOrWhiteSpace(def.TrueWord), $"{kind}/{def.Id} has no TrueWord");
            else
                Assert.Null(def.TrueWord);
        }
    }
}
