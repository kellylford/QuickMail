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
    public void Rows_InterleavesSectionHeadersWithDataRows()
    {
        var vm = new PropertiesViewModel("Test", [
            new("Headers", [new("From", "alice@example.com")]),
            new("Storage", [new("Folder", "INBOX")]),
        ]);

        // 2 section header rows + 2 data rows
        Assert.Equal(4, vm.Rows.Count);

        Assert.True(vm.Rows[0].IsHeader);
        Assert.Equal("Headers", vm.Rows[0].Label);

        Assert.False(vm.Rows[1].IsHeader);
        Assert.Equal("From",    vm.Rows[1].Label);

        Assert.True(vm.Rows[2].IsHeader);
        Assert.Equal("Storage", vm.Rows[2].Label);

        Assert.False(vm.Rows[3].IsHeader);
        Assert.Equal("Folder",  vm.Rows[3].Label);
    }

    [Fact]
    public void Rows_ContainsAllSectionsIncludingSubLists()
    {
        var vm = new PropertiesViewModel("Test", [
            new("Group",   [new("Name", "Team")]),
            new("Members", [new("Alice", "alice@example.com"), new("Bob", "bob@example.com")]),
        ]);

        // 2 section headers + 3 data rows
        Assert.Equal(5, vm.Rows.Count);
    }

    [Fact]
    public void Rows_SkipsEmptySections()
    {
        var vm = new PropertiesViewModel("Test", [
            new("A", [new("X", "1")]),
            new("B", []),
            new("C", [new("Y", "2")]),
        ]);

        // Section B is empty so no header or rows for it: 2 headers + 2 data = 4
        Assert.Equal(4, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.SectionName == "B");
    }

    [Fact]
    public void Rows_EmptyWhenNoSections()
    {
        var vm = new PropertiesViewModel("Test", []);
        Assert.Empty(vm.Rows);
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
        var row = new FlatRow("Headers", "From", "alice@example.com");

        vm.CopyRowCommand.Execute(row);

        var text = Clipboard.GetText();
        Assert.Equal("From: alice@example.com", text);
    }

    [Fact]
    public void CopyRow_HeaderRow_RaisesAnnouncementWithSectionName()
    {
        string? announced = null;
        var vm = Make();
        vm.AnnouncementRequested += (text, _) => announced = text;

        var thread = new Thread(() =>
            vm.CopyRowCommand.Execute(vm.Rows.First(r => r.IsHeader)));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.NotNull(announced);
        Assert.Contains("Headers", announced);
    }

    [Fact]
    public void CopyRow_RaisesAnnouncementRequested()
    {
        string? announced = null;
        var vm = Make();
        vm.AnnouncementRequested += (text, _) => announced = text;

        var row = new FlatRow("Headers", "Subject", "Hello World");

        var thread = new Thread(() =>
        {
            vm.CopyRowCommand.Execute(row);
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
        vm.CopyRowCommand.Execute(null);
    }

    [Fact]
    public void Title_IsSetFromConstructor()
    {
        var vm = new PropertiesViewModel("Folder Properties", []);
        Assert.Equal("Folder Properties", vm.Title);
    }
}
