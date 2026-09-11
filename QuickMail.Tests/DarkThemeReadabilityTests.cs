using System;
using System.IO;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Two dark-theme contrast failures measured by the UI probe (#689, #690). Both came from a style that took
/// its colour from WPF's defaults instead of the theme, which looks right in the light themes and so
/// passes every sighted spot-check there.
/// </summary>
public class DarkThemeReadabilityTests
{
    [Fact]
    public void StatusBarButtons_TakeTheThemesTextColour() // #689
    {
        // A Button's default style sets the system control-text colour, which beats the colour the themed
        // StatusBar passes down, so the rule summary and update notice were black on the dark status bar.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Styles", "AccessibleStyles.xaml"));
        var start = xaml.IndexOf("x:Key=\"StatusBarButtonStyle\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "StatusBarButtonStyle is gone.");
        var style = xaml[start..xaml.IndexOf("</Style>", start, StringComparison.Ordinal)];

        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource Theme.TextPrimary}\"/>",
                        style, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesList_BuildsOnTheThemedRowStyle() // #690
    {
        // Without BasedOn, the item-container style replaces the themed ListBoxItem style, and a selected
        // row gets WPF's own highlight under the theme's light text: about 1.1:1 in the dark theme.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "UnifiedRulesWindow.xaml"));

        Assert.Contains("<Style TargetType=\"ListBoxItem\" BasedOn=\"{StaticResource {x:Type ListBoxItem}}\">",
                        xaml, StringComparison.Ordinal);
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
