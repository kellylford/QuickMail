// A read-only TextBox without IsReadOnlyCaretVisible has no insertion point to move, so a screen
// reader arrowing through it stays on the first line and the rest cannot be reached. Fixed for six
// boxes in #558 and found again in the settings-transfer windows (#840); this reads every window's
// XAML so a new read-only box that can hold more than one line cannot ship without the caret.
//
// Deliberately NOT in the "WpfTests" collection — nothing here loads a window; it is XML parsing.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

public class ReadOnlyTextBoxCaretTests
{
    [Fact]
    public void MultiLineReadOnlyTextBoxes_ShowTheirCaret()
    {
        var root = RepoRoot();
        var files = new[] { "Views", "Controls" }
            .Select(d => Path.Combine(root, "QuickMail", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.xaml", SearchOption.AllDirectories))
            .ToList();
        Assert.NotEmpty(files);

        var missing = files
            .SelectMany(file => XDocument.Load(file, LoadOptions.SetLineInfo).Descendants()
                .Where(e => e.Name.LocalName == "TextBox"
                         && Is(e, "IsReadOnly", "True")
                         && (Is(e, "TextWrapping", "Wrap") || Is(e, "AcceptsReturn", "True"))
                         && !Is(e, "IsReadOnlyCaretVisible", "True"))
                .Select(e => $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)e).LineNumber} " +
                             $"({(string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? (string?)e.Attribute("AutomationProperties.Name") ?? "unnamed"})"))
            .ToList();

        Assert.True(missing.Count == 0,
            "These read-only TextBoxes can hold more than one line but have no IsReadOnlyCaretVisible=\"True\", " +
            "so a screen reader can read only their first line (#558, #840): " + string.Join(", ", missing));
    }

    [Fact]
    public void ExportSettingsSummary_ShowsItsCaret()
    {
        // The box #840 was reported against, named so that renaming or restructuring it cannot
        // quietly drop it out of the general scan above.
        var xaml = XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", "ExportSettingsWindow.xaml"));
        var box = xaml.Descendants().SingleOrDefault(e => e.Name.LocalName == "TextBox"
            && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SummaryBox");
        Assert.NotNull(box);
        Assert.True(Is(box, "IsReadOnlyCaretVisible", "True"));
    }

    private static bool Is(XElement element, string attribute, string value) =>
        string.Equals((string?)element.Attribute(attribute), value, StringComparison.OrdinalIgnoreCase);

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
