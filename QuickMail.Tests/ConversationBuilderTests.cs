using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ConversationBuilder. The subject-normalisation logic is non-obvious
/// — review §4 flagged this as untested pure code that's exactly the kind of
/// thing that breaks on edge cases.
/// </summary>
public class ConversationBuilderTests
{
    private static MailMessageSummary Msg(string subject, int dayOffset = 0, string from = "x@example.com")
        => new()
        {
            MessageId  = Math.Abs(subject.GetHashCode() ^ dayOffset).ToString(),
            AccountId  = Guid.NewGuid(),
            FolderName = "Inbox",
            From       = from,
            Subject    = subject,
            Date       = DateTimeOffset.UtcNow.AddDays(dayOffset),
        };

    [Theory]
    [InlineData("Re: Meeting",            "Meeting")]
    [InlineData("RE: Meeting",            "Meeting")]
    [InlineData("re:Meeting",             "Meeting")]
    [InlineData("Fwd: Meeting",           "Meeting")]
    [InlineData("FW: Meeting",            "Meeting")]
    [InlineData("Re: Re: Fwd: Meeting",   "Meeting")]
    [InlineData("Re:   Re:   Meeting",    "Meeting")]
    [InlineData("Meeting",                "Meeting")]
    [InlineData("",                       "")]
    [InlineData("   ",                    "")]
    public void NormalizeSubject_StripsLeadingPrefixChain(string input, string expected)
    {
        Assert.Equal(expected, ConversationBuilder.NormalizeSubject(input));
    }

    [Fact]
    public void NormalizeSubject_DoesNotStripEmbeddedRe()
    {
        // "Reminder" shouldn't be stripped to "minder".
        Assert.Equal("Reminder", ConversationBuilder.NormalizeSubject("Reminder"));
    }

    [Fact]
    public void Build_GroupsByNormalisedSubject_CaseInsensitive()
    {
        var msgs = new[]
        {
            Msg("Meeting"),
            Msg("Re: meeting"),
            Msg("FWD: MEETING"),
            Msg("Other"),
        };

        var groups = ConversationBuilder.Build(msgs);

        Assert.Equal(2, groups.Count);
        var meeting = groups.Single(g => string.Equals(g.NormalizedSubject, "meeting", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(g.NormalizedSubject, "Meeting", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(g.NormalizedSubject, "MEETING", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, meeting.Messages.Count);
    }

    [Fact]
    public void Build_OrdersMessagesWithinGroupNewestFirst()
    {
        var msgs = new[]
        {
            Msg("X", dayOffset: -3),
            Msg("Re: X", dayOffset: 0),
            Msg("Re: Re: X", dayOffset: -1),
        };

        var group = ConversationBuilder.Build(msgs).Single();

        Assert.Equal(3, group.Messages.Count);
        Assert.True(group.Messages[0].Date > group.Messages[1].Date);
        Assert.True(group.Messages[1].Date > group.Messages[2].Date);
    }

    [Fact]
    public void Build_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ConversationBuilder.Build(Array.Empty<MailMessageSummary>()));
    }

    [Fact]
    public void Build_BlankSubjectsGroupedTogether()
    {
        var msgs = new[]
        {
            Msg(""),
            Msg("   "),
            Msg("Re:  "),
        };

        var groups = ConversationBuilder.Build(msgs);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Messages.Count);
    }
}
