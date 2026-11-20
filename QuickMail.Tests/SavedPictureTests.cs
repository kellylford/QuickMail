using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pictures in a saved web page, PDF or printout (#728, #729): the message's own pictures, and web
/// pictures QuickMail already loaded, are written into the file as data; nothing else is, and the
/// page still fetches nothing.
/// </summary>
public class SavedPictureTests
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    private static readonly string PngData = "data:image/png;base64," + Convert.ToBase64String(Png);

    private static readonly MessageSaveContext Context = new("Kelly", "Inbox", null, DateTimeOffset.Now);

    private static string Page(string html, SavedPictures? pictures) =>
        MessageExport.BuildHtmlDocument(new MailMessageDetail { Subject = "s", HtmlBody = html }, Context, pictures);

    private static SavedPictures With(Dictionary<string, SavedPicture>? embedded = null,
        Dictionary<string, SavedPicture>? web = null) =>
        new(embedded ?? new Dictionary<string, SavedPicture>(StringComparer.OrdinalIgnoreCase),
            url => web is not null && web.TryGetValue(url, out var p) ? p : null);

    [Fact]
    public void AnEmbeddedPicture_IsWrittenIntoThePage()
    {
        var page = Page("<p><img src=\"cid:a@x\" alt=\"Chart\" width=\"20\" height=\"10\" onerror=\"x()\"></p>",
            With(new() { ["a@x"] = new SavedPicture(Png, "image/png") }));

        Assert.Contains($"<img src=\"{PngData}\" alt=\"Chart\" width=\"20\" height=\"10\">", page);
        Assert.Contains("img-src data:;", page);
        Assert.DoesNotContain("onerror", page);
        Assert.DoesNotContain("quickmail-images", page);
    }

    [Fact]
    public void AWebPictureAlreadyLoaded_IsWrittenIntoThePage()
    {
        var page = Page("<img src=\"https://news.example/a.png\" alt=\"Sale\">",
            With(web: new() { ["https://news.example/a.png"] = new SavedPicture(Png, "image/png") }));

        Assert.Contains($"<img src=\"{PngData}\" alt=\"Sale\">", page);
        Assert.DoesNotContain("news.example", page);
    }

    [Fact]
    public void APictureNotToHand_LeavesItsDescription()
    {
        var page = Page("<p>A <img src=\"cid:gone@x\" alt=\"Missing\"> and <img src=\"https://x.example/b.png\" alt=\"Web\"></p>",
            With());

        Assert.DoesNotContain("<img", page);
        Assert.Contains("Missing", page);
        Assert.Contains("Web", page);
        Assert.Contains("img-src 'none'", page);
    }

    [Fact]
    public void WithNoPictures_ThePageIsAsBefore()
    {
        var html = "<p>Hi <img src=\"cid:a@x\" alt=\"Chart\"></p>";
        var page = MessageExport.BuildHtmlDocument(new MailMessageDetail { Subject = "s", HtmlBody = html }, Context);

        Assert.DoesNotContain("<img", page);
        Assert.Contains("Chart", page);
        Assert.Contains("img-src 'none'", page);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public void OnlyOrdinaryPictureTypes_AreWritten(string type)
    {
        var page = Page("<img src=\"cid:a@x\" alt=\"Chart\">",
            With(new() { ["a@x"] = new SavedPicture(Png, type) }));

        Assert.DoesNotContain("<img", page);
        Assert.Contains("img-src 'none'", page);
    }

    [Fact]
    public void ASendersOwnDataPicture_IsNotKept()
    {
        // Only QuickMail writes data: pictures. One the sender wrote is removed as before.
        var page = Page("<img src=\"data:image/png;base64,AAAA\" alt=\"Theirs\"><img src=\"cid:a@x\" alt=\"Ours\">",
            With(new() { ["a@x"] = new SavedPicture(Png, "image/png") }));

        Assert.DoesNotContain("base64,AAAA", page);
        Assert.Contains("Theirs", page);
        Assert.Contains($"<img src=\"{PngData}\" alt=\"Ours\">", page);
    }

    [Fact]
    public void ATrackingPixel_IsNeverWritten()
    {
        var page = Page("<p>Hi<img src=\"https://t.example/o.gif\" width=\"1\" height=\"1\"></p>",
            With(web: new() { ["https://t.example/o.gif"] = new SavedPicture(Png, "image/png") }));

        Assert.DoesNotContain("<img", page);
    }

    [Fact]
    public void APictureInsideATag_IsNotRestored()
    {
        var page = Page("<sty<img src=\"cid:a@x\" alt=\"a\">le>body{}</style>",
            With(new() { ["a@x"] = new SavedPicture(Png, "image/png") }));

        Assert.DoesNotContain("<style>body{}", page);
        Assert.DoesNotContain(PngData, page);
    }

    [Fact]
    public void ARepeatedPicture_IsEncodedOnce_AndThePageHasALimit()
    {
        var html = string.Concat(System.Linq.Enumerable.Repeat("<img src=\"cid:a@x\" alt=\"Dog\">", 5));
        var picture = new SavedPicture(Png, "image/png");
        var page = MessageExport.BuildHtmlDocument(new MailMessageDetail { Subject = "s", HtmlBody = html }, Context,
            With(new() { ["a@x"] = picture }), maxPictureChars: PngData.Length * 3L);

        // Three fit under the limit; the other two are their description.
        Assert.Equal(3, CountOf(page, PngData));
        Assert.Equal(2, CountOf(page, "Dog") - 3);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void APictureHiddenInAnEndTagsAttribute_CannotBreakOut()
    {
        // #728 review: an end tag kept its quoted attributes, whose ">" fooled the check that a
        // picture sits in text, and the restored picture's quotes ended the attribute early.
        var page = Page("<a href=\"x\">t</a foo=\"><img src=cid:a@x alt=Q> \">z",
            With(new() { ["a@x"] = new SavedPicture(Png, "image/png") }));

        Assert.DoesNotContain("foo=", page);
        Assert.DoesNotContain("\">z", page);
    }

    [Fact]
    public void AContentIdWrittenInAngleBrackets_IsTheSamePicture()
    {
        Assert.Equal(["a@x"], InlineImages.ReferencedContentIds("<img src=\"cid:&lt;a@x&gt;\">"));
        var page = Page("<img src=\"cid:&lt;a@x&gt;\" alt=\"Dog\">",
            With(new() { ["a@x"] = new SavedPicture(Png, "image/png") }));
        Assert.Contains(PngData, page);
    }

    [Fact]
    public void ADescriptionCannotBecomeMarkup()
    {
        var page = Page("<img src=\"cid:gone@x\" alt=\"&lt;script&gt;alert(1)&lt;/script&gt;\">", With());

        Assert.DoesNotContain("<script>", page);
    }
}

/// <summary>Saving asks only for web pictures already held; it never fetches one.</summary>
[Collection("WpfTests")]
public class WebPictureCacheLookupTests
{
    [Fact]
    public async Task TryGetCached_NeverTouchesTheNetwork()
    {
        var requests = 0;
        WebPictureFetcherTests.UseNetwork(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]),
            };
        });
        try
        {
            Assert.Null(WebPictureFetcher.TryGetCached("https://x.example/a.png"));
            Assert.Equal(0, requests);

            Assert.NotNull(await WebPictureFetcher.FetchAsync("https://x.example/a.png", TestContext.Current.CancellationToken));
            Assert.NotNull(WebPictureFetcher.TryGetCached("https://x.example/a.png"));
            Assert.Equal(1, requests);
        }
        finally
        {
            WebPictureFetcherTests.UseRealNetwork();
        }
    }
}
