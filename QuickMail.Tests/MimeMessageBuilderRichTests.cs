using System.Linq;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the multipart/alternative output added to MimeMessageBuilder for
/// Markdown and HTML compose modes.
/// </summary>
public class MimeMessageBuilderRichTests
{
    private static AccountModel MakeAccount() => new()
    {
        DisplayName = "Test Sender",
        Username = "sender@example.com",
    };

    [Fact]
    public void Build_NoHtmlBody_ReturnsTextPlainOnly()
    {
        var compose = new ComposeModel { To = "a@b.com", Body = "hello" };
        var message = MimeMessageBuilder.Build(compose, MakeAccount());

        var part = Assert.IsType<TextPart>(message.Body);
        Assert.True(part.IsPlain);
        Assert.Equal("hello", part.Text);
    }

    [Fact]
    public void Build_WithHtmlBody_ReturnsMultipartAlternative()
    {
        var compose = new ComposeModel
        {
            To = "a@b.com",
            Body = "**bold**",
            Mode = ComposeMode.Markdown,
            HtmlBody = "<html><body><p><strong>bold</strong></p></body></html>",
        };
        var message = MimeMessageBuilder.Build(compose, MakeAccount());

        var alternative = Assert.IsType<MultipartAlternative>(message.Body);
        Assert.Equal(2, alternative.Count);

        var plain = Assert.IsType<TextPart>(alternative[0]);
        Assert.True(plain.IsPlain);
        Assert.Equal("**bold**", plain.Text);

        var html = Assert.IsType<TextPart>(alternative[1]);
        Assert.True(html.IsHtml);
        Assert.Contains("<strong>bold</strong>", html.Text);
    }

    [Fact]
    public void Build_WithHtmlBodyAndAttachments_NestsAlternativeInsideMixed()
    {
        var compose = new ComposeModel
        {
            To = "a@b.com",
            Body = "see attached",
            Mode = ComposeMode.Html,
            HtmlBody = "<html><body><p>see attached</p></body></html>",
            Attachments =
            {
                new AttachmentModel
                {
                    FileName = "doc.pdf",
                    ContentType = "application/pdf",
                    Content = [1, 2, 3],
                    FileSize = 3,
                },
            },
        };
        var message = MimeMessageBuilder.Build(compose, MakeAccount());

        var mixed = Assert.IsType<Multipart>(message.Body);
        Assert.Equal("mixed", mixed.ContentType.MediaSubtype);
        Assert.IsType<MultipartAlternative>(mixed[0]);
        var attachment = Assert.IsType<MimePart>(mixed[1]);
        Assert.Equal("doc.pdf", attachment.FileName);
    }

    [Fact]
    public void Build_PlainWithAttachments_UnchangedShape()
    {
        var compose = new ComposeModel
        {
            To = "a@b.com",
            Body = "hello",
            Attachments =
            {
                new AttachmentModel
                {
                    FileName = "doc.txt",
                    ContentType = "text/plain",
                    Content = [1],
                    FileSize = 1,
                },
            },
        };
        var message = MimeMessageBuilder.Build(compose, MakeAccount());

        var mixed = Assert.IsType<Multipart>(message.Body);
        var bodyPart = Assert.IsType<TextPart>(mixed[0]);
        Assert.True(bodyPart.IsPlain);
    }
}
