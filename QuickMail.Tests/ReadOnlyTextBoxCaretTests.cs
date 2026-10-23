// A read-only TextBox without IsReadOnlyCaretVisible has no insertion point to move, so a screen
// reader arrowing through it stays on the first line and the rest cannot be reached. Fixed for six
// boxes in #558 and found again in the settings-transfer windows (#840); this reads every window's
// XAML so a new read-only box that can hold more than one line cannot ship without the caret.
//
// Deliberately NOT in the "WpfTests" collection — nothing here loads a window; it is XML parsing.

using System;
using System.Collections.Generic;
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

        // Known blind spots: property-element syntax (<TextBox.IsReadOnly>), values set by binding,
        // styles from another file, and bound text with newlines in a box that neither wraps nor
        // accepts returns. None exists today; same-file keyed styles (ViewManagerWindow's
        // ReadonlyField) are resolved below.
        var missing = files
            .SelectMany(file =>
            {
                var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
                var styles = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Style" && e.Attribute(XName.Get("Key", XamlNs)) != null)
                    .GroupBy(e => (string)e.Attribute(XName.Get("Key", XamlNs))!)
                    .ToDictionary(g => g.Key, g => g.First());
                return doc.Descendants()
                    .Where(e => e.Name.LocalName == "TextBox"
                             && Is(e, styles, "IsReadOnly", "True")
                             && (Is(e, styles, "TextWrapping", "Wrap") || Is(e, styles, "AcceptsReturn", "True"))
                             && !Is(e, styles, "IsReadOnlyCaretVisible", "True"))
                    .Select(e => $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)e).LineNumber} " +
                                 $"({(string?)e.Attribute(XName.Get("Name", XamlNs)) ?? (string?)e.Attribute("AutomationProperties.Name") ?? "unnamed"})");
            })
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

    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static bool Is(XElement element, string attribute, string value) =>
        string.Equals((string?)element.Attribute(attribute), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The element's own attribute, or failing that a Setter in its same-file keyed Style.</summary>
    private static bool Is(XElement element, IReadOnlyDictionary<string, XElement> styles, string property, string value)
    {
        if (element.Attribute(property) != null) return Is(element, property, value);
        var styleRef = (string?)element.Attribute("Style");
        if (styleRef == null) return false;
        var key = styleRef.Replace("{StaticResource", "").Replace("{DynamicResource", "").Trim(' ', '}');
        return styles.TryGetValue(key, out var style) && style.Descendants()
            .Any(s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == property
                   && string.Equals((string?)s.Attribute("Value"), value, StringComparison.OrdinalIgnoreCase));
    }

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
