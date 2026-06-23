using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class GraphSendMailServiceTests
{
    /// <summary>Captures the single request it serves and returns the configured status (202 by default).</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public CapturingHandler(HttpStatusCode status = HttpStatusCode.Accepted) => _status = status;

        public string? Url { get; private set; }
        public string? ContentType { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            if (request.Content != null)
            {
                ContentType = request.Content.Headers.ContentType?.MediaType;
                Body = await request.Content.ReadAsByteArrayAsync(ct);
            }
            // Always attach a body so a non-2xx response is surfaced via EnsureSuccessAsync
            // (which reads the error body) rather than NRE-ing on null content.
            return new HttpResponseMessage(_status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private static (GraphSendMailService Svc, CapturingHandler Handler) Make(HttpStatusCode status = HttpStatusCode.Accepted)
    {
        var handler = new CapturingHandler(status);
        return (new GraphSendMailService(new StubOAuthService(), new HttpClient(handler)), handler);
    }

    private static AccountModel GraphAccount() => new()
    {
        Id = Guid.NewGuid(),
        Username = "me@contoso.com",
        BackendKind = BackendKind.MicrosoftGraph,
    };

    /// <summary>The body is ASCII base64; decode it back to the raw MIME text.</summary>
    private static string DecodeMime(byte[] body) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(body)));

    [Fact]
    public async Task SendAsync_PostsBase64MimeToSendMail()
    {
        var (svc, handler) = Make();
        var compose = new ComposeModel { To = "alice@x.com", Subject = "Hi there", Body = "Hello" };

        await svc.SendAsync(compose, GraphAccount(), null, TestContext.Current.CancellationToken);

        Assert.Equal("https://graph.microsoft.com/v1.0/me/sendMail", handler.Url);
        Assert.Equal("text/plain", handler.ContentType);
        var mime = DecodeMime(handler.Body!);
        Assert.Contains("Subject: Hi there", mime);
        Assert.Contains("alice@x.com", mime);
    }

    [Fact]
    public async Task SendIcsReplyAsync_PostsCalendarReplyToSendMail()
    {
        var (svc, handler) = Make();

        await svc.SendIcsReplyAsync("BEGIN:VCALENDAR\nEND:VCALENDAR", GraphAccount(), null, "organizer@x.com", TestContext.Current.CancellationToken);

        Assert.Equal("https://graph.microsoft.com/v1.0/me/sendMail", handler.Url);
        var mime = DecodeMime(handler.Body!);
        Assert.Contains("<me@contoso.com>", mime);                   // From carries the sending account
        Assert.Contains("organizer@x.com", mime);                    // To is the organizer
        Assert.Contains("method=REPLY", mime.Replace("\"", "")); // calendar method survives the round-trip
    }

    [Fact]
    public async Task SendIcsReplyAsync_Throws_ForNullContent()
    {
        // A descriptive ArgumentException at the call site beats an NRE from inside MimeKit.
        var (svc, handler) = Make();
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SendIcsReplyAsync(null!, GraphAccount(), null, "organizer@x.com", TestContext.Current.CancellationToken));
        Assert.Null(handler.Url); // never reached the network
    }

    [Fact]
    public async Task SendIcsReplyAsync_Throws_ForMissingOrganizer()
    {
        var (svc, handler) = Make();
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SendIcsReplyAsync("BEGIN:VCALENDAR\nEND:VCALENDAR", GraphAccount(), null, "", TestContext.Current.CancellationToken));
        Assert.Null(handler.Url);
    }

    [Fact]
    public async Task SendAsync_SurfacesNon2xxResponse_AsException()
    {
        // A failed /sendMail must propagate — a silently-swallowed send would tell the user the
        // message was sent when it wasn't. GraphClient.EnsureSuccessAsync throws on non-2xx.
        var (svc, _) = Make(HttpStatusCode.InternalServerError);
        var compose = new ComposeModel { To = "alice@x.com", Subject = "Hi", Body = "Hello" };

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.SendAsync(compose, GraphAccount(), null, TestContext.Current.CancellationToken));
    }
}
