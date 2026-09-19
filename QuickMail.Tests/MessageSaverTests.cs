using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Save / Save As / Print flow (#728) against a fake window and stub services: which format and
/// folder are used, that nothing is overwritten, that saving never marks a message read, and — the
/// decision the feature turns on — that when the original is out of reach Save says why and lets the
/// user choose another format, rather than writing something else in its place.
/// </summary>
public sealed class MessageSaverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"QuickMailSave-{Guid.NewGuid():N}");

    public MessageSaverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] Original = Encoding.ASCII.GetBytes(
        "From: Jane <jane@example.com>\r\nSubject: Shipped\r\nMessage-ID: <1@example.com>\r\n\r\nOriginal body\r\n");

    private sealed class FakeMail : StubImapMailServiceBase, IMailService
    {
        public Func<string, byte[]>? Original;
        public Exception? OriginalFailure;
        public int PrefetchCalls, GetDetailCalls;

        public byte[]? PartialBeforeFailure;

        public async Task CopyOriginalMessageToAsync(Guid accountId, string folderName, string messageId, Stream destination, CancellationToken ct = default)
        {
            if (OriginalFailure is not null)
            {
                if (PartialBeforeFailure is not null) await destination.WriteAsync(PartialBeforeFailure, ct);
                throw OriginalFailure;
            }
            await destination.WriteAsync(Original!(messageId), ct);
        }

        public override Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            PrefetchCalls++;
            return Task.FromResult(new MailMessageDetail
            {
                MessageId = messageId, AccountId = accountId, FolderName = folderName,
                Subject = "Shipped", From = "Jane <jane@example.com>", PlainTextBody = "Server body",
            });
        }

        public override Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            GetDetailCalls++;   // the mark-read path: saving must never use it
            return base.GetMessageDetailAsync(accountId, folderName, messageId, ct);
        }
    }

    private sealed class FakeUi : IMessageSaveUi
    {
        public Func<string, MessageSaveFormat, string, MessageSaveTarget?>? OnChooseFile;
        public Func<int, MessageSaveFormat, string, MessageSaveTarget?>? OnChooseFolder;
        public bool ConfirmAnswer;
        public List<string> Explanations = [];
        public List<(string Suggested, MessageSaveFormat Format)> FileDialogs = [];
        public List<string> PdfPages = [];
        public string? PrintedHtml;
        public bool PrintAnswer = true;

        public MessageSaveTarget? ChooseFile(string suggestedFileName, MessageSaveFormat format, string initialFolder)
        {
            FileDialogs.Add((suggestedFileName, format));
            return OnChooseFile?.Invoke(suggestedFileName, format, initialFolder);
        }

        public MessageSaveTarget? ChooseFolder(int count, MessageSaveFormat format, string initialFolder) =>
            OnChooseFolder?.Invoke(count, format, initialFolder);

        public bool ConfirmTryAnotherFormat(string explanation)
        {
            Explanations.Add(explanation);
            return ConfirmAnswer;
        }

        public Task<byte[]> RenderPdfAsync(string html, CancellationToken ct)
        {
            PdfPages.Add(html);
            return Task.FromResult(Encoding.ASCII.GetBytes("%PDF-fake"));
        }

        public Task<bool> PrintAsync(string html, string documentTitle, CancellationToken ct)
        {
            PrintedHtml = html;
            return Task.FromResult(PrintAnswer);
        }
    }

    private (MessageSaver Saver, FakeMail Mail, StubConfigService Config) NewSaver(
        string format = "eml", StubLocalStoreService? store = null, Func<string, bool>? fileExists = null)
    {
        var mail = new FakeMail { Original = _ => Original };
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.SaveMessageFormat = format;
        cfg.SaveMessageFolder = _dir;
        var saver = new MessageSaver(mail, store, config,
            m => new MessageSaveContext("Kelly (kelly@example.com)", "Inbox", null, DateTimeOffset.Now),
            fileExists, defaultFolder: () => Path.Combine(_dir, "Documents"));
        return (saver, mail, config);
    }

    private static MailMessageSummary Summary(string id = "7", string subject = "Shipped") => new()
    {
        MessageId = id, AccountId = Guid.NewGuid(), FolderName = "INBOX",
        Subject = subject, From = "Jane <jane@example.com>",
        Date = new DateTimeOffset(2026, 9, 17, 14, 32, 0, TimeSpan.Zero),
        IsRead = false,
    };

    // ── Save (no dialog) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Save_WritesTheOriginalByteForByte_IntoTheSaveFolder_WithNoDialog()
    {
        var (saver, _, _) = NewSaver();
        var ui = new FakeUi();

        var outcome = await saver.SaveAsync([Summary()], chooseLocation: false, ui, ct: TestContext.Current.CancellationToken);

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(Original, File.ReadAllBytes(file));
        Assert.StartsWith("Shipped - Jane - ", Path.GetFileName(file));
        Assert.Empty(ui.FileDialogs);
        Assert.Equal($"Saved {Path.GetFileName(file)} in {MessageSaver.FolderLabel(_dir)}.", outcome.Text);
    }

    [Fact]
    public async Task Save_TwiceKeepsBothCopies()
    {
        var (saver, _, _) = NewSaver();
        var ui = new FakeUi();
        var message = Summary();

        await saver.SaveAsync([message], false, ui, ct: TestContext.Current.CancellationToken);
        await saver.SaveAsync([message], false, ui, ct: TestContext.Current.CancellationToken);

        var names = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(names, n => n!.EndsWith(" (2).eml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_SeveralMessages_OneFileEach()
    {
        var (saver, _, _) = NewSaver();
        var outcome = await saver.SaveAsync([Summary("1", "One"), Summary("2", "Two"), Summary("3", "Three")],
            false, new FakeUi(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, Directory.GetFiles(_dir).Length);
        Assert.Equal($"Saved 3 messages in {MessageSaver.FolderLabel(_dir)}.", outcome.Text);
    }

    [Theory]
    [InlineData("txt", ".txt")]
    [InlineData("html", ".html")]
    public async Task Save_InTheDefaultFormat_UsesTheCachedCopy_AndNeverMarksRead(string format, string ext)
    {
        var store = new StubLocalStoreService
        {
            SeededDetail = new MailMessageDetail { Subject = "Shipped", From = "Jane <jane@example.com>", PlainTextBody = "Cached body" },
        };
        var (saver, mail, _) = NewSaver(format, store);

        await saver.SaveAsync([Summary()], false, new FakeUi(), ct: TestContext.Current.CancellationToken);

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.EndsWith(ext, file);
        Assert.Contains("Cached body", File.ReadAllText(file));
        Assert.Equal(0, mail.PrefetchCalls);
        Assert.Equal(0, mail.GetDetailCalls);
    }

    [Fact]
    public async Task Save_AsText_FallsBackToTheServer_WithoutMarkingRead()
    {
        var (saver, mail, _) = NewSaver("txt", new StubLocalStoreService());

        await saver.SaveAsync([Summary()], false, new FakeUi(), ct: TestContext.Current.CancellationToken);

        Assert.Contains("Server body", File.ReadAllText(Assert.Single(Directory.GetFiles(_dir))));
        Assert.Equal(1, mail.PrefetchCalls);
        Assert.Equal(0, mail.GetDetailCalls);
    }

    [Fact]
    public async Task Save_TextFile_HasAByteOrderMark_SoWindowsEditorsReadItAsUnicode()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Grüße" } };
        var (saver, _, _) = NewSaver("txt", store);
        await saver.SaveAsync([Summary()], false, new FakeUi(), ct: TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    [Fact]
    public async Task Save_AsPdf_RendersTheWebPage()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Body" } };
        var (saver, _, _) = NewSaver("pdf", store);
        var ui = new FakeUi();

        await saver.SaveAsync([Summary()], false, ui, ct: TestContext.Current.CancellationToken);

        Assert.EndsWith(".pdf", Assert.Single(Directory.GetFiles(_dir)));
        Assert.Contains("Content-Security-Policy", Assert.Single(ui.PdfPages));
    }

    [Fact]
    public async Task Save_AnUnavailableSaveFolder_SaysSo()
    {
        var (saver, _, config) = NewSaver();
        // A path under an existing FILE can never be created as a directory.
        var blocker = Path.Combine(_dir, "file.txt");
        File.WriteAllText(blocker, "x");
        config.Load().SaveMessageFolder = Path.Combine(blocker, "sub");

        var outcome = await saver.SaveAsync([Summary()], false, new FakeUi(), ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("The save folder ", outcome.Text);
        Assert.Contains("Save As", outcome.Text);
    }

    // ── Save As ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAs_OneMessage_SuggestsTheName_AndUsesTheChosenPathAndFormat()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Body" } };
        var (saver, _, config) = NewSaver("eml", store);
        var chosen = Path.Combine(_dir, "mine.html");
        var ui = new FakeUi { OnChooseFile = (_, _, _) => new MessageSaveTarget(chosen, MessageSaveFormat.Html) };

        await saver.SaveAsync([Summary()], chooseLocation: true, ui, ct: TestContext.Current.CancellationToken);

        var (suggested, format) = Assert.Single(ui.FileDialogs);
        Assert.StartsWith("Shipped - Jane - ", suggested);
        Assert.EndsWith(".eml", suggested);
        Assert.Equal(MessageSaveFormat.Eml, format);          // starts on the default format
        Assert.True(File.Exists(chosen));
        Assert.Contains("<main>", File.ReadAllText(chosen));
        Assert.Equal(_dir, config.Load().LastSaveAsFolder);   // the next Save As starts here
        Assert.Equal("eml", config.Load().SaveMessageFormat); // Save As does not change the default
    }

    [Fact]
    public async Task SaveAs_Cancelled_DoesNothing_AndSaysNothing()
    {
        var (saver, _, _) = NewSaver();
        var outcome = await saver.SaveAsync([Summary()], true, new FakeUi(), ct: TestContext.Current.CancellationToken);
        Assert.Null(outcome.Text);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task SaveAs_SeveralMessages_ChoosesAFolderAndFormat_ForAll()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Body" } };
        var (saver, _, _) = NewSaver("eml", store);
        var target = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        int? askedFor = null;
        var ui = new FakeUi
        {
            OnChooseFolder = (count, _, _) => { askedFor = count; return new MessageSaveTarget(target, MessageSaveFormat.Text); },
        };

        await saver.SaveAsync([Summary("1", "One"), Summary("2", "Two")], true, ui, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, askedFor);
        Assert.Empty(ui.FileDialogs);
        var files = Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.EndsWith(".txt", f));
        Assert.StartsWith("One - ", files[0]);
    }

    // ── The original out of reach (#728 decision: refuse, explain, let the user choose) ──

    [Fact]
    public async Task Offline_TheOriginalIsRefused_WithAReason_AndNothingElseIsWritten()
    {
        var (saver, mail, _) = NewSaver();
        mail.OriginalFailure = new SocketException((int)SocketError.NetworkUnreachable);
        var ui = new FakeUi { ConfirmAnswer = false };

        var outcome = await saver.SaveAsync([Summary()], false, ui, ct: TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(_dir));   // no substitute written silently
        var explanation = Assert.Single(ui.Explanations);
        Assert.Contains("cannot reach the server", explanation);
        Assert.Contains("text file, web page, or PDF", explanation);
        Assert.StartsWith("Could not save the message:", outcome.Text);
    }

    [Fact]
    public async Task Offline_ChoosingAnotherFormat_ReopensSaveAs_OnText()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Cached body" } };
        var (saver, mail, _) = NewSaver("eml", store);
        mail.OriginalFailure = new SocketException((int)SocketError.NetworkUnreachable);
        var chosen = Path.Combine(_dir, "instead.txt");
        var ui = new FakeUi
        {
            ConfirmAnswer = true,
            OnChooseFile  = (_, format, _) => new MessageSaveTarget(chosen, format),
        };

        var outcome = await saver.SaveAsync([Summary()], false, ui, ct: TestContext.Current.CancellationToken);

        var (suggested, format) = Assert.Single(ui.FileDialogs);
        Assert.Equal(MessageSaveFormat.Text, format);
        Assert.EndsWith(".txt", suggested);
        Assert.Contains("Cached body", File.ReadAllText(chosen));
        Assert.Equal($"Saved instead.txt in {MessageSaver.FolderLabel(_dir)}.", outcome.Text);
    }

    [Fact]
    public async Task AnOriginalThatWasNeverKept_IsExplainedInTheBackendsOwnWords()
    {
        var (saver, mail, _) = NewSaver();
        mail.OriginalFailure = new MessageOriginalUnavailableException("QuickMail did not keep the original of this message.");
        var ui = new FakeUi();

        await saver.SaveAsync([Summary()], false, ui, ct: TestContext.Current.CancellationToken);

        Assert.Contains("did not keep the original", Assert.Single(ui.Explanations));
    }

    [Fact]
    public async Task SomeOriginalsOutOfReach_TheRestAreStillSaved()
    {
        var (saver, mail, _) = NewSaver();
        mail.Original = id => id == "2" ? throw new MessageOriginalUnavailableException("Gone.") : Original;
        var ui = new FakeUi();

        var outcome = await saver.SaveAsync([Summary("1", "One"), Summary("2", "Two"), Summary("3", "Three")],
            false, ui, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, Directory.GetFiles(_dir).Length);
        Assert.Contains("1 of 3", Assert.Single(ui.Explanations));
        Assert.StartsWith("Saved 2 of 3 messages", outcome.Text);
    }

    [Fact]
    public async Task ADiskFailure_IsNotMistakenForAnUnreachableOriginal()
    {
        var (saver, mail, _) = NewSaver();
        mail.OriginalFailure = new UnauthorizedAccessException("Access denied.");
        var ui = new FakeUi();

        var outcome = await saver.SaveAsync([Summary()], false, ui, ct: TestContext.Current.CancellationToken);

        Assert.Empty(ui.Explanations);   // another format would not get around this
        Assert.Contains("Access denied", outcome.Text);
    }

    // ── Security review follow-ups ──────────────────────────────────────────

    [Fact]
    public async Task ANameTakenBetweenTheCheckAndTheWrite_IsNotOverwritten()
    {
        // The existence check says every name is free, as it would if another save created the file a
        // moment after the check. The write must still refuse to replace it.
        var (saver, _, _) = NewSaver(fileExists: _ => false);
        var ui = new FakeUi();
        var message = Summary();
        var name = MessageExport.BuildFileName(message, MessageSaveFormat.Eml);
        File.WriteAllText(Path.Combine(_dir, name), "someone else's file");

        await saver.SaveAsync([message], false, ui, ct: TestContext.Current.CancellationToken);

        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(_dir, name)));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task SaveAs_AReplacementTheDialogDidNotAskAbout_IsSavedBeside_NotOver()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Body" } };
        var (saver, _, _) = NewSaver("eml", store);
        var existing = Path.Combine(_dir, "Invoice 3.2.eml");
        File.WriteAllText(existing, "keep me");
        var ui = new FakeUi { OnChooseFile = (_, _, _) => new MessageSaveTarget(existing, MessageSaveFormat.Eml, OverwriteConfirmed: false) };

        await saver.SaveAsync([Summary()], true, ui, ct: TestContext.Current.CancellationToken);

        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(_dir, "Invoice 3.2 (2).eml")));
    }

    [Fact]
    public async Task SaveAs_AConfirmedReplacement_ReplacesOnlyOnceTheNewFileIsComplete()
    {
        var (saver, mail, _) = NewSaver();
        var existing = Path.Combine(_dir, "old.eml");
        File.WriteAllText(existing, "the file the user chose to replace");
        mail.OriginalFailure = new SocketException((int)SocketError.ConnectionReset);
        mail.PartialBeforeFailure = [1, 2, 3];
        var ui = new FakeUi { OnChooseFile = (_, _, _) => new MessageSaveTarget(existing, MessageSaveFormat.Eml, OverwriteConfirmed: true) };

        await saver.SaveAsync([Summary()], true, ui, ct: TestContext.Current.CancellationToken);

        // The download failed half way: the old file is untouched and nothing partial is left behind.
        Assert.Equal("the file the user chose to replace", File.ReadAllText(existing));
        Assert.Equal([existing], Directory.GetFiles(_dir));

        mail.OriginalFailure = null;
        await saver.SaveAsync([Summary()], true, ui, ct: TestContext.Current.CancellationToken);
        Assert.Equal(Original, File.ReadAllBytes(existing));
        Assert.Equal([existing], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AFailedDownload_LeavesNoPartialFile()
    {
        var (saver, mail, _) = NewSaver();
        mail.OriginalFailure = new MessageOriginalUnavailableException("Gone.");
        mail.PartialBeforeFailure = [1, 2, 3];

        await saver.SaveAsync([Summary()], false, new FakeUi(), ct: TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void ARelativeSaveFolder_IsIgnored_ForDocuments()
    {
        var (saver, _, config) = NewSaver();
        config.Load().SaveMessageFolder = @"Saved Mail";
        Assert.Equal(Path.Combine(_dir, "Documents"), saver.SaveFolder());
    }

    // ── Print ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Print_SendsTheWebPage_AndReportsIt()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { Subject = "Shipped", PlainTextBody = "Body" } };
        var (saver, mail, _) = NewSaver("eml", store);
        var ui = new FakeUi();

        var outcome = await saver.PrintAsync(Summary(), ui, TestContext.Current.CancellationToken);

        Assert.Contains("<main>", ui.PrintedHtml);
        Assert.Equal("Sent Shipped to the printer.", outcome.Text);
        Assert.Equal(0, mail.GetDetailCalls);
    }

    [Fact]
    public async Task Print_Cancelled_SaysNothing()
    {
        var store = new StubLocalStoreService { SeededDetail = new MailMessageDetail { PlainTextBody = "Body" } };
        var (saver, _, _) = NewSaver("eml", store);
        var outcome = await saver.PrintAsync(Summary(), new FakeUi { PrintAnswer = false }, TestContext.Current.CancellationToken);
        Assert.Null(outcome.Text);
    }
}
