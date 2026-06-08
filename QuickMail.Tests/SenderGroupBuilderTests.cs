using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for SenderGroupBuilder (used by From and To view modes).
/// Pure logic, no dependencies — see review §4.
/// </summary>
public class SenderGroupBuilderTests
{
    private static MailMessageSummary Msg(string from = "", string to = "", int dayOffset = 0)
        => new()
        {
            MessageId  = Math.Abs((from + to + dayOffset).GetHashCode()).ToString(),
            AccountId  = Guid.NewGuid(),
            FolderName = "Inbox",
            From       = from,
            To         = to,
            Subject    = "subj",
            Date       = DateTimeOffset.UtcNow.AddDays(dayOffset),
        };

    [Fact]
    public void Build_GroupsBySender_CaseInsensitive()
    {
        var groups = SenderGroupBuilder.Build(new[]
        {
            Msg(from: "alice@example.com"),
            Msg(from: "ALICE@example.com"),
            Msg(from: "bob@example.com"),
        });

        Assert.Equal(2, groups.Count);
        var alice = groups.Single(g => string.Equals(g.SenderKey, "alice@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, alice.Messages.Count);
    }

    [Fact]
    public void Build_TrimsWhitespaceOnSender()
    {
        var groups = SenderGroupBuilder.Build(new[]
        {
            Msg(from: "alice@example.com"),
            Msg(from: "  alice@example.com  "),
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Messages.Count);
    }

    [Fact]
    public void Build_OrdersMessagesWithinGroupNewestFirst()
    {
        var groups = SenderGroupBuilder.Build(new[]
        {
            Msg(from: "x@y", dayOffset: -2),
            Msg(from: "x@y", dayOffset: 0),
            Msg(from: "x@y", dayOffset: -1),
        });

        var msgs = groups.Single().Messages;
        Assert.True(msgs[0].Date > msgs[1].Date);
        Assert.True(msgs[1].Date > msgs[2].Date);
    }

    [Fact]
    public void Build_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SenderGroupBuilder.Build(Array.Empty<MailMessageSummary>()));
    }

    [Fact]
    public void BuildByTo_GroupsByRecipient_CaseInsensitive()
    {
        var groups = SenderGroupBuilder.BuildByTo(new[]
        {
            Msg(to: "kelly@example.com"),
            Msg(to: "KELLY@example.com"),
            Msg(to: "other@example.com"),
        });

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void BuildByTo_UsesPlaceholderForBlankRecipient()
    {
        var groups = SenderGroupBuilder.BuildByTo(new[]
        {
            Msg(to: ""),
            Msg(to: "   "),
            Msg(to: "kelly@example.com"),
        });

        Assert.Equal(2, groups.Count);
        var placeholder = groups.Single(g => g.SenderKey == "(no recipient)");
        Assert.Equal(2, placeholder.Messages.Count);
    }
}
