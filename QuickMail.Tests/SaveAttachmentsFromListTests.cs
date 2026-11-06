using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Save All Attachments from the message list, without opening the message (#931): the message is
/// read from the cache or fetched without marking it read, the folder is asked for only when there
/// is something to save, and attachments with the same name do not overwrite one another.
/// </summary>
public sealed class SaveAttachmentsFromListTests : IDisposable
{
    private static readonly Guid Work = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qm-931-" + Guid.NewGuid().ToString("N"));

    public SaveAttachmentsFromListTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static MailMessageSummary Msg(string id, string folder = "INBOX") => new()
    {
        MessageId = id, AccountId = Work, FolderName = folder, From = "someone@example.com",
        Subject = $"Subject {id}", Date = DateTimeOffset.Now,
    };

    private static MailMessageDetail Detail(string id, params string[] attachmentNames) => new()
    {
        MessageId = id, AccountId = Work, FolderName = "INBOX", From = "a <a@example.com>",
        Attachments = attachmentNames.Select((n, i) => new AttachmentModel
        {
            FileName = n, PartSpecifier = $"{i + 2}",
        }).ToList(),
    };

    private sealed class DetailStore : StubLocalStoreService
    {
        public Dictionary<string, MailMessageDetail> Details { get; } = [];
        public bool Throw { get; set; }
        public override Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId)
        {
            if (Throw) throw new InvalidOperationException("no such table: MessageDetail");
            return Task.FromResult(Details.TryGetValue(messageId, out var d) ? d : null);
        }
    }

    private sealed class Mail : StubImapMailServiceBase
    {
        public Dictionary<string, MailMessageDetail> ServerDetails { get; } = [];
        public List<string> Prefetched { get; } = [];
        public List<string> Downloads { get; } = [];
        public int OpenCalls;
        public Exception? DownloadFailure { get; set; }

        public override Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            Prefetched.Add(messageId);
            return Task.FromResult(ServerDetails[messageId]);
        }

        // The path that marks a message read. Saving attachments must never take it.
        public override Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            OpenCalls++;
            return Task.FromResult(ServerDetails[messageId]);
        }

        public override Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default)
        {
            if (DownloadFailure is not null) return Task.FromException<byte[]>(DownloadFailure);
            Downloads.Add($"{messageId}/{partSpecifier}");
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes($"{messageId}/{partSpecifier}"));
        }
    }

    private sealed class Fixture
    {
        public Mail Mail { get; } = new();
        public DetailStore Store { get; } = new();
        public MainViewModel Vm { get; }
        public int FolderPrompts;
        public string? FolderToPick;

        public Fixture(string? folder)
        {
            FolderToPick = folder;
            Vm = new MainViewModel(
                Mail, new StubAccountService(), new StubCredentialService(),
                Store, new StubOAuthService(), new StubSyncService(),
                new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
                new StubRuleService(), new StubSmtpService());
            Vm.SaveFolderPathRequested = _ => { FolderPrompts++; return FolderToPick; };
        }
    }

    private string[] SavedNames() =>
        Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task ACachedMessagesAttachmentsAreDownloadedAndSaved()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1", "report.pdf", "photo.jpg");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(["photo.jpg", "report.pdf"], SavedNames());
        Assert.Equal("m1/2", File.ReadAllText(Path.Combine(_folder, "report.pdf")));
        Assert.Equal("Saved 2 attachments.", f.Vm.StatusText);
        Assert.Equal(1, f.FolderPrompts);
        Assert.Empty(f.Mail.Prefetched);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task AnUncachedMessageIsFetchedWithoutMarkingItRead()
    {
        var f = new Fixture(_folder);
        f.Mail.ServerDetails["m1"] = Detail("m1", "notes.txt");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(["m1"], f.Mail.Prefetched);
        Assert.Equal(0, f.Mail.OpenCalls);
        Assert.Equal(["notes.txt"], SavedNames());
        Assert.Equal("Saved 1 attachment.", f.Vm.StatusText);
    }

    [Fact]
    public async Task AStoreThatThrowsStillFallsThroughToTheServer()
    {
        var f = new Fixture(_folder);
        f.Store.Throw = true;
        f.Mail.ServerDetails["m1"] = Detail("m1", "notes.txt");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(["notes.txt"], SavedNames());
    }

    [Fact]
    public async Task AMessageWithNoAttachmentsSaysSoAndNeverAsksForAFolder()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(0, f.FolderPrompts);
        Assert.Equal("This message has no attachments.", f.Vm.StatusText);
        Assert.Empty(SavedNames());
    }

    [Fact]
    public async Task SeveralMessagesWithNoAttachmentsSaySo()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1");
        f.Store.Details["m2"] = Detail("m2");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1"), Msg("m2")]);

        Assert.Equal(0, f.FolderPrompts);
        Assert.Equal("None of the selected messages have attachments.", f.Vm.StatusText);
    }

    [Fact]
    public async Task CancellingTheFolderDialogWritesNothing()
    {
        var f = new Fixture(folder: null);
        f.Store.Details["m1"] = Detail("m1", "report.pdf");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(1, f.FolderPrompts);
        Assert.Empty(f.Mail.Downloads);
        Assert.Empty(SavedNames());
    }

    [Fact]
    public async Task SameNamesAcrossMessagesAndAFileAlreadyThereAreNotOverwritten()
    {
        File.WriteAllText(Path.Combine(_folder, "image001.png"), "mine");
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1", "image001.png");
        f.Store.Details["m2"] = Detail("m2", "image001.png");
        f.Store.Details["m3"] = Detail("m3");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1"), Msg("m2"), Msg("m3")]);

        Assert.Equal(["image001 (2).png", "image001 (3).png", "image001.png"], SavedNames());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_folder, "image001.png")));
        Assert.Equal("m1/2", File.ReadAllText(Path.Combine(_folder, "image001 (2).png")));
        Assert.Equal("m2/2", File.ReadAllText(Path.Combine(_folder, "image001 (3).png")));
        Assert.Equal("Saved 2 attachments.", f.Vm.StatusText);
    }

    [Fact]
    public async Task AServerNameWithAPathCannotWriteOutsideTheFolder()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1", @"..\..\evil.exe");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(["evil.exe"], SavedNames());
    }

    [Fact]
    public async Task ADownloadFailureOfflineSaysSoAndClearsBusy()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1", "report.pdf");
        f.Mail.DownloadFailure = new SocketException((int)SocketError.HostUnreachable);

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal("Attachments are not available offline.", f.Vm.StatusText);
        Assert.False(f.Vm.IsBusy);
        Assert.Empty(SavedNames());
    }

    [Fact]
    public async Task OneMessageThatCannotBeReadDoesNotStopTheOthers()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1", "a.txt");
        // m2 is neither cached nor on the server: the prefetch throws.
        f.Store.Details["m3"] = Detail("m3", "b.txt");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1"), Msg("m2"), Msg("m3")]);

        Assert.Equal(["a.txt", "b.txt"], SavedNames());
        Assert.Equal("Saved 2 attachments. 1 message could not be read.", f.Vm.StatusText);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task AFailedWriteIsReportedAsTheDisksProblemNotAsOffline()
    {
        var missing = Path.Combine(_folder, "gone");
        var f = new Fixture(missing);
        f.Store.Details["m1"] = Detail("m1", "a.txt");

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.StartsWith("Saved 0 attachments. 1 could not be saved: ", f.Vm.StatusText);
        Assert.DoesNotContain("offline", f.Vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task ADownloadThatFailsAfterOthersSavedSaysHowMany()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1", "a.txt");
        f.Store.Details["m1"].Attachments.Add(new AttachmentModel { FileName = "lost.bin" }); // no part, no content

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(["a.txt"], SavedNames());
        Assert.Equal("Saved 1 attachment. 1 could not be downloaded from the server.", f.Vm.StatusText);
    }

    [Fact]
    public async Task TheSameResultTwiceIsAnnouncedBothTimes()
    {
        var f = new Fixture(_folder);
        f.Store.Details["m1"] = Detail("m1");
        f.Store.Details["m2"] = Detail("m2");
        var raised = 0;
        f.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.StatusText)) raised++; };

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);
        var afterFirst = raised;
        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m2")]);

        Assert.Equal("This message has no attachments.", f.Vm.StatusText);
        Assert.True(raised > afterFirst);
    }

    [Fact]
    public async Task TheOpenMessageIsUsedAsItIsAndKeepsWhatItDownloaded()
    {
        var f = new Fixture(_folder);
        var open = Detail("m1", "a.txt");
        open.Attachments[0].Content = [1, 2, 3];
        f.Vm.MessageDetail = open;

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Equal(["a.txt"], SavedNames());
        Assert.Empty(f.Mail.Prefetched);
        Assert.Empty(f.Mail.Downloads);
        Assert.NotNull(open.Attachments[0].Content);
    }

    [Fact]
    public async Task AnotherMessagesBytesAreDroppedOnceWritten()
    {
        var f = new Fixture(_folder);
        var cached = Detail("m1", "a.txt");
        f.Store.Details["m1"] = cached;

        await f.Vm.SaveAttachmentsOfMessagesAsync([Msg("m1")]);

        Assert.Null(cached.Attachments[0].Content);
    }

    [Fact]
    public async Task TheReadingPanesSaveAllNumbersRepeatsToo()
    {
        File.WriteAllText(Path.Combine(_folder, "a.txt"), "mine");
        var f = new Fixture(_folder);
        f.Vm.MessageDetail = Detail("m1", "a.txt");

        await f.Vm.SaveAllAttachmentsCommand.ExecuteAsync(null);

        Assert.Equal(["a (2).txt", "a.txt"], SavedNames());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_folder, "a.txt")));
    }

    [Fact]
    public async Task OutboxRowsAreNotSaved()
    {
        var f = new Fixture(_folder);
        var outboxRow = Msg("o1", folder: MainViewModel.OutboxFolder.FullName);

        await f.Vm.SaveAttachmentsOfMessagesAsync([outboxRow]);

        Assert.Equal(0, f.FolderPrompts);
        Assert.Contains("Outbox", f.Vm.StatusText);
    }

    // ── AttachmentSafety.UniquePath ──────────────────────────────────────────────

    [Fact]
    public void UniquePathNumbersRepeatsCaseInsensitivelyAndKeepsTheExtension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool None(string _) => false;

        Assert.Equal(Path.Combine(@"C:\d", "a.txt"), AttachmentSafety.UniquePath(@"C:\d", "a.txt", taken, None));
        Assert.Equal(Path.Combine(@"C:\d", "A (2).TXT"), AttachmentSafety.UniquePath(@"C:\d", "A.TXT", taken, None));
        Assert.Equal(Path.Combine(@"C:\d", "a (3).txt"), AttachmentSafety.UniquePath(@"C:\d", "a.txt", taken, None));
        Assert.Equal(Path.Combine(@"C:\d", "noext"), AttachmentSafety.UniquePath(@"C:\d", "noext", taken, None));
        Assert.Equal(Path.Combine(@"C:\d", "noext (2)"), AttachmentSafety.UniquePath(@"C:\d", "noext", taken, None));
        Assert.Equal(Path.Combine(@"C:\d", "attachment"), AttachmentSafety.UniquePath(@"C:\d", "", taken, None));
    }

    [Fact]
    public void UniquePathSkipsFilesThatExist()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\d", "r.pdf"), Path.Combine(@"C:\d", "r (2).pdf"),
        };

        Assert.Equal(Path.Combine(@"C:\d", "r (3).pdf"),
            AttachmentSafety.UniquePath(@"C:\d", "r.pdf", taken, existing.Contains));
    }
}
