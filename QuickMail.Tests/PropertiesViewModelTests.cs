using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// Deliberately NOT an STA/WpfTests class any more, and no longer touching the real clipboard.
//
// These tests used to drive the VM against the machine-wide Windows clipboard. Only one process may
// hold it at a time, so whenever anything else did — a clipboard manager, a remote session, or
// QuickMail itself running on the same machine — Clipboard.SetText threw CLIPBRD_E_CANT_OPEN. In
// the two tests that drove the VM from a hand-rolled STA thread that exception was unhandled on a
// foreground thread, which terminates the process: one contended clipboard took down the whole test
// host mid-run and left the suite hanging.
//
// PropertiesViewModel now takes an IClipboardService, so these assert on what the VM copied without
// involving the operating system, and need neither an STA apartment nor a thread.
public class PropertiesViewModelTests
{
    /// <summary>Records what the VM copied. No Windows clipboard, no apartment requirements.</summary>
    private sealed class FakeClipboard : IClipboardService
    {
        public string Text { get; private set; } = string.Empty;
        public bool SetText(string text) { Text = text; return true; }
        public string GetText() => Text;
    }

    private static PropertiesViewModel Make(
        IReadOnlyList<PropertySection>? sections = null,
        string? rawHeaders = null,
        IClipboardService? clipboard = null)
    {
        sections ??= [
            new("Headers", [new("From", "alice@example.com")]),
            new("Storage", [new("Folder", "INBOX")]),
        ];
        return new PropertiesViewModel("Test Properties", sections, rawHeaders, clipboard);
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

    [Fact]
    public void CopyAll_ProducesFormattedText()
    {
        var clipboard = new FakeClipboard();
        var vm = Make(clipboard: clipboard);

        vm.CopyAllCommand.Execute(null);

        var text = clipboard.Text;
        Assert.Contains("Test Properties", text);
        Assert.Contains("Headers", text);
        Assert.Contains("From: alice@example.com", text);
        Assert.Contains("Storage", text);
    }

    [Fact]
    public void CopyAll_IncludesRawHeadersWhenPresent()
    {
        const string rawHeaders = "From: alice@example.com\r\nSubject: Test";
        var clipboard = new FakeClipboard();
        var vm = Make(rawHeaders: rawHeaders, clipboard: clipboard);

        vm.CopyAllCommand.Execute(null);

        var text = clipboard.Text;
        Assert.Contains("Raw headers", text);
        Assert.Contains("From: alice@example.com", text);
    }

    [Fact]
    public void CopyAll_OmitsRawHeadersWhenAbsent()
    {
        var clipboard = new FakeClipboard();
        var vm = Make(clipboard: clipboard);

        vm.CopyAllCommand.Execute(null);

        Assert.DoesNotContain("Raw headers", clipboard.Text);
    }

    [Fact]
    public void CopyRow_PutsLabelColonValueOnClipboard()
    {
        var clipboard = new FakeClipboard();
        var vm = Make(clipboard: clipboard);
        var row = new FlatRow("Headers", "From", "alice@example.com");

        vm.CopyRowCommand.Execute(row);

        Assert.Equal("From: alice@example.com", clipboard.Text);
    }

    [Fact]
    public void CopyRow_HeaderRow_CopiesJustTheSectionName()
    {
        var clipboard = new FakeClipboard();
        var vm = Make(clipboard: clipboard);

        vm.CopyRowCommand.Execute(vm.Rows.First(r => r.IsHeader));

        Assert.Equal("Headers", clipboard.Text);
    }

    [Fact]
    public void CopyRow_HeaderRow_RaisesAnnouncementWithSectionName()
    {
        string? announced = null;
        var vm = Make(clipboard: new FakeClipboard());
        vm.AnnouncementRequested += (text, _) => announced = text;

        vm.CopyRowCommand.Execute(vm.Rows.First(r => r.IsHeader));

        Assert.NotNull(announced);
        Assert.Contains("Headers", announced);
    }

    [Fact]
    public void CopyRow_RaisesAnnouncementRequested()
    {
        string? announced = null;
        var vm = Make(clipboard: new FakeClipboard());
        vm.AnnouncementRequested += (text, _) => announced = text;

        var row = new FlatRow("Headers", "Subject", "Hello World");

        vm.CopyRowCommand.Execute(row);

        Assert.NotNull(announced);
        Assert.Contains("Subject", announced);
        Assert.Contains("Hello World", announced);
    }

    [Fact]
    public void CopyRow_NullItem_DoesNothing()
    {
        var clipboard = new FakeClipboard();
        var vm = Make(clipboard: clipboard);

        vm.CopyRowCommand.Execute(null);

        Assert.Equal(string.Empty, clipboard.Text);
    }

    [Fact]
    public void Title_IsSetFromConstructor()
    {
        var vm = new PropertiesViewModel("Folder Properties", []);
        Assert.Equal("Folder Properties", vm.Title);
    }
}
