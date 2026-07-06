using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickMail.Theming;

namespace QuickMail.Models;

/// <summary>
/// Thrown when a theme file cannot be parsed or fails validation. The message is
/// written in plain language and is shown to the user verbatim (e.g. on import),
/// so it must name the specific problem: which key, which value, what was expected.
/// </summary>
public class ThemeFormatException : Exception
{
    public ThemeFormatException() { }
    public ThemeFormatException(string message) : base(message) { }
    public ThemeFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Typography block of a theme. All members optional in JSON.</summary>
public class ThemeTypography
{
    /// <summary>UI font family, e.g. "Segoe UI".</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Monospace font family for code content.</summary>
    public string MonoFontFamily { get; set; } = "Consolas";

    /// <summary>Base UI font size in device-independent pixels, before user scaling.</summary>
    public double BaseFontSize { get; set; } = 13;

    public ThemeTypography Clone() => new()
    {
        FontFamily     = FontFamily,
        MonoFontFamily = MonoFontFamily,
        BaseFontSize   = BaseFontSize,
    };
}

/// <summary>
/// A theme: sparse camelCase color map over a light/dark base, plus typography.
/// Serialized as documented JSON in .quickmailtheme files and {profile}\themes\.
/// Colors hold hex strings only — never System.Windows.Media types — so ViewModels
/// can consume a theme without touching the UI layer.
/// </summary>
public class ThemeDefinition
{
    /// <summary>The newest theme file format this build understands.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Minimum and maximum accepted BaseFontSize.</summary>
    public const double MinBaseFontSize = 9;
    public const double MaxBaseFontSize = 24;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Stable identifier, e.g. "quill" or a Guid string for user themes.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name, e.g. "Quill".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"light" or "dark" — which built-in supplies missing color keys.</summary>
    public string Base { get; set; } = "light";

    /// <summary>Sparse camelCase token name → hex color (#RGB / #RRGGBB / #AARRGGBB).</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);

    public ThemeTypography Typography { get; set; } = new();

