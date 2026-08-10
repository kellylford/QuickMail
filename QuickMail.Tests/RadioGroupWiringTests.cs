// The RadioGroupNavigation behaviour (issue #441) is opt-in per group, so it is only useful while
// the XAML still asks for it. These read the real windows: deleting the attached property from
// every container would otherwise leave the whole suite green while the bug came straight back.
//
// Deliberately NOT in the "WpfTests" collection — nothing here loads a window or touches WPF; it
// is XML parsing. The behaviour tests live in RadioGroupNavigationTests.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

public class RadioGroupWiringTests
{
    public static TheoryData<string, string> RadioGroupSites() => new()
    {
        // window XAML, GroupName of a group that must select on arrow
        { "SettingsDialog.xaml",    "MessageOpenMode" },
        { "SettingsDialog.xaml",    "ListDensity" },
        { "SettingsDialog.xaml",    "LogFormat" },
        { "SettingsDialog.xaml",    "SpellingSuggestionsVerbosity" },
        { "SettingsDialog.xaml",    "StartupSyncScope" },
        { "EventEditorWindow.xaml", "EditScope" },
    };

    [Theory]
    [MemberData(nameof(RadioGroupSites))]
    public void RadioGroup_ContainerDeclaresSelectionFollowsFocus(string xamlFile, string groupName)
    {
        var xaml = XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", xamlFile));

        var buttons = xaml.Descendants()
            .Where(e => e.Name.LocalName == "RadioButton"
                     && (string?)e.Attribute("GroupName") == groupName)
            .ToList();
        Assert.True(buttons.Count >= 2,
            $"{xamlFile} has fewer than two radio buttons in group '{groupName}' — did it move or get renamed?");

        foreach (var button in buttons)
        {
            // An attached property parses as LocalName "RadioGroupNavigation.SelectionFollowsFocus"
            // in the clr-namespace the helpers prefix maps to.
            var declared = button.Ancestors().Any(a => a.Attributes().Any(attr =>
                attr.Name.LocalName == "RadioGroupNavigation.SelectionFollowsFocus"
                && attr.Name.NamespaceName.Contains("QuickMail.Helpers", StringComparison.Ordinal)
                && string.Equals((string)attr, "True", StringComparison.OrdinalIgnoreCase)));

            Assert.True(declared,
                $"'{(string?)button.Attribute("Content")}' in group '{groupName}' ({xamlFile}) is not inside a " +
                 "container with RadioGroupNavigation.SelectionFollowsFocus=\"True\" — arrowing would move " +
                 "focus without choosing the option (issue #441).");
        }
    }

    [Fact]
    public void SettingsRadioButtons_DoNotAnnounceThemselves()
    {
        // A radio button that speaks its own name on check is QuickMail talking over the platform:
        // the choice is already reported, and how much of it the user hears is a decision they have
        // made in their own software. A Checked handler was added in f71f86f to compensate for
        // choices going unannounced; the real fault was that arrowing moved focus without selecting
        // anything (#441), and that is fixed. Do not reintroduce one here.
        var xaml = XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", "SettingsDialog.xaml"));

        var announcing = xaml.Descendants()
            .Where(e => e.Name.LocalName == "RadioButton" && e.Attribute("Checked") != null)
            .Select(e => $"{(string?)e.Attribute("Content") ?? "(unnamed)"} → {(string?)e.Attribute("Checked")}")
            .ToList();

        Assert.True(announcing.Count == 0,
            "These SettingsDialog radio buttons have a Checked handler, which speaks over the " +
            $"platform's own reporting of the choice: {string.Join(", ", announcing)}");
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
