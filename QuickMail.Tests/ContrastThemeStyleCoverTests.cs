// #1116: QuickMail did not start under a Windows contrast theme.
//
// Under a contrast theme ThemeService withdraws Styles/ThemedControls.xaml so WPF's own
// contrast-aware templates take over, which leaves Styles/AccessibleStyles.xaml as the only
// dictionary. A view that looks up something only ThemedControls defined then throws while it is
// parsed. That had been patched one resource at a time (DateTimeField, the menu fonts,
// RowSecondaryText, GridViewColumnHeader) until ListView took startup down.
//
// The rule these tests hold is about the two dictionaries, not about what any one view happens to
// use today: whatever ThemedControls offers a view by key, AccessibleStyles offers too.
// ContrastThemeXamlParseTests is the other half: it parses every window with ThemedControls out.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

public class ContrastThemeStyleCoverTests
{
    // An implicit style: <Style TargetType="X"> or <Style TargetType="{x:Type X}">, with no x:Key
    // anywhere in the tag. [^>] spans line breaks, so a tag written over several lines counts.
    private static readonly Regex StyleTag = new(@"<Style\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex TargetType = new(@"TargetType=""(?:\{x:Type\s+)?([A-Za-z0-9_:]+)\}?""", RegexOptions.Compiled);
    private static readonly Regex NamedKey = new(@"x:Key=""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);
    private static readonly Regex TypeLookup = new(@"StaticResource\s+(?:ResourceKey=)?\{x:Type\s+([A-Za-z0-9_:]+)\s*\}", RegexOptions.Compiled);

    private static HashSet<string> ImplicitTypes(string xaml) =>
        StyleTag.Matches(xaml).Select(m => m.Value)
            .Where(tag => !tag.Contains("x:Key", StringComparison.Ordinal))
            .Select(tag => TargetType.Match(tag))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string Style(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Styles", name + ".xaml"));

    [Fact]
    public void EveryTypeThemedControlsStyles_IsStyledInTheAlwaysLoadedDictionary()
    {
        var missing = ImplicitTypes(Style("ThemedControls")).Except(ImplicitTypes(Style("AccessibleStyles"))).OrderBy(t => t).ToList();

        Assert.True(missing.Count == 0,
            "Styles/ThemedControls.xaml styles these types and Styles/AccessibleStyles.xaml does not: " +
            string.Join(", ", missing) + ". ThemedControls is withdrawn under a Windows contrast theme, and a view " +
            "that then looks one of them up by type throws while it is parsed (#1116). Add an empty " +
            "<Style TargetType=\"…\"/> for each to AccessibleStyles.xaml.");
    }

    /// <summary>
    /// A named key (x:Key="ToolbarButton") a view uses must be in the always-loaded dictionary as
    /// well. Keys ThemedControls keeps to itself, used by nothing outside that file, are its own
    /// business.
    /// </summary>
    [Fact]
    public void EveryNamedKeyAViewTakesFromThemedControls_IsInTheAlwaysLoadedDictionary()
    {
        var root = RepoRoot();
        var themedOnly = NamedKey.Matches(Style("ThemedControls")).Select(m => m.Groups[1].Value)
            .Except(NamedKey.Matches(Style("AccessibleStyles")).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var used = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var text = File.ReadAllText(file);
            foreach (var key in themedOnly)
                if (Regex.IsMatch(text, @"(StaticResource|DynamicResource)\s+(ResourceKey=)?" + Regex.Escape(key) + @"\b")
                    || text.Contains("\"" + key + "\"", StringComparison.Ordinal))
                    used.Add($"{Path.GetFileName(file)}: {key}");
        }

        Assert.True(used.Count == 0,
            "These are defined only in Styles/ThemedControls.xaml, which is withdrawn under a Windows contrast " +
            "theme, and are used outside it: " + string.Join("; ", used.Distinct()) +
            ". Define them in Styles/AccessibleStyles.xaml too (#1116).");
    }

    /// <summary>
    /// A view may also look up a type ThemedControls does not style at all. That needs a style in
    /// the always-loaded dictionary just the same.
    /// </summary>
    [Fact]
    public void EveryTypeAViewLooksUpStatically_IsStyledInTheAlwaysLoadedDictionary()
    {
        var root = RepoRoot();
        var styled = ImplicitTypes(Style("AccessibleStyles"));
        var missing = new List<string>();
        foreach (var file in SourceFiles(root).Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in TypeLookup.Matches(File.ReadAllText(file)))
                if (!styled.Contains(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");

        Assert.True(missing.Count == 0,
            "These views look up a style by type that Styles/AccessibleStyles.xaml does not define: " +
            string.Join("; ", missing.Distinct()) + ". Add an empty <Style TargetType=\"…\"/> for each (#1116).");
    }

    /// <summary>
    /// The guards are only as good as their patterns. This pins them to the real files: the lookup
    /// that took startup down is seen, and so are styles in each form the files use.
    /// </summary>
    [Fact]
    public void ThePatternsSeeTheRealFiles()
    {
        var mainWindow = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml"));
        Assert.Contains(TypeLookup.Matches(mainWindow), m => m.Groups[1].Value == "ListView");

        var accessible = ImplicitTypes(Style("AccessibleStyles"));
        Assert.Contains("ListView", accessible);
        Assert.Contains("Button", accessible);
        Assert.Contains("controls:DateTimeField", accessible);   // written as TargetType="{x:Type …}"

        var themed = ImplicitTypes(Style("ThemedControls"));
        Assert.Contains("ListView", themed);
        Assert.True(themed.Count >= 20, $"Only {themed.Count} implicit styles were found in ThemedControls.xaml; the pattern has stopped matching.");

        Assert.Contains(NamedKey.Matches(Style("ThemedControls")), m => m.Groups[1].Value == "ToolbarButton");
    }

    [Theory]
    [InlineData(@"<Style TargetType=""ListView""/>")]
    [InlineData(@"<Style TargetType=""{x:Type ListView}"">")]
    [InlineData("<Style\n        TargetType=\"ListView\">")]
    public void AnImplicitStyleCounts(string xaml) => Assert.Contains("ListView", ImplicitTypes(xaml));

    /// <summary>A keyed style is not found by a lookup of the type, so it must not count.</summary>
    [Theory]
    [InlineData(@"<Style x:Key=""Special"" TargetType=""ListView"">")]
    [InlineData(@"<Style TargetType=""ListView"" x:Key=""Special"">")]
    [InlineData(@"<Style x:Key=""{x:Static ToolBar.ButtonStyleKey}"" TargetType=""Button"">")]
    public void AKeyedStyleDoesNotCount(string xaml) => Assert.Empty(ImplicitTypes(xaml));

    private static IEnumerable<string> SourceFiles(string root) =>
        new[] { "Views", "Controls", "Helpers", "ViewModels", "Services" }
            .Select(folder => Path.Combine(root, "QuickMail", folder))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}
