using System.Diagnostics;
using System.IO;
using MailKit.Net.Pop3;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// The POP3 backend (#128) against a real POP3 server. This is the half the unit tests cannot
/// reach: RETR, UIDL deduplication across sessions, and DELE — including the case that decides
/// whether a user keeps their mail, where the server has dropped every message QuickMail holds.
///
/// <para>Everything runs against the real <see cref="LocalStoreService"/> on a temp profile,
/// because for POP3 the store is not a cache of the mailbox — it is the mailbox.</para>
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class Pop3ProtocolTests : IDisposable
{
    private readonly GreenMailFixture _greenMail;
    private readonly string _profileDir;

    public Pop3ProtocolTests(GreenMailFixture greenMail)
    {
        _greenMail  = greenMail;
        _profileDir = Path.Combine(Path.GetTempPath(), $"QuickMailPop3IT-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true); }
        catch { /* a temp dir left behind is not a test failure */ }
    }

    private LocalStoreService NewStore()
    {
        var store = new LocalStoreService(new ProfileContext(
            Path.Combine(_profileDir, Guid.NewGuid().ToString("N"))));
        store.Initialize();
        return store;
    }

    // ── Collecting mail ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_DownloadsMailIntoTheLocalStore()
    {
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-download");
        await SeedAsync(account.Username, "First message", "Body of the first message.", ct);
        await SeedAsync(account.Username, "Second message", "Body of the second message.", ct);

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);

        Assert.True(pop.IsConnected(account.Id));

        var downloaded = await WaitForDownloadAsync(pop, account.Id, expected: 2, ct);

        Assert.Equal(2, downloaded.Count);
        Assert.Contains(downloaded, m => m.Subject == "First message");

        // The whole message is stored, not just a header: opening it later must not need the server.
        var summary = downloaded.First(m => m.Subject == "First message");
        var detail  = await pop.GetMessageDetailAsync(account.Id, "Inbox", summary.MessageId, ct);
        Assert.Contains("first message", detail.PlainTextBody, StringComparison.OrdinalIgnoreCase);
        Assert.False(summary.IsRead);
    }

    [Fact]
    public async Task Resync_DownloadsNothingTwice()
    {
        // UIDL deduplication is the property POP3 correctness rests on: the protocol has no
        // high-water mark, so without it every sync would re-download the whole mailbox.
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-dedup");
        await SeedAsync(account.Username, "Only message", "Body.", ct);

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);

        await WaitForDownloadAsync(pop, account.Id, expected: 1, ct);

        var second = await pop.GetMessagesSinceAsync(account.Id, "Inbox", "0", 50, ct);
        var third  = await pop.PollAsync(account.Id, "Inbox", ct);

        Assert.Empty(second);
        Assert.Equal(0, third);
        Assert.Single(await store.GetAllMessageIdsAsync(account.Id, "Inbox"));
    }

    [Fact]
    public async Task LeaveOnServer_KeepsTheServerCopy()
    {
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-leave", leaveOnServer: true);
        await SeedAsync(account.Username, "Keep me", "Body.", ct);

        using var pop = new Pop3MailService(NewStore());
        await pop.ConnectAsync(account, "pw", ct);
        await WaitForDownloadAsync(pop, account.Id, expected: 1, ct);

        // The default must never remove the user's mail from the server — another client, or the
        // provider's webmail, is still the copy they expect to find.
        Assert.Equal(1, await ServerMessageCountAsync(account, ct));
    }

    [Fact]
    public async Task LeaveOnServerOff_RemovesTheServerCopyOnceItIsStored()
    {
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-remove", leaveOnServer: false);
        await SeedAsync(account.Username, "Collect me", "Body.", ct);

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);
        await WaitForDownloadAsync(pop, account.Id, expected: 1, ct);

        Assert.Equal(0, await ServerMessageCountAsync(account, ct));
        // ...and the local copy — now the only one — survived the deletion.
        Assert.Single(await store.GetAllMessageIdsAsync(account.Id, "Inbox"));
    }

    // ── The sync contract, against a server that really has dropped the mail ─────

    [Fact]
    public async Task AfterTheServerDropsEverything_TheSweepStillCannotDeleteTheLocalCopies()
    {
        // The failure this guards against destroys mail: SyncService's id-diff sweep deletes cached
        // ids the backend's listing omits, and with leave-on-server off the server legitimately
        // lists nothing at all a moment after collection.
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-sweep", leaveOnServer: false);
        await SeedAsync(account.Username, "Vanishing message", "Body.", ct);

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);
        await WaitForDownloadAsync(pop, account.Id, expected: 1, ct);
        Assert.Equal(0, await ServerMessageCountAsync(account, ct));

        var cached  = await store.GetAllMessageIdsAsync(account.Id, "Inbox");
        var listing = await pop.GetFolderMessageIdDatesAsync(account.Id, "Inbox", ct);
        var listed  = listing.Select(l => l.Id).ToHashSet();

        // This subtraction IS the sweep's deletion reconcile (SyncService.ReconcileDeletionsAsync).
        Assert.Empty(cached.Except(listed));

        // The same for the older reconcile path.
        var ids = await pop.GetFolderMessageIdsAsync(account.Id, "Inbox", ct);
        Assert.Empty(cached.Except(ids));
    }

    [Fact]
    public async Task ANewServerMessageIsReportedAsSomethingToFetch()
    {
        // The other half of the contract: the listing must make genuinely new mail visible to the
        // sweep, or POP3 accounts would only ever collect when the user opens the folder.
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-newmail");

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);
        await SeedAsync(account.Username, "Fresh mail", "Body.", ct);
        await WaitForServerCountAsync(account, 1, ct);

        var listing = await pop.GetFolderMessageIdDatesAsync(account.Id, "Inbox", ct);
        var fresh   = Assert.Single(listing);

        // Reported as arriving now, so it falls inside the sweep's window and triggers the fetch.
        Assert.True(fresh.ReceivedUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(fresh.IsRead);

        // And the fetch the sweep then makes is what actually downloads it.
        var fetched = await pop.GetMessagesSinceDateAsync(account.Id, "Inbox", DateTime.UtcNow.AddDays(-30), ct);
        Assert.Contains(fetched, m => m.Subject == "Fresh mail");
    }

    // ── Deleting ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PermanentDelete_RemovesTheRightMessageFromTheServer()
    {
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-delete", leaveOnServer: true);
        await SeedAsync(account.Username, "Delete me", "Body.", ct);
        await SeedAsync(account.Username, "Keep me",   "Body.", ct);

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);
        var downloaded = await WaitForDownloadAsync(pop, account.Id, expected: 2, ct);

        var doomed = downloaded.First(m => m.Subject == "Delete me");
        var keeper = downloaded.First(m => m.Subject == "Keep me");

        // Deleting is two steps: to local Trash, then permanently. Only the second reaches the
        // server, and only for an account set to remove collected mail.
        await pop.MoveToTrashAsync(account.Id, "Inbox", doomed.MessageId, ct);
        Assert.Equal(2, await ServerMessageCountAsync(account, ct));

        account.Pop3LeaveMailOnServer = false;   // the user changes their mind in Account Manager
        await pop.ConnectAsync(account, "pw", ct);
        await pop.PermanentlyDeleteBatchAsync(account.Id, "Trash", [doomed.MessageId], ct);

        // Exactly one message gone, and it is the right one — DELE takes a session-scoped index, so
        // resolving it against a stale listing would delete a message the user meant to keep.
        var remaining = await ServerUidlsAsync(account, ct);
        Assert.Single(remaining);
        Assert.Equal(keeper.MessageId, remaining[0]);
        Assert.Empty(await store.GetAllMessageIdsAsync(account.Id, "Trash"));
    }

    [Fact]
    public async Task PermanentDelete_OfAMessageTheServerNoLongerHas_TouchesNothing()
    {
        // The UIDL safety check. Another client collected the message with delete-on-collect, so the
        // id QuickMail holds is gone; deleting by position instead would destroy someone else's mail.
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-safety", leaveOnServer: false);
        await SeedAsync(account.Username, "Still here", "Body.", ct);
        await WaitForServerCountAsync(account, 1, ct);

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);

        await pop.PermanentlyDeleteBatchAsync(account.Id, "Trash", ["uidl-that-is-not-on-this-server"], ct);

        Assert.Equal(1, await ServerMessageCountAsync(account, ct));
    }

    // ── Sending from a POP3 account ──────────────────────────────────────────────

    [Fact]
    public async Task APop3AccountSendsOverSmtp_AndFilesItsOwnCopyInSent()
    {
        // POP3 is receive-only, so send goes through the ordinary SmtpService path. Worth proving
        // end to end: the account carries POP3 host settings, and nothing in the send path may take
        // those for the outgoing server.
        _greenMail.RequirePop3();
        var ct        = TestContext.Current.CancellationToken;
        var sender    = _greenMail.CreatePop3Account("pop-sender");
        var recipient = _greenMail.CreatePop3Account("pop-recipient");

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(sender, "pw", ct);

        var compose = new ComposeModel
        {
            To      = recipient.Username,
            Subject = "Sent from POP3",
            Body    = "Outgoing mail from an account that receives over POP3.",
        };

        var smtp = new SmtpService(new NoOpOAuthService(), new NoOpGraphSendService());
        await smtp.SendAsync(compose, sender, "pw", ct);

        // Sent mail has no server folder to land in, so QuickMail files its own copy locally.
        await pop.AppendToSentAsync(sender.Id, compose, ct);
        var sent = await store.LoadFolderSummariesAsync(sender.Id, "Sent");
        Assert.Equal("Sent from POP3", sent.Single().Subject);

        // And it really arrived: collect it as the recipient.
        using var recipientPop = new Pop3MailService(NewStore());
        await recipientPop.ConnectAsync(recipient, "pw", ct);
        var delivered = await WaitForDownloadAsync(recipientPop, recipient.Id, expected: 1, ct);
        Assert.Equal("Sent from POP3", delivered[0].Subject);
    }

    // ── Attachments ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAttachmentSurvivesTheRoundTrip_AndOpensWithoutTheServer()
    {
        _greenMail.RequirePop3();
        var ct      = TestContext.Current.CancellationToken;
        var account = _greenMail.CreatePop3Account("pop-attach", leaveOnServer: false);
        var payload = new byte[3000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        await SeedAsync(account.Username, "With attachment", "See attached.", ct, builder =>
            builder.Attachments.Add("report.bin", payload, new ContentType("application", "octet-stream")));

        var store = NewStore();
        using var pop = new Pop3MailService(store);
        await pop.ConnectAsync(account, "pw", ct);
        var downloaded = await WaitForDownloadAsync(pop, account.Id, expected: 1, ct);

        var detail = await pop.GetMessageDetailAsync(account.Id, "Inbox", downloaded[0].MessageId, ct);
        var attachment = Assert.Single(detail.Attachments);
        Assert.Equal("report.bin", attachment.FileName);
        Assert.Equal(payload.Length, attachment.FileSize);

        // The server has already dropped the message (leaveOnServer: false), so this can only come
        // from the bytes stored at download time.
        Assert.Equal(0, await ServerMessageCountAsync(account, ct));
        var bytes = await pop.DownloadAttachmentAsync(
            account.Id, "Inbox", downloaded[0].MessageId, attachment.PartSpecifier!, ct);
        Assert.Equal(payload, bytes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Collects until the expected number of messages has arrived. SMTP delivery is asynchronous, so
    /// a single sync can legitimately run before GreenMail has filed the message.
    /// </summary>
    private static async Task<List<MailMessageSummary>> WaitForDownloadAsync(
        Pop3MailService pop, Guid accountId, int expected, CancellationToken ct)
    {
        var collected = new List<MailMessageSummary>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            collected.AddRange(await pop.GetMessagesSinceAsync(accountId, "Inbox", "0", 50, ct));
            if (collected.Count >= expected) return collected;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException(
            $"Expected {expected} message(s) to arrive over POP3; collected {collected.Count}.");
    }

    private static async Task WaitForServerCountAsync(AccountModel account, int expected, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await ServerMessageCountAsync(account, ct) >= expected) return;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"GreenMail never held {expected} message(s) for {account.Username}.");
    }

    /// <summary>Message count straight from the server, bypassing QuickMail entirely.</summary>
    private static async Task<int> ServerMessageCountAsync(AccountModel account, CancellationToken ct)
    {
        using var client = await OpenAsync(account, ct);
        var count = client.Count;
        await client.DisconnectAsync(quit: false, ct);
        return count;
    }

    private static async Task<IList<string>> ServerUidlsAsync(AccountModel account, CancellationToken ct)
    {
        using var client = await OpenAsync(account, ct);
        var uidls = client.Count == 0 ? new List<string>() : await client.GetMessageUidsAsync(ct);
        await client.DisconnectAsync(quit: false, ct);
        return uidls;
    }

    private static async Task<Pop3Client> OpenAsync(AccountModel account, CancellationToken ct)
    {
        var client = new Pop3Client();
        await client.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.Pop3Port, SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(account.Username, "pw", ct);
        return client;
    }

    private static async Task SeedAsync(
        string recipient, string subject, string body, CancellationToken ct,
        Action<BodyBuilder>? customize = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;

        var builder = new BodyBuilder { TextBody = body };
        customize?.Invoke(builder);
        message.Body = builder.ToMessageBody();

        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
