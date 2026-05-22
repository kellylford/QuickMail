using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ComposeViewModel.CreateReplyAll. Regression for §2.9: the original sender
/// could appear on both the To and Cc lines of a reply-all when they were Cc'd on their
/// own message (common with mailing lists).
/// </summary>
public class ComposeViewModelReplyTests
{
    private static MailMessageDetail MakeDetail(string from, string to, string cc, string? replyTo = null)
        => new()
        {
            UniqueId      = 1,
            AccountId     = Guid.NewGuid(),
            FolderName    = "Inbox",
            From          = from,
            To            = to,
            Cc            = cc,
            ReplyTo       = replyTo ?? string.Empty,
            Subject       = "test",
            PlainTextBody = "hi",
            Date          = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void CreateReplyAll_ExcludesOriginalSenderFromCc()
    {
        // Sender alice was also Cc'd on her own message — common on mailing lists.
        // Old behaviour: alice ended up on both To (because she's From) AND Cc.
        var detail = MakeDetail(
            from: "alice@example.com",
            to:   "bob@example.com, carol@example.com",
            cc:   "alice@example.com, dave@example.com");

        var model = ComposeViewModel.CreateReplyAll(detail, Guid.NewGuid(), ownAddress: "me@example.com");

        Assert.Contains("alice@example.com", model.To);
        Assert.DoesNotContain("alice@example.com", model.Cc);
        Assert.Contains("bob@example.com",  model.Cc);
        Assert.Contains("carol@example.com", model.Cc);
        Assert.Contains("dave@example.com", model.Cc);
    }

    [Fact]
    public void CreateReplyAll_UsesReplyToWhenPresent_AndExcludesItFromCc()
    {
        var detail = MakeDetail(
            from:    "noreply@example.com",
            to:      "list@example.com",
            cc:      "list-archive@example.com, list@example.com",
            replyTo: "list@example.com");

        var model = ComposeViewModel.CreateReplyAll(detail, Guid.NewGuid(), ownAddress: "me@example.com");

        Assert.Contains("list@example.com", model.To);
        // list@ was the reply-to recipient — it shouldn't appear in Cc too.
        Assert.DoesNotContain("list@example.com", model.Cc);
        Assert.Contains("list-archive@example.com", model.Cc);
    }

    [Fact]
    public void CreateReplyAll_ExcludesOwnAddressFromCc()
    {
        var detail = MakeDetail(
            from: "alice@example.com",
            to:   "me@example.com, bob@example.com",
            cc:   "carol@example.com");

        var model = ComposeViewModel.CreateReplyAll(detail, Guid.NewGuid(), ownAddress: "me@example.com");

        Assert.DoesNotContain("me@example.com", model.Cc);
    }

    [Fact]
    public void CreateReplyAll_HandlesEmptyAddressFields()
    {
        var detail = MakeDetail(
            from: "alice@example.com",
            to:   string.Empty,
            cc:   string.Empty);

        var model = ComposeViewModel.CreateReplyAll(detail, Guid.NewGuid());

        Assert.Equal("alice@example.com", model.To);
        Assert.Equal(string.Empty, model.Cc);
    }

    [Fact]
    public void CreateReplyAll_DedupesAddressesWithDifferentDisplayNames()
    {
        // Same email twice with different display names — should appear once in Cc.
        var detail = MakeDetail(
            from: "alice@example.com",
            to:   "Bob X <bob@example.com>",
            cc:   "Robert <bob@example.com>, carol@example.com");

        var model = ComposeViewModel.CreateReplyAll(detail, Guid.NewGuid());

        // bob should appear exactly once.
        var idx1 = model.Cc.IndexOf("bob@example.com", StringComparison.OrdinalIgnoreCase);
        var idx2 = idx1 < 0 ? -1 : model.Cc.IndexOf("bob@example.com", idx1 + 1, StringComparison.OrdinalIgnoreCase);
        Assert.True(idx1 >= 0, "bob should appear at least once");
        Assert.True(idx2 < 0, "bob should appear exactly once");
    }
}
