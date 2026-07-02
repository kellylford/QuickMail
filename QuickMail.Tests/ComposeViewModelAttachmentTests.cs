using System;
using System.IO;
using System.Threading.Tasks;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Covers the View-callback pattern for the Add Attachments file dialog
/// (CLAUDE.md MVVM rules: Win32 dialogs live in the View; the VM requests paths).
/// </summary>
public class ComposeViewModelAttachmentTests
{
    private static ComposeViewModel MakeVm() => new(
        new StubSmtpService(),
        new StubAccountService(),
        new StubCredentialService(),
        new StubImapMailService(),
        new TrackedTemplateService());

    [Fact]
    public async Task AddAttachments_UnwiredPicker_AddsNothing()
    {
        var vm = MakeVm();

        // Headless/test context: no picker wired — the command must no-op, not crash.
        await vm.AddAttachmentsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task AddAttachments_PickerCancelled_AddsNothing()
    {
        var vm = MakeVm();
        vm.OpenFilePathsRequested = () => null;

        await vm.AddAttachmentsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task AddAttachments_PickerReturnsFiles_AddsEachAsAttachment()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"QM-ATT-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "attachment content");
        try
        {
            var vm = MakeVm();
            vm.OpenFilePathsRequested = () => [tempFile];

            await vm.AddAttachmentsCommand.ExecuteAsync(null);

            var att = Assert.Single(vm.Attachments);
            Assert.Equal(Path.GetFileName(tempFile), att.FileName);
            Assert.True(att.FileSize > 0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AddAttachments_MissingFileFromPicker_IsSkipped()
    {
        var vm = MakeVm();
        vm.OpenFilePathsRequested = () => [Path.Combine(Path.GetTempPath(), "QM-does-not-exist.bin")];

        await vm.AddAttachmentsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Attachments);
    }
}
