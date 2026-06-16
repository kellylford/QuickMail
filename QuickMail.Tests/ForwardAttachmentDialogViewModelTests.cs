using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ForwardAttachmentDialogViewModelTests
{
    private static AttachmentModel MakeAttachment(string name, long size = 1024)
        => new() { FileName = name, FileSize = size, PartSpecifier = "1" };

    [Fact]
    public void AllCheckedByDefault()
    {
        var attachments = new List<AttachmentModel>
        {
            MakeAttachment("report.pdf"),
            MakeAttachment("photo.jpg"),
        };
        var vm = new ForwardAttachmentDialogViewModel(attachments);

        Assert.All(vm.Items, item => Assert.True(item.IsIncluded));
    }

    [Fact]
    public void IncludeSelectedReturnsCheckedSubset()
    {
        var a = MakeAttachment("a.pdf");
        var b = MakeAttachment("b.pdf");
        var vm = new ForwardAttachmentDialogViewModel(new List<AttachmentModel> { a, b });

        vm.Items[1].IsIncluded = false;
        vm.IncludeSelectedCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Single(vm.Result!);
        Assert.Equal("a.pdf", vm.Result![0].FileName);
    }

    [Fact]
    public void IncludeNoneReturnsEmptyList()
    {
        var vm = new ForwardAttachmentDialogViewModel(new List<AttachmentModel>
        {
            MakeAttachment("a.pdf"),
            MakeAttachment("b.pdf"),
        });

        vm.IncludeNoneCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Empty(vm.Result!);
    }

    [Fact]
    public void CancelReturnsNull()
    {
        var vm = new ForwardAttachmentDialogViewModel(new List<AttachmentModel>
        {
            MakeAttachment("a.pdf"),
        });

        vm.CancelCommand.Execute(null);

        Assert.Null(vm.Result);
    }

    [Fact]
    public void IncludeSelectedFiresCloseRequested()
    {
        var vm = new ForwardAttachmentDialogViewModel(new List<AttachmentModel> { MakeAttachment("a.pdf") });
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.IncludeSelectedCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CancelFiresCloseRequested()
    {
        var vm = new ForwardAttachmentDialogViewModel(new List<AttachmentModel> { MakeAttachment("a.pdf") });
        bool fired = false;
        vm.CloseRequested += () => fired = true;

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void AutomationLabelIncludesNameAndSize()
    {
        var att = new AttachmentModel { FileName = "report.pdf", FileSize = 1_048_576, PartSpecifier = "1" };
        var vm = new ForwardAttachmentDialogViewModel(new List<AttachmentModel> { att });

        Assert.Contains("report.pdf", vm.Items[0].AutomationLabel);
        Assert.Contains("1.0 MB", vm.Items[0].AutomationLabel);
    }
}
