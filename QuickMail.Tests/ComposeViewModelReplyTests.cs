using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ComposeViewModel.CreateReplyAll and CreateForward.
/// </summary>
public class ComposeViewModelReplyTests
{
    private static MailMessageDetail MakeDetail(string from, string to, string cc, string? replyTo = null,
        string plainBody = "hi", string htmlBody = "")
        => new()
        {
            MessageId     = "1",
            AccountId     = Guid.NewGuid(),
            FolderName    = "Inbox",
            From          = from,
            To            = to,
            Cc            = cc,
            ReplyTo       = replyTo ?? string.Empty,
            Subject       = "test",
            PlainTextBody = plainBody,
            HtmlBody      = htmlBody,
            Date          = DateTimeOffset.UtcNow,
        };

    // ── CreateForward tests ─────────────────────────────────────────────────────

    [Fact]
    public void CreateForward_HtmlOnlyMessage_BodyIsNotEmpty()
    {
        var detail = MakeDetail("alice@example.com", "bob@example.com", "",
            plainBody: "", htmlBody: "<p>Hello from HTML</p>");

        var model = ComposeViewModel.CreateForward(detail, Guid.NewGuid());

        Assert.False(string.IsNullOrWhiteSpace(model.Body), "Body must not be blank for HTML-only messages");
        Assert.Contains("Hello from HTML", model.Body);
    }

    [Fact]
    public void CreateForward_HtmlMessage_HtmlBodyContainsBlockquote()
    {
        var detail = MakeDetail("alice@example.com", "bob@example.com", "",
            plainBody: "plain text", htmlBody: "<p>Hello from HTML</p>");

        var model = ComposeViewModel.CreateForward(detail, Guid.NewGuid());

        Assert.NotNull(model.HtmlBody);
        Assert.Contains("<blockquote", model.HtmlBody);
        Assert.Contains("Hello from HTML", model.HtmlBody);
    }

    [Fact]
    public void CreateForward_HtmlMessage_ModeIsHtml()
    {
        var detail = MakeDetail("alice@example.com", "bob@example.com", "",
            plainBody: "plain text", htmlBody: "<p>Hello</p>");

        var model = ComposeViewModel.CreateForward(detail, Guid.NewGuid());

        Assert.Equal(ComposeMode.Html, model.Mode);
    }

    [Fact]
    public void CreateForward_PlainMessage_BodyUnchanged()
    {
        var detail = MakeDetail("alice@example.com", "bob@example.com", "",
            plainBody: "Plain text body", htmlBody: "");

        var model = ComposeViewModel.CreateForward(detail, Guid.NewGuid());

        Assert.Contains("Plain text body", model.Body);
        Assert.Null(model.HtmlBody);
        Assert.Equal(ComposeMode.PlainText, model.Mode);
    }

    [Fact]
    public void CreateForward_HtmlMessage_HtmlBodyContainsForwardHeader()
    {
        var detail = MakeDetail("alice@example.com", "bob@example.com", "",
            plainBody: "", htmlBody: "<p>content</p>");
        detail.Subject = "Meeting notes";

        var model = ComposeViewModel.CreateForward(detail, Guid.NewGuid());

        Assert.NotNull(model.HtmlBody);
        Assert.Contains("Forwarded message", model.HtmlBody);
        Assert.Contains("Meeting notes", model.HtmlBody);
    }

    // ── CreateReplyAll tests ────────────────────────────────────────────────────

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
