using System;
using System.Text.RegularExpressions;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The document a message opens in (#959, #851): its pictures must not hold up its load, and it
/// names the render it belongs to so focus can tell it from the page it replaces.
/// </summary>
public class MessageOpenDocumentTests
{
    private const string Embedded = "https://quickmail-images.invalid/e1/";
    private const string Web = "https://quickmail-images.invalid/w1/";

    private static readonly Regex ImgTag = new("<img\\b[^>]*>", RegexOptions.IgnoreCase);

    private static string Display(string html, bool web = true) =>
        MessageBodyHtmlBuilder.BuildMessageDocument(
            new MailMessageDetail { Subject = "s", HtmlBody = html }, null, false, null,
            new PictureSources(Embedded, web ? Web : null, !web)).Html;

    /// <summary>
    /// Every picture QuickMail writes into a displayed message is lazy: an eager one delays the load
    /// event, and so NavigationCompleted, until the host has answered it — which for an embedded
    /// picture can mean downloading the whole original.
    /// </summary>
    [Fact]
    public void EveryPictureInADisplayedMessage_IsLazy()
    {
        var html = Display(
            "<p><img src=\"cid:logo@x\" alt=\"Logo\" width=\"20\" height=\"10\"></p>" +
            "<a href=\"https://example.com/\"><img src=\"cid:fb@x\" alt=\"Facebook\"></a>" +
            "<img src=\"https://news.example/a.png\" alt=\"Banner\"><img src='cid:undescribed@x'>");

        var tags = ImgTag.Matches(html);
        Assert.Equal(4, tags.Count);
        foreach (Match tag in tags)
        {
            Assert.Contains(" loading=\"lazy\"", tag.Value);
            Assert.Contains(" decoding=\"async\"", tag.Value);
        }
    }

    /// <summary>
    /// A saved or printed page keeps its pictures eager: they are in the file already, and a lazy
    /// picture out of view might not be there when the page is printed.
    /// </summary>
    [Fact]
    public void SavedPagePictures_AreNotLazy()
    {
        Assert.True(MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(
            "<p><img src=\"cid:a@x\" alt=\"A\"></p>", _ => "data:image/png;base64,AAAA", null, out var fragment));

        var tag = Assert.Single(ImgTag.Matches(fragment));
        Assert.DoesNotContain("loading=", tag.Value);
    }

    [Fact]
    public void RenderToken_GoesOnTheDocumentsOwnHtmlTag()
    {
        var html = MessageBodyHtmlBuilder.WithRenderToken(Display("<p>Hello</p>"), "17");

        Assert.StartsWith("<!DOCTYPE html><html data-qm-render=\"17\" lang=\"en\">", html);
        Assert.Single(Regex.Matches(html, "data-qm-render="));
    }

    public enum DocumentKind { Html, PlainTextView, ReaderMode, NoHtml }

    /// <summary>HTML, the plain-text view, reader mode and a plain-text-only message are all documents a message opens in.</summary>
    [Theory]
    [InlineData(DocumentKind.Html)]
    [InlineData(DocumentKind.PlainTextView)]
    [InlineData(DocumentKind.ReaderMode)]
    [InlineData(DocumentKind.NoHtml)]
    public void RenderToken_IsCarriedByEveryKindOfMessageDocument(DocumentKind kind)
    {
        var htmlBody = kind switch
        {
            // More tables than the reading pane renders richly: shown as a simplified body instead.
            DocumentKind.ReaderMode => "<p>Hello</p>" + string.Concat(
                System.Linq.Enumerable.Repeat("<table><tr><td>x</td></tr></table>", MessageBodyHtmlBuilder.MaxRichHtmlTableCount + 1)),
            DocumentKind.NoHtml => "",
            _ => "<p>Hello</p>",
        };
        Assert.Equal(kind == DocumentKind.ReaderMode, MessageBodyHtmlBuilder.ShouldUseReaderMode(htmlBody));
        var detail = new MailMessageDetail { Subject = "s", HtmlBody = htmlBody, PlainTextBody = "Hello" };
        var document = MessageBodyHtmlBuilder.BuildMessageDocument(
            detail, null, kind == DocumentKind.PlainTextView, null, PictureSources.None);
        if (kind == DocumentKind.ReaderMode)
            Assert.Contains("simplified body", document.Html);

        var html = MessageBodyHtmlBuilder.WithRenderToken(document.Html, "3");

        Assert.Contains("<html data-qm-render=\"3\"", html);
    }

    /// <summary>A message carrying its own &lt;html&gt; attributes cannot put a token on the page.</summary>
    [Fact]
    public void SendersOwnHtmlTag_DoesNotCarryAToken()
    {
        var html = Display("<html data-qm-render=\"99\"><body><p>Hi</p></body></html>");

        Assert.DoesNotContain("data-qm-render=\"99\"", html);
    }

    [Fact]
    public void RenderToken_IsEncoded()
    {
        var html = MessageBodyHtmlBuilder.WithRenderToken("<html><body></body></html>", "a\"b");
        Assert.Equal("<html data-qm-render=\"a&quot;b\"><body></body></html>", html);
    }

    [Fact]
    public void ADocumentWithNoHtmlTag_IsUnchanged() =>
        Assert.Equal("<p>x</p>", MessageBodyHtmlBuilder.WithRenderToken("<p>x</p>", "1"));
}
