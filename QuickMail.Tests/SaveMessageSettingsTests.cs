using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The persistent half of saving messages (#728): the three config.ini keys, the Settings controls
/// for the default format and folder, and POP3 keeping every message's original so there is one
/// to save.
/// </summary>
public class SaveMessageSettingsTests
{
    private static ProfileContext TempProfile() =>
        new(Path.Combine(Path.GetTempPath(), $"QM-SaveSettings-{Guid.NewGuid():N}"));

    [Fact]
    public void SaveKeys_RoundTripThroughConfigIni_IncludingAPathWithSpaces()
    {
        var profile = TempProfile();
        var service = new ConfigService(profile);
        var config  = service.Load();
        Assert.Equal("eml", config.SaveMessageFormat);   // the default is the original message
        Assert.Equal(string.Empty, config.SaveMessageFolder);

        config.SaveMessageFormat = "html";
        config.SaveMessageFolder = @"C:\Users\someone\My Saved Mail";
        config.LastSaveAsFolder  = @"D:\Mail Archive";
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("html", reloaded.SaveMessageFormat);
        Assert.Equal(@"C:\Users\someone\My Saved Mail", reloaded.SaveMessageFolder);
        Assert.Equal(@"D:\Mail Archive", reloaded.LastSaveAsFolder);
    }

    [Fact]
    public void AnUnknownSaveFormatInConfigIni_FallsBackToTheOriginal()
    {
        var profile = TempProfile();
        Directory.CreateDirectory(profile.ProfileDir);
        File.WriteAllText(Path.Combine(profile.ProfileDir, "config.ini"), "[global]\r\nSaveMessageFormat = docx\r\n");
        Assert.Equal("eml", new ConfigService(profile).Load().SaveMessageFormat);
    }

    [Fact]
    public void SettingsViewModel_RoundTripsTheSaveFormatAndFolder()
    {
        var config = new StubConfigService();
        config.Save(new ConfigModel { SaveMessageFormat = "pdf", SaveMessageFolder = @"C:\Saved" });
        var vm = new SettingsViewModel(config, new StubCommandRegistry());

        Assert.Equal(MessageSaveFormat.Pdf, vm.SaveMessageFormat);
        Assert.Equal(@"C:\Saved", vm.SaveMessageFolderDisplay);

        vm.SaveMessageFormat = MessageSaveFormat.Text;
        vm.UseDocumentsFolderCommand.Execute(null);
        Assert.Equal("Documents", vm.SaveMessageFolderDisplay);
        vm.SaveCommand.Execute(null);

        Assert.Equal("txt", config.Load().SaveMessageFormat);
        Assert.Equal(string.Empty, config.Load().SaveMessageFolder);
    }

    [Fact]
    public void SettingsViewModel_ChooseFolder_AsksTheView_AndCancelKeepsTheOldFolder()
    {
        var config = new StubConfigService();
        config.Save(new ConfigModel { SaveMessageFolder = @"C:\Old" });
        var vm = new SettingsViewModel(config, new StubCommandRegistry());

        string? startedAt = null;
        vm.PickSaveFolderRequested = start => { startedAt = start; return null; };
        vm.ChooseSaveFolderCommand.Execute(null);
        Assert.Equal(@"C:\Old", startedAt);
        Assert.Equal(@"C:\Old", vm.SaveMessageFolder);

        vm.PickSaveFolderRequested = _ => @"E:\New";
        vm.ChooseSaveFolderCommand.Execute(null);
        Assert.Equal(@"E:\New", vm.SaveMessageFolder);
    }

    [Fact]
    public void SaveFormatOptions_OfferEveryFormat_AndReadAsTheirNames()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        Assert.Equal(MessageSaveFormats.All, vm.SaveFormatOptions.Select(o => o.Format));
        Assert.Equal("Email message (.eml)", vm.SaveFormatOptions[0].ToString());
    }

    // ── POP3 keeps every original (#728) ──────────────────────────────────────

    private static LocalStoreService NewStore()
    {
        var store = new LocalStoreService(new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-SavePop3-{Guid.NewGuid():N}")));
        store.Initialize();
        return store;
    }

    [Fact]
    public async Task Pop3_KeepsTheOriginal_OfAMessageWithNoAttachments()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var pop     = new Pop3MailService(store);

        // Until #728 only messages WITH attachments kept their bytes; a plain draft is the case that changed.
        var id = await pop.AppendDraftAsync(account, new ComposeModel
        {
            To = "someone@example.com", Subject = "Plain draft", Body = "no attachments here",
        }, replaceMessageId: null);

        using var ms = new MemoryStream();
        await pop.CopyOriginalMessageToAsync(account, "Drafts", id, ms, TestContext.Current.CancellationToken);
        ms.Position = 0;
        Assert.Equal("Plain draft", (await MimeMessage.LoadAsync(ms, TestContext.Current.CancellationToken)).Subject);
    }

    [Fact]
    public async Task Pop3_ReturnsStoredBytes_Unchanged()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        await store.UpsertDetailAsync(new MailMessageDetail { AccountId = account, FolderName = "Inbox", MessageId = "uidl-1" });
        await store.StoreMimeBytesAsync(account, "Inbox", "uidl-1", [1, 2, 3]);

        var pop = new Pop3MailService(store);
        using var ms = new MemoryStream();
        await pop.CopyOriginalMessageToAsync(account, "Inbox", "uidl-1", ms, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], ms.ToArray());
    }

    [Fact]
    public async Task Pop3_ALocalMessageWithNoOriginal_IsNeverRebuilt()
    {
        var store   = NewStore();
        var account = Guid.NewGuid();
        var localId = Pop3MailService.LocalIdPrefix + "x";
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            AccountId = account, FolderName = "Sent", MessageId = localId, PlainTextBody = "cached parts",
        });

        var pop = new Pop3MailService(store);
        var ex = await Assert.ThrowsAsync<MessageOriginalUnavailableException>(
            () => pop.CopyOriginalMessageToAsync(account, "Sent", localId, Stream.Null, TestContext.Current.CancellationToken));
        Assert.Contains("did not keep the original", ex.Message);
    }

    [Fact]
    public async Task ABackendWithNoWayToGetTheOriginal_SaysSo()
    {
        IMailService stub = new StubImapMailService();
        await Assert.ThrowsAsync<MessageOriginalUnavailableException>(
            () => stub.CopyOriginalMessageToAsync(Guid.NewGuid(), "INBOX", "1", Stream.Null, TestContext.Current.CancellationToken));
    }
}
