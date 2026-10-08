using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #780: replacing an IMAP draft must save the new copy before removing the old one. The other
/// order deleted and expunged the old draft first, so an append that then failed — a dropped
/// connection mid-save — left the server with no draft at all. Driven against a scripted IMAP
/// server so the order of the commands on the wire is what is asserted.
/// </summary>
public partial class ImapDraftReplaceOrderTests
{
    private static AccountModel Account(int port) => new()
    {
        Id          = Guid.NewGuid(),
        AccountName = "Kelly",
        Username    = "kelly@example.com",
        AuthType    = AuthType.Password,
        ImapHost    = "127.0.0.1",
        ImapPort    = port,
        ImapUseSsl  = false,
    };

    private static ComposeModel Draft() => new() { To = "someone@example.com", Subject = "v2", Body = "newer text" };

    [Fact]
    public async Task ReplacingDraft_AppendsNewCopyBeforeDeletingOld()
    {
        using var server = new ScriptedImapServer(appendSucceeds: true);
        using var service = new ImapMailService(new StubOAuthService());
        var account = Account(server.Port);
        await service.ConnectAsync(account, "pw", TestContext.Current.CancellationToken);

        var newId = await service.AppendDraftAsync(account.Id, Draft(), "7", TestContext.Current.CancellationToken);

        Assert.Equal("11", newId);
        var verbs = server.Commands;
        var append = verbs.FindIndex(c => c.StartsWith("APPEND", StringComparison.Ordinal));
        var store  = verbs.FindIndex(c => c.StartsWith("UID STORE 7", StringComparison.Ordinal));
        Assert.True(append >= 0, "no APPEND sent: " + string.Join(" | ", verbs));
        Assert.True(store > append, "old draft flagged deleted before the new one was saved: " + string.Join(" | ", verbs));
        // UIDPLUS is advertised, so only the superseded draft is expunged.
        Assert.Contains(verbs, c => c.StartsWith("UID EXPUNGE 7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedAppend_LeavesOldDraftUntouched()
    {
        using var server = new ScriptedImapServer(appendSucceeds: false);
        using var service = new ImapMailService(new StubOAuthService());
        var account = Account(server.Port);
        await service.ConnectAsync(account, "pw", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<MailKit.Net.Imap.ImapCommandException>(
            () => service.AppendDraftAsync(account.Id, Draft(), "7", TestContext.Current.CancellationToken));

        var verbs = server.Commands;
        // The refusal must be the APPEND's, not an earlier failure that never reached it.
        Assert.Contains(verbs, c => c.StartsWith("APPEND", StringComparison.Ordinal));
        Assert.DoesNotContain(verbs, c => c.Contains("STORE", StringComparison.Ordinal));
        Assert.DoesNotContain(verbs, c => c.Contains("EXPUNGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedRemovalOfOldDraft_StillReturnsTheNewOne()
    {
        // The new draft is on the server once APPEND answers; failing to delete the old copy is a
        // duplicate, and must not report the save as failed and lose the new UID.
        using var server = new ScriptedImapServer(appendSucceeds: true, storeSucceeds: false);
        using var service = new ImapMailService(new StubOAuthService());
        var account = Account(server.Port);
        await service.ConnectAsync(account, "pw", TestContext.Current.CancellationToken);

        var newId = await service.AppendDraftAsync(account.Id, Draft(), "7", TestContext.Current.CancellationToken);

        Assert.Equal("11", newId);
    }

    [Fact]
    public async Task WithoutUidPlus_FallsBackToPlainExpunge()
    {
        using var server = new ScriptedImapServer(appendSucceeds: true, uidPlus: false);
        using var service = new ImapMailService(new StubOAuthService());
        var account = Account(server.Port);
        await service.ConnectAsync(account, "pw", TestContext.Current.CancellationToken);

        await service.AppendDraftAsync(account.Id, Draft(), "7", TestContext.Current.CancellationToken);

        var verbs = server.Commands;
        var append = verbs.FindIndex(c => c.StartsWith("APPEND", StringComparison.Ordinal));
        Assert.True(verbs.FindIndex(c => c.StartsWith("UID STORE 7", StringComparison.Ordinal)) > append);
        Assert.Contains(verbs, c => c == "EXPUNGE");
        Assert.DoesNotContain(verbs, c => c.StartsWith("UID EXPUNGE", StringComparison.Ordinal));
    }

    /// <summary>
    /// Just enough IMAP for a login, a folder list with a \Drafts folder, SELECT, APPEND, UID STORE
    /// and UID EXPUNGE. Records each command without its tag; anything else gets a plain OK.
    /// </summary>
    private sealed partial class ScriptedImapServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly bool _appendSucceeds;
        private readonly bool _storeSucceeds;
        private readonly string _capabilities;

        public ScriptedImapServer(bool appendSucceeds, bool storeSucceeds = true, bool uidPlus = true)
        {
            _appendSucceeds = appendSucceeds;
            _storeSucceeds = storeSucceeds;
            _capabilities = uidPlus ? "IMAP4rev1 UIDPLUS SPECIAL-USE LITERAL+" : "IMAP4rev1 SPECIAL-USE LITERAL+";
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public List<string> Commands => _commands.ToList();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = ServeAsync(socket);
                }
            }
            catch (Exception) { /* listener stopped */ }
        }

        [GeneratedRegex(@"\{(\d+)\+?\}$")]
        private static partial Regex LiteralSuffix();

        private async Task ServeAsync(TcpClient socket)
        {
            using var _ = socket;
            var stream = socket.GetStream();
            async Task Send(string line)
            {
                var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
                await stream.WriteAsync(bytes, _cts.Token);
            }

            try
            {
                await Send($"* OK [CAPABILITY {_capabilities}] ready");
                while (true)
                {
                    var line = await ReadLineAsync(stream);
                    if (line == null) return;

                    // Swallow any literal (APPEND's message) so the stream stays in step.
                    var literal = LiteralSuffix().Match(line);
                    if (literal.Success)
                    {
                        if (!line.EndsWith("+}", StringComparison.Ordinal)) await Send("+ go ahead");
                        await ReadExactlyAsync(stream, int.Parse(literal.Groups[1].Value));
                        await ReadLineAsync(stream); // the CRLF that ends the command
                    }

                    var space = line.IndexOf(' ');
                    var tag = line[..space];
                    var command = line[(space + 1)..];
                    _commands.Enqueue(command);
                    var verb = command.Split(' ')[0].ToUpperInvariant();

                    switch (verb)
                    {
                        case "CAPABILITY":
                            await Send($"* CAPABILITY {_capabilities}");
                            await Send($"{tag} OK done");
                            break;
                        case "LIST":
                            await Send("* LIST (\\HasNoChildren) \"/\" INBOX");
                            await Send("* LIST (\\HasNoChildren \\Drafts) \"/\" Drafts");
                            await Send($"{tag} OK done");
                            break;
                        case "SELECT":
                        case "EXAMINE":
                            await Send("* 2 EXISTS");
                            await Send("* 0 RECENT");
                            await Send("* OK [UIDVALIDITY 1] ok");
                            await Send("* OK [UIDNEXT 11] ok");
                            await Send("* FLAGS (\\Seen \\Deleted \\Draft)");
                            await Send($"{tag} OK [READ-WRITE] selected");
                            break;
                        case "UID" when !_storeSucceeds && command.StartsWith("UID STORE", StringComparison.Ordinal):
                            await Send($"{tag} NO store refused");
                            break;
                        case "APPEND":
                            await Send(_appendSucceeds
                                ? $"{tag} OK [APPENDUID 1 11] appended"
                                : $"{tag} NO [OVERQUOTA] append refused");
                            break;
                        case "LOGOUT":
                            await Send("* BYE bye");
                            await Send($"{tag} OK done");
                            return;
                        default:
                            await Send($"{tag} OK done");
                            break;
                    }
                }
            }
            catch (Exception) { /* client went away or server stopped */ }
        }

        private async Task<string?> ReadLineAsync(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one, _cts.Token);
                if (n == 0) return null;
                if (one[0] == '\n')
                    return sb.ToString().TrimEnd('\r');
                sb.Append((char)one[0]);
            }
        }

        private async Task ReadExactlyAsync(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, _cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
