using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Regression guards: no new hardcoded colors or numeric font sizes in view XAML.
/// Every color must come from a Theme.* token so all six themes and the High
/// Contrast passthrough stay correct; every font size must come from a
/// Theme.FontSize* token so the text-scale setting reaches it.
/// </summary>
public class ThemeRegressionGuardTests
{
    /// <summary>Deliberate exceptions, reviewed case by case. Keep this list short.</summary>
    private static readonly Dictionary<string, string[]> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Translucent backdrop scrim — intentionally not themed.
        ["TutorialOverlay.xaml"] = ["#CC1E1E1E"],
        // Flag color swatches are user data, not chrome; the white focus ring and
        // check ring must stay visible on every user-chosen swatch color.
        ["FlagManagerWindow.xaml"] = ["White", "#80FFFFFF"],
        // Tiny unread-count badge overlay: fixed geometry, deliberately unscaled.
        ["MainWindow.xaml"] = ["FontSize=\"9\""],
    };

    // (?<![A-Za-z]) keeps e.g. DockPanel's LastChildFill from matching as Fill.
    private static readonly Regex BrushAttribute = new(
        "(?<![A-Za-z])(?:Foreground|Background|BorderBrush|Stroke|Fill)=\"(?<value>[^\"{][^\"]*)\"",
        RegexOptions.Compiled);

    private static readonly Regex NumericFontSize = new(
        "FontSize=\"(?<value>[0-9][0-9.]*)\"",
        RegexOptions.Compiled);

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        return null;
    }

    private static IEnumerable<string> ViewXamlFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "QuickMail", "Views"), "*.xaml")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "QuickMail", "Controls"), "*.xaml"));

    private static bool IsAllowed(string fileName, string offendingText) =>
        Allowlist.TryGetValue(fileName, out var allowed)
        && allowed.Any(a => offendingText.Contains(a, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ViewXaml_HasNoHardcodedBrushColors()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var violations = new List<string>();
        foreach (var file in ViewXamlFiles(root!))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in BrushAttribute.Matches(lines[i]))
                {
                    var value = m.Groups["value"].Value;
                    if (value == "Transparent") continue;      // structural, not a color choice
                    if (IsAllowed(name, m.Value) || IsAllowed(name, value)) continue;
                    violations.Add($"{name}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Hardcoded brush colors in view XAML — use a Theme.* token (or extend the reviewed allowlist):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void ViewXaml_HasNoNumericFontSizes()
    {
        var root = FindRepoRoot();
        Assert.False(root is null, "Repo source tree not found from test base directory.");

        var violations = new List<string>();
        foreach (var file in ViewXamlFiles(root!))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in NumericFontSize.Matches(lines[i]))
                {
                    if (IsAllowed(name, m.Value)) continue;
                    violations.Add($"{name}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Numeric FontSize in view XAML — use Theme.FontSizeSmall/Base/Large/Header so text scaling applies:\n"
            + string.Join("\n", violations));
    }
}
