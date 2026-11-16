using System.Linq;
using System.Text;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Behavior MimeKit 5 tightened that this app must keep working through (MailKit 5.0, PR #1024).
/// </summary>
public class MimeKit5CompatibilityTests
{
    private static AccountModel Account() => new() { DisplayName = "Me", Username = "me@example.com" };

    /// <summary>A message whose To header is about 24 KB: 400 recipients, an all-staff announcement.</summary>
    private static byte[] MessageWithLongToHeader()
    {
        var to = string.Join(",\r\n ", Enumerable.Range(1, 400).Select(i => $"Staff Member {i} <staff.member{i}@example.com>"));
        var raw = "From: boss@example.com\r\n"
                + $"To: {to}\r\n"
                + "Subject: All staff\r\n"
                + "Message-ID: <all-staff@example.com>\r\n"
                + "Content-Type: text/plain\r\n\r\n"
                + "Hello everyone.\r\n";
        return Encoding.ASCII.GetBytes(raw);
    }

    [Fact]
    public void TheParserAcceptsHeadersLongerThanMimeKitsDefault()
    {
        // Touching a QuickMail type runs the module initializer, as the app's first line of code does.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(MimeMessageBuilder).Module.ModuleHandle);
        Assert.True(ParserOptions.Default.MaxHeaderLength >= MimeParsing.MaxHeaderLength);

        var bytes = MessageWithLongToHeader();
        Assert.True(bytes.Length > 16 * 1024, "the header must exceed MimeKit 5's 16 KB default to test anything");
        using var stream = new System.IO.MemoryStream(bytes);
        var message = MimeMessage.Load(stream);
        Assert.Equal(400, message.To.Count);
    }

    [Fact]
    public void AnAttachedEmailWithALongHeaderIsStillEmbeddedAsAnEmail()
    {
        // The parse inside AttachmentEntityFor falls back to a plain file on FormatException, which is
        // what MimeKit 5's 16 KB header limit raised: the recipient would get an opaque file.
        var entity = MimeMessageBuilder.AttachmentEntityFor(new AttachmentModel
        {
            FileName = "all-staff.eml", ContentType = "message/rfc822", Content = MessageWithLongToHeader(),
        });

        var part = Assert.IsType<MessagePart>(entity);
        Assert.NotNull(part.Message);
        Assert.Equal(400, part.Message.To.Count);
    }

    [Theory]
    [InlineData("<a@example.com> <b@example.com>")]
    [InlineData("a b@example.com")]
    public void AReplyToAMalformedMessageIdStillBuilds(string inReplyTo)
    {
        var compose = new ComposeModel { To = "a@b.com", Subject = "Re: hi", Body = "text", InReplyToMessageId = inReplyTo };

        var message = MimeMessageBuilder.Build(compose, Account());

        Assert.Equal("Re: hi", message.Subject);
    }

    [Fact]
    public void AReplyToAWellFormedMessageIdKeepsInReplyTo()
    {
        var compose = new ComposeModel { To = "a@b.com", Body = "text", InReplyToMessageId = "<orig@example.com>" };

        var message = MimeMessageBuilder.Build(compose, Account());

        Assert.Equal("orig@example.com", message.InReplyTo);
    }

    [Theory]
    [InlineData("application/")]
    [InlineData("application/pdf; name=x.pdf")]
    public void AnAttachmentWithAnUnusableContentTypeIsSentAsAGenericFile(string contentType)
    {
        var compose = new ComposeModel
        {
            To = "a@b.com",
            Body = "text",
            Attachments = [new AttachmentModel { FileName = "x.pdf", ContentType = contentType, Content = [1, 2, 3] }],
        };

        var message = MimeMessageBuilder.Build(compose, Account());

        var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("x.pdf", attachment.FileName);
        Assert.Equal("application/octet-stream", attachment.ContentType.MimeType);
    }
}
