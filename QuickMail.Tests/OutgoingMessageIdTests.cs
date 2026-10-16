using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The send and the copy filed in Sent build separate MIME messages; they must carry one
/// Message-ID so a server that files its own copy is recognised and not given a second (#811).
/// </summary>
public class OutgoingMessageIdTests
{
    private static AccountModel MakeAccount(string username = "sender@example.com") => new()
    {
        DisplayName = "Test Sender",
        Username = username,
    };

    [Fact]
    public void EnsureOutgoingMessageId_AssignsOnce_WithSenderDomain()
    {
        var compose = new ComposeModel { To = "a@b.com", Body = "hello" };

        var first = MimeMessageBuilder.EnsureOutgoingMessageId(compose, MakeAccount());
        var second = MimeMessageBuilder.EnsureOutgoingMessageId(compose, MakeAccount());

        Assert.Equal(first, second);
        Assert.Equal(first, compose.OutgoingMessageId);
        Assert.EndsWith("@example.com", first);
        Assert.DoesNotContain("<", first);
    }

    [Fact]
    public void EnsureOutgoingMessageId_UsernameWithoutDomain_StillAssigns()
    {
        var compose = new ComposeModel();

        var id = MimeMessageBuilder.EnsureOutgoingMessageId(compose, MakeAccount("localuser"));

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Contains("@", id);
    }

    [Fact]
    public void EnsureOutgoingMessageId_FreshComposes_GetDistinctIds()
    {
        var a = MimeMessageBuilder.EnsureOutgoingMessageId(new ComposeModel(), MakeAccount());
        var b = MimeMessageBuilder.EnsureOutgoingMessageId(new ComposeModel(), MakeAccount());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Build_TwiceFromOneSentCompose_SharesTheMessageId()
    {
        // The SMTP send and AppendToSentAsync each call Build: the copies must be one message.
        var compose = new ComposeModel { To = "a@b.com", Subject = "s", Body = "hello" };
        var id = MimeMessageBuilder.EnsureOutgoingMessageId(compose, MakeAccount());

        var sent  = MimeMessageBuilder.Build(compose, MakeAccount(), MimeMessageBuilder.AppUserAgent);
        var filed = MimeMessageBuilder.Build(compose, MakeAccount());

        Assert.Equal(id, sent.MessageId);
        Assert.Equal(id, filed.MessageId);
    }

    [Fact]
    public void Build_WithoutOutgoingId_LeavesEachBuildItsOwn()
    {
        // Drafts never set it: each save is a different message and must not share an id.
        var compose = new ComposeModel { To = "a@b.com", Body = "draft" };

        var first  = MimeMessageBuilder.Build(compose, MakeAccount());
        var second = MimeMessageBuilder.Build(compose, MakeAccount());

        Assert.Null(compose.OutgoingMessageId);
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public void WithoutAttachments_KeepsOutgoingMessageId()
    {
        // The Outbox stores WithoutAttachments(); a queued retry must keep the id it was given.
        var compose = new ComposeModel { OutgoingMessageId = "abc@example.com" };

        Assert.Equal("abc@example.com", compose.WithoutAttachments().OutgoingMessageId);
    }
}
