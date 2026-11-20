// No tooltips on message rows (issue #638).
//
// The subject cell of the flat message list carried ToolTip="{Binding Subject}". Alt+Down and
// Alt+Up jump to the next or previous unread message, which can scroll the list a long way. When
// the list scrolls under a resting mouse pointer, whichever row lands beneath the pointer opens its
// tooltip, and screen readers speak it: the user heard the subject of some unrelated message before
// the unread one they asked for. The tooltip added nothing for anyone else either — the subject is
// already the visible cell text and part of the row's spoken name, and the reading pane shows it in
// full. Same reasoning as the account list's tooltip, removed in e5ac5dac.
//
// These guards read the XAML source, so they cover every presentation of a message row: the flat
// list, the conversations tree, and the From and To group trees, including the row styles those
// trees declare in their own resources, plus any app-wide ListViewItem/TreeViewItem style.
//
// Issue #825 extends the same guard to the attachment lists and the tab strip; see below.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

public class MessageListToolTipTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Every element that presents message rows in the main window.</summary>
    private static readonly string[] MessageLists =
        ["MessageList", "ConversationTree", "SenderGroupTree", "ToGroupTree"];

    [Fact]
    public void EveryMessageList_IsFoundInMainWindowXaml()
    {
        var doc = Load("Views", "MainWindow.xaml");
        foreach (var name in MessageLists)
            Assert.NotNull(Named(doc, name));
    }

    [Fact]
    public void NoMessageRow_CarriesAToolTip()
    {
        var doc = Load("Views", "MainWindow.xaml");
        var problems = new List<string>();

        foreach (var name in MessageLists)
        {
            var list = Named(doc, name);
            if (list is null) continue; // reported by EveryMessageList_IsFoundInMainWindowXaml

            foreach (var element in RowContent(list))
                problems.AddRange(ToolTipsOn(element).Select(t => $"MainWindow.xaml/{name}: {t}"));
        }

        Assert.True(problems.Count == 0,
            "A tooltip on a message row opens whenever the list scrolls under a resting mouse "
          + "pointer — Alt+Down/Alt+Up to the next unread message does that — and screen readers "
          + "speak it, naming the wrong message (issue #638):\n  " + string.Join("\n  ", problems));
    }

    // ── Other lists whose rows change under a resting pointer (issue #825) ──────────────────────
    //
    // The attachment lists refill when the user moves to another message (Alt+Down in the reading
    // pane) or adds a file in compose, and the tab strip's headers shift whenever a tab opens or
    // closes (and the Open Tabs list loses a row when one is closed from it). A row tooltip in any
    // of them opens under a resting pointer just as #638's did. The attachment rows used to keep
    // the file size only in a tooltip; it is now visible text.

    /// <summary>(XAML file under Views/, x:Name of the list) for every list guarded for #825.</summary>
    public static readonly TheoryData<string, string> ShiftingRowLists = new()
    {
        { "MainWindow.xaml",    "ReadingPaneAttachmentList" },
        { "MessageWindow.xaml", "AttachmentList" },
        { "ComposeWindow.xaml", "AttachmentList" },
        { "MainWindow.xaml",    "TabStrip" },
        { "TabListWindow.xaml", "TabList" },
    };

    [Theory]
    [MemberData(nameof(ShiftingRowLists))]
    public void ShiftingRowList_CarriesNoRowToolTip(string file, string name)
    {
        var list = Named(Load("Views", file), name);
        Assert.True(list is not null, $"{file}: no element named {name} — renamed or removed?");

        var problems = RowContent(list!)
            .SelectMany(ToolTipsOn)
            .ToList();

        Assert.True(problems.Count == 0,
            $"{file}/{name}: a row tooltip opens when the rows change under a resting mouse pointer, "
          + "and screen readers speak it (issue #825):\n  " + string.Join("\n  ", problems));
    }

    public static readonly TheoryData<string, string> AttachmentLists = new()
    {
        { "MainWindow.xaml",    "ReadingPaneAttachmentList" },
        { "MessageWindow.xaml", "AttachmentList" },
        { "ComposeWindow.xaml", "AttachmentList" },
    };

    [Theory]
    [MemberData(nameof(AttachmentLists))]
    public void AttachmentRow_ShowsSizeAsTextAndKeepsItsNameOnTheContainer(string file, string name)
    {
        var list = Named(Load("Views", file), name);
        Assert.True(list is not null, $"{file}: no element named {name} — renamed or removed?");

        // The size is visible to sighted users now that the tooltip is gone.
        var template = list!.Descendants().Single(e => e.Name.LocalName == "ListBox.ItemTemplate");
        var texts = template.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => (string?)e.Attribute("Text") ?? "")
            .ToList();
        Assert.Contains("{Binding FileSizeDisplay}", texts);
        Assert.Contains("{Binding FileName}", texts);

        // A long file name trims rather than pushing the size off the row: the size is docked
        // right, the name ellipsizes, and the list does not scroll sideways (a horizontal
        // ScrollViewer measures rows at infinite width, so nothing would ever trim).
        var size = TextBlockBound(template, "{Binding FileSizeDisplay}");
        var fileName = TextBlockBound(template, "{Binding FileName}");
        Assert.Equal("Right", (string?)size.Attribute("DockPanel.Dock"));
        Assert.Equal("CharacterEllipsis", (string?)fileName.Attribute("TextTrimming"));
        Assert.Equal("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));

        // Its themed secondary style: Theme.TextSecondary while unselected, the row's own
        // foreground when selected. BuiltInThemeTests holds those colours to 4.5:1 against the
        // theme's backgrounds, which is only true while the list draws a themed background — so
        // a local ListBox.Style must be BasedOn the themed one. Without BasedOn it replaces the
        // theme's style and the list falls back to stock white, where the dark theme's secondary
        // text measured about 2:1 (compose's list did exactly that).
        Assert.Equal("{StaticResource RowSecondaryText}", (string?)size.Attribute("Style"));
        foreach (var localStyle in list.Elements().Where(e => e.Name.LocalName == "ListBox.Style")
                                       .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Style")))
            Assert.Equal("{StaticResource {x:Type ListBox}}", (string?)localStyle.Attribute("BasedOn"));

        // The spoken name stays on the container, as "name, size" — never on a template child,
        // which would not be read (see AddressBookRowSpeechTests).
        Assert.DoesNotContain(template.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name"));
        var containerStyle = list.Descendants().Single(e => e.Name.LocalName == "ListBox.ItemContainerStyle");
        Assert.Contains(containerStyle.Descendants(), e => e.Name.LocalName == "Setter"
            && (string?)e.Attribute("Property") == "AutomationProperties.Name"
            && (string?)e.Attribute("Value") == "{Binding AccessibleName}");
    }

    [Fact]
    public void NoAppWideRowStyle_SetsAToolTip()
    {
        var problems = new List<string>();
        var stylesDir = Path.Combine(RepoRoot(), "QuickMail", "Styles");

        foreach (var file in Directory.EnumerateFiles(stylesDir, "*.xaml"))
        {
            var doc = XDocument.Load(file);
            foreach (var style in doc.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var target = ((string?)style.Attribute("TargetType") ?? "")
                             .Replace("{x:Type ", "").TrimEnd('}').Trim();
                if (target is not ("ListViewItem" or "TreeViewItem")) continue;

                foreach (var element in style.DescendantsAndSelf())
                    problems.AddRange(ToolTipsOn(element)
                        .Select(t => $"Styles/{Path.GetFileName(file)} ({target} style): {t}"));
            }
        }

        Assert.True(problems.Count == 0,
            "An app-wide row style sets a tooltip, which every message row would inherit "
          + "(issue #638):\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The list's subtree minus its GridView column headers and its context menus. A column header
    /// tooltip opens only when the pointer rests on the header row, which does not scroll, and a
    /// context menu's items are not rows, so neither is this bug. Only GridViewColumn.Header is
    /// excluded among headers: any other header (an Expander's, say) inside a row template
    /// scrolls with the rows and is still checked.
    /// </summary>
    private static IEnumerable<XElement> RowContent(XElement list) =>
        list.DescendantsAndSelf().Where(e => !e.AncestorsAndSelf().Any(a =>
            a.Name.LocalName == "GridViewColumn.Header"
            || a.Name.LocalName == "ContextMenu"
            || a.Name.LocalName.EndsWith(".ContextMenu", StringComparison.Ordinal)));

    private static XElement TextBlockBound(XElement root, string binding) =>
        root.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == binding);

    private static IEnumerable<string> ToolTipsOn(XElement element)
    {
        foreach (var attr in element.Attributes())
        {
            var local = attr.Name.LocalName;
            if (local == "ToolTip" || (local.StartsWith("ToolTipService.", StringComparison.Ordinal)
                                       && local != "ToolTipService.IsEnabled"))
                yield return $"<{element.Name.LocalName}> has {local}=\"{attr.Value}\"";
        }

        if (element.Name.LocalName.EndsWith(".ToolTip", StringComparison.Ordinal))
            yield return $"<{element.Name.LocalName}> property element";

        if (element.Name.LocalName == "Setter"
            && ((string?)element.Attribute("Property") ?? "") is "ToolTip" or "FrameworkElement.ToolTip")
            yield return $"<Setter Property=\"ToolTip\" Value=\"{(string?)element.Attribute("Value")}\">";
    }

    private static XElement? Named(XDocument doc, string name) =>
        doc.Descendants().FirstOrDefault(e => (string?)e.Attribute(X + "Name") == name);

    private static XDocument Load(params string[] relative) =>
        XDocument.Load(Path.Combine([RepoRoot(), "QuickMail", .. relative]));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
