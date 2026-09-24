using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pictures sent inside a message, shown in the reading pane, tabs and message windows (#729).
/// The sanitizer lets through only pictures that are parts of the message, as tags QuickMail
/// writes itself, and the document's policy allows images from the host's own address alone.
/// </summary>
public class EmbeddedPictureTests
{
    private const string Base = "https://quickmail-images.invalid/key123/";

    private static string Render(string html, string? pictureBase = Base) =>
        MessageBodyHtmlBuilder.BuildMessageHtml(
            new MailMessageDetail { Subject = "s", HtmlBody = html }, embeddedPictureBase: pictureBase);

    [Fact]
    public void EmbeddedPicture_IsRebuiltFromTheMessagesOwnPart()
    {
        var html = Render("<p>Hi <img src=\"cid:logo@x\" alt=\"Our logo\" width=\"120\" height=\"40\"></p>");

        Assert.Contains($"<img src=\"{Base}logo%40x\" alt=\"Our logo\" width=\"120\" height=\"40\">", html);
        Assert.Contains("img-src https://quickmail-images.invalid;", html);
        Assert.DoesNotContain("img-src 'none'", html);
    }

    [Fact]
    public void OnlyTheContentIdAltAndSizeSurvive()
    {
        var html = Render(
            "<img src=\"cid:a@x\" alt=\"A\" onerror=\"alert(1)\" style=\"position:fixed\" class=\"x\" " +
            "width=\"100px\" srcset=\"https://evil/1.png 2x\" usemap=\"#m\">");

        var tag = html[html.IndexOf("<img", StringComparison.Ordinal)..];
        tag = tag[..(tag.IndexOf('>') + 1)];
        Assert.Equal($"<img src=\"{Base}a%40x\" alt=\"A\">", tag); // "100px" is not a plain number either
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("evil", html);
    }

    [Fact]
    public void AltText_CannotBreakOutOfTheAttribute()
    {
        var html = Render("<img src=\"cid:a@x\" alt='x\" onerror=\"alert(1)'>");
        Assert.DoesNotContain("onerror=\"alert", html);
        Assert.Contains("alt=\"x&quot; onerror=&quot;alert(1)\"", html);
    }

    [Fact]
    public void PicturesByWebAddress_StayBlocked_AndReadAsTheirAltText()
    {
        var html = Render("<p><img src=\"https://tracker.example/p.gif\" alt=\"Sale\"></p>");

        Assert.DoesNotContain("tracker.example", html);
        Assert.Contains("Sale", html);
        // No picture of the message's own, so nothing may load at all.
        Assert.Contains("img-src 'none'", html);
    }

    [Fact]
    public void WithPicturesOff_EverythingIsBlockedAsBefore()
    {
        var html = Render("<p><img src=\"cid:a@x\" alt=\"Chart\"></p>", pictureBase: null);

        Assert.DoesNotContain("<img", html);
        Assert.Contains("Chart", html);
        Assert.Contains("img-src 'none'", html);
    }

    [Fact]
    public void AMarkerWrittenBySender_IsNotTakenForAPicture()
    {
        var html = Render("<p>before \uE0000\uE001 after <img src=\"cid:a@x\" alt=\"A\"></p>");

        Assert.Equal(1, html.Split("<img").Length - 1);
        Assert.DoesNotContain("\uE000", html);
        Assert.Contains("before  after", html);
    }

    /// <summary>
    /// The body the reading pane gets, between the host document's own &lt;body&gt; tags — so a
    /// test can ask what the sender's content turned into without the host markup around it.
    /// </summary>
    private static string BodyOf(string document)
    {
        var start = document.IndexOf("<body tabindex=\"0\">", StringComparison.Ordinal) + "<body tabindex=\"0\">".Length;
        return document[start..document.LastIndexOf("</body>", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnclosedTagAtTheEnd_CannotKeepAStyle(bool picturesOn)
    {
        // #729 security review: "<div/style=…" is never read whole by the passes, and a closing
        // bracket from anywhere — QuickMail's restored <img>, or the host's own </body> — would
        // complete it with the sender's style, laying a page of their own over the message.
        var html = Render(
            "<p>Real message</p><div/style=\"position:fixed;inset:0;background:white\" " +
            "<img src=cid:x>Your password expired.",
            picturesOn ? Base : null);

        var body = BodyOf(html);
        Assert.DoesNotContain("<div", body);
        Assert.Contains("&lt;div", body);   // shown as text
        Assert.Contains("Real message", body);
    }

    [Theory]
    // A ">" inside a quoted value does not end the tag, so a check for any ">" is fooled.
    [InlineData("<div/style=\"position:fixed;inset:0;background:white\" title='>' <img src=cid:x>Your password expired.", true)]
    [InlineData("<div/style=\"position:fixed;inset:0;background:white\" title=\">\"", true)]
    [InlineData("<div/style=\"position:fixed;inset:0;background:white\" title=\">\"", false)]
    // An unclosed tag that is not the last "<" in the body.
    [InlineData("<div/style=\"position:fixed;inset:0;background:white\" <b", true)]
    [InlineData("<div/style=\"position:fixed;inset:0;background:white\" <b", false)]
    public void NoUnclosedTag_SurvivesAnywhereInTheBody(string attack, bool picturesOn)
    {
        var body = BodyOf(Render("<p>Real message</p>" + attack, picturesOn ? Base : null));

        Assert.DoesNotContain("<div", body);      // the tag is text, not markup
        Assert.Contains("&lt;div", body);
        Assert.Contains("Real message", body);
    }

    [Fact]
    public void AMarkerInsideATag_RestoresNoPicture()
    {
        // A picture forged into an attribute value would put QuickMail's quotes and ">" inside
        // the sender's tag. It is dropped instead.
        var html = Render("<p><a href=\"https://example.com/\" title=\"<img src=cid:x>\">link</a></p>");
        var body = BodyOf(html);
        Assert.DoesNotContain("<img", body);
        Assert.Contains(">link</a>", body);
    }

    [Fact]
    public void AMarkerJoinedTogetherByStripping_IsStillNotRestoredInsideATag()
    {
        var html = Render("<p title=\"\uE000<script>x</script>0\uE001\">x</p><img src=\"cid:a@x\" alt=\"A\">");
        var body = BodyOf(html);
        // The one real picture is restored where it was written; nothing inside the <p> tag.
        Assert.Equal(1, body.Split("<img").Length - 1);
        Assert.StartsWith("<p", body);
        Assert.DoesNotContain("title=\"<img", body);
    }

    [Fact]
    public void PictureInsideRemovedContent_GoesWithIt()
    {
        var html = Render("<script><img src=\"cid:a@x\" alt=\"A\"></script><p>text</p>");
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void UndescribedPicture_IsSilentLikeADecorativeOne()
    {
        var html = Render("<img src=\"cid:a@x\">");
        Assert.Contains($"<img src=\"{Base}a%40x\" alt=\"\">", html);
    }

    [Fact]
    public void LinkedPicture_KeepsItsLinkNamedByTheAltText()
    {
        var html = Render("<a href=\"https://example.com/\"><img src=\"cid:fb@x\" alt=\"Facebook\"></a>");
        Assert.Contains($"<a href=\"https://example.com/\"><img src=\"{Base}fb%40x\" alt=\"Facebook\"></a>", html);
    }

    // ── Loading the pictures ─────────────────────────────────────────────────

    private sealed class CountingMail(byte[]? original = null) : StubImapMailServiceBase, IMailService
    {
        public int PartDownloads, OriginalDownloads;

        public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PartDownloads);
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }

        public async Task CopyOriginalMessageToAsync(Guid accountId, string folderName, string messageId, Stream destination, CancellationToken ct = default)
        {
            Interlocked.Increment(ref OriginalDownloads);
            await destination.WriteAsync(original!, ct);
        }
    }

    private static MailMessageDetail Detail(string id, params AttachmentModel[] listed) => new()
    {
        AccountId = Guid.NewGuid(), FolderName = "Inbox", MessageId = id,
        HtmlBody = "<img src=\"cid:a@x\" alt=\"A\"><img src=\"cid:svg@x\" alt=\"S\">",
        InlineImages = [.. listed],
    };

    [Fact]
    public async Task ListedParts_AreDownloadedOneByOne_AndCached()
    {
        EmbeddedPictureLoader.ClearCache();
        var mail = new CountingMail();
        var detail = Detail("m1",
            new AttachmentModel { ContentId = "a@x", ContentType = "image/png", PartSpecifier = "2" },
            new AttachmentModel { ContentId = "svg@x", ContentType = "image/svg+xml", PartSpecifier = "3" });

        var pictures = await EmbeddedPictureLoader.LoadAsync(mail, detail);
        var again = await EmbeddedPictureLoader.LoadAsync(mail, detail);

        Assert.Equal(["a@x"], pictures.Keys.ToArray()); // SVG is never handed to the reading pane
        Assert.Same(pictures, again);
        Assert.Equal(1, mail.PartDownloads);
        Assert.Equal(0, mail.OriginalDownloads);
    }

    [Fact]
    public async Task WithoutAPartList_TheOriginalIsRead()
    {
        EmbeddedPictureLoader.ClearCache();
        var message = MimeMessageBuilder.Build(new ComposeModel
        {
            To = "me@example.com", Body = "x",
            HtmlBody = "<img src=\"cid:a@x\" alt=\"A\">",
            InlineImages = [new AttachmentModel { FileName = "a.png", ContentType = "image/png", Content = [9, 9], ContentId = "a@x" }],
        }, new AccountModel { Username = "me@example.com" });
        using var raw = new MemoryStream();
        message.WriteTo(raw);
        var mail = new CountingMail(raw.ToArray());

        var pictures = await EmbeddedPictureLoader.LoadAsync(mail, Detail("m2"));

        Assert.Equal(new byte[] { 9, 9 }, pictures["a@x"].Content);
        Assert.Equal(1, mail.OriginalDownloads);
    }

    [Fact]
    public void Pop_ListsPicturesByContentId()
    {
        var message = MimeMessageBuilder.Build(new ComposeModel
        {
            To = "me@example.com", Body = "x",
            HtmlBody = "<img src=\"cid:a@x\" alt=\"A\">",
            InlineImages = [new AttachmentModel { FileName = "a.png", ContentType = "image/png", Content = [9, 9], ContentId = "a@x" }],
        }, new AccountModel { Username = "me@example.com" });

        var listed = Assert.Single(Pop3MailService.InlineImagesOf(message));
        Assert.Equal("a@x", listed.ContentId);
        Assert.Equal("cid:a@x", listed.PartSpecifier);
    }

    // ── The setting ──────────────────────────────────────────────────────────

    [Fact]
    public void Setting_IsOnByDefault_AndRoundTrips()
    {
        Assert.True(new ConfigModel().ShowEmbeddedPictures);
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPics-{Guid.NewGuid():N}");
        var profile = new ProfileContext(dir);
        var service = new ConfigService(profile);
        var config = service.Load();
        config.ShowEmbeddedPictures = false;
        service.Save(config);
        Assert.False(new ConfigService(profile).Load().ShowEmbeddedPictures);
    }
}
