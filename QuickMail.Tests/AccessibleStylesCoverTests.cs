// #1116: QuickMail did not start under a Windows contrast theme.
//
// A view that writes BasedOn="{StaticResource {x:Type ListView}}" needs a resource dictionary to
// hold a style for ListView: that lookup does not fall through to WPF's theme style. Under a
// contrast theme ThemeService withdraws ThemedControls.xaml, leaving AccessibleStyles.xaml as the
// only dictionary, and a type styled only in ThemedControls made the window throw while parsing.
//
// The window tests cannot catch this: they load both dictionaries into the one Application the
// test process shares, and withdrawing ThemedControls there would break every other window test
// running beside it. So this reads the XAML as text and holds the rule directly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

public class AccessibleStylesCoverTests
{
    private static readonly Regex TypeLookup = new(@"StaticResource\s+\{x:Type\s+([A-Za-z0-9_:]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex ImplicitStyle = new(@"<Style\s+TargetType=""(?:\{x:Type\s+)?([A-Za-z0-9_:]+)\}?""(?![^>]*x:Key)", RegexOptions.Compiled);

    [Fact]
    public void EveryTypeAViewLooksUpStatically_HasAStyleInTheAlwaysLoadedDictionary()
    {
        var root = RepoRoot();
        var styled = ImplicitStyle.Matches(File.ReadAllText(Path.Combine(root, "QuickMail", "Styles", "AccessibleStyles.xaml")))
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var folder in new[] { "Views", "Controls" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "QuickMail", folder), "*.xaml", SearchOption.AllDirectories))
            {
                foreach (Match m in TypeLookup.Matches(File.ReadAllText(file)))
                {
                    var type = m.Groups[1].Value;
                    if (!styled.Contains(type))
                        missing.Add($"{Path.GetFileName(file)}: {type}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "These views look up a style by type that Styles/AccessibleStyles.xaml does not define. " +
            "Under a Windows contrast theme ThemedControls.xaml is withdrawn and the window throws while " +
            "parsing (#1116). Add an implicit style for the type to AccessibleStyles.xaml: " +
            string.Join("; ", missing.Distinct()));
    }

    /// <summary>
    /// The guard above is only as good as its two patterns. This pins them to the real files: the
    /// lookup that started #1116 is found, and so are styles the file is known to hold in both of
    /// the forms it uses.
    /// </summary>
    [Fact]
    public void ThePatternsSeeTheRealFiles()
    {
        var root = RepoRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "QuickMail", "Views", "MainWindow.xaml"));
        Assert.Contains(TypeLookup.Matches(mainWindow), m => m.Groups[1].Value == "ListView");

        var styled = ImplicitStyle.Matches(File.ReadAllText(Path.Combine(root, "QuickMail", "Styles", "AccessibleStyles.xaml")))
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("ListView", styled);
        Assert.Contains("Button", styled);
        Assert.Contains("controls:DateTimeField", styled);   // written as TargetType="{x:Type …}"
    }

    [Theory]
    [InlineData(@"<Style TargetType=""ListView""/>", "ListView")]
    [InlineData(@"<Style TargetType=""{x:Type ListView}"">", "ListView")]
    public void AnImplicitStyleCounts(string xaml, string type)
        => Assert.Equal(type, ImplicitStyle.Match(xaml).Groups[1].Value);

    /// <summary>A keyed style is not found by a lookup of the type, so it must not count.</summary>
    [Fact]
    public void AKeyedStyleDoesNotCount()
        => Assert.False(ImplicitStyle.IsMatch(@"<Style x:Key=""Special"" TargetType=""ListView"">")
                     || ImplicitStyle.IsMatch(@"<Style TargetType=""ListView"" x:Key=""Special"">"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}
