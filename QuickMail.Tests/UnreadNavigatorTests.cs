using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="UnreadNavigator"/> — the Alt+Down / Alt+Up "next unread message" search
/// (issue #617). Pure list logic, deliberately kept out of MainWindow so it is testable without
/// a TreeView: the window contributes only focus and expansion on top of these answers.
/// </summary>
public class UnreadNavigatorTests
{
    // "u" = unread, "r" = read. One character per message keeps each case's shape readable.
    private static List<MailMessageSummary> Msgs(string pattern) =>
        pattern.Select((c, i) => new MailMessageSummary
        {
            MessageId = i.ToString(),
            Subject   = $"m{i}",
            IsRead    = c == 'r',
        }).ToList();

    private static ConversationGroup Group(string subject, string pattern) =>
        new() { NormalizedSubject = subject, Messages = Msgs(pattern) };

    // ── Flat list ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rurur",  0, 1)]   // from a read row, the next unread is below it
    [InlineData("rurur",  1, 3)]   // standing on an unread row does not answer with itself
    [InlineData("rurur",  3, -1)]  // nothing unread further down
    [InlineData("rrrrr",  0, -1)]  // nothing unread at all
    [InlineData("urrrr", -1, 0)]   // no selection: the search starts above the first row
    public void FindNextUnread_Forward(string pattern, int from, int expected) =>
        Assert.Equal(expected, UnreadNavigator.FindNextUnread(Msgs(pattern), from, forward: true));

    [Theory]
    [InlineData("rurur", 4, 3)]
    [InlineData("rurur", 3, 1)]
    [InlineData("rurur", 1, -1)]
    [InlineData("rrrrr", 4, -1)]
    [InlineData("rrrru", 5, 4)]    // no selection: the search starts below the last row
    public void FindNextUnread_Backward(string pattern, int from, int expected) =>
        Assert.Equal(expected, UnreadNavigator.FindNextUnread(Msgs(pattern), from, forward: false));

    [Fact]
    public void FindNextUnread_DoesNotWrap()
    {
        var messages = Msgs("urrrr");
        // The one unread message is at the top, and the selection is at the bottom. Wrapping
        // would find it; not wrapping is the contract, so that running out says so instead.
        Assert.Equal(-1, UnreadNavigator.FindNextUnread(messages, 4, forward: true));
    }

    [Fact]
    public void FindNextUnread_EmptyList_ReturnsNotFound()
    {
        Assert.Equal(-1, UnreadNavigator.FindNextUnread(Msgs(""), -1, forward: true));
        Assert.Equal(-1, UnreadNavigator.FindNextUnread(Msgs(""),  0, forward: false));
    }

    // ── Grouped views ────────────────────────────────────────────────────────

    [Fact]
    public void Flatten_PutsMessagesInTreeOrder()
    {
        var a = Group("a", "rr");
        var b = Group("b", "u");
        var flat = UnreadNavigator.Flatten([a, b], g => g.Messages);

        Assert.Equal(3, flat.Count);
        Assert.Equal([a, a, b], flat.Select(e => e.Group));
        Assert.Equal([0, 1, 0],  flat.Select(e => e.MessageIndex));
    }

    [Fact]
    public void Flatten_IsBlindToExpansionState()
    {
        // Guards against anyone later teaching Flatten to skip closed groups. It must not: the
        // caller expands whatever group it lands in, so a collapsed conversation holding the only
        // unread message has to stay reachable. Both states must flatten identically.
        var collapsed = Group("g", "ru");
        collapsed.IsExpanded = false;
        var expanded = Group("g", "ru");
        expanded.IsExpanded = true;

        var closed = UnreadNavigator.Flatten([collapsed], g => g.Messages);
        var open   = UnreadNavigator.Flatten([expanded],  g => g.Messages);

        Assert.Equal(open.Count, closed.Count);
        Assert.Equal(
            UnreadNavigator.FindNextUnread(open,   e => e.Message.IsUnread, -1, forward: true),
            UnreadNavigator.FindNextUnread(closed, e => e.Message.IsUnread, -1, forward: true));
        Assert.Equal(1, UnreadNavigator.FindNextUnread(closed, e => e.Message.IsUnread, -1, forward: true));
    }

