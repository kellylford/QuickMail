using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pictures from the web (#508) in the message document: left out with a notice until the user
/// asks, then shown through QuickMail's own picture address. The sender's address never reaches
/// the document, and the policy never names a web origin.
/// </summary>
public class WebPictureTests
{
    private const string Embedded = "https://quickmail-images.invalid/e1/";
    private const string Web = "https://quickmail-images.invalid/w1/";

    private static MessageDocument Build(string html, bool web, bool note = true, bool embedded = true) =>
        MessageBodyHtmlBuilder.BuildMessageDocument(
            new MailMessageDetail { Subject = "s", HtmlBody = html }, null, false, null,
            new PictureSources(embedded ? Embedded : null, web ? Web : null, note && !web));

    [Fact]
    public void Blocked_LeavesThePictureOut_WithANoticeAndALink()
    {
        var doc = Build("<p>Hello <img src=\"https://news.example/a.png\" alt=\"Sale\"></p>", web: false);

        Assert.Equal(1, doc.BlockedWebPictures);
        Assert.Empty(doc.WebPictures);
        Assert.DoesNotContain("news.example", doc.Html);
        Assert.Contains(MessageBodyHtmlBuilder.WebPicturesNoticeText, doc.Html);
        Assert.Contains(QuickMailLinks.Build(MessageBodyHtmlBuilder.LoadPicturesAction), doc.Html);
        Assert.Contains("img-src 'none'", doc.Html);
        // Blocked as before: its description is still read in its place.
        Assert.Contains("Sale", doc.Html);
    }

    [Fact]
    public void Blocked_WithNoNoticeAsked_SaysNothing()
    {
        var doc = Build("<img src=\"https://news.example/a.png\" alt=\"x\">", web: false, note: false);

        Assert.Equal(1, doc.BlockedWebPictures);
        Assert.DoesNotContain(MessageBodyHtmlBuilder.WebPicturesNoticeText, doc.Html);
    }

    [Fact]
    public void NoWebPictures_NoNotice()
    {
        var doc = Build("<p>Just words <img src=\"cid:a@b\" alt=\"inline\"></p>", web: false);

        Assert.Equal(0, doc.BlockedWebPictures);
        Assert.DoesNotContain(MessageBodyHtmlBuilder.WebPicturesNoticeText, doc.Html);
    }

    [Fact]
    public void Loaded_PointsAtQuickMailsOwnAddress_NeverTheSenders()
    {
        var doc = Build("<p><img src=\"https://news.example/a.png?u=kelly\" alt=\"Sale\" width=\"300\" height=\"100\" " +
                        "onerror=\"alert(1)\" style=\"position:fixed\"></p>", web: true);

        Assert.Equal(["https://news.example/a.png?u=kelly"], doc.WebPictures);
        Assert.Equal(0, doc.BlockedWebPictures);
        Assert.Contains($"<img src=\"{Web}0\" alt=\"Sale\" width=\"300\" height=\"100\">", doc.Html);
        Assert.DoesNotContain("news.example", doc.Html);
        Assert.DoesNotContain("onerror", doc.Html);
        Assert.DoesNotContain("position:fixed", doc.Html);
        Assert.Contains("img-src https://quickmail-images.invalid;", doc.Html);
        Assert.DoesNotContain(MessageBodyHtmlBuilder.WebPicturesNoticeText, doc.Html);
    }

    [Fact]
    public void Loaded_TheSameAddressTwice_IsFetchedOnce()
    {
        var doc = Build("<img src=\"https://x.example/a.png\" alt=\"1\"><img src=\"https://x.example/b.png\" alt=\"2\">" +
                        "<img src=\"https://x.example/a.png\" alt=\"3\">", web: true);

        Assert.Equal(["https://x.example/a.png", "https://x.example/b.png"], doc.WebPictures);
        Assert.Contains($"<img src=\"{Web}0\" alt=\"1\">", doc.Html);
        Assert.Contains($"<img src=\"{Web}1\" alt=\"2\">", doc.Html);
        Assert.Contains($"<img src=\"{Web}0\" alt=\"3\">", doc.Html);
    }

    [Fact]
    public void EmbeddedAndWeb_BothShow()
    {
        var doc = Build("<img src=\"cid:logo@x\" alt=\"Logo\"><img src=\"https://x.example/a.png\" alt=\"Web\">", web: true);

        Assert.Contains($"<img src=\"{Embedded}logo%40x\" alt=\"Logo\">", doc.Html);
        Assert.Contains($"<img src=\"{Web}0\" alt=\"Web\">", doc.Html);
    }

    [Theory]
    [InlineData("width=\"1\" height=\"1\"")]
    [InlineData("width=1 height=1")]
    [InlineData("width=\"0\"")]
    [InlineData("height=\"1px\"")]
    [InlineData("width=\"2\" height=\"40\"")]
    public void TrackingPixels_AreNeverFetched_NorCounted(string size)
    {
        var loaded = Build($"<p>Hi<img src=\"https://t.example/o.gif\" {size}></p>", web: true);
        Assert.Empty(loaded.WebPictures);
        Assert.DoesNotContain("<img", loaded.Html);

        var blocked = Build($"<p>Hi<img src=\"https://t.example/o.gif\" {size}></p>", web: false);
        Assert.Equal(0, blocked.BlockedWebPictures);
        Assert.DoesNotContain(MessageBodyHtmlBuilder.WebPicturesNoticeText, blocked.Html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("//news.example/a.png")]
    [InlineData("/a.png")]
    [InlineData("https://user:pass@news.example/a.png")]
    [InlineData("ftp://news.example/a.png")]
    [InlineData("https://quickmail-images.invalid/e1/logo%40x")]
    public void OtherAddresses_AreNotWebPictures(string src)
    {
        // The last is a web address in form, but pointing at QuickMail's own origin: fetched from
        // the network it goes nowhere (.invalid never resolves), and it is never served locally
        // because only QuickMail's own markup carries the per-message key it would need.
        var doc = Build($"<img src=\"{src}\" alt=\"x\">", web: true);

        if (src.StartsWith("https://quickmail-images.invalid", StringComparison.Ordinal))
        {
            Assert.Equal([src], doc.WebPictures);
            return;
        }
        Assert.Empty(doc.WebPictures);
        Assert.DoesNotContain("<img", doc.Html);
    }

    [Fact]
    public void AWebPictureInsideATag_IsNotSetAside()
    {
        // As with embedded pictures: set aside inside a tag name, the marker would hide the name
        // from every pass and rejoin a live tag when dropped.
        var doc = Build("<sty<img src=\"https://x.example/a.png\" alt=\"a\">le>body{}</style>", web: true);

        Assert.DoesNotContain("<style>body", doc.Html);
        Assert.DoesNotContain($"<img src=\"{Web}", doc.Html);
    }

    [Fact]
    public void MoreThanTheLimit_TheRestAreLeftOut()
    {
        var html = string.Concat(Enumerable.Range(0, MessageBodyHtmlBuilder.MaxWebPictures + 5)
            .Select(i => $"<img src=\"https://x.example/{i}.png\" alt=\"p{i}\">"));

        var doc = Build(html, web: true);

        Assert.Equal(MessageBodyHtmlBuilder.MaxWebPictures, doc.WebPictures.Count);
    }

    [Fact]
    public void AnAddressWithQuotes_CannotBreakOutOfTheAttribute()
    {
        var doc = Build("<img src='https://x.example/a.png\"onerror=\"alert(1)' alt=\"a\">", web: true);

        Assert.DoesNotContain("onerror", doc.Html);
    }

    [Fact]
    public void PlainTextRendering_HasNoPicturesAndNoNotice()
    {
        var doc = MessageBodyHtmlBuilder.BuildMessageDocument(
            new MailMessageDetail { Subject = "s", HtmlBody = "<img src=\"https://x.example/a.png\" alt=\"a\">", PlainTextBody = "text" },
            null, true, null, PictureSources.None);

        Assert.Equal(0, doc.BlockedWebPictures);
        Assert.DoesNotContain(MessageBodyHtmlBuilder.WebPicturesNoticeText, doc.Html);
    }

    [Fact]
    public void ASenderWrittenLoadPicturesLink_IsNotQuickMails()
    {
        // The notice's link carries this run's token; a sender cannot write one.
        Assert.False(QuickMailLinks.TryParse("quickmail:load-pictures", out _));
        Assert.False(QuickMailLinks.TryParse("quickmail:load-pictures?t=0000", out _));
        Assert.True(QuickMailLinks.TryParse(QuickMailLinks.Build(MessageBodyHtmlBuilder.LoadPicturesAction), out var action));
        Assert.Equal(MessageBodyHtmlBuilder.LoadPicturesAction, action);
    }

    [Fact]
    public void Setting_IsOffByDefault_AndRoundTrips()
    {
        Assert.False(new ConfigModel().LoadWebPictures);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"QuickMailWebPics-{Guid.NewGuid():N}");
        var profile = new ProfileContext(dir);
        var service = new ConfigService(profile);
        var config = service.Load();
        Assert.False(config.LoadWebPictures);
        config.LoadWebPictures = true;
        service.Save(config);
        Assert.True(new ConfigService(profile).Load().LoadWebPictures);
    }
}

/// <summary>
/// The fetch itself (#508): only pictures, only public addresses, no cookies or Referer, within
/// size limits. Shares a collection with the WebView2 tests, which also replace the network.
/// </summary>
[Collection("WpfTests")]
public class WebPictureFetcherTests
{
    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    internal static void UseNetwork(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        WebPictureFetcher.ClearCache();
        WebPictureFetcher.HandlerOverride = new FakeNetwork(respond);
        WebPictureFetcher.ResolveOverride = null;
    }

    internal static void UseRealNetwork()
    {
        WebPictureFetcher.HandlerOverride = null;
        WebPictureFetcher.ResolveOverride = null;
        WebPictureFetcher.ClearCache();
    }

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    private static HttpResponseMessage Ok(byte[] body, string? claimedType = null)
    {
        var content = new ByteArrayContent(body);
        if (claimedType is not null) content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(claimedType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<WebPictureFetcher.WebPicture?> Fetch(string url) =>
        await WebPictureFetcher.FetchAsync(url, TestContext.Current.CancellationToken);

    [Fact]
    public async Task APicture_IsKnownByItsBytes_NotByWhatTheServerSays()
    {
        try
        {
            UseNetwork(_ => Ok(PngBytes, "application/octet-stream"));
            var picture = await Fetch("https://x.example/a");
            Assert.NotNull(picture);
            Assert.Equal("image/png", picture.ContentType);
        }
        finally { UseRealNetwork(); }
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "image/svg+xml")]
    [InlineData("<html><script>alert(1)</script></html>", "image/png")]
    [InlineData("GIF", "image/gif")]
    public async Task AnythingButARasterPicture_IsRefused(string body, string claimedType)
    {
        try
        {
            UseNetwork(_ => Ok(System.Text.Encoding.UTF8.GetBytes(body), claimedType));
            Assert.Null(await Fetch("https://x.example/a.svg"));
        }
        finally { UseRealNetwork(); }
    }

    [Fact]
    public async Task NoCookieOrReferer_IsSent()
    {
        try
        {
            var network = new FakeNetwork(_ => Ok(PngBytes));
            WebPictureFetcher.ClearCache();
            WebPictureFetcher.HandlerOverride = network;
            Assert.NotNull(await Fetch("https://x.example/a.png"));
            var request = Assert.Single(network.Requests);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Referrer);
            Assert.Equal(HttpMethod.Get, request.Method);
        }
        finally { UseRealNetwork(); }
    }

    [Fact]
    public async Task TooLarge_IsRefused_ByLengthOrByWhatArrives()
    {
        try
        {
            var big = new byte[WebPictureFetcher.MaxPictureBytes + 1];
            PngBytes.CopyTo(big, 0);
            UseNetwork(_ => Ok(big));
            Assert.Null(await Fetch("https://x.example/big.png"));

            UseNetwork(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new System.IO.MemoryStream(big)), // no length declared
            });
            Assert.Null(await Fetch("https://x.example/big2.png"));
        }
        finally { UseRealNetwork(); }
    }

    [Fact]
    public async Task Redirects_AreFollowed_ButOnlyToWebAddresses()
    {
        try
        {
            UseNetwork(r => r.RequestUri!.AbsolutePath switch
            {
                "/start" => Redirect("/final.png"),
                "/final.png" => Ok(PngBytes),
                "/to-file" => Redirect("file:///C:/Windows/win.ini"),
                "/loop" => Redirect("/loop"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            Assert.NotNull(await Fetch("https://x.example/start"));
            Assert.Null(await Fetch("https://x.example/to-file"));
            Assert.Null(await Fetch("https://x.example/loop"));
        }
        finally { UseRealNetwork(); }

        static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        }
    }

    [Fact]
    public async Task ALocalAddress_IsNeverRequested_EvenByRedirect()
    {
        try
        {
            var network = new FakeNetwork(r => r.RequestUri!.Host == "public.example"
                ? RedirectTo("http://router.example/reboot")
                : Ok(PngBytes));
            WebPictureFetcher.ClearCache();
            WebPictureFetcher.HandlerOverride = network;
            WebPictureFetcher.ResolveOverride = (host, _) => Task.FromResult(host switch
            {
                "public.example" => new[] { IPAddress.Parse("93.184.216.34") },
                _ => new[] { IPAddress.Parse("192.168.1.1") },
            });

            Assert.Null(await Fetch("http://router.example/admin.png"));
            Assert.Null(await Fetch("http://127.0.0.1/a.png"));
            Assert.Null(await Fetch("http://public.example/a.png"));
            Assert.DoesNotContain(network.Requests, r => r.RequestUri!.Host is "router.example" or "127.0.0.1");
        }
        finally { UseRealNetwork(); }

        static HttpResponseMessage RedirectTo(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        }
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:2800:220:1::1", true)]
    [InlineData("64:ff9b::5db8:d822", true)]
    public void PublicAddresses(string address, bool isPublic) =>
        Assert.Equal(isPublic, WebPictureFetcher.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task AFetchedPicture_IsRemembered()
    {
        try
        {
            var network = new FakeNetwork(_ => Ok(PngBytes));
            WebPictureFetcher.ClearCache();
            WebPictureFetcher.HandlerOverride = network;
            Assert.NotNull(await Fetch("https://x.example/a.png"));
            Assert.NotNull(await Fetch("https://x.example/a.png"));
            Assert.Single(network.Requests);
        }
        finally { UseRealNetwork(); }
    }

    /// <summary>A body that never arrives: headers sent, then silence.</summary>
    private sealed class StalledStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StalledServers_GiveUp_AndDoNotBlockOtherPictures()
    {
        // #508 security review: six servers that send headers and then nothing held every fetch
        // slot for good, and no web picture loaded anywhere until QuickMail restarted.
        var saved = WebPictureFetcher.DownloadDeadline;
        try
        {
            WebPictureFetcher.DownloadDeadline = TimeSpan.FromSeconds(1);
            UseNetwork(r => r.RequestUri!.Host == "slow.example"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }
                : Ok(PngBytes));

            var slow = Enumerable.Range(0, 8).Select(i => Fetch($"https://slow.example/{i}.png")).ToArray();
            var started = DateTime.UtcNow;
            var ordinary = await Fetch("https://fine.example/a.png");

            Assert.NotNull(ordinary);
            Assert.All(await Task.WhenAll(slow), Assert.Null);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        }
        finally
        {
            WebPictureFetcher.DownloadDeadline = saved;
            UseRealNetwork();
        }
    }

    [Fact]
    public async Task AnEncryptedPicture_NeverRedirectsToPlainHttp()
    {
        try
        {
            UseNetwork(r => r.RequestUri!.Scheme == "https"
                ? RedirectTo("http://x.example/a.png")
                : Ok(PngBytes));
            Assert.Null(await Fetch("https://x.example/a.png"));
            // Plain to encrypted is fine.
            UseNetwork(r => r.RequestUri!.Scheme == "http"
                ? RedirectTo("https://x.example/b.png")
                : Ok(PngBytes));
            Assert.NotNull(await Fetch("http://x.example/b.png"));
        }
        finally { UseRealNetwork(); }

        static HttpResponseMessage RedirectTo(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        }
    }

    [Fact]
    public async Task AFailure_IsNull_NotAnException()
    {
        try
        {
            UseNetwork(_ => throw new HttpRequestException("offline"));
            Assert.Null(await Fetch("https://x.example/a.png"));
            UseNetwork(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            Assert.Null(await Fetch("https://x.example/b.png"));
        }
        finally { UseRealNetwork(); }
    }
}
