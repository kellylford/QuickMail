using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.Theming;
using Xunit;

namespace QuickMail.Tests;

public class ThemeDefinitionTests
{
    private const string MinimalJson = """
        {
          "formatVersion": 1,
          "id": "test-theme",
          "name": "Test Theme",
          "base": "light",
          "colors": { "accent": "#8F4531", "windowBackground": "#FBF7F2" },
          "typography": { "fontFamily": "Segoe UI", "monoFontFamily": "Cascadia Code", "baseFontSize": 13 }
        }
        """;

    [Fact]
    public void Parse_MinimalTheme_ReadsAllFields()
    {
        var theme = ThemeDefinition.Parse(MinimalJson);

        Assert.Equal("test-theme", theme.Id);
        Assert.Equal("Test Theme", theme.Name);
        Assert.Equal("light", theme.Base);
        Assert.Equal("#8F4531", theme.Colors["accent"]);
        Assert.Equal("#FBF7F2", theme.Colors["windowBackground"]);
        Assert.Equal("Segoe UI", theme.Typography.FontFamily);
        Assert.Equal("Cascadia Code", theme.Typography.MonoFontFamily);
        Assert.Equal(13, theme.Typography.BaseFontSize);
    }

    [Fact]
    public void RoundTrip_ToJsonThenParse_PreservesEverything()
    {
        var original = ThemeDefinition.Parse(MinimalJson);
        var reparsed = ThemeDefinition.Parse(original.ToJson());

        Assert.Equal(original.Id, reparsed.Id);
        Assert.Equal(original.Name, reparsed.Name);
        Assert.Equal(original.Base, reparsed.Base);
        Assert.Equal(original.Colors, reparsed.Colors);
        Assert.Equal(original.Typography.FontFamily, reparsed.Typography.FontFamily);
        Assert.Equal(original.Typography.MonoFontFamily, reparsed.Typography.MonoFontFamily);
        Assert.Equal(original.Typography.BaseFontSize, reparsed.Typography.BaseFontSize);
    }

    [Fact]
    public void ResolveAgainst_SparseTheme_FillsMissingKeysFromBase()
    {
        var baseTheme = new ThemeDefinition { Id = "base", Name = "Base", Base = "light" };
        foreach (var key in ThemeKeys.ColorTokens.Keys)
            baseTheme.Colors[key] = "#111111";

        var sparse = ThemeDefinition.Parse(MinimalJson);
        var resolved = sparse.ResolveAgainst(baseTheme);

        Assert.True(resolved.IsComplete);
        Assert.Equal("#8F4531", resolved.ColorOf("accent"));          // own value wins
        Assert.Equal("#111111", resolved.ColorOf("textPrimary"));     // missing → base
        Assert.Equal(ThemeKeys.ColorTokens.Count, resolved.Colors.Count);
    }

    [Fact]
    public void Parse_UnknownColorKey_IsToleratedAndDropped()
    {
        var json = MinimalJson.Replace("\"accent\":", "\"futureToken\": \"#123456\", \"accent\":");
        var theme = ThemeDefinition.Parse(json);

        Assert.False(theme.Colors.ContainsKey("futureToken"));
        Assert.Equal("#8F4531", theme.Colors["accent"]);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    public void Parse_InvalidHex_ThrowsWithFriendlyMessage(string bad)
    {
        var json = MinimalJson.Replace("#8F4531", bad);
        var ex = Assert.Throws<ThemeFormatException>(() => ThemeDefinition.Parse(json));
        // The message must name the key and the bad value — it is shown to the user.
        Assert.Contains("accent", ex.Message);
        Assert.Contains("hex", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ShortHex_IsExpandedAndNormalized()
    {
        var json = MinimalJson.Replace("#8F4531", "#abc");
        var theme = ThemeDefinition.Parse(json);
        Assert.Equal("#AABBCC", theme.Colors["accent"]);
    }

    [Fact]
    public void Parse_NewerFormatVersion_IsRejected()
    {
        var json = MinimalJson.Replace("\"formatVersion\": 1", "\"formatVersion\": 99");
        var ex = Assert.Throws<ThemeFormatException>(() => ThemeDefinition.Parse(json));
        Assert.Contains("newer version", ex.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(80)]
    public void Parse_BaseFontSizeOutOfRange_IsRejected(double size)
    {
        var json = MinimalJson.Replace("\"baseFontSize\": 13", $"\"baseFontSize\": {size}");
        var ex = Assert.Throws<ThemeFormatException>(() => ThemeDefinition.Parse(json));
        Assert.Contains("baseFontSize", ex.Message);
    }

    [Fact]
    public void Parse_MissingId_ThrowsWithFriendlyMessage()
    {
        var json = MinimalJson.Replace("\"id\": \"test-theme\",", string.Empty);
        var ex = Assert.Throws<ThemeFormatException>(() => ThemeDefinition.Parse(json));
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void Parse_UnknownBase_ThrowsWithFriendlyMessage()
    {
        var json = MinimalJson.Replace("\"base\": \"light\"", "\"base\": \"sepia\"");
        var ex = Assert.Throws<ThemeFormatException>(() => ThemeDefinition.Parse(json));
        Assert.Contains("sepia", ex.Message);
    }

    [Fact]
    public void Parse_NotJson_ThrowsThemeFormatException()
    {
        Assert.Throws<ThemeFormatException>(() => ThemeDefinition.Parse("this is not json"));
    }

    [Fact]
    public void HexToArgb_ParsesAllThreeForms()
    {
        Assert.Equal(((byte)255, (byte)0xAA, (byte)0xBB, (byte)0xCC), ThemeDefinition.HexToArgb("#ABC"));
        Assert.Equal(((byte)255, (byte)0x3D, (byte)0x5A, (byte)0x80), ThemeDefinition.HexToArgb("#3D5A80"));
        Assert.Equal(((byte)0x80, (byte)0x3D, (byte)0x5A, (byte)0x80), ThemeDefinition.HexToArgb("#803D5A80"));
    }
}
