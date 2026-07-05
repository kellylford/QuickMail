using System;
using System.IO;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class ThemeDescriberTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"QM-DescriberTests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ThemeDefinition Resolved(string id)
    {
        var store = new ThemeStore(new ProfileContext(_dir));
        var theme = store.LoadBuiltIns().First(t => t.Id == id);
        var baseTheme = store.LoadBuiltIns().First(t => t.Id == (theme.Base == "dark" ? "dark" : "quill"));
        return theme.ResolveAgainst(baseTheme);
    }

    [Theory]
    [InlineData("#FFFFFF", "white")]
    [InlineData("#000000", "black")]
    [InlineData("#3D5A80", "blue")]     // Quill accent
    [InlineData("#B3261E", "red")]      // Quill error
    [InlineData("#2E6B3E", "green")]    // Quill success
    [InlineData("#8A5A00", "orange")]   // Quill warning (dark amber)
    public void DescribeColor_NamesTheHueFamily(string hex, string expectedWord)
    {
        var text = ThemeDescriber.DescribeColor(hex);
        Assert.Contains(expectedWord, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ThemeDefinition.NormalizeHex(hex), text);   // the hex is always shown
    }

    [Fact]
    public void DescribeColor_WarmOffWhite_ReadsAsOffWhite()
    {
        // Quill window background is a very light, slightly warm neutral.
        var text = ThemeDescriber.DescribeColor("#FBFAF8");
        Assert.Contains("off-white", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_Quill_CoversFontsAndEveryTokenGroup_WithUsage()
    {
        var text = ThemeDescriber.Describe(Resolved("quill"));

        Assert.Contains("Quill", text);
        Assert.Contains("light theme", text);
        Assert.Contains("Fonts", text);
        Assert.Contains("Segoe UI", text);

        // Section headers present.
        Assert.Contains("Backgrounds and borders", text);
        Assert.Contains("Text", text);
        Assert.Contains("Accent and selection", text);
        Assert.Contains("Status colors", text);

        // A token line carries both a color and a "where it is used" clause.
        Assert.Contains("Window background:", text);
        Assert.Contains("the main window", text);
        Assert.Contains("Hyperlink:", text);

        // Every one of the 26 tokens is described (each line has an em dash usage clause).
        var usageLines = text.Split('\n').Count(l => l.Contains(" — "));
        Assert.True(usageLines >= 26, $"expected at least 26 described tokens, got {usageLines}");
    }

    [Fact]
    public void Describe_System_SaysItFollowsTheOs()
    {
        var text = ThemeDescriber.Describe(Resolved("quill"), isSystem: true);
        Assert.Contains("follows the Windows light and dark setting", text);
    }
}
