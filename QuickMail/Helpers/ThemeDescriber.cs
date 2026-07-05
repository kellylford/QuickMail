using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.Theming;

namespace QuickMail.Helpers;

/// <summary>
/// Produces a plain-text, screen-reader-friendly description of a resolved theme:
/// what each color is (a spoken descriptor plus the nearest documented CSS/X11
/// color name and the hex), and where it is used in the app, plus the fonts. It
/// is the text behind the Theme Manager's read-only "Theme description" box, so
/// someone who cannot see the swatches can still understand what a theme does.
///
/// Input must be a fully resolved theme (every token present) — call
/// <c>IThemeService.ResolveForPreview</c> first. This helper is deliberately
/// string-only on its public surface so a ViewModel can consume it without
/// touching the UI layer.
/// </summary>
public static class ThemeDescriber
{
    /// <summary>Token → (spoken label, where it is used). Grouped into the sections below.</summary>
    private static readonly (string Header, (string Key, string Label, string Usage)[] Tokens)[] Sections =
    {
        ("Backgrounds and borders", new[]
        {
            ("windowBackground",  "Window background",   "the main window and the message reading area"),
            ("surfaceBackground", "Panel background",    "raised surfaces — the message list, the folder tree, and dialogs"),
            ("chromeBackground",  "Toolbar background",  "the menu bar, toolbars, and the status bar"),
            ("inputBackground",   "Input background",    "inside text boxes, search fields, and dropdowns"),
            ("border",            "Border",              "lines around controls and between panels"),
            ("borderSubtle",      "Subtle border",       "faint dividers, such as between list rows"),
            ("inputBorder",       "Input border",        "the outline of text boxes and dropdowns"),
        }),
        ("Text", new[]
        {
            ("textPrimary",   "Primary text",   "body text — messages, list items, and labels"),
            ("textSecondary", "Secondary text", "supporting detail — timestamps, previews, and hints"),
            ("textDisabled",  "Disabled text",  "labels on disabled buttons and unavailable options"),
            ("textOnAccent",  "Text on accent", "text drawn on top of the accent color, such as on a selected button"),
        }),
        ("Accent and selection", new[]
        {
            ("accent",              "Accent",              "primary highlights — default buttons and the unread marker"),
            ("accentSubtle",        "Subtle accent",       "soft accent fills, such as hover backgrounds"),
            ("hyperlink",           "Hyperlink",           "clickable links in messages and text"),
            ("selectionBackground", "Selection background", "the highlighted background of the selected list or tree item"),
            ("selectionText",       "Selection text",      "text of the selected item, over the selection background"),
            ("selectionInactive",   "Inactive selection",  "the selected item when its list does not have keyboard focus"),
            ("focusIndicator",      "Focus outline",       "the keyboard focus rectangle around the focused control"),
        }),
        ("Status colors", new[]
        {
            ("error",             "Error",                  "error text and icons"),
            ("errorBackground",   "Error background",       "the fill behind error messages"),
            ("warning",           "Warning",                "warning text and icons"),
            ("warningBackground", "Warning background",     "the fill behind warnings"),
            ("success",           "Success",                "success text and confirmations"),
            ("successBackground", "Success background",     "the fill behind success messages"),
            ("info",              "Information",            "informational text and icons"),
            ("infoBackground",    "Information background", "the fill behind informational messages"),
        }),
    };