    /// <summary>True for themes shipped as embedded resources. Not serialized.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    // ── JSON ──────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ThemeJson
    {
        public int? FormatVersion { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Base { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
        public TypographyJson? Typography { get; set; }
    }

    private sealed class TypographyJson
    {
        public string? FontFamily { get; set; }
        public string? MonoFontFamily { get; set; }
        public double? BaseFontSize { get; set; }
    }

    /// <summary>
    /// Parses and validates theme JSON. Throws <see cref="ThemeFormatException"/>
    /// with a plain-language message on any problem. Unknown color keys are
    /// tolerated and dropped so older builds keep reading newer files.
    /// </summary>
    public static ThemeDefinition Parse(string json)
    {
        ThemeJson? raw;
        try
        {
            raw = JsonSerializer.Deserialize<ThemeJson>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ThemeFormatException($"The file is not valid JSON: {ex.Message}", ex);
        }

        if (raw is null)
            throw new ThemeFormatException("The file is empty.");

        var version = raw.FormatVersion ?? 1;
        if (version > CurrentFormatVersion)
            throw new ThemeFormatException(
                $"This theme uses format version {version}, which was created by a newer version of QuickMail. " +
                "Update QuickMail to use it.");

        if (string.IsNullOrWhiteSpace(raw.Id))
            throw new ThemeFormatException("The theme has no \"id\". Every theme needs a short identifier, e.g. \"my-theme\".");
        if (string.IsNullOrWhiteSpace(raw.Name))
            throw new ThemeFormatException("The theme has no \"name\". Give it a display name, e.g. \"My Theme\".");

        var baseName = (raw.Base ?? "light").Trim().ToLowerInvariant();
        if (baseName is not ("light" or "dark"))
            throw new ThemeFormatException(
                $"The \"base\" value \"{raw.Base}\" is not recognized. Use \"light\" or \"dark\".");

        var theme = new ThemeDefinition
        {
            FormatVersion = version,
            Id   = raw.Id.Trim(),
            Name = raw.Name.Trim(),
            Base = baseName,
        };

        if (raw.Colors != null)
        {
            foreach (var (key, value) in raw.Colors)
            {
                // Unknown keys tolerated: newer app versions may add tokens.
                if (!ThemeKeys.ColorTokens.ContainsKey(key))
                    continue;
                if (!IsValidHexColor(value))
                    throw new ThemeFormatException(
                        $"The color \"{key}\" has the value \"{value}\", which is not a hex color. " +
                        "Use #RGB, #RRGGBB, or #AARRGGBB, e.g. \"#3D5A80\".");
                theme.Colors[key] = NormalizeHex(value);
            }
        }

        if (raw.Typography != null)
        {
            if (!string.IsNullOrWhiteSpace(raw.Typography.FontFamily))
                theme.Typography.FontFamily = raw.Typography.FontFamily.Trim();
            if (!string.IsNullOrWhiteSpace(raw.Typography.MonoFontFamily))
                theme.Typography.MonoFontFamily = raw.Typography.MonoFontFamily.Trim();
            if (raw.Typography.BaseFontSize is double size)
            {
                if (double.IsNaN(size) || size < MinBaseFontSize || size > MaxBaseFontSize)
                    throw new ThemeFormatException(
                        $"The \"baseFontSize\" value {size} is out of range. Use a size between {MinBaseFontSize} and {MaxBaseFontSize}.");
                theme.Typography.BaseFontSize = size;
            }
        }

        return theme;
    }

    /// <summary>Serializes the theme as indented, camelCase JSON (the .quickmailtheme format).</summary>
    public string ToJson()
    {
        var raw = new ThemeJson
        {
            FormatVersion = FormatVersion,
            Id = Id,
            Name = Name,
            Base = Base,
            Colors = new Dictionary<string, string>(Colors, StringComparer.Ordinal),
            Typography = new TypographyJson
            {
                FontFamily = Typography.FontFamily,
                MonoFontFamily = Typography.MonoFontFamily,
                BaseFontSize = Typography.BaseFontSize,
            },
        };
        return JsonSerializer.Serialize(raw, WriteOptions);
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a fully-populated copy: every color token present, missing keys
    /// filled from <paramref name="baseTheme"/> (the built-in Light or Dark theme
    /// selected by <see cref="Base"/>). The base theme must itself be complete.
    /// </summary>
    public ThemeDefinition ResolveAgainst(ThemeDefinition baseTheme)
    {
        var resolved = new ThemeDefinition
        {
            FormatVersion = FormatVersion,
            Id = Id,
            Name = Name,
            Base = Base,
            IsBuiltIn = IsBuiltIn,
            Typography = Typography.Clone(),
        };
        foreach (var (jsonKey, _) in ThemeKeys.ColorTokens)
        {
            if (Colors.TryGetValue(jsonKey, out var hex))
                resolved.Colors[jsonKey] = hex;
            else if (baseTheme.Colors.TryGetValue(jsonKey, out var baseHex))
                resolved.Colors[jsonKey] = baseHex;
            else
                throw new ThemeFormatException(
                    $"The base theme \"{baseTheme.Id}\" is missing the color \"{jsonKey}\". Built-in themes must define every token.");
        }
        return resolved;
    }

    /// <summary>True when every color token has a value (i.e. the theme is fully resolved).</summary>
    public bool IsComplete => ThemeKeys.ColorTokens.Keys.All(Colors.ContainsKey);

    /// <summary>The hex value of a token by camelCase name. Throws if absent — call on resolved themes only.</summary>
    public string ColorOf(string jsonKey) => Colors[jsonKey];

    public ThemeDefinition Clone()
    {
        var copy = new ThemeDefinition
        {
            FormatVersion = FormatVersion,
            Id = Id,
            Name = Name,
            Base = Base,
            IsBuiltIn = IsBuiltIn,
            Typography = Typography.Clone(),
        };
        foreach (var (k, v) in Colors) copy.Colors[k] = v;
        return copy;
    }

    // ── Hex validation ────────────────────────────────────────────────────────

    /// <summary>Accepts #RGB, #RRGGBB, or #AARRGGBB.</summary>
    public static bool IsValidHexColor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '#')
            return false;
        var digits = value.Length - 1;
        if (digits is not (3 or 6 or 8))
            return false;
        for (int i = 1; i < value.Length; i++)
            if (!Uri.IsHexDigit(value[i]))
                return false;
        return true;
    }

    /// <summary>Normalizes to uppercase #RRGGBB / #AARRGGBB (expands #RGB).</summary>
    public static string NormalizeHex(string value)
    {
        var v = value.ToUpperInvariant();
        if (v.Length == 4) // #RGB → #RRGGBB
            return string.Create(7, v, (span, src) =>
            {
                span[0] = '#';
                span[1] = span[2] = src[1];
                span[3] = span[4] = src[2];
                span[5] = span[6] = src[3];
            });
        return v;
    }

    /// <summary>Parses a normalized hex color into A/R/G/B bytes (A=255 when absent).</summary>
    public static (byte A, byte R, byte G, byte B) HexToArgb(string hex)
    {
        var v = NormalizeHex(hex);
        if (v.Length == 9)
            return (
                byte.Parse(v.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(v.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(v.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(v.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return (
            255,
            byte.Parse(v.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(v.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(v.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
