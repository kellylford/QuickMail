// Issue #929: copied and cut messages also go on the Windows clipboard as .eml files, so they paste
// into Explorer (saving them) and into a message being written (attaching them, as Outlook does).
//
// The clipboard here is a fake: the real one is machine-wide, and a test that touched it failed
// whenever anything else on the machine held it (see IClipboardService).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public sealed class CopiedMessagesAsFilesTests : IDisposable
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qm-clip-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for the Windows clipboard. <see cref="CopyTextElsewhere"/> plays the user
    /// copying something in another program.</summary>
    private sealed class FakeClipboard : IClipboardService
    {
        public string? Marker { get; private set; }
        public IReadOnlyList<string> Files { get; private set; } = Array.Empty<string>();
        public int FileSets { get; private set; }

        public bool SetText(string text) { Marker = null; Files = []; return true; }
        public string GetText() => string.Empty;
        public bool SetFiles(IReadOnlyList<string> paths, string marker) { Marker = marker; Files = paths; FileSets++; return true; }
        public bool SetMarker(string marker) { Marker = marker; Files = []; return true; }
        public string? GetMarker() => Marker;

        public void CopyTextElsewhere() => SetText("something else");
    }

    private static MailMessageSummary Msg(string id, int daysAgo = 0) => new()
    {
        MessageId  = id,
        AccountId  = Work,
        FolderName = "INBOX",
        From       = "Jane Smith <jane@example.com>",
        Subject    = $"Subject {id}",
        Date       = new DateTimeOffset(2026, 9, 17, 14, 32, 0, TimeSpan.Zero).AddDays(-daysAgo),
    };

    private sealed record Fixture(MainViewModel Vm, FolderedMailService Mail, FakeClipboard Clipboard,
                                  ClipboardStaging Staging, StatusAnnouncementRecorder Announced,
                                  MailFolderModel Archive)
    {
        public MailMessageSummary Row(string id) => Vm.Messages.First(m => m.MessageId == id);
    }

    private async Task<Fixture> CreateAsync(int messageCount = 3)
    {
        var inbox   = new MailFolderModel { AccountId = Work, FullName = "INBOX", DisplayName = "INBOX", Kind = SpecialFolderKind.Inbox };
        var archive = new MailFolderModel { AccountId = Work, FullName = "Archive", DisplayName = "Archive", Kind = SpecialFolderKind.Archive };
        var mail = new FolderedMailService(
            new Dictionary<Guid, List<MailFolderModel>> { [Work] = [inbox, archive] },
            new Dictionary<(Guid, string), List<MailMessageSummary>>
            {
                [(Work, "INBOX")] = [.. Enumerable.Range(0, messageCount).Select(i => Msg(((char)('a' + i % 26)).ToString() + (i / 26), i))],
            });
        var clipboard = new FakeClipboard();
        var staging   = new ClipboardStaging(_root, processId: 4242);

        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), clipboard: clipboard, clipboardStaging: staging);

        vm.Accounts.Add(new AccountModel { Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft });
        await vm.ConnectAllAccountsAsync();
        await vm.SelectFolderCommand.ExecuteAsync(inbox);
        return new Fixture(vm, mail, clipboard, staging, StatusAnnouncementRecorder.Watch(vm), archive);
    }

    // ── Copy puts .eml files on the clipboard ────────────────────────────────────

    [Fact]
    public async Task Copy_PutsEachOriginalOnTheClipboardAsAnEmlFile_NamedAsSaveNamesIt()
    {
        var f = await CreateAsync();

        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a0"), f.Row("b0")]);

        Assert.Equal(2, f.Clipboard.Files.Count);
        Assert.All(f.Clipboard.Files, p => Assert.True(File.Exists(p), p));
        Assert.All(f.Clipboard.Files, p => Assert.StartsWith(f.Staging.Folder, p, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Subject a0 - Jane Smith - ", Path.GetFileName(f.Clipboard.Files[0]));
        Assert.EndsWith(".eml", f.Clipboard.Files[0]);

        // The file is the original as the server sent it, and so an email any program can open.
        var parsed = MimeMessage.Load(f.Clipboard.Files[1]);
        Assert.Equal("Subject b0", parsed.Subject);

        Assert.Equal(("2 messages copied.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task Cut_PutsFilesOnTheClipboardToo_AndMovesNothing()
    {
        var f = await CreateAsync();

        await f.Vm.CutMessagesToClipboardAsync([f.Row("a0")]);

        Assert.Single(f.Clipboard.Files);
        Assert.Empty(f.Mail.Transfers);
        Assert.Equal(3, f.Vm.Messages.Count);
    }

    [Fact]
    public async Task ANewCopy_ReplacesTheEarlierFiles()
    {
        var f = await CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a0")]);
        var first = f.Clipboard.Files[0];

        await f.Vm.CopyMessagesToClipboardAsync([f.Row("b0")]);

        Assert.False(File.Exists(first)); // nothing can paste it any more
        Assert.True(File.Exists(f.Clipboard.Files[0]));
    }

    [Fact]
    public async Task AnOriginalThatCannotBeFetched_StillLeavesACopyThatPastesIntoFolders()
    {
        // All or nothing for the files, so the clipboard never holds some of what was copied; the copy
        // itself still pastes into a folder, and the user is told why there are no files.
        var f = await CreateAsync();
        f.Mail.OriginalsUnavailable.Add("b0");

        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a0"), f.Row("b0")]);

        Assert.Empty(f.Clipboard.Files);
        Assert.NotNull(f.Clipboard.Marker);
        Assert.Empty(Directory.EnumerateFiles(f.Staging.Folder, "*", SearchOption.AllDirectories));
        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.Contains("not as files", f.Announced.Last.Text);

        await f.Vm.PasteMessagesAsync(f.Archive);
        Assert.Single(f.Mail.Transfers);
    }

    [Fact]
    public async Task TooManyMessages_AreCopiedForFoldersOnly_WithoutDownloadingThem()
    {
        // Select All and Ctrl+C in a big folder must not download every message in it.
        var f = await CreateAsync(MainViewModel.MaxMessagesCopiedAsFiles + 1);

        await f.Vm.CopyMessagesToClipboardAsync([.. f.Vm.Messages]);

        Assert.Equal(0, f.Mail.OriginalsFetched);
        Assert.Empty(f.Clipboard.Files);
        Assert.NotNull(f.Clipboard.Marker);
        Assert.Contains("paste them into a folder", f.Announced.Last.Text);
    }

    // ── The clipboard decides whether there is anything to paste ─────────────────

    [Fact]
    public async Task CopyingSomethingElseAfterwards_LeavesNothingToPaste()
    {
        // The user copied messages, then copied text in another program. Ctrl+V in a folder now must
        // not bring back the messages they moved on from, as in Explorer.
        var f = await CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a0")]);
        f.Clipboard.CopyTextElsewhere();

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.Empty(f.Mail.Transfers);
        Assert.StartsWith("Nothing to paste", f.Announced.Last.Text);
    }

    [Fact]
    public async Task WhileTheClipboardStillHoldsTheCopy_PasteWorks()
    {
        var f = await CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a0")]);

        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.Single(f.Mail.Transfers);
    }

    // ── The staging folder ───────────────────────────────────────────────────────

    [Fact]
    public void DeleteAbandoned_RemovesOnlyFoldersOfQuickMailsThatAreNotRunning()
    {
        var mine    = new ClipboardStaging(_root, processId: 100);
        var running = new ClipboardStaging(_root, processId: 200);
        var gone    = new ClipboardStaging(_root, processId: 300);
        mine.NewSet(); running.NewSet(); gone.NewSet();
        var notOurs = Directory.CreateDirectory(Path.Combine(_root, "not-a-process"));

        mine.DeleteAbandoned(pid => pid == 200);

        Assert.True(Directory.Exists(mine.Folder));      // never its own, whatever isRunning says
        Assert.True(Directory.Exists(running.Folder));   // another profile's QuickMail, still open
        Assert.False(Directory.Exists(gone.Folder));     // left by a QuickMail that did not close cleanly
        Assert.True(notOurs.Exists);                     // not something it made
    }

    [Fact]
    public void DeleteMine_RemovesThisProcessFilesOnly()
    {
        var mine  = new ClipboardStaging(_root, processId: 100);
        var other = new ClipboardStaging(_root, processId: 200);
        File.WriteAllText(Path.Combine(mine.NewSet(), "x.eml"), "x");
        File.WriteAllText(Path.Combine(other.NewSet(), "y.eml"), "y");

        mine.DeleteMine();

        Assert.False(Directory.Exists(mine.Folder));
        Assert.True(Directory.Exists(other.Folder));
    }

    // ── A pasted .eml is attached as an email ────────────────────────────────────

    [Fact]
    public void AnAttachedEml_IsEmbeddedAsAMessage_NotBase64()
    {
        // RFC 2046 allows message/rfc822 only 7bit, 8bit or binary; a base64 one is shown by many mail
        // programs as an opaque file instead of an email the recipient can open.
        var eml = System.Text.Encoding.ASCII.GetBytes("From: a@example.com\r\nSubject: Inner\r\n\r\nHello\r\n");
        var att = new AttachmentModel { FileName = "Inner.eml", ContentType = "message/rfc822", Content = eml };

        var entity = MimeMessageBuilder.AttachmentEntityFor(att);

        var part = Assert.IsType<MessagePart>(entity);
        Assert.Equal("Inner", part.Message?.Subject);
        Assert.Equal("Inner.eml", part.ContentDisposition?.FileName);
        Assert.True(part.ContentDisposition?.IsAttachment);
    }

    [Fact]
    public void AnEmlThatIsNotAnEmail_IsSentAsAPlainFile()
    {
        var att = new AttachmentModel { FileName = "junk.eml", ContentType = "message/rfc822", Content = [0, 1, 2, 3] };

        var entity = MimeMessageBuilder.AttachmentEntityFor(att);

        var part = Assert.IsType<MimePart>(entity);
        Assert.Equal("application/octet-stream", part.ContentType.MimeType);
    }

    [Fact]
    public void OtherAttachments_AreUnchanged()
    {
        var att = new AttachmentModel { FileName = "a.pdf", ContentType = "application/pdf", Content = [1, 2, 3] };

        var part = Assert.IsType<MimePart>(MimeMessageBuilder.AttachmentEntityFor(att));

        Assert.Equal("application/pdf", part.ContentType.MimeType);
        Assert.Equal(ContentEncoding.Base64, part.ContentTransferEncoding);
    }

    // ── Copy attachments ─────────────────────────────────────────────────────────

    private static MailMessageDetail DetailWith(params AttachmentModel[] attachments) => new()
    {
        AccountId = Work, FolderName = "INBOX", MessageId = "a0", Subject = "Subject a0",
        Attachments = [.. attachments],
    };

    [Fact]
    public async Task CopyAttachment_DownloadsItAndPutsItOnTheClipboardAsAFile()
    {
        var f = await CreateAsync();
        var att = new AttachmentModel { FileName = "report.pdf", ContentType = "application/pdf", PartSpecifier = "2" };

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(att), [att]);

        var path = Assert.Single(f.Clipboard.Files);
        Assert.Equal("report.pdf", Path.GetFileName(path));
        Assert.Equal("content of 2", File.ReadAllText(path));
        Assert.Equal(["2"], f.Mail.Downloads);
        Assert.Equal(("report.pdf copied.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task AnAttachmentAlreadyDownloaded_IsNotFetchedAgain()
    {
        var f = await CreateAsync();
        var att = new AttachmentModel { FileName = "a.txt", PartSpecifier = "2", Content = [65] };

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(att), [att]);

        Assert.Empty(f.Mail.Downloads);
        Assert.Single(f.Clipboard.Files);
    }

    [Fact]
    public async Task CopyAll_KeepsTwoAttachmentsWithOneName_Apart()
    {
        // "image001.png" in every signature: overwriting would put one file on the clipboard twice.
        var f = await CreateAsync();
        AttachmentModel[] atts =
        [
            new() { FileName = "image001.png", PartSpecifier = "2" },
            new() { FileName = "image001.png", PartSpecifier = "3" },
        ];

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(atts), atts);

        Assert.Equal(["image001.png", "image001 (2).png"], f.Clipboard.Files.Select(Path.GetFileName));
        Assert.Equal(("2 attachments copied.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task ACraftedAttachmentName_CannotWriteOutsideTheStagingFolder()
    {
        var f = await CreateAsync();
        var att = new AttachmentModel { FileName = @"..\..\evil.exe", PartSpecifier = "2" };

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(att), [att]);

        var path = Assert.Single(f.Clipboard.Files);
        Assert.StartsWith(f.Staging.Folder, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("evil.exe", Path.GetFileName(path));
    }

    [Fact]
    public async Task AFailedDownload_SaysWhy_AndLeavesTheClipboardAlone()
    {
        var f = await CreateAsync();
        await f.Vm.CopyMessagesToClipboardAsync([f.Row("a0")]);
        var before = f.Clipboard.Marker;
        f.Mail.FailDownloads = true;
        var att = new AttachmentModel { FileName = "report.pdf", PartSpecifier = "2" };

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(att), [att]);

        Assert.Equal(before, f.Clipboard.Marker);
        Assert.False(f.Vm.MessageClipboard.IsEmpty);   // the earlier copy still stands
        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.StartsWith("Could not copy report.pdf", f.Announced.Last.Text);
    }

    [Fact]
    public async Task CopyingAnAttachment_ReplacesACopyOfMessages()
    {
        // The clipboard holds the attachment now; Ctrl+V in a folder has no messages to paste.
        var f = await CreateAsync();
        await f.Vm.CutMessagesToClipboardAsync([f.Row("a0")]);
        var att = new AttachmentModel { FileName = "a.txt", Content = [65] };

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(att), [att]);
        await f.Vm.PasteMessagesAsync(f.Archive);

        Assert.Empty(f.Mail.Transfers);
        Assert.StartsWith("Nothing to paste", f.Announced.Last.Text);
    }

    [Fact]
    public async Task FromAMessageWindow_TheOutcomeIsSpokenThere_NotInTheMainWindow()
    {
        var f = await CreateAsync();
        var heard = new List<string>();
        var att = new AttachmentModel { FileName = "a.txt", Content = [65] };
        var spokenBefore = f.Announced.Announced.Count;

        await f.Vm.CopyAttachmentsToClipboardAsync(DetailWith(att), [att], heard.Add);

        Assert.Equal(["a.txt copied."], heard);
        Assert.Equal(spokenBefore, f.Announced.Announced.Count); // nothing said through the main window
        Assert.Equal("a.txt copied.", f.Vm.StatusText);           // still shown in its status bar
    }
}