    /// <summary>
    /// Builds the full description for a resolved theme. When <paramref name="isSystem"/>
    /// is true the theme is the virtual System entry and the text says so, then
    /// describes the base it currently resolves to.
    /// </summary>
    public static string Describe(ThemeDefinition theme, bool isSystem = false)
    {
        var sb = new StringBuilder();

        if (isSystem)
        {
            sb.AppendLine("System theme — follows the Windows light and dark setting.");
            sb.AppendLine($"It currently shows the {theme.Name} colors, described below.");
        }
        else
        {
            var baseWord = string.Equals(theme.Base, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
            sb.AppendLine($"{theme.Name} — a {baseWord} theme.");
        }
        sb.AppendLine();

        if (theme.IsComplete)
        {
            var bg = DescribeColorShort(theme.ColorOf("windowBackground"));
            var txt = DescribeColorShort(theme.ColorOf("textPrimary"));
            var accent = DescribeColorShort(theme.ColorOf("accent"));
            sb.AppendLine("Overall");
            sb.AppendLine($"{Article(bg, capital: true)} {bg} background with {txt} text and " +
                          $"{Article(accent, capital: false)} {accent} accent.");
            sb.AppendLine();
        }

        sb.AppendLine("Fonts");
        sb.AppendLine($"Interface font: {theme.Typography.FontFamily}, base size {Number(theme.Typography.BaseFontSize)} pixels.");
        sb.AppendLine($"Code font: {theme.Typography.MonoFontFamily}.");
        sb.AppendLine();

        foreach (var (header, tokens) in Sections)
        {
            sb.AppendLine(header);
            foreach (var (key, label, usage) in tokens)
            {
                if (!theme.Colors.TryGetValue(key, out var hex))
                    continue;
                sb.AppendLine($"{label}: {DescribeColor(hex)} — {usage}.");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ── Color naming ────────────────────────────────────────────────────────────

    /// <summary>e.g. "dark blue (#3D5A80, like Steel Blue)".</summary>
    public static string DescribeColor(string hex)
    {
        var (_, r, g, b) = ThemeDefinition.HexToArgb(hex);
        return $"{Descriptor(r, g, b)} ({ThemeDefinition.NormalizeHex(hex)}, like {NearestName(r, g, b)})";
    }

    /// <summary>The spoken descriptor only, e.g. "warm off-white" — for the one-line overall summary.</summary>
    public static string DescribeColorShort(string hex)
    {
        var (_, r, g, b) = ThemeDefinition.HexToArgb(hex);
        return Descriptor(r, g, b);
    }

    /// <summary>Lightness + saturation + hue in words, or a neutral descriptor for near-grays.</summary>
    private static string Descriptor(byte r, byte g, byte b)
    {
        var (h, _, l) = ToHsl(r, g, b);
        // Absolute chroma, not HSL saturation: near-white and near-black colors get
        // wildly inflated HSL saturation from a 2-3 unit RGB spread, which would call
        // an off-white background "pale orange". Chroma stays honest at the extremes.
        double chroma = (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;

        // Near-neutral: describe as black/white/off-white/gray with a warm/cool tinge.
        if (chroma < 0.10)
        {
            var warmCool = r - b >= 6 ? "warm " : (b - r >= 6 ? "cool " : "");
            if (l >= 0.985 && warmCool.Length == 0) return "white";
            if (l >= 0.90) return (warmCool + "off-white").Trim();
            if (l <= 0.06) return "black";
            var grayLight = l <= 0.20 ? "very dark " : l <= 0.38 ? "dark " : l >= 0.72 ? "light " : "";
            return (grayLight + warmCool + "gray").Trim();
        }

        var lightWord = l switch
        {
            < 0.18 => "very dark",
            < 0.38 => "dark",
            < 0.60 => "medium",
            < 0.78 => "light",
            _      => "pale",
        };
        var satWord = chroma < 0.30 ? "muted" : chroma > 0.55 ? "vivid" : "";

        var parts = new List<string> { lightWord };
        if (satWord.Length > 0) parts.Add(satWord);
        parts.Add(HueName(h));
        return string.Join(" ", parts);
    }

    private static string HueName(double h)
    {
        h = ((h % 360) + 360) % 360;
        return h switch
        {
            < 15  => "red",
            < 45  => "orange",
            < 70  => "yellow",
            < 95  => "yellow-green",
            < 150 => "green",
            < 175 => "teal",
            < 200 => "cyan",
            < 240 => "blue",
            < 270 => "indigo",
            < 300 => "purple",
            < 330 => "magenta",
            < 345 => "pink",
            _     => "red",
        };
    }

    private static (double H, double S, double L) ToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2.0;
        double h = 0, s = 0;
        double d = max - min;
        if (d > 1e-6)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == rd)      h = (gd - bd) / d + (gd < bd ? 6 : 0);
            else if (max == gd) h = (bd - rd) / d + 2;
            else                h = (rd - gd) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    // ── Nearest documented name (the CSS / X11 set WPF exposes) ──────────────────

    private static readonly Lazy<List<(string Name, byte R, byte G, byte B)>> NamedColors =
        new(BuildNamedColors);

    private static List<(string, byte, byte, byte)> BuildNamedColors()
    {
        var list = new List<(string, byte, byte, byte)>();
        foreach (var p in typeof(System.Windows.Media.Colors)
                     .GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.PropertyType != typeof(System.Windows.Media.Color))
                continue;
            var c = (System.Windows.Media.Color)p.GetValue(null)!;
            if (c.A == 0) continue; // skip Transparent
            list.Add((Spaced(p.Name), c.R, c.G, c.B));
        }
        return list;
    }

    private static string NearestName(byte r, byte g, byte b)
    {
        var best = "gray";
        double bestDist = double.MaxValue;
        foreach (var (name, cr, cg, cb) in NamedColors.Value)
        {
            // Redmean: a cheap perceptual RGB distance, better than plain Euclidean.
            double rmean = (r + (double)cr) / 2.0;
            double dr = r - cr, dg = g - cg, db = b - cb;
            double dist = (512 + rmean) * dr * dr / 256.0 + 4 * dg * dg + (767 - rmean) * db * db / 256.0;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = name;
            }
        }
        return best;
    }

    /// <summary>"SteelBlue" → "Steel Blue".</summary>
    private static string Spaced(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");

    private static string Number(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>"a" / "an" (capitalized on request) for the word that follows.</summary>
    private static string Article(string word, bool capital)
    {
        var vowel = word.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(word[0]));
        var article = vowel ? "an" : "a";
        return capital ? char.ToUpperInvariant(article[0]) + article[1..] : article;
    }
}
