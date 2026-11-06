// Issue #939: opening .eml files (from Explorer) and attached emails inside QuickMail.
//
// Found testing #929: a copied message pasted into Explorer or a new message could not be opened.
// QuickMail ignored a file it was started with, and handed an attached email to Windows. Opened
// messages now live in OpenedMessageService under a reserved account id the router sends to it,
// so pictures, attachments, Save, Print and Forward work on them through the usual paths.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public sealed class OpenEmailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-open-test-" + Guid.NewGuid().ToString("N")[..8]);

    public OpenEmailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Home = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>A message with a body, an attached PDF, an inline picture and an attached email.</summary>
    private static byte[] SampleEmail()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        var eml =
            "From: Alice <alice@example.com>\r\n" +
            "To: kelly@example.com\r\n" +
            "Subject: Quarterly report\r\n" +
            "Date: Thu, 05 Nov 2026 09:00:00 +0000\r\n" +
            "Message-ID: <report@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=OUTER\r\n\r\n" +
            "--OUTER\r\n" +
            "Content-Type: multipart/related; boundary=REL\r\n\r\n" +
            "--REL\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n\r\n" +
            "<p>See the chart <img src=\"cid:chart@x\" alt=\"Chart\"></p>\r\n" +
            "--REL\r\n" +
            "Content-Type: image/png\r\nContent-ID: <chart@x>\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            png + "\r\n" +
            "--REL--\r\n" +
            "--OUTER\r\n" +
            "Content-Type: application/pdf; name=report.pdf\r\nContent-Disposition: attachment; filename=report.pdf\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(Encoding.ASCII.GetBytes("PDF bytes")) + "\r\n" +
            "--OUTER\r\n" +
            "Content-Type: message/rfc822\r\nContent-Disposition: attachment; filename=\"Earlier.eml\"\r\n\r\n" +
            "From: Bob <bob@example.com>\r\nSubject: Earlier message\r\n\r\nThe earlier one.\r\n" +
            "--OUTER--\r\n";
        return Encoding.ASCII.GetBytes(eml);
    }

    // ── The service ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_ShowsTheMessage_UnderTheReservedAccount()
    {
        using var service = new OpenedMessageService();

        var detail = service.Open(SampleEmail());

        Assert.Equal(OpenedMessageService.AccountId, detail.AccountId);
        Assert.True(OpenedMessageService.IsOpenedMessage(detail));
        Assert.Equal("Quarterly report", detail.Subject);
        Assert.Contains("alice@example.com", detail.From);
        Assert.Equal(["report.pdf", "Earlier.eml"], detail.Attachments.Select(a => a.FileName));
        Assert.Single(detail.InlineImages);
    }

    [Fact]
    public async Task ItsAttachmentsPicturesAndOriginal_ComeFromTheHeldBytes()
    {
        using var service = new OpenedMessageService();
        var original = SampleEmail();
        var detail = service.Open(original);
        var ct = TestContext.Current.CancellationToken;

        var pdf = await service.DownloadAttachmentAsync(detail.AccountId, detail.FolderName, detail.MessageId, detail.Attachments[0].PartSpecifier!, ct);
        Assert.Equal("PDF bytes", Encoding.ASCII.GetString(pdf));

        var picture = await service.DownloadAttachmentAsync(detail.AccountId, detail.FolderName, detail.MessageId, detail.InlineImages[0].PartSpecifier!, ct);
        Assert.Equal(0x89, picture[0]); // PNG signature

        var attachedEmail = await service.DownloadAttachmentAsync(detail.AccountId, detail.FolderName, detail.MessageId, detail.Attachments[1].PartSpecifier!, ct);
        Assert.Contains("Earlier message", Encoding.ASCII.GetString(attachedEmail));

        using var copy = new MemoryStream();
        await service.CopyOriginalMessageToAsync(detail.AccountId, detail.FolderName, detail.MessageId, copy, ct);
        Assert.Equal(original, copy.ToArray());

        var again = await service.GetMessageDetailAsync(detail.AccountId, detail.FolderName, detail.MessageId, ct);
        Assert.Equal("Quarterly report", again.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0000\u0001\u0002 not an email")]
    public void BytesThatAreNotAnEmail_AreRefused(string text)
    {
        using var service = new OpenedMessageService();
        Assert.Throws<FormatException>(() => service.Open(Encoding.ASCII.GetBytes(text)));
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public async Task Close_LetsGoOfTheMessage()
    {
        using var service = new OpenedMessageService();
        var detail = service.Open(SampleEmail());

        service.Close(detail.MessageId);

        Assert.Equal(0, service.Count);
        using var sink = new MemoryStream();
        await Assert.ThrowsAsync<MessageOriginalUnavailableException>(() =>
            service.CopyOriginalMessageToAsync(detail.AccountId, detail.FolderName, detail.MessageId, sink, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangingAnOpenedMessage_IsRefusedWithAReason()
    {
        using var service = new OpenedMessageService();
        var detail = service.Open(SampleEmail());
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MoveToTrashAsync(detail.AccountId, detail.FolderName, detail.MessageId, ct));
        Assert.Contains("not in a mailbox", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MarkReadAsync(detail.AccountId, detail.FolderName, detail.MessageId, ct));
    }

    [Fact]
    public async Task TheRouter_SendsTheReservedAccountToIt()
    {
        using var service = new OpenedMessageService();
        var router = new MailServiceRouter([new StubImapMailService()]);
        router.RegisterAccount(OpenedMessageService.AccountId, service);
        var detail = service.Open(SampleEmail());

        var pdf = await router.DownloadAttachmentAsync(detail.AccountId, detail.FolderName, detail.MessageId,
            detail.Attachments[0].PartSpecifier!, TestContext.Current.CancellationToken);

        Assert.Equal("PDF bytes", Encoding.ASCII.GetString(pdf));
    }

    // ── Through the view model ───────────────────────────────────────────────────

    private sealed record Fixture(MainViewModel Vm, OpenedMessageService Opened, StatusAnnouncementRecorder Announced,
                                  List<MailMessageDetail> Windows, List<ComposeModel> Composed);

    private static Fixture CreateVm()
    {
        var opened = new OpenedMessageService();
        var router = new MailServiceRouter([new StubImapMailService()]);
        router.RegisterAccount(OpenedMessageService.AccountId, opened);
        var vm = new MainViewModel(
            router, new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), openedMessages: opened);
        vm.Accounts.Add(new AccountModel { Id = Work, AccountName = "Work", Username = "work@example.com" });
        vm.Accounts.Add(new AccountModel { Id = Home, AccountName = "Home", Username = "home@example.com", IsDefault = true });
        var windows = new List<MailMessageDetail>();
        var composed = new List<ComposeModel>();
        vm.OpenedMessageRequested += windows.Add;
        vm.ComposeRequested += composed.Add;
        return new Fixture(vm, opened, StatusAnnouncementRecorder.Watch(vm), windows, composed);
    }

    [Fact]
    public async Task OpeningAFile_ShowsItInAWindow()
    {
        var f = CreateVm();
        var path = Path.Combine(_dir, "Quarterly report.eml");
        await File.WriteAllBytesAsync(path, SampleEmail(), TestContext.Current.CancellationToken);

        await f.Vm.OpenEmailFileAsync(path);

        Assert.Equal("Quarterly report", Assert.Single(f.Windows).Subject);
    }

    [Fact]
    public async Task AMissingFile_SaysSo()
    {
        var f = CreateVm();

        await f.Vm.OpenEmailFileAsync(Path.Combine(_dir, "gone.eml"));

        Assert.Empty(f.Windows);
        Assert.Equal(AnnouncementCategory.Result, f.Announced.Last.Category);
        Assert.StartsWith("Could not open gone.eml", f.Announced.Last.Text);
    }

    [Fact]
    public async Task AFileThatIsNotAnEmail_SaysSo()
    {
        var f = CreateVm();
        var path = Path.Combine(_dir, "junk.eml");
        await File.WriteAllBytesAsync(path, [0, 1, 2], TestContext.Current.CancellationToken);

        await f.Vm.OpenEmailFileAsync(path);

        Assert.Empty(f.Windows);
        Assert.Equal(("Could not open junk.eml: The file is not an email message.", AnnouncementCategory.Result), f.Announced.Last);
    }

    [Fact]
    public async Task AnAttachedEmail_OpensInQuickMail_NotInWindows()
    {
        var f = CreateVm();
        var outer = f.Opened.Open(SampleEmail());
        f.Vm.MessageDetail = outer;
        var attachedEmail = outer.Attachments.Single(a => a.FileName == "Earlier.eml");

        await f.Vm.OpenAttachmentCommand.ExecuteAsync(attachedEmail);

        Assert.Equal("Earlier message", Assert.Single(f.Windows).Subject);
    }

    [Theory]
    [InlineData("message/rfc822", "forwarded", true)]
    [InlineData("application/octet-stream", "Saved.EML", true)]
    [InlineData("application/pdf", "report.pdf", false)]
    public void WhatCountsAsAnAttachedEmail(string contentType, string fileName, bool expected) =>
        Assert.Equal(expected, MainViewModel.IsAttachedEmail(new AttachmentModel { ContentType = contentType, FileName = fileName }));

    [Fact]
    public void ReplyingToAnOpenedMessage_SendsFromTheDefaultAccount()
    {
        // It is in no account; a reply goes from the account a new message would use.
        var f = CreateVm();
        var opened = f.Opened.Open(SampleEmail());

        f.Vm.ReplyToMessage(opened, all: false);
        f.Vm.ReplyToMessage(opened, all: true);

        Assert.All(f.Composed, c => Assert.Equal(Home, c.AccountId));
        Assert.Equal("Re: Quarterly report", f.Composed[0].Subject);
    }

    [Fact]
    public async Task ForwardingAnOpenedMessage_BringsItsAttachments()
    {
        // The attachments are downloaded through the router, which sends the reserved id to the
        // opened-message service: the same path as for mail in an account.
        var f = CreateVm();
        var opened = f.Opened.Open(SampleEmail());

        await f.Vm.ForwardMessageAsync(opened);

        var compose = Assert.Single(f.Composed);
        Assert.Equal(Home, compose.AccountId);
        var pdf = compose.Attachments.Single(a => a.FileName == "report.pdf");
        Assert.Equal("PDF bytes", Encoding.ASCII.GetString(pdf.Content!));
    }

    // ── Handing a file to the instance already running ───────────────────────────

    [Theory]
    [InlineData(new[] { @"C:\mail\a.eml" }, @"C:\mail\a.eml")]
    [InlineData(new[] { "--profileDir", @"C:\p", @"C:\mail\B.EML" }, @"C:\mail\B.EML")]
    [InlineData(new[] { "/debug" }, null)]
    [InlineData(new[] { @"C:\mail\a.txt" }, null)]
    public void TheFileToOpen_IsAPlainPathEndingInEml(string[] args, string? expected) =>
        Assert.Equal(expected, SingleInstanceService.EmailFileFrom(args));

    [Fact]
    public void ARequestLeftByASecondLaunch_IsTakenOnceInOrder()
    {
        var key = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            SingleInstanceService.LeaveOpenRequest(key, Path.Combine(_dir, "first.eml"));
            SingleInstanceService.LeaveOpenRequest(key, Path.Combine(_dir, "second.eml"));

            Assert.Equal([Path.Combine(_dir, "first.eml"), Path.Combine(_dir, "second.eml")],
                         SingleInstanceService.TakeOpenRequests(key));
            Assert.Empty(SingleInstanceService.TakeOpenRequests(key));
        }
        finally
        {
            try { Directory.Delete(SingleInstanceService.OpenRequestFolder(key), recursive: true); } catch (IOException) { }
        }
    }

    // ── Open with ────────────────────────────────────────────────────────────────

    [Fact]
    public void Registering_OffersQuickMailInOpenWith_WithoutTakingTheDefault()
    {
        var path = @"Software\QuickMailTests\" + Guid.NewGuid().ToString("N");
        using var classes = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            // Whatever opens .eml now, and another program's Open with entry, must be left alone.
            using (var eml = classes.CreateSubKey(".eml")) eml.SetValue("", "Outlook.File.eml");
            using (var openWith = classes.CreateSubKey(@".eml\OpenWithProgids")) openWith.SetValue("Other.eml", Array.Empty<byte>(), RegistryValueKind.None);
            const string exe = @"C:\Users\me\AppData\Local\QuickMail\current\QuickMail.exe";

            EmailFileAssociation.Register(exe, classes);

            Assert.True(EmailFileAssociation.IsRegistered(exe, classes));
            using (var command = classes.OpenSubKey(@"QuickMail.eml\shell\open\command"))
                Assert.Equal($"\"{exe}\" \"%1\"", command?.GetValue(""));
            using (var eml = classes.OpenSubKey(".eml"))
                Assert.Equal("Outlook.File.eml", eml?.GetValue(""));

            EmailFileAssociation.Unregister(classes);

            Assert.False(EmailFileAssociation.IsRegistered(exe, classes));
            Assert.Null(classes.OpenSubKey("QuickMail.eml"));
            using (var openWith = classes.OpenSubKey(@".eml\OpenWithProgids"))
                Assert.Equal(["Other.eml"], openWith?.GetValueNames() ?? []);
            using (var eml = classes.OpenSubKey(".eml"))
                Assert.Equal("Outlook.File.eml", eml?.GetValue(""));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
