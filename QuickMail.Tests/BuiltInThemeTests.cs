using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Theming;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Every built-in theme must parse from its embedded resource and — resolved
/// against its base — pass the WCAG contrast policy from the theming spec §6.
/// Contrast is a unit test, not a review comment: a palette change that breaks
/// AA fails the build.
/// </summary>
public class BuiltInThemeTests
{
    private static ThemeStore NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QM-ThemeTests-{Guid.NewGuid():N}");
        return new ThemeStore(new ProfileContext(dir));
    }

    private static IReadOnlyList<ThemeDefinition> ResolvedBuiltIns()
    {
        var store = NewStore();
        var builtIns = store.LoadBuiltIns();
        var light = builtIns.First(t => t.Id == "parchment");
        var dark  = builtIns.First(t => t.Id == "dark");
        return builtIns.Select(t => t.ResolveAgainst(t.Base == "dark" ? dark : light)).ToList();
    }

    [Fact]
    public void AllBuiltInsParse_AndCarryTheExpectedLineup()
    {
        var ids = NewStore().LoadBuiltIns().Select(t => t.Id).ToList();
        Assert.Equal(new[] { "parchment", "dark", "ember", "fjord", "heather" }, ids);
    }

    [Fact]
    public void BaseThemes_DefineEveryToken()
    {
        var builtIns = NewStore().LoadBuiltIns();
        foreach (var id in new[] { "parchment", "dark" })
        {
            var theme = builtIns.First(t => t.Id == id);
            var missing = ThemeKeys.ColorTokens.Keys.Where(k => !theme.Colors.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"{id} is missing tokens: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void EveryBuiltIn_ResolvesToACompleteTheme()
    {
        foreach (var theme in ResolvedBuiltIns())
            Assert.True(theme.IsComplete, $"{theme.Id} did not resolve to a complete token set");
    }

    // ── WCAG contrast policy (§6) ─────────────────────────────────────────────

    public static IEnumerable<object[]> BuiltInIds() =>
        new[] { "parchment", "dark", "ember", "fjord", "heather" }.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_TextTokens_MeetAA(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        var backgrounds = new[] { "windowBackground", "surfaceBackground", "chromeBackground", "inputBackground" };

        foreach (var bg in backgrounds)
        {
            AssertContrast(t, "textPrimary", bg, 4.5);
            AssertContrast(t, "textSecondary", bg, 4.5);
            // Disabled text is exempt from AA per WCAG, but must stay perceivable.
            AssertContrast(t, "textDisabled", bg, 3.0);
            AssertContrast(t, "hyperlink", bg, 4.5);
        }

        AssertContrast(t, "textOnAccent", "accent", 4.5);
        AssertContrast(t, "selectionText", "selectionBackground", 4.5);
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_NonTextIndicators_MeetThreeToOne(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        foreach (var bg in new[] { "windowBackground", "surfaceBackground" })
        {
            AssertContrast(t, "accent", bg, 3.0);
            AssertContrast(t, "focusIndicator", bg, 3.0);
        }
    }

    /// <summary>
    /// The selected row must be identifiable from its fill alone, not just the
    /// focus outline: the original palettes shipped selection tints at ~1.15:1
    /// against the list surface, which visual review found indistinguishable
    /// from unselected rows in every theme.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_SelectionState_IsVisible(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        // Active selection: a state indicator — non-text minimum of 3:1.
        AssertContrast(t, "selectionBackground", "surfaceBackground", 3.0);
        // Inactive selection stays deliberately quiet, but must not vanish,
        // and primary text on it must stay AA-readable.
        AssertContrast(t, "selectionInactive", "surfaceBackground", 1.2);
        AssertContrast(t, "textPrimary", "selectionInactive", 4.5);
    }

    /// <summary>
    /// Border policy (#421): borders that identify a control or separate
    /// functional regions are non-text indicators (WCAG 1.4.11) and need 3:1;
    /// borderSubtle is decorative but must not vanish — its floor is a
    /// deliberate 1.5:1, not drift. Every built-in shipped borders at
    /// 1.3–2.4:1 before this test existed.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_Borders_AreVisible(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        foreach (var bg in new[] { "windowBackground", "surfaceBackground", "chromeBackground" })
            AssertContrast(t, "border", bg, 3.0);
        AssertContrast(t, "inputBorder", "inputBackground", 3.0);
        AssertContrast(t, "borderSubtle", "windowBackground", 1.5);
    }

    /// <summary>
    /// The focus visual is a two-tone ring: focusIndicator dash over a
    /// windowBackground halo. Focus stays visible on any fill only if, for the
    /// worst case (a selected row), at least the halo clears 3:1 against
    /// selectionBackground — focusIndicator alone ran 2.2–2.3:1 on selection in
    /// every light theme before #421.
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_FocusRing_VisibleOnSelection(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        AssertContrast(t, "windowBackground", "selectionBackground", 3.0);
    }

    /// <summary>accentSubtle is the hover fill; primary text on it must stay AA.</summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_TextOnHoverFill_MeetsAA(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        AssertContrast(t, "textPrimary", "accentSubtle", 4.5);
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void ContrastPolicy_StatusColors_MeetAA(string id)
    {
        var t = ResolvedBuiltIns().First(x => x.Id == id);
        foreach (var status in new[] { "error", "warning", "success", "info" })
        {
            AssertContrast(t, status, status + "Background", 4.5);
            AssertContrast(t, status, "windowBackground", 4.5);
        }
    }

    private static void AssertContrast(ThemeDefinition theme, string fg, string bg, double minimum)
    {
        var ratio = ContrastRatio(theme.ColorOf(fg), theme.ColorOf(bg));
        Assert.True(ratio >= minimum,
            $"{theme.Id}: {fg} ({theme.ColorOf(fg)}) on {bg} ({theme.ColorOf(bg)}) is {ratio:F2}:1, needs {minimum}:1");
    }

    // WCAG 2.x relative luminance and contrast ratio.
    private static double ContrastRatio(string hexA, string hexB)
    {
        var la = RelativeLuminance(hexA);
        var lb = RelativeLuminance(hexB);
        var (light, dark) = la >= lb ? (la, lb) : (lb, la);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        var (_, r, g, b) = ThemeDefinition.HexToArgb(hex);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
