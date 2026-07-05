using System;
using System.Collections.Generic;

namespace QuickMail.Theming;

/// <summary>
/// The single source of truth for semantic theme token names.
///
/// Each color token exists in two spellings:
///   - the resource key published into Application.Current.Resources
///     (e.g. "Theme.WindowBackground"), consumed by XAML via DynamicResource
///     and by C# via SetResourceReference / TryFindResource; and
///   - the camelCase JSON name used in .quickmailtheme files
///     (e.g. "windowBackground").
///
/// <see cref="ColorTokens"/> maps camelCase JSON name → resource key and is the
/// authoritative list of the 26 color tokens. Typography and vision-assist keys
/// are resource-only (they come from the theme's typography block and user
/// settings, not the colors map).
/// </summary>
public static class ThemeKeys
{
    private const string Prefix = "Theme.";

    // ── Surfaces (7) ──────────────────────────────────────────────────────────
    public const string WindowBackground  = Prefix + "WindowBackground";
    public const string SurfaceBackground = Prefix + "SurfaceBackground";
    public const string ChromeBackground  = Prefix + "ChromeBackground";
    public const string InputBackground   = Prefix + "InputBackground";
    public const string Border            = Prefix + "Border";
    public const string BorderSubtle      = Prefix + "BorderSubtle";
    public const string InputBorder       = Prefix + "InputBorder";

    // ── Text (4) ──────────────────────────────────────────────────────────────
    public const string TextPrimary   = Prefix + "TextPrimary";
    public const string TextSecondary = Prefix + "TextSecondary";
    public const string TextDisabled  = Prefix + "TextDisabled";
    public const string TextOnAccent  = Prefix + "TextOnAccent";

    // ── Accent / interaction (7) ──────────────────────────────────────────────
    public const string Accent              = Prefix + "Accent";
    public const string AccentSubtle        = Prefix + "AccentSubtle";
    public const string Hyperlink           = Prefix + "Hyperlink";
    public const string SelectionBackground = Prefix + "SelectionBackground";
    public const string SelectionText       = Prefix + "SelectionText";
    public const string SelectionInactive   = Prefix + "SelectionInactive";
    public const string FocusIndicator      = Prefix + "FocusIndicator";

    // ── Status (8) ────────────────────────────────────────────────────────────
    public const string Error             = Prefix + "Error";
    public const string ErrorBackground   = Prefix + "ErrorBackground";
    public const string Warning           = Prefix + "Warning";
    public const string WarningBackground = Prefix + "WarningBackground";
    public const string Success           = Prefix + "Success";
    public const string SuccessBackground = Prefix + "SuccessBackground";
    public const string Info              = Prefix + "Info";
    public const string InfoBackground    = Prefix + "InfoBackground";

    // ── Typography (resource-only) ────────────────────────────────────────────
    public const string FontFamily     = Prefix + "FontFamily";
    public const string FontFamilyMono = Prefix + "FontFamilyMono";
    public const string FontSizeBase   = Prefix + "FontSizeBase";
    public const string FontSizeSmall  = Prefix + "FontSizeSmall";
    public const string FontSizeLarge  = Prefix + "FontSizeLarge";
    public const string FontSizeHeader = Prefix + "FontSizeHeader";

    // ── Vision assist (resource-only) ─────────────────────────────────────────
    public const string FocusThickness = Prefix + "FocusThickness";

    /// <summary>camelCase JSON name → application resource key, for all 26 color tokens.</summary>
    public static readonly IReadOnlyDictionary<string, string> ColorTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["windowBackground"]    = WindowBackground,
            ["surfaceBackground"]   = SurfaceBackground,
            ["chromeBackground"]    = ChromeBackground,
            ["inputBackground"]     = InputBackground,
            ["border"]              = Border,
            ["borderSubtle"]        = BorderSubtle,
            ["inputBorder"]         = InputBorder,
            ["textPrimary"]         = TextPrimary,
            ["textSecondary"]       = TextSecondary,
            ["textDisabled"]        = TextDisabled,
            ["textOnAccent"]        = TextOnAccent,
            ["accent"]              = Accent,
            ["accentSubtle"]        = AccentSubtle,
            ["hyperlink"]           = Hyperlink,
            ["selectionBackground"] = SelectionBackground,
            ["selectionText"]       = SelectionText,
            ["selectionInactive"]   = SelectionInactive,
            ["focusIndicator"]      = FocusIndicator,
            ["error"]               = Error,
            ["errorBackground"]     = ErrorBackground,
            ["warning"]             = Warning,
            ["warningBackground"]   = WarningBackground,
            ["success"]             = Success,
            ["successBackground"]   = SuccessBackground,
            ["info"]                = Info,
            ["infoBackground"]      = InfoBackground,
        };
}
