using System.Text;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Fixtures;

/// <summary>
/// The deterministic "default" fixture set (#180 spec §6): one account, seeded
/// Inbox/Sent mail covering every visually risky case, plus contacts, a custom
/// flag, a saved view, a rule, a template, and calendar events. Fixed GUIDs and
/// a fixed clock (UiProbeFixture.T0) — no Guid.NewGuid(), no DateTime.Now — so
/// two runs produce byte-identical content and probe shots are diffable.
/// </summary>
public static class DefaultFixtureSet
{
    private static readonly Guid AccountId = UiProbeFixture.AccountId;
    private static DateTimeOffset T0 => UiProbeFixture.T0;

    public static async Task<string> WriteAsync(string profileDir)
    {
        var profile = new ProfileContext(profileDir);

        // ── Account ──────────────────────────────────────────────────────────
        // Hosts use example.com (outside ProviderCatalog) so AccountStartupRepair
        // never rewrites accounts.json at boot; automation mode never contacts them.
        var accountService = new AccountService(profile);
        accountService.SaveAccounts(
        [
            new AccountModel
            {
                Id = AccountId,
                AccountName = "Test",
                DisplayName = "Test User",
                Username = "test@example.com",
                AuthType = AuthType.Password,
                ImapHost = "imap.example.com", ImapPort = 993, ImapUseSsl = true,
                SmtpHost = "smtp.example.com", SmtpPort = 587,
                IsDefault = true,
            },
        ]);

        // ── Config: suppress first-run UI so probe shots show the app, not a tutorial ──
        var configService = new ConfigService(profile);
        var cfg = configService.Load();
        cfg.TutorialCompleted = true;
        cfg.CloseToTray = false;   // the probe's exit must be an exit, not a minimize
        configService.Save(cfg);

        // ── Mail: through the real store so the schema is always current ─────
        var localStore = new LocalStoreService(profile);
        localStore.Initialize();

        var inbox = new List<MailMessageSummary>
        {
            Summary("1001", UiProbeFixture.InboxFolder, "Ava Chen", "Quick question",
                hoursAgo: 1, isRead: false,
                preview: "Do you have five minutes today to look at the draft?"),
            Summary(UiProbeFixture.HtmlMessageId, UiProbeFixture.InboxFolder, "Newsletter Weekly",
                UiProbeFixture.HtmlMessageSubject,
                hoursAgo: 3, isRead: true,
                preview: "This week: headings, lists, links, and a blockquote."),
            Summary("1003", UiProbeFixture.InboxFolder, "Ben Ortiz", "Invoice attached — please review",
                hoursAgo: 5, isRead: false, isServerFlagged: true,
                preview: "Flagged for follow-up before Friday."),
            Summary("1004", UiProbeFixture.InboxFolder, "Files Team", "Q4 report",
                hoursAgo: 8, isRead: true,
                preview: "The quarterly report is attached as a PDF."),
            Summary("1005", UiProbeFixture.InboxFolder,
                "Extraordinarily Long Sender Display Name That Should Ellipsize Somewhere Sensible",
                "This subject line is pathologically long on purpose so that truncation, wrapping, and ellipsis behavior in every theme and text scale is exercised by a single deterministic fixture row",
                hoursAgo: 26, isRead: false,
                preview: "Long-subject rendering check."),
            Summary("1006", UiProbeFixture.InboxFolder, "dev-list@example.com", "[dev-list] Build 4821 is green",
                hoursAgo: 30, isRead: true, isMailingList: true,
                preview: "All 2,258 tests passed on the main branch."),
            Summary("1007", UiProbeFixture.InboxFolder, "Dana Organizer", "Project X planning session",
                hoursAgo: 2, isRead: false,
                preview: "You are invited to the Project X planning session."),
        };
        var sent = new List<MailMessageSummary>
        {
            Summary("2001", UiProbeFixture.SentFolder, "Test User", "Re: Quick question",
                hoursAgo: 4, isRead: true, to: "Ava Chen <ava@example.com>",
                preview: "Sure — free after 2pm."),
            Summary("2002", UiProbeFixture.SentFolder, "Test User", "Notes from Tuesday",
                hoursAgo: 40, isRead: true, to: "Ben Ortiz <ben@example.com>",
                preview: "Attaching my notes from the Tuesday sync."),
        };
        await localStore.UpsertSummariesAsync(inbox);
        await localStore.UpsertSummariesAsync(sent);

        await localStore.UpsertDetailAsync(Detail(inbox[0],
            plain: "Do you have five minutes today to look at the draft?\n\nThanks,\nAva"));
        await localStore.UpsertDetailAsync(Detail(inbox[1], html:
            "<h1>Weekly digest</h1>" +
            "<p>This week's items, rendered as real HTML:</p>" +
            "<h2>Highlights</h2>" +
            "<ul><li>First list item</li><li>Second list item with a <a href=\"https://example.com\">link</a></li><li>Third item</li></ul>" +
            "<blockquote>A blockquote to exercise quoted styling in the reading pane.</blockquote>" +
            "<p>Plain closing paragraph.</p>"));
        await localStore.UpsertDetailAsync(Detail(inbox[2],
            plain: "Please review the attached invoice before Friday.\n\n— Ben"));
        await localStore.UpsertDetailAsync(Detail(inbox[3],
            plain: "The quarterly report is attached as a PDF.",
            attachments:
            [
                new AttachmentModel
                {
                    FileName = "Q4-report.pdf",
                    ContentType = "application/pdf",
                    FileSize = 245_760,
                    PartSpecifier = "2",
                },
            ]));
        await localStore.UpsertDetailAsync(Detail(inbox[4],
            plain: "Body of the long-subject message."));
        await localStore.UpsertDetailAsync(Detail(inbox[5],
            plain: "All 2,258 tests passed on the main branch.\n\n-- \ndev-list"));
        await localStore.UpsertDetailAsync(Detail(inbox[6],
            plain: "You are invited to the Project X planning session.",
            calendarIcs: InviteIcs));
        await localStore.UpsertDetailAsync(Detail(sent[0], plain: "Sure — free after 2pm."));
        await localStore.UpsertDetailAsync(Detail(sent[1], plain: "Attaching my notes from the Tuesday sync."));

        // ── Calendar events (locally-authored; IsGraph stays false) ──────────
        await localStore.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "fixture-event-0001@example.com",
            AccountId = Guid.Empty,
            Summary = "Team stand-up",
            Location = "Room 4",
            StartTimeTicks = T0.AddDays(1).AddHours(1).UtcTicks,
            EndTimeTicks = T0.AddDays(1).AddHours(1.5).UtcTicks,
        });
        await localStore.UpsertCalendarEventAsync(new CalendarEvent
        {
            Uid = "fixture-event-0002@example.com",
            AccountId = Guid.Empty,
            Summary = "Release day",
            IsAllDay = true,
            StartTimeTicks = T0.AddDays(3).UtcTicks,
            EndTimeTicks = T0.AddDays(4).UtcTicks,
        });

        // ── Supporting state, each through its real service ──────────────────
        var contactService = new ContactService(profile);
        foreach (var (name, email) in new[]
        {
            ("Ava Chen", "ava@example.com"),
            ("Ben Ortiz", "ben@example.com"),
            ("Dana Organizer", "dana@example.com"),
            ("Newsletter Weekly", "news@example.com"),
        })
        {
            await contactService.UpsertContactAsync(new ContactModel
            {
                DisplayName = name,
                EmailAddress = email,
                LastUsedTicks = T0.UtcTicks,
            });
        }
        var groupId = await contactService.CreateGroupAsync("Project X team");
        await contactService.AddMemberAsync(groupId, 1);   // ids are deterministic: max+1 from empty
        await contactService.AddMemberAsync(groupId, 2);

        var offlineMail = new ProbeOfflineMailService();
        var flagService = new FlagService(profile, configService, localStore, offlineMail);
        await flagService.SaveFlagDefinitionsAsync(
        [
            FlagDefinition.CreateBuiltIn(),
            new FlagDefinition
            {
                Id = new Guid("22222222-2222-2222-2222-222222222222"),
                Name = "Follow up",
                ColorHex = "#C05621",
                SortOrder = 1,
            },
        ]);

        new ViewService(profile).Save(
        [
            new SavedView
            {
                Id = new Guid("33333333-3333-3333-3333-333333333333"),
                Name = "All mail, unread",
                VirtualFolderKey = "AllMail",
                ViewMode = "messages",
                Filter = "unread",
                Sort = "dateDesc",
            },
        ]);

        new RuleService(offlineMail, localStore, profile.ProfileDir, accountService).SaveRules(
        [
            new MailRule
            {
                Id = new Guid("44444444-4444-4444-4444-444444444444"),
                Name = "Mark invoices read",
                AccountId = AccountId,
                UseSubjectCondition = true,
                SubjectContains = "invoice",
                Action = RuleAction.MarkAsRead,
            },
        ]);

        await new TemplateService(profile).AddAsync(new MessageTemplate
        {
            Title = "Status update",
            Subject = "Weekly status",
            Body = "Progress this week:\n- \n\nBlockers:\n- none",
        });

        var report = new StringBuilder();
        report.AppendLine($"Fixture profile written to {profileDir}");
        report.AppendLine($"  account: Test User <test@example.com> ({AccountId})");
        report.AppendLine($"  mail: {inbox.Count} inbox + {sent.Count} sent (clock T0 = {T0:u})");
        report.AppendLine("  plus: 2 calendar events, 4 contacts + 1 group, 1 custom flag, 1 saved view, 1 rule, 1 template");
        return report.ToString();
    }

    private static MailMessageSummary Summary(string id, string folder, string from, string subject,
        double hoursAgo, bool isRead, string preview, string? to = null,
        bool isServerFlagged = false, bool isMailingList = false) => new()
    {
        MessageId = id,
        AccountId = AccountId,
        FolderName = folder,
        InternetMessageId = $"<msg-{id}@example.com>",
        From = from,
        To = to ?? "Test User <test@example.com>",
        Subject = subject,
        Date = T0.AddHours(-hoursAgo),
        IsRead = isRead,
        Preview = preview,
        IsServerFlagged = isServerFlagged,
        IsMailingList = isMailingList,
    };

    private static MailMessageDetail Detail(MailMessageSummary summary, string? plain = null,
        string? html = null, List<AttachmentModel>? attachments = null, string calendarIcs = "")
    {
        var detail = new MailMessageDetail
        {
            MessageId = summary.MessageId,
            AccountId = summary.AccountId,
            FolderName = summary.FolderName,
            InternetMessageId = summary.InternetMessageId,
            From = summary.From,
            To = summary.To,
            Subject = summary.Subject,
            Date = summary.Date,
            IsRead = summary.IsRead,
            PlainTextBody = plain ?? string.Empty,
            HtmlBody = html ?? string.Empty,
            CalendarIcs = calendarIcs,
        };
        if (attachments != null)
            detail.Attachments = attachments;
        return detail;
    }

    private const string InviteIcs =
        "BEGIN:VCALENDAR\r\n" +
        "VERSION:2.0\r\n" +
        "PRODID:-//QuickMail Fixtures//EN\r\n" +
        "METHOD:REQUEST\r\n" +
        "BEGIN:VEVENT\r\n" +
        "UID:fixture-invite-0001@example.com\r\n" +
        "DTSTAMP:20260115T090000Z\r\n" +
        "DTSTART:20260122T170000Z\r\n" +
        "DTEND:20260122T180000Z\r\n" +
        "SUMMARY:Project X planning session\r\n" +
        "LOCATION:Room 4\r\n" +
        "ORGANIZER;CN=Dana Organizer:mailto:dana@example.com\r\n" +
        "ATTENDEE;CN=Test User;PARTSTAT=NEEDS-ACTION:mailto:test@example.com\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";
}
