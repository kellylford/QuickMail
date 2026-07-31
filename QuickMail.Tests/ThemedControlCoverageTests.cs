using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Every WPF control type used in view XAML must have an implicit style in
/// Styles/ThemedControls.xaml, or a reviewed exemption below. WPF's default
/// control chrome ignores QuickMail theming entirely — an unstyled control
/// renders light Aero visuals in a dark theme. The ToolBar shipped that way
/// for months and passed every sighted spot-check until an external review
/// caught near-invisible toolbar text (#421). This test turns that class of
/// omission into a build failure at the moment a new control type is used.
/// </summary>
public class ThemedControlCoverageTests
{
    /// <summary>
    /// Control types that deliberately carry no implicit style. Each entry
    /// states why the WPF default look is correct in every theme. Add here
    /// only after checking the control in a dark theme.
    /// </summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        ["ScrollViewer"] = "Transparent host: its ScrollBar parts carry the themed chrome.",
        ["StatusBarItem"] = "Chromeless content holder inside the themed StatusBar.",
        ["ItemsControl"] = "Chromeless presenter; items carry their own themed styles.",
        ["UserControl"] = "Chromeless container base for app controls.",
        ["Window"] = "Title bar is OS-drawn; every window binds its own Background "
                   + "to a Theme token (enforced by EveryWindow_BindsAThemedBackground).",
    };

    // Bare (unprefixed) element names: <Button ...>, <ToolBar/>. Prefixed
    // elements (<controls:DateTimeField>) are app types, out of scope here.
    private static readonly Regex ElementName = new("<(?<name>[A-Z][A-Za-z0-9]*)[\\s/>]", RegexOptions.Compiled);

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>Short name → WPF type, for every Control-derived type in PresentationFramework.</summary>
    private static Dictionary<string, Type> WpfControlTypes()
    {
        return typeof(Control).Assembly.GetTypes()
            .Where(t => typeof(Control).IsAssignableFrom(t) && t.IsPublic && !t.IsAbstract)
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private static HashSet<string> UsedControlTypeNames(string root)
    {
        var wpfTypes = WpfControlTypes();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in new[] { "Views", "Controls" })
        {
            var dir = Path.Combine(root, "QuickMail", sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.xaml"))
            foreach (Match m in ElementName.Matches(File.ReadAllText(file)))
            {
                var name = m.Groups["name"].Value;
                if (wpfTypes.ContainsKey(name))
                    used.Add(name);
            }
        }
        return used;
    }

    /// <summary>Type names carrying an implicit (keyless) style in ThemedControls.xaml.</summary>
    private static HashSet<string> ImplicitlyStyledTypeNames(string root)
    {
        var path = Path.Combine(root, "QuickMail", "Styles", "ThemedControls.xaml");
        var doc = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var styled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var style in doc.Root!.Elements().Where(e => e.Name.LocalName == "Style"))
        {
            if (style.Attribute(x + "Key") != null) continue; // keyed = opt-in, not coverage
            var target = style.Attribute("TargetType")?.Value;
            if (target is null) continue;
            // "Button" or "{x:Type controls:Foo}" → bare type name.
            var name = target.Trim('{', '}').Split(':').Last().Replace("x:Type", "").Trim();
            styled.Add(name);
        }
        return styled;
    }

    [Fact]
    public void EveryControlTypeInViews_HasThemedStyleOrReviewedExemption()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var used = UsedControlTypeNames(root!);
        var styled = ImplicitlyStyledTypeNames(root!);

        var uncovered = used
            .Where(name => !styled.Contains(name) && !Exemptions.ContainsKey(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "Control types used in view XAML with neither an implicit style in ThemedControls.xaml "
            + "nor a reviewed exemption (these render default light Aero chrome in every theme):\n"
            + string.Join("\n", uncovered)
            + "\nAdd a themed implicit style, or add an exemption entry with a reason after checking the control in a dark theme.");
    }

    /// <summary>
    /// Backs the Window exemption above: a Window that forgets to bind its
    /// Background renders default white behind every dark theme.
    /// </summary>
    [Fact]
    public void EveryWindow_BindsAThemedBackground()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "QuickMail", "Views"), "*.xaml"))
        {
            var doc = XDocument.Load(file);
            if (doc.Root?.Name.LocalName != "Window") continue;
            var background = doc.Root.Attribute("Background")?.Value;
            // Borderless overlays (command palette, tab list) are transparent at
            // the root by design; their inner Border carries the themed surface.
            var isOverlay = background == "Transparent"
                && doc.Root.Attribute("AllowsTransparency")?.Value == "True"
                && doc.Root.Descendants().Any(e =>
                    e.Attribute("Background")?.Value.Contains("Theme.") == true);
            if (isOverlay) continue;
            if (background is null || !background.Contains("Theme."))
                missing.Add(Path.GetFileName(file));
        }

        Assert.True(missing.Count == 0,
            "Windows without Background=\"{DynamicResource Theme.*}\" on the root element "
            + "(these render default white in dark themes):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void ExemptionTable_HasNoStaleEntries()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var used = UsedControlTypeNames(root!);
        var styled = ImplicitlyStyledTypeNames(root!);

        var stale = Exemptions.Keys
            .Where(name => styled.Contains(name) || !used.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "Exemption entries that are now styled or no longer used — remove them so the table stays honest:\n"
            + string.Join("\n", stale));
    }
}
