using System.Diagnostics;
using System.IO;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.IntegrationTests;

/// <summary>
/// End-to-end regression tests for issue #297: a fetched calendar invite must persist its RAW
/// ICS (<c>calendar_ics</c>) into the local store, not just the parsed in-memory model —
/// otherwise the Accept/Decline card silently vanishes as soon as the row is served from cache
/// (which prefetch causes almost immediately). The whole path runs against a real IMAP server:
/// seeded MIME → MailKit fetch → <c>ImapMailService</c> → SQLite round-trip.
/// </summary>
[Collection(GreenMailCollection.Name)]
public sealed class InvitePersistenceTests : IDisposable
{
    private readonly GreenMailFixture _greenMail;
    private readonly string _profileDir;

    public InvitePersistenceTests(GreenMailFixture greenMail)
    {
        _greenMail = greenMail;
        _profileDir = Path.Combine(Path.GetTempPath(), "quickmail-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_profileDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FetchedInvite_PersistsRawIcs_AndSurvivesCacheServedReopen()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;

        var account = _greenMail.CreateAccount("invitee");
        const string inviteUid = "e2e-invite-297@quickmail.test";
        await SeedInviteAsync(account.Username, inviteUid, ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "test-password", ct);
        try
        {
            var summary = await WaitForInboxMessageAsync(imap, account.Id, ct);

            // The #297 site: prefetch is what caches the row in production, and prefetch was
            // the fetch that persisted an empty calendar_ics. Assert on the prefetch-shaped
            // detail, then push it through the same store round-trip the app performs.
            var fetched = await imap.PrefetchMessageDetailAsync(account.Id, "INBOX", summary.MessageId, ct);

            Assert.False(string.IsNullOrEmpty(fetched.CalendarIcs),
                "Fetched invite must carry the raw ICS text (calendar_ics), not only the parsed model.");
            Assert.NotNull(fetched.CalendarInvite);
            Assert.Equal(inviteUid, fetched.CalendarInvite!.Uid);

            var store = new LocalStoreService(new ProfileContext(_profileDir));
            store.Initialize();
            await store.UpsertDetailAsync(fetched);

            var cached = await store.LoadDetailAsync(account.Id, "INBOX", summary.MessageId);

            Assert.NotNull(cached);
            Assert.Equal(fetched.CalendarIcs, cached!.CalendarIcs);
            Assert.NotNull(cached.CalendarInvite);
            Assert.Equal(inviteUid, cached.CalendarInvite!.Uid);
            Assert.Equal("REQUEST", cached.CalendarInvite.Method);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    // The multipart/alternative invite shape (plain text + text/calendar) is the other common
    // form real invites take, and SHOULD be covered here — but GreenMail's BODY[n.MIME]
    // response omits the blank line that must terminate MIME headers (RFC 3501), so MailKit
    // decodes every nested body part to an empty entity. The app-side logic difference is only
    // FindCalendarPart's multipart traversal, which is pure tree-walking; the decode-and-persist
    // path is identical and covered by the single-part test above. Re-enable when GreenMail
    // fixes section fetching, or when a recorded-fixture/alternative-server tier exists.
    [Fact(Skip = "GreenMail defect: multipart section fetch returns empty entities — see docs/TESTING-INTEGRATION.md")]
    public async Task MultipartInvite_PersistsRawIcs_BlockedByGreenMailSectionFetch()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;

        var account = _greenMail.CreateAccount("mp-invitee");
        const string inviteUid = "e2e-invite-297-multipart@quickmail.test";
        await SeedInviteAsync(account.Username, inviteUid, ct, multipart: true);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "test-password", ct);
        try
        {
            var summary = await WaitForInboxMessageAsync(imap, account.Id, ct);
            var fetched = await imap.PrefetchMessageDetailAsync(account.Id, "INBOX", summary.MessageId, ct);
            Assert.False(string.IsNullOrEmpty(fetched.CalendarIcs));
            Assert.NotNull(fetched.CalendarInvite);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    [Fact]
    public async Task PlainMessage_FetchesWithEmptyCalendarFields()
    {
        _greenMail.RequireServers();
        var ct = TestContext.Current.CancellationToken;

        var account = _greenMail.CreateAccount("plain");
        await SeedPlainMessageAsync(account.Username, ct);

        using var imap = new ImapMailService(new NoOpOAuthService());
        await imap.ConnectAsync(account, "test-password", ct);
        try
        {
            var summary = await WaitForInboxMessageAsync(imap, account.Id, ct);
            var fetched = await imap.GetMessageDetailAsync(account.Id, "INBOX", summary.MessageId, ct);

            Assert.Equal(string.Empty, fetched.CalendarIcs);
            Assert.Null(fetched.CalendarInvite);
            Assert.Contains("just a plain message", fetched.PlainTextBody);
        }
        finally
        {
            await imap.DisconnectAsync(account.Id, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<MailMessageSummary> WaitForInboxMessageAsync(
        ImapMailService imap, Guid accountId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var summaries = await imap.GetMessageSummariesAsync(accountId, "INBOX", 10, ct);
            if (summaries.Count > 0)
                return summaries[0];
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Seeded message never appeared in the GreenMail INBOX.");
    }

    private async Task SeedInviteAsync(string recipient, string inviteUid, CancellationToken ct, bool multipart = false)
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//QuickMail Integration Tests//EN",
            "METHOD:REQUEST",
            "BEGIN:VEVENT",
            $"UID:{inviteUid}",
            "SEQUENCE:0",
            "ORGANIZER;CN=Organizer:mailto:organizer@example.test",
            $"ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:{recipient}",
            "SUMMARY:Integration Test Meeting",
            "LOCATION:Conference Room 297",
            "DTSTART:20260901T140000Z",
            "DTEND:20260901T150000Z",
            "END:VEVENT",
            "END:VCALENDAR");

        var calendarPart = new TextPart("calendar") { Text = ics };
        calendarPart.ContentType.Charset = "utf-8";
        calendarPart.ContentType.Parameters.Add("method", "REQUEST");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Organizer", "organizer@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "Invitation: Integration Test Meeting";
        // Default is a single-part text/calendar body (a shape real providers send).
        // Deliberately NOT multipart/alternative: GreenMail's BODY[n.MIME] response omits the
        // blank line that must terminate the MIME headers, so MailKit decodes any nested part
        // to an empty entity — see "Known GreenMail limitations" in docs/TESTING-INTEGRATION.md.
        message.Body = multipart
            ? new MultipartAlternative
              {
                  new TextPart("plain") { Text = "You are invited to the Integration Test Meeting." },
                  calendarPart,
              }
            : calendarPart;

        await DeliverAsync(message, recipient, ct);
    }

    private async Task SeedPlainMessageAsync(string recipient, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "Plain message";
        message.Body = new TextPart("plain") { Text = "This is just a plain message." };

        await DeliverAsync(message, recipient, ct);
    }

    private async Task DeliverAsync(MimeMessage message, string recipient, CancellationToken ct)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(GreenMailFixture.Host, GreenMailFixture.SmtpPort, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
