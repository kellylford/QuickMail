using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The pure half of saving a message (#728): the file name, the details block, and the text and
/// web-page documents. The web page carries the sender's HTML, so its safety properties — the strict
/// CSP, the sanitizer, the details the body cannot cover — are asserted here as well as its content.
/// </summary>
public class MessageExportTests
{
    private static readonly MessageSaveContext Context =
        new("Kelly Ford (kelly@example.com)", "Inbox/Receipts", null, new DateTimeOffset(2026, 9, 18, 9, 5, 0, TimeSpan.Zero));

    private static MailMessageDetail Detail(
        string subject = "Your order has shipped",
        string from = "Jane Smith <jane@example.com>",
        string plain = "Hello Kelly,\nYour order is on its way.",
        string html = "",
        DateTimeOffset? date = null) => new()
    {
        MessageId  = "42",
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX/Receipts",
        Subject    = subject,
        From       = from,
        To         = "Kelly Ford <kelly@example.com>",
        Cc         = "",
        ReplyTo    = "",
        Date       = date ?? new DateTimeOffset(2026, 9, 17, 14, 32, 0, TimeSpan.Zero),
        PlainTextBody = plain,
        HtmlBody   = html,
        IsRead     = true,
    };

    // ── File names ────────────────────────────────────────────────────────────

    [Fact]
    public void FileName_LeadsWithTheSubject_ThenSender_ThenDate()
    {
        var d = Detail();
        var name = MessageExport.BuildFileName(d, MessageSaveFormat.Eml);
        var local = d.Date.ToLocalTime().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"Your order has shipped - Jane Smith - {local}.eml", name);
    }

    [Theory]
    [InlineData(MessageSaveFormat.Eml, ".eml")]
    [InlineData(MessageSaveFormat.Text, ".txt")]
    [InlineData(MessageSaveFormat.Html, ".html")]
    [InlineData(MessageSaveFormat.Pdf, ".pdf")]
    public void FileName_CarriesTheFormatsExtension(MessageSaveFormat format, string ext) =>
        Assert.EndsWith(ext, MessageExport.BuildFileName(Detail(), format));

    [Fact]
    public void FileName_ASlashInTheSubjectIsNotADirectory()
    {
        // AttachmentSafety.SanitizeFileName would keep only " Q4 plan" here: for an attachment a
        // separator IS a path boundary. For a subject it is punctuation.
        var name = MessageExport.BuildFileName(Detail(subject: "Q3 / Q4 plan: draft?"), MessageSaveFormat.Text);
        Assert.StartsWith("Q3 Q4 plan draft - ", name);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    [Fact]
    public void FileName_PathTraversalInTheSubjectStaysInTheFolder()
    {
        var name = MessageExport.BuildFileName(Detail(subject: @"..\..\Startup\evil"), MessageSaveFormat.Html);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.DoesNotContain("..", name);
        var folder = Path.Combine(Path.GetTempPath(), "qm-save");
        Assert.Equal(folder, Path.GetDirectoryName(Path.Combine(folder, name)));
    }

    [Fact]
    public void FileName_ARightToLeftOverrideCannotDisguiseTheExtension()
    {
        // "invoice\u202Etxt.exe" would display as "invoiceexe.txt".
        var name = MessageExport.BuildFileName(Detail(subject: "invoice\u202Eexe.txt"), MessageSaveFormat.Eml);
        Assert.DoesNotContain('\u202E', name);
        Assert.EndsWith(".eml", name);
    }

    [Fact]
    public void FileName_EmptySubjectAndSender_StillNamesTheFile()
    {
        var name = MessageExport.BuildFileName(Detail(subject: "   ", from: ""), MessageSaveFormat.Eml);
        Assert.StartsWith("No subject - ", name);
    }

    [Fact]
    public void FileName_ALongSubjectIsClipped()
    {
        var name = MessageExport.BuildFileName(Detail(subject: new string('x', 500)), MessageSaveFormat.Eml);
        Assert.True(name.Length < 160, name);
    }

    [Fact]
    public void FileName_AReservedDeviceNameIsNotUsedBare()
    {
        var d = Detail(subject: "CON", from: "");
        d.Date = default;
        Assert.Equal("_CON.eml", MessageExport.BuildFileName(d, MessageSaveFormat.Eml));
    }

    [Fact]
    public void SenderName_PrefersTheDisplayName_ThenTheAddress()
    {
        Assert.Equal("Jane Smith", MessageExport.SenderName("\"Jane Smith\" <jane@example.com>"));
        Assert.Equal("jane@example.com", MessageExport.SenderName("jane@example.com"));
        Assert.Equal(string.Empty, MessageExport.SenderName(null));
    }

    [Fact]
    public void UniquePath_NeverOverwrites()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("C:\\f", "a.eml"),
            Path.Combine("C:\\f", "a (2).eml"),
        };
        Assert.Equal(Path.Combine("C:\\f", "a (3).eml"), MessageExport.UniquePath("C:\\f", "a.eml", existing.Contains));
        Assert.Equal(Path.Combine("C:\\f", "b.eml"),     MessageExport.UniquePath("C:\\f", "b.eml", existing.Contains));
    }

    // ── Details ───────────────────────────────────────────────────────────────

    [Fact]
    public void Details_AreHumanReadable_AndIncludeWhereTheMessageLives()
    {
        var d = Detail();
        d.Attachments = [new AttachmentModel { FileName = "receipt.pdf", FileSize = 2048 }];
        d.IsReplied = true;
        var rows = MessageExport.BuildDetails(d, Context with { FlagName = "Follow up" });
        var labels = rows.Select(r => r.Label).ToList();

        Assert.Equal(["Subject", "From", "To", "Date", "Account", "Folder", "Status", "Attachment", "Saved"], labels);
        Assert.Equal("Inbox/Receipts", rows.Single(r => r.Label == "Folder").Value);
        Assert.Equal("Kelly Ford (kelly@example.com)", rows.Single(r => r.Label == "Account").Value);
        Assert.Equal("receipt.pdf (2 KB)", rows.Single(r => r.Label == "Attachment").Value);
        Assert.EndsWith(", from QuickMail", rows.Single(r => r.Label == "Saved").Value);
    }

    [Fact]
    public void Details_OmitEmptyFields_AndAReplyToThatMatchesFrom()
    {
        var d = Detail();
        d.ReplyTo = d.From;
        var labels = MessageExport.BuildDetails(d, Context).Select(r => r.Label).ToList();
        Assert.DoesNotContain("Cc", labels);
        Assert.DoesNotContain("Reply to", labels);
        Assert.DoesNotContain("Invitation", labels);
    }

    [Fact]
    public void Status_ReadsAsAPersonWouldSayIt()
    {
        var d = Detail();
        d.IsRead = false; d.FlagId = "f"; d.IsForwarded = true;
        Assert.Equal("Unread, flagged (Follow up), forwarded", MessageExport.StatusText(d, "Follow up"));
    }

    [Fact]
    public void Details_IncludeACalendarInvitation()
    {
        var d = Detail();
        d.CalendarInvite = new IcsModel
        {
            Summary = "Planning", Location = "Room 4",
            StartTime = new DateTime(2026, 9, 20, 10, 0, 0), EndTime = new DateTime(2026, 9, 20, 11, 0, 0),
        };
        var rows = MessageExport.BuildDetails(d, Context);
        Assert.Equal("Planning", rows.Single(r => r.Label == "Invitation").Value);
        Assert.Equal("Room 4", rows.Single(r => r.Label == "Where").Value);
        Assert.Contains(" to ", rows.Single(r => r.Label == "When").Value);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Text_IsDetailsThenTheSendersPlainText()
    {
        var text = MessageExport.BuildTextDocument(Detail(), Context);
        Assert.StartsWith("Subject: Your order has shipped\r\n", text);
        Assert.Contains("Folder: Inbox/Receipts\r\n", text);
        Assert.Contains("Hello Kelly,\r\nYour order is on its way.", text);
        Assert.DoesNotContain("extracted", text);
    }

    [Fact]
    public void Text_FromHtmlOnly_SaysTheTextWasExtracted()
    {
        var text = MessageExport.BuildTextDocument(Detail(plain: "", html: "<p>Only <b>HTML</b></p>"), Context);
        Assert.Contains("extracted", text);
        Assert.Contains("Only HTML", text);
        Assert.DoesNotContain("<b>", text);
    }

    [Fact]
    public void Text_IsNotTruncated()
    {
        var body = new string('a', 300_000);
        Assert.Contains(body, MessageExport.BuildTextDocument(Detail(plain: body), Context));
    }

    [Fact]
    public void Text_AHeaderValueWithALineBreakCannotForgeAField()
    {
        var text = MessageExport.BuildTextDocument(Detail(subject: "Hi\r\nFrom: ceo@example.com"), Context);
        Assert.StartsWith("Subject: Hi From: ceo@example.com\r\n", text);
    }

    // ── Web page ──────────────────────────────────────────────────────────────

    [Fact]
    public void Html_CarriesTheStrictCsp_InTheRealHead()
    {
        var html = MessageExport.BuildHtmlDocument(Detail(html: "<p>Body</p>"), Context);
        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("Content-Security-Policy", head);
        Assert.Contains("script-src 'none'", head);
        Assert.Contains("img-src 'none'", head);
        Assert.Contains("<p>Body</p>", html);
    }

    [Fact]
    public void Html_StripsScriptsHandlersImagesAndFormsFromTheBody()
    {
        var hostile =
            "<script>alert(1)</script><img src=\"https://tracker.example/p.gif\" alt=\"Logo\">" +
            "<a href=\"https://example.com\" onclick=\"evil()\">link</a><form action=\"https://x\"><input></form>" +
            "<meta http-equiv=\"refresh\" content=\"0;url=https://x\"><base href=\"https://x/\">" +
            "<head><meta http-equiv=\"Content-Security-Policy\" content=\"script-src *\"></head>" +
            "<style>header{display:none}</style>";
        var html = MessageExport.BuildHtmlDocument(Detail(html: hostile), Context);
        var body = html[html.IndexOf("<main>", StringComparison.Ordinal)..];

        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", body);
        Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<meta", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<base", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Logo", body);                       // the image's alt text stands in for it
        Assert.Contains("href=\"https://example.com\"", body); // links still work
        // Exactly one CSP, and it is ours.
        Assert.Equal(1, CountOf(html, "Content-Security-Policy"));
    }

    [Fact]
    public void Html_TheDetailsAreEncoded_SoAHeaderCannotInjectMarkup()
    {
        var html = MessageExport.BuildHtmlDocument(
            Detail(subject: "<script>alert(1)</script>", from: "\"<img src=x onerror=alert(1)>\" <a@example.com>"), Context);
        var header = html[..html.IndexOf("<main>", StringComparison.Ordinal)];
        Assert.DoesNotContain("<script>", header);
        Assert.DoesNotContain("<img", header);
        Assert.Contains("&lt;script&gt;", header);
    }

    [Fact]
    public void Html_TheBodyCannotPaintOverTheDetails()
    {
        var html = MessageExport.BuildHtmlDocument(Detail(html: "<p>x</p>"), Context);
        Assert.Contains("main{contain:paint;", html);
        Assert.True(html.IndexOf("<header>", StringComparison.Ordinal) < html.IndexOf("<main>", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_DetailsAreATableWithRowHeaders()
    {
        var html = MessageExport.BuildHtmlDocument(Detail(), Context);
        Assert.Contains("<h1>Your order has shipped</h1>", html);
        Assert.Contains("<th scope=\"row\">From</th>", html);
        Assert.Contains("<th scope=\"row\">Folder</th><td>Inbox/Receipts</td>", html);
        Assert.DoesNotContain("<th scope=\"row\">Subject</th>", html);   // the heading is the subject
    }

    [Fact]
    public void Html_PlainTextOnly_IsEncodedAndLinked()
    {
        var html = MessageExport.BuildHtmlDocument(Detail(plain: "See https://example.com <b>now</b>", html: ""), Context);
        Assert.Contains("<div class=\"qm-plain\">", html);
        Assert.Contains("&lt;b&gt;now&lt;/b&gt;", html);
        Assert.Contains("<a href=\"https://example.com\"", html);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0) { count++; i += value.Length; }
        return count;
    }

    // ── Formats ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("eml", MessageSaveFormat.Eml)]
    [InlineData("TXT", MessageSaveFormat.Text)]
    [InlineData("html", MessageSaveFormat.Html)]
    [InlineData("pdf", MessageSaveFormat.Pdf)]
    [InlineData("nonsense", MessageSaveFormat.Eml)]
    [InlineData(null, MessageSaveFormat.Eml)]
    public void ConfigValues_RoundTrip(string? value, MessageSaveFormat expected)
    {
        var parsed = MessageSaveFormats.FromConfigValue(value);
        Assert.Equal(expected, parsed);
        Assert.Equal(parsed, MessageSaveFormats.FromConfigValue(MessageSaveFormats.ToConfigValue(parsed)));
    }

    [Fact]
    public void FolderPaths_WriteInboxAsAPersonWould_AndNeverShowAGraphId()
    {
        var account = Guid.NewGuid();
        var graph = new List<MailFolderModel>
        {
            new() { AccountId = account, FullName = "AAMk-inbox", DisplayName = "Inbox" },
            new() { AccountId = account, FullName = "AAMk-receipts", DisplayName = "Receipts", ParentId = "AAMk-inbox" },
        };
        Assert.Equal("Inbox/Receipts", FolderPaths.Describe("AAMk-receipts", graph));
        Assert.Equal("Inbox/Receipts", FolderPaths.Describe("INBOX/Receipts", null));
        Assert.Equal("Inbox", FolderPaths.Describe("INBOX", null));
        Assert.Equal("Receipts", FolderPaths.Describe("AAMk-unknown", graph, "Receipts"));
    }
}
