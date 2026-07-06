using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Theming;
using Xunit;

namespace QuickMail.Tests;

// In the WpfTests collection: the service publishes into Application.Current.Resources
// when an Application exists, so these must not run in parallel with other WPF tests.
[Collection("WpfTests")]
public class ThemeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"QM-ThemeSvcTests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Service wired for tests: fixed OS probes, no ThemedControls pack URI load.</summary>
    private ThemeService NewService(bool highContrast = false, bool osLight = true)
    {
        var service = new ThemeService(new ThemeStore(new ProfileContext(_dir)))
        {
            HighContrastProbe = () => highContrast,
            OsLightModeProbe = () => osLight,
            EnableThemedControls = false,
        };
        return service;
    }

    private static ConfigModel Config(string themeId = "system", double scale = 1.0,
        string font = "", bool underline = false, bool thickFocus = false) => new()
    {
        AppearanceThemeId = themeId,
        AppearanceTextScale = scale,
        AppearanceFontFamily = font,
        AppearanceUnderlineLinks = underline,
        AppearanceThickFocus = thickFocus,
    };

    // ── Resolution ────────────────────────────────────────────────────────────

    [StaFact]
    public void System_FollowsOsLightAndDark()
    {
        using var svc = NewService(osLight: true);
        svc.Initialize(Config("system"));
        Assert.Equal("parchment", svc.ResolvedTheme.Id);

        using var dark = NewService(osLight: false);
        dark.Initialize(Config("system"));
        Assert.Equal("dark", dark.ResolvedTheme.Id);
    }

    [StaFact]
    public void UnknownThemeId_FallsBackToSystem_WithoutThrowing()
    {
        using var svc = NewService(osLight: true);
        svc.Initialize(Config("no-such-theme"));
        Assert.Equal("parchment", svc.ResolvedTheme.Id);
    }

    [StaFact]
    public void ApplyTheme_SwitchesResolvedThemeAndRaisesThemeChanged()
    {
        using var svc = NewService();
        svc.Initialize(Config("system"));

        var raised = 0;
        svc.ThemeChanged += (_, _) => raised++;

        svc.ApplyTheme("ember");
        Assert.Equal("ember", svc.ConfiguredThemeId);
        Assert.Equal("ember", svc.ResolvedTheme.Id);
        Assert.True(svc.ResolvedTheme.IsComplete);
        Assert.Equal(1, raised);

        // Re-applying the same theme is a no-op: no event, no dictionary churn.
        svc.ApplyTheme("ember");
        Assert.Equal(1, raised);
    }

    [StaFact]
    public void ApplyTheme_NonPersistent_LeavesConfiguredIdUntouched()
    {
        using var svc = NewService();
        svc.Initialize(Config("system"));

        svc.ApplyTheme("fjord", persist: false);   // the Phase 5 per-view hook

        Assert.Equal("system", svc.ConfiguredThemeId);
        Assert.Equal("fjord", svc.ResolvedTheme.Id);
    }

    [StaFact]
    public void ApplyAppearance_ThemeAndVisionTogether_RaisesThemeChangedOnce()
    {
        using var svc = NewService();
        svc.Initialize(Config("system"));

        var raised = 0;
        svc.ThemeChanged += (_, _) => raised++;

        // A combined Settings save: new theme id AND a vision setting (thick focus).
        // Both mutations must coalesce into a single re-publish / single event.
        svc.ApplyAppearance(Config("ember", thickFocus: true));

        Assert.Equal("ember", svc.ConfiguredThemeId);
        Assert.Equal("ember", svc.ResolvedTheme.Id);
        Assert.Equal(1, raised);
    }

    // ── Announcement naming ───────────────────────────────────────────────────

    [StaFact]
    public void ConfiguredThemeName_System_NamesTheResolvedBase()
    {
        using var light = NewService(osLight: true);
        light.Initialize(Config("system"));
        Assert.Equal("System, showing Parchment", light.ConfiguredThemeName);

        using var dark = NewService(osLight: false);
        dark.Initialize(Config("system"));
        Assert.Equal("System, showing Parchment Dark", dark.ConfiguredThemeName);
    }

    [StaFact]
    public void ConfiguredThemeName_RealTheme_IsItsOwnName()
    {
        using var svc = NewService();
        svc.Initialize(Config("ember"));
        Assert.Equal(svc.ResolvedTheme.Name, svc.ConfiguredThemeName);
        Assert.DoesNotContain("System", svc.ConfiguredThemeName);
    }

    // ── High Contrast passthrough ─────────────────────────────────────────────

    [StaFact]
    public void HighContrast_ResolvesEveryTokenFromSystemColors()
    {
        using var svc = NewService(highContrast: true);
        svc.Initialize(Config("ember"));

        Assert.True(svc.IsHighContrastActive);
        var t = svc.ResolvedTheme;
        Assert.Equal("high-contrast", t.Id);
        Assert.True(t.IsComplete);

        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Assert.Equal(Hex(SystemColors.WindowColor),     t.ColorOf("windowBackground"));
        Assert.Equal(Hex(SystemColors.WindowTextColor), t.ColorOf("textPrimary"));
        Assert.Equal(Hex(SystemColors.HighlightColor),  t.ColorOf("selectionBackground"));
        Assert.Equal(Hex(SystemColors.HotTrackColor),   t.ColorOf("accent"));
        // Status colors have no visual opinion in HC — text carries the meaning.
        Assert.Equal(Hex(SystemColors.WindowTextColor), t.ColorOf("error"));
        Assert.Equal(Hex(SystemColors.WindowColor),     t.ColorOf("errorBackground"));

        // The configured id survives HC; leaving HC restores it.
        Assert.Equal("ember", svc.ConfiguredThemeId);
    }

    // ── Token dictionary ──────────────────────────────────────────────────────

    [StaFact]
    public void TokenDictionary_ContainsEveryTokenAsFrozenBrush_PlusTypographyAndFocus()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var dict = svc.BuildTokenDictionary(svc.ResolvedTheme);

        foreach (var resourceKey in ThemeKeys.ColorTokens.Values)
        {
            var brush = Assert.IsType<SolidColorBrush>(dict[resourceKey]);
            Assert.True(brush.IsFrozen, $"{resourceKey} brush must be frozen");
        }
        Assert.IsType<FontFamily>(dict[ThemeKeys.FontFamily]);
        Assert.IsType<FontFamily>(dict[ThemeKeys.FontFamilyMono]);
        Assert.Equal(13.0, dict[ThemeKeys.FontSizeBase]);
        Assert.Equal(11.0, dict[ThemeKeys.FontSizeSmall]);
        Assert.Equal(15.0, dict[ThemeKeys.FontSizeLarge]);
        Assert.Equal(18.0, dict[ThemeKeys.FontSizeHeader]);
        Assert.Equal(2.0, dict[ThemeKeys.FocusThickness]);
    }

    [StaFact]
    public void TokenDictionary_AppliesTextScaleAndVisionSettings()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment", scale: 1.5, font: "Verdana", thickFocus: true));

        var dict = svc.BuildTokenDictionary(svc.ResolvedTheme);

        Assert.Equal(19.5, dict[ThemeKeys.FontSizeBase]);
        Assert.Equal(4.0, dict[ThemeKeys.FocusThickness]);
        Assert.Equal("Verdana", ((FontFamily)dict[ThemeKeys.FontFamily]).Source);
    }

    // ── WebView2 CSS bridge ───────────────────────────────────────────────────

    [StaFact]
    public void BuildMessageCss_EmitsAllColorVariables()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var css = svc.BuildMessageCss(forceOnContent: false);

        foreach (var v in new[] { "--qm-bg", "--qm-text", "--qm-text-muted", "--qm-surface",
                                  "--qm-border", "--qm-accent", "--qm-link", "--qm-font", "--qm-font-size" })
            Assert.Contains(v, css);
        Assert.DoesNotContain("!important", css);
    }

    [StaFact]
    public void BuildMessageCss_InvalidFontOverride_DoesNotReachCss()
    {
        using var svc = NewService();
        // A hand-edited config font that tries to break out of the CSS string.
        svc.Initialize(Config("parchment", font: "x;} body{display:none"));

        var css = svc.BuildMessageCss(forceOnContent: false);

        // The invalid override is rejected; the CSS falls back to the theme font and
        // the injection payload never appears in the reading-pane CSS.
        Assert.DoesNotContain("display:none", css);
        Assert.DoesNotContain("x;}", css);
    }

    [StaFact]
    public void BuildMessageCss_ForceOnContent_AppendsImportantOverrides()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var css = svc.BuildMessageCss(forceOnContent: true);
        Assert.Contains("background-color:var(--qm-bg) !important", css);
        Assert.Contains("color:var(--qm-text) !important", css);
    }

    [StaFact]
    public void BuildMessageCss_UnderlineLinks_EmitsUnderlineRule()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment", underline: true));

        Assert.Contains("text-decoration:underline", svc.BuildMessageCss(forceOnContent: false));
    }

    [StaFact]
    public void BuildMessageCss_HighContrast_EmitsNoColorVariables()
    {
        using var svc = NewService(highContrast: true);
        svc.Initialize(Config("parchment"));

        var css = svc.BuildMessageCss(forceOnContent: true);

        // Typography still applies in HC; colors are the browser's business.
        Assert.Contains("--qm-font", css);
        foreach (var v in new[] { "--qm-bg", "--qm-text:", "--qm-link", "--qm-accent" })
            Assert.DoesNotContain(v, css);
        Assert.DoesNotContain("!important", css); // force-on-content is inert in HC
    }

    // ── Import / export ───────────────────────────────────────────────────────

    [StaFact]
    public void ExportThenImport_RoundTrips_WithReIdOnCollision()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var exportPath = Path.Combine(_dir, "exported.quickmailtheme");
        svc.ExportTheme("ember", exportPath);
        Assert.True(File.Exists(exportPath));

        // "ember" already exists, so the import must re-id and rename.
        var imported = svc.ImportTheme(exportPath);
        Assert.NotEqual("ember", imported.Id);
        Assert.Equal("Ember (imported)", imported.Name);
        Assert.Contains(svc.GetAvailableThemes(), t => t.Id == imported.Id);

        // The imported copy carries Ember's palette.
        Assert.Equal("#8F4531", imported.Colors["accent"]);
    }

    [StaFact]
    public void ImportTheme_MalformedFile_ThrowsFriendlyThemeFormatException()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var path = Path.Combine(_dir, "broken.quickmailtheme");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "not json at all");

        Assert.Throws<ThemeFormatException>(() => svc.ImportTheme(path));
    }

    [StaFact]
    public void ImportTheme_OversizedFile_IsRejectedBeforeParsing()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var path = Path.Combine(_dir, "huge.quickmailtheme");
        Directory.CreateDirectory(_dir);
        // One byte over the cap is enough; content need not be valid JSON — the
        // size check must fire before any read/parse.
        File.WriteAllBytes(path, new byte[ThemeDefinition.MaxFileBytes + 1]);

        var ex = Assert.Throws<ThemeFormatException>(() => svc.ImportTheme(path));
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [StaFact]
    public void ReadThemeFile_OversizedFile_Throws()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "huge.json");
        File.WriteAllBytes(path, new byte[ThemeDefinition.MaxFileBytes + 1]);

        Assert.Throws<ThemeFormatException>(() => ThemeStore.ReadThemeFile(path));
    }

    [StaFact]
    public void LoadUserThemes_SkipsOversizedFile()
    {
        var themesFolder = Path.Combine(_dir, "themes");
        Directory.CreateDirectory(themesFolder);
        File.WriteAllBytes(Path.Combine(themesFolder, "huge.json"), new byte[ThemeDefinition.MaxFileBytes + 1]);

        var store = new ThemeStore(new ProfileContext(_dir));
        // The oversized file is skipped (logged), not thrown, and never appears.
        Assert.Empty(store.LoadUserThemes());
    }

    [StaFact]
    public void SaveUserTheme_MaliciousIdWithSeparators_StaysInThemesFolder()
    {
        var store = new ThemeStore(new ProfileContext(_dir));
        // Parse now rejects a traversal id outright, so build the object directly to
        // exercise PathForId's sanitization + containment for a theme that did not
        // come through Parse (belt-and-suspenders).
        var theme = new ThemeDefinition { Id = "../../escape", Name = "Escape", Base = "light" };

        store.SaveUserTheme(theme);

        // The id's separators are sanitized to '-', so the file lands directly in the
        // themes folder and nothing is written outside it.
        var themesFolder = Path.Combine(_dir, "themes");
        var written = Directory.GetFiles(themesFolder, "*.json");
        Assert.Single(written);
        Assert.Equal(Path.GetFullPath(themesFolder),
            Path.GetFullPath(Path.GetDirectoryName(written[0])!));
    }

    [StaFact]
    public void DeleteUserTheme_WhenActive_FallsBackToSystem()
    {
        using var svc = NewService();
        svc.Initialize(Config("parchment"));

        var custom = new ThemeDefinition { Id = "my-theme", Name = "Mine", Base = "light" };
        svc.SaveUserTheme(custom);
        svc.ApplyTheme("my-theme");
        Assert.Equal("my-theme", svc.ConfiguredThemeId);

        svc.DeleteUserTheme("my-theme");

        Assert.Equal("system", svc.ConfiguredThemeId);
        Assert.DoesNotContain(svc.GetAvailableThemes(), t => t.Id == "my-theme");
    }

    [StaFact]
    public void CorruptUserThemeFile_IsSkipped_AndDoesNotBlockLoading()
    {
        var themesDir = Path.Combine(_dir, "themes");
        Directory.CreateDirectory(themesDir);
        File.WriteAllText(Path.Combine(themesDir, "broken.json"), "{ definitely not a theme");
        File.WriteAllText(Path.Combine(themesDir, "good.json"),
            new ThemeDefinition { Id = "good", Name = "Good", Base = "light" }.ToJson());

        using var svc = NewService();
        svc.Initialize(Config("system"));

        var themes = svc.GetAvailableThemes();
        Assert.Contains(themes, t => t.Id == "good");
        Assert.Equal(1, themes.Count(t => !t.IsBuiltIn && t.Id != "system"));
    }
}