    [Fact]
    public void FindNextUnread_CrossesGroupBoundaries()
    {
        var a = Group("a", "rr");
        var b = Group("b", "ru");
        var flat = UnreadNavigator.Flatten([a, b], g => g.Messages);

        var idx = UnreadNavigator.FindNextUnread(flat, e => e.Message.IsUnread, 0, forward: true);
        Assert.Equal(3, idx);
        Assert.Same(b, flat[idx].Group);
        Assert.Equal(1, flat[idx].MessageIndex);
    }

    [Fact]
    public void IndexOfMessage_FindsTheSelectedMessage()
    {
        var a = Group("a", "ru");
        var flat = UnreadNavigator.Flatten([a], g => g.Messages);

        Assert.Equal(1, UnreadNavigator.IndexOfMessage(flat, a.Messages[1]));
        Assert.Equal(-1, UnreadNavigator.IndexOfMessage(flat, Msgs("u")[0]));
    }

    [Fact]
    public void IndexOfGroupStart_IsTheGroupsFirstMessage()
    {
        var a = Group("a", "rr");
        var b = Group("b", "ru");
        var flat = UnreadNavigator.Flatten([a, b], g => g.Messages);

        Assert.Equal(0, UnreadNavigator.IndexOfGroupStart(flat, a));
        Assert.Equal(2, UnreadNavigator.IndexOfGroupStart(flat, b));
        Assert.Equal(-1, UnreadNavigator.IndexOfGroupStart(flat, Group("absent", "u")));
    }

    [Fact]
    public void FromGroupHeader_DownConsidersTheGroupsOwnMessages()
    {
        // A header sits above its own messages, so Alt+Down from it behaves like Down arrow:
        // the group's first message is the next row, and is eligible.
        var a = Group("a", "ur");
        var flat = UnreadNavigator.Flatten([a], g => g.Messages);
        var start = UnreadNavigator.IndexOfGroupStart(flat, a);

        var from = UnreadNavigator.SearchOriginForGroupHeader(start, forward: true);
        Assert.Equal(0, UnreadNavigator.FindNextUnread(flat, e => e.Message.IsUnread, from, forward: true));
    }

    [Fact]
    public void FromGroupHeader_UpSkipsTheGroupsOwnMessages()
    {
        // Going up from a header must leave the group entirely — its messages are all below it.
        var a = Group("a", "ru");
        var b = Group("b", "uu");
        var flat = UnreadNavigator.Flatten([a, b], g => g.Messages);
        var start = UnreadNavigator.IndexOfGroupStart(flat, b);

        var from = UnreadNavigator.SearchOriginForGroupHeader(start, forward: false);
        var idx  = UnreadNavigator.FindNextUnread(flat, e => e.Message.IsUnread, from, forward: false);

        Assert.Same(a, flat[idx].Group);
        Assert.Equal(1, flat[idx].MessageIndex);
    }

    [Fact]
    public void SearchOriginForNoSelection_SearchesTheWholeViewEitherWay()
    {
        var flat = UnreadNavigator.Flatten([Group("a", "uru")], g => g.Messages);

        var down = UnreadNavigator.SearchOriginForNoSelection(flat.Count, forward: true);
        var up   = UnreadNavigator.SearchOriginForNoSelection(flat.Count, forward: false);

        Assert.Equal(0, UnreadNavigator.FindNextUnread(flat, e => e.Message.IsUnread, down, forward: true));
        Assert.Equal(2, UnreadNavigator.FindNextUnread(flat, e => e.Message.IsUnread, up,   forward: false));
    }
}
