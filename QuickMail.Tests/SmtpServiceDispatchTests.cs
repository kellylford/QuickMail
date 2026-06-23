using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SmtpServiceDispatchTests
{
    /// <summary>Records which method was invoked, so we can assert routing without any network.</summary>
    private sealed class RecordingSmtp : ISendMailService
    {
        public bool SendCalled { get; private set; }
        public bool IcsCalled { get; private set; }

        public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
        {
            SendCalled = true;
            return Task.CompletedTask;
        }

        public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
            string organizerEmail, CancellationToken ct = default)
        {
            IcsCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SendAsync_RoutesGraphAccount_ToGraphBackend()
    {
        var graph = new RecordingSmtp();
        var svc = new SmtpService(new StubOAuthService(), graph);
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "me@contoso.com", BackendKind = BackendKind.MicrosoftGraph };

        await svc.SendAsync(new ComposeModel(), account, null, TestContext.Current.CancellationToken);

        Assert.True(graph.SendCalled); // dispatched, never reached the MailKit SmtpClient
    }

    [Fact]
    public async Task SendIcsReplyAsync_RoutesGraphAccount_ToGraphBackend()
    {
        var graph = new RecordingSmtp();
        var svc = new SmtpService(new StubOAuthService(), graph);
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "me@contoso.com", BackendKind = BackendKind.MicrosoftGraph };

        await svc.SendIcsReplyAsync("BEGIN:VCALENDAR\nEND:VCALENDAR", account, null, "organizer@x.com", TestContext.Current.CancellationToken);

        Assert.True(graph.IcsCalled);
    }

    [Fact]
    public async Task SendAsync_DoesNotRouteImapAccount_ToGraphBackend()
    {
        var graph = new RecordingSmtp();
        var svc = new SmtpService(new StubOAuthService(), graph);
        // ImapSmtp account with an empty SMTP host: the MailKit path is taken and fails fast on
        // connect. The point is the dispatch must NOT hand an IMAP account to the Graph sender.
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "me@example.com", BackendKind = BackendKind.ImapSmtp, SmtpHost = "" };

        await Assert.ThrowsAnyAsync<Exception>(() => svc.SendAsync(new ComposeModel(), account, "pw", TestContext.Current.CancellationToken));

        Assert.False(graph.SendCalled); // took the MailKit path, not the Graph dispatch
    }

    [Fact]
    public async Task SendIcsReplyAsync_DoesNotRouteImapAccount_ToGraphBackend()
    {
        var graph = new RecordingSmtp();
        var svc = new SmtpService(new StubOAuthService(), graph);
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "me@example.com", BackendKind = BackendKind.ImapSmtp, SmtpHost = "" };

        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.SendIcsReplyAsync("BEGIN:VCALENDAR\nEND:VCALENDAR", account, "pw", "organizer@x.com", TestContext.Current.CancellationToken));

        Assert.False(graph.IcsCalled); // took the MailKit path, not the Graph dispatch
    }
}
