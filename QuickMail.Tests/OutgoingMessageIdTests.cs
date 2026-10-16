using System;
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

    [Theory]
    [InlineData("someone@bücher.de")]          // Unicode domain
    [InlineData("someone@xn--bcher-kva.de")]   // the same domain in punycode, which MimeKit turns back
    [InlineData("someone@exa mple.com")]       // MimeKit would cut this at the space
    [InlineData("someone@example.com>")]
    public void EnsureOutgoingMessageId_UnusableDomain_FallsBackToAnAsciiId(string username)
    {
        var compose = new ComposeModel { To = "a@b.com", Body = "hello" };

        var id = MimeMessageBuilder.EnsureOutgoingMessageId(compose, MakeAccount(username));
        var built = MimeMessageBuilder.Build(compose, MakeAccount(username));

        Assert.EndsWith("@" + MimeMessageBuilder.FallbackMessageIdDomain, id);
        // What is written is what the Sent search will look for.
        Assert.Equal(id, built.MessageId);
        Assert.All(built.MessageId!, c => Assert.True(c < 128));
    }

    [Fact]
    public void Build_WithOutgoingDate_UsesIt()
    {
        var when = new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.FromHours(-5));
        var compose = new ComposeModel { To = "a@b.com", Body = "hello", OutgoingDate = when };

        Assert.Equal(when, MimeMessageBuilder.Build(compose, MakeAccount()).Date);
    }

    [Fact]
    public void WithoutAttachments_DropsOutgoingDate()
    {
        // A queued retry is a new send with its own time.
        var compose = new ComposeModel { OutgoingDate = DateTimeOffset.Now };

        Assert.Null(compose.WithoutAttachments().OutgoingDate);
    }

    [Theory]
    [InlineData(new[] { "abc@example.com" }, "abc@example.com", true)]
    [InlineData(new[] { "<ABC@Example.com>" }, "abc@example.com", true)]
    [InlineData(new[] { "other@example.com", "abc@example.com" }, "abc@example.com", true)]
    // A server that ignores the HEADER condition returns everything: no match must mean no skip.
    [InlineData(new[] { "other@example.com", "third@example.com" }, "abc@example.com", false)]
    [InlineData(new[] { "xabc@example.com" }, "abc@example.com", false)]
    [InlineData(new string[0], "abc@example.com", false)]
    public void SentCopyMatches_OnlyAnExactIdCounts(string[] found, string wanted, bool expected)
    {
        Assert.Equal(expected, ImapMailService.SentCopyMatches(found, wanted));
    }

    [Fact]
    public void SentCopyMatches_IgnoresMissingIds()
    {
        Assert.False(ImapMailService.SentCopyMatches([null, ""], "abc@example.com"));
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
