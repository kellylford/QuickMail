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
    /// <summary>Captures the single request it serves and returns 202 Accepted (Graph /sendMail).</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
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
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static (GraphSendMailService Svc, CapturingHandler Handler) Make()
    {
        var handler = new CapturingHandler();
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

        await svc.SendAsync(compose, GraphAccount(), null);

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

        await svc.SendIcsReplyAsync("BEGIN:VCALENDAR\nEND:VCALENDAR", GraphAccount(), null, "organizer@x.com");

        Assert.Equal("https://graph.microsoft.com/v1.0/me/sendMail", handler.Url);
        var mime = DecodeMime(handler.Body!);
        Assert.Contains("organizer@x.com", mime);
        Assert.Contains("method=REPLY", mime.Replace("\"", "")); // calendar method survives the round-trip
    }
}
