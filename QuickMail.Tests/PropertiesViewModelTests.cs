using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class PropertiesViewModelTests
{
    private static PropertiesViewModel Make(
        IReadOnlyList<PropertySection>? sections = null,
        string? rawHeaders = null)
    {
        sections ??= [
            new("Headers", [new("From", "alice@example.com")]),
            new("Storage", [new("Folder", "INBOX")]),
        ];
        return new PropertiesViewModel("Test Properties", sections, rawHeaders);
    }

    [Fact]
    public void FieldSections_ExcludesMembersSection()
    {
        var vm = new PropertiesViewModel("Test", [
            new("Group",   [new("Name", "Team")]),
            new("Members", [new("Alice", "alice@example.com")]),
        ]);

        Assert.DoesNotContain(vm.FieldSections, s => s.Header == "Members");
        Assert.Contains(vm.FieldSections, s => s.Header == "Group");
    }

    [Fact]
    public void SubListSections_IncludesMembersSection()
    {
        var vm = new PropertiesViewModel("Test", [
            new("Group",   [new("Name", "Team")]),
            new("Members", [new("Alice", "alice@example.com")]),
        ]);

        Assert.Contains(vm.SubListSections, s => s.Header == "Members");
    }

    [Fact]
    public void FieldSections_ExcludesAttachmentsSection()
    {
        var vm = new PropertiesViewModel("Test", [
            new("Headers",     [new("From", "alice@example.com")]),
            new("Attachments", [new("report.pdf", "245 KB")]),
        ]);

        Assert.DoesNotContain(vm.FieldSections, s => s.Header == "Attachments");
        Assert.Contains(vm.SubListSections, s => s.Header == "Attachments");
    }

    [Fact]
    public void SubListSections_IsCasInsensitiveForSectionNames()
    {
        var vm = new PropertiesViewModel("Test", [
            new("members", [new("Alice", "alice@example.com")]),
        ]);

        Assert.Contains(vm.SubListSections, s => s.Header == "members");
        Assert.Empty(vm.FieldSections);
    }

    [Fact]
    public void RawHeaders_IsNull_WhenNotProvided()
    {
        var vm = Make();
        Assert.Null(vm.RawHeaders);
    }

    [Fact]
    public void RawHeaders_IsSet_WhenProvided()
    {
        var vm = Make(rawHeaders: "From: alice@example.com\r\nTo: bob@example.com");
        Assert.NotNull(vm.RawHeaders);
        Assert.Contains("From:", vm.RawHeaders);
    }

    [StaFact]
    public void CopyAll_ProducesFormattedText()
    {
        var vm = Make();
        Clipboard.SetText(string.Empty);

        vm.CopyAllCommand.Execute(null);

        var text = Clipboard.GetText();
        Assert.Contains("Test Properties", text);
        Assert.Contains("Headers", text);
        Assert.Contains("From: alice@example.com", text);
        Assert.Contains("Storage", text);
    }

    [StaFact]
    public void CopyAll_IncludesRawHeadersWhenPresent()
    {
        const string rawHeaders = "From: alice@example.com\r\nSubject: Test";
        var vm = Make(rawHeaders: rawHeaders);

        vm.CopyAllCommand.Execute(null);

        var text = Clipboard.GetText();
        Assert.Contains("Raw headers", text);
        Assert.Contains("From: alice@example.com", text);
    }

    [StaFact]
    public void CopyRow_PutsLabelColonValueOnClipboard()
    {
        var vm = Make();
        var item = new PropertyItem("From", "alice@example.com");

        vm.CopyRowCommand.Execute(item);

        var text = Clipboard.GetText();
        Assert.Equal("From: alice@example.com", text);
    }

    [Fact]
    public void CopyRow_RaisesAnnouncementRequested()
    {
        // Clipboard.SetText requires STA, but we can still test the event fires.
        // Use a STA thread.
        string? announced = null;
        var vm = Make();
        vm.AnnouncementRequested += (text, _) => announced = text;

        var item = new PropertyItem("Subject", "Hello World");

        // Run on STA thread since CopyRow calls Clipboard.SetText.
        var thread = new Thread(() =>
        {
            vm.CopyRowCommand.Execute(item);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.NotNull(announced);
        Assert.Contains("Subject", announced);
        Assert.Contains("Hello World", announced);
    }

    [Fact]
    public void CopyRow_NullItem_DoesNothing()
    {
        var vm = Make();
        // Should not throw
        vm.CopyRowCommand.Execute(null);
    }

    [Fact]
    public void Title_IsSetFromConstructor()
    {
        var vm = new PropertiesViewModel("Folder Properties", []);
        Assert.Equal("Folder Properties", vm.Title);
    }
}
