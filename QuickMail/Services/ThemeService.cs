using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickMail.Models;
using QuickMail.Theming;

namespace QuickMail.Services;

/// <summary>
/// Builds the semantic token dictionary (Theme.* DynamicResources) and keeps it in
/// sync with the configured theme, the OS light/dark preference, and Windows High
/// Contrast.
///
/// Publication is an atomic replacement of one merged dictionary — never an
/// in-place mutate/Clear, which creates transient missing-resource states while
/// DynamicResource listeners re-resolve. Under High Contrast every token is
/// rebuilt from live SystemColors and the ThemedControls dictionary is withdrawn
/// so controls fall back to WPF's built-in HC-aware templates: QuickMail has no
/// visual opinion when HC is on.
/// </summary>
public class ThemeService : IThemeService
{
    /// <summary>The virtual theme id that follows the OS: light↔dark and HC passthrough.</summary>
    public const string SystemThemeId = "system";

    private const string ThemedControlsUri = "pack://application:,,,/Styles/ThemedControls.xaml";

    private readonly ThemeStore _store;

    // Effective state.
    private string _configuredThemeId = SystemThemeId;
    private string _activeThemeId = SystemThemeId;   // differs from configured only for non-persistent applies (Phase 5)
    private ThemeDefinition _resolved;
    private bool _isHighContrast;

    // Vision-assist settings (from config; applied via ApplyVisionSettings).
    private double _textScale = 1.0;
    private string _fontFamilyOverride = string.Empty;
    private bool _underlineLinks;
    private bool _thickFocus;

    // Published dictionaries (tracked by reference for atomic replacement).
    private ResourceDictionary? _publishedTokens;
    private ResourceDictionary? _themedControls;
    private bool _themedControlsInserted;

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _debounceTimer;
    private bool _initialized;
    private bool _disposed;
    private string _lastPublishedSignature = string.Empty;

    /// <summary>Test hook: overrides the OS light/dark probe (default reads AppsUseLightTheme).</summary>
    internal Func<bool> OsLightModeProbe { get; set; } = ReadOsLightMode;

    /// <summary>Test hook: overrides the High Contrast probe (default reads SystemParameters.HighContrast).</summary>
    internal Func<bool> HighContrastProbe { get; set; } = () => SystemParameters.HighContrast;

    /// <summary>Test hook: when false, skips loading Styles/ThemedControls.xaml (needs a pack URI host).</summary>
    internal bool EnableThemedControls { get; set; } = true;

    public event EventHandler? ThemeChanged;

    public ThemeService(ThemeStore store)
    {
        _store = store;
        // Safe default so ResolvedTheme is never null even before Initialize.
        _resolved = BuiltInById("quill").ResolveAgainst(BuiltInById("quill"));
    }

    public string ConfiguredThemeId => _configuredThemeId;

    /// <summary>
    /// The display name to announce for the current selection. For the virtual
    /// System theme this is "System" qualified with the base it currently shows
    /// (e.g. "System, showing Quill") — so cycling to System never announces the
    /// resolved built-in name as if the user had picked it directly.
    /// </summary>
    public string ConfiguredThemeName =>
        string.Equals(_configuredThemeId, SystemThemeId, StringComparison.OrdinalIgnoreCase)
            ? $"System, showing {_resolved.Name}"
            : _resolved.Name;

    public ThemeDefinition ResolvedTheme => _resolved;
    public bool IsHighContrastActive => _isHighContrast;
    public string UserThemesFolder => _store.ThemesFolder;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the configured theme and vision settings and subscribes to OS
    /// change signals. Call once at startup, before the first window is created,
    /// on the UI thread.
    /// </summary>
    public void Initialize(ConfigModel config)
    {
        // The calling thread's dispatcher — Initialize runs on the UI thread at
        // startup, so this is the application dispatcher in production and the
        // test thread's dispatcher under xUnit STA.
        _dispatcher = Dispatcher.CurrentDispatcher;
        ReadVisionSettings(config);
        _configuredThemeId = string.IsNullOrWhiteSpace(config.AppearanceThemeId)
            ? SystemThemeId
            : config.AppearanceThemeId.Trim();
        _activeThemeId = _configuredThemeId;

        Refresh(raiseEvent: false);

        // HighContrast: canonical WPF signal, raised on the UI thread.
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        // OS light/dark (and HC palette edits): fires on a non-UI thread, in bursts.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _initialized = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_initialized)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        _debounceTimer?.Stop();
        GC.SuppressFinalize(this);
    }

    // ── OS change detection ───────────────────────────────────────────────────

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            ScheduleRefresh();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            // Non-UI thread: marshal before touching WPF objects.
            _dispatcher?.BeginInvoke(ScheduleRefresh);
        }
    }

    /// <summary>Debounces bursts of preference-change signals into one Refresh (~250 ms).</summary>
    private void ScheduleRefresh()
    {
        if (_dispatcher is null || !_dispatcher.CheckAccess())
        {
            _dispatcher?.BeginInvoke(ScheduleRefresh);
            return;
        }
        _debounceTimer ??= CreateDebounceTimer();
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private DispatcherTimer CreateDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed)
                Refresh(raiseEvent: true);
        };
        return timer;
    }

    // ── Theme selection ───────────────────────────────────────────────────────

    public void ApplyTheme(string themeId) => ApplyTheme(themeId, persist: true);

    /// <summary>
    /// Applies a theme. With <paramref name="persist"/> false the configured id is
    /// untouched — the hook for per-view themes (Phase 5); the caller persists the
    /// configured id to config.ini either way (the service never writes config).
    /// </summary>
    internal void ApplyTheme(string themeId, bool persist)
    {
        var id = string.IsNullOrWhiteSpace(themeId) ? SystemThemeId : themeId.Trim();
        _activeThemeId = id;
        if (persist)
            _configuredThemeId = id;
        Refresh(raiseEvent: true);
    }

    /// <summary>Re-reads vision-assist settings (scale, font, underline, focus) and re-publishes.</summary>
    public void ApplyVisionSettings(ConfigModel config)
    {
        ReadVisionSettings(config);
        Refresh(raiseEvent: true);
    }

    /// <summary>
    /// Applies the configured theme id and the vision-assist settings from config
    /// in a single re-publish. A combined Settings save (theme + font/scale/focus)
    /// must not raise <see cref="ThemeChanged"/> twice — that would speak the
    /// "Theme changed" announcement twice and re-render an open message's WebView2
    /// back-to-back. Both mutations land before one <see cref="Refresh"/>.
    /// </summary>
    public void ApplyAppearance(ConfigModel config)
    {
        ReadVisionSettings(config);
        var id = string.IsNullOrWhiteSpace(config.AppearanceThemeId) ? SystemThemeId : config.AppearanceThemeId.Trim();
        _activeThemeId = id;
        _configuredThemeId = id;
        Refresh(raiseEvent: true);
    }

    private void ReadVisionSettings(ConfigModel config)
    {
        _textScale = Math.Clamp(config.AppearanceTextScale, 1.0, 2.0);
        _fontFamilyOverride = config.AppearanceFontFamily?.Trim() ?? string.Empty;
        _underlineLinks = config.AppearanceUnderlineLinks;
        _thickFocus = config.AppearanceThickFocus;
    }

    public IReadOnlyList<ThemeDefinition> GetAvailableThemes()
    {
        var list = new List<ThemeDefinition>
        {
            new()
            {
                Id = SystemThemeId,
                Name = "System",
                Base = "light",
                IsBuiltIn = true,
            },
        };
        list.AddRange(_store.LoadBuiltIns());
        list.AddRange(_store.LoadUserThemes());
        return list;
    }

    // ── User theme management ─────────────────────────────────────────────────

    public ThemeDefinition ImportTheme(string filePath)
    {
        string json;
        try
        {
            json = System.IO.File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            throw new ThemeFormatException($"The file could not be read: {ex.Message}", ex);
        }

        var theme = ThemeDefinition.Parse(json);
        theme.IsBuiltIn = false;

        var existing = GetAvailableThemes();
        if (existing.Any(t => string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase)))
            theme.Id = Guid.NewGuid().ToString("N");
        if (existing.Any(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase)))
            theme.Name += " (imported)";

        _store.SaveUserTheme(theme);
        return theme;
    }

    public void ExportTheme(string themeId, string filePath)
    {
        var theme = GetAvailableThemes().FirstOrDefault(t =>
                string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No theme with id \"{themeId}\".");
        if (theme.Id == SystemThemeId)
            throw new InvalidOperationException("The System theme follows the OS and cannot be exported.");
        Helpers.AtomicFile.WriteAllText(filePath, theme.ToJson());
    }

    public void SaveUserTheme(ThemeDefinition theme)
    {
        _store.SaveUserTheme(theme);
        if (string.Equals(_activeThemeId, theme.Id, StringComparison.OrdinalIgnoreCase))
            Refresh(raiseEvent: true);
    }

    public void DeleteUserTheme(string themeId)
    {
        _store.DeleteUserTheme(themeId);
        if (string.Equals(_activeThemeId, themeId, StringComparison.OrdinalIgnoreCase))
            ApplyTheme(SystemThemeId);
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    private ThemeDefinition BuiltInById(string id) =>
        _store.LoadBuiltIns().First(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The complete built-in that supplies missing keys for a given base name.</summary>
    private ThemeDefinition BaseTheme(string baseName) =>
        BuiltInById(baseName == "dark" ? "dark" : "quill");

    private ThemeDefinition ResolveEffectiveTheme()
    {
        if (HighContrastProbe())
            return BuildHighContrastTheme();

        var id = _activeThemeId;
        if (string.Equals(id, SystemThemeId, StringComparison.OrdinalIgnoreCase))
            id = OsLightModeProbe() ? "quill" : "dark";

        var theme = _store.LoadBuiltIns()
                        .Concat(_store.LoadUserThemes())
                        .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (theme is null)
        {
            // Unknown configured id — fall back to system resolution, never throw.
            LogService.Log($"Theme id \"{id}\" not found; falling back to system.");
            theme = OsLightModeProbe() ? BuiltInById("quill") : BuiltInById("dark");
        }

        return theme.ResolveAgainst(BaseTheme(theme.Base));
    }

    /// <summary>
    /// In High Contrast QuickMail has no visual opinion: every token resolves to
    /// the live SystemColors palette. Status colors map to WindowText/Window — HC
    /// palettes don't guarantee distinguishable red/green, and status information
    /// is always conveyed in text as well.
    /// </summary>
    private static ThemeDefinition BuildHighContrastTheme()
    {
        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        var window     = Hex(SystemColors.WindowColor);
        var windowText = Hex(SystemColors.WindowTextColor);
        var control    = Hex(SystemColors.ControlColor);
        var grayText   = Hex(SystemColors.GrayTextColor);
        var border     = Hex(SystemColors.ActiveBorderColor);
        var hotTrack   = Hex(SystemColors.HotTrackColor);
        var highlight  = Hex(SystemColors.HighlightColor);
        var highlightText = Hex(SystemColors.HighlightTextColor);

        var theme = new ThemeDefinition
        {
            Id = "high-contrast",
            Name = "High Contrast",
            Base = "light",
            IsBuiltIn = true,
            // Font/scale still apply — typography matters more to low-vision HC users.
            Typography = new ThemeTypography(),
        };

        var colors = theme.Colors;
        colors["windowBackground"]    = window;
        colors["surfaceBackground"]   = control;
        colors["chromeBackground"]    = control;
        colors["inputBackground"]     = window;
        colors["border"]              = border;
        colors["borderSubtle"]        = border;
        colors["inputBorder"]         = border;
        colors["textPrimary"]         = windowText;
        colors["textSecondary"]       = grayText;
        colors["textDisabled"]        = grayText;
        colors["textOnAccent"]        = window;
        colors["accent"]              = hotTrack;
        colors["accentSubtle"]        = control;
        colors["hyperlink"]           = hotTrack;
        colors["selectionBackground"] = highlight;
        colors["selectionText"]       = highlightText;
        colors["selectionInactive"]   = control;
        colors["focusIndicator"]      = windowText;
        colors["error"]               = windowText;
        colors["errorBackground"]     = window;
        colors["warning"]             = windowText;
        colors["warningBackground"]   = window;
        colors["success"]             = windowText;
        colors["successBackground"]   = window;
        colors["info"]                = windowText;
        colors["infoBackground"]      = window;
        return theme;
    }

    // ── Publication ───────────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the effective theme; if it changed, atomically replaces the
    /// token dictionary, inserts/withdraws ThemedControls, and raises ThemeChanged.
    /// </summary>
    private void Refresh(bool raiseEvent)
    {
        var wasHc = _isHighContrast;
        _isHighContrast = HighContrastProbe();
        var resolved = ResolveEffectiveTheme();

        var signature = BuildSignature(resolved);
        if (signature == _lastPublishedSignature && wasHc == _isHighContrast)
            return; // effective theme unchanged — no dictionary churn, no event

        _resolved = resolved;
        _lastPublishedSignature = signature;

        PublishDictionary(resolved);

        if (raiseEvent)
        {
            // Raised on the Dispatcher thread per the interface contract.
            if (_dispatcher is null || _dispatcher.CheckAccess())
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            else
                _dispatcher.BeginInvoke(() => ThemeChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private string BuildSignature(ThemeDefinition resolved) =>
        $"{_isHighContrast}|{resolved.Id}|{string.Join(",", resolved.Colors.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))}"
        + $"|{resolved.Typography.FontFamily}|{resolved.Typography.MonoFontFamily}|{resolved.Typography.BaseFontSize}"
        + $"|{_textScale}|{_fontFamilyOverride}|{_underlineLinks}|{_thickFocus}";

    private void PublishDictionary(ThemeDefinition resolved)
    {
        var app = Application.Current;
        if (app is null)
            return; // unit tests without an Application — resolution still works

        var merged = app.Resources.MergedDictionaries;
        var tokens = BuildTokenDictionary(resolved);

        var index = _publishedTokens is null ? -1 : merged.IndexOf(_publishedTokens);
        if (index >= 0)
            merged[index] = tokens;          // atomic swap in place
        else
            merged.Add(tokens);              // first publication: after AccessibleStyles
        _publishedTokens = tokens;

        // ThemedControls: present in normal themes, withdrawn under HC so WPF's
        // built-in HC-aware templates take over.
        if (EnableThemedControls)
        {
            if (_isHighContrast)
            {
                if (_themedControlsInserted && _themedControls != null)
                {
                    merged.Remove(_themedControls);
                    _themedControlsInserted = false;
                }
            }
            else
            {
                _themedControls ??= new ResourceDictionary { Source = new Uri(ThemedControlsUri) };
                if (!_themedControlsInserted)
                {
                    merged.Add(_themedControls);
                    _themedControlsInserted = true;
                }
            }
        }
    }

    /// <summary>Builds the frozen token dictionary for a resolved theme. Internal for tests.</summary>
    internal ResourceDictionary BuildTokenDictionary(ThemeDefinition resolved)
    {
        var dict = new ResourceDictionary();

        foreach (var (jsonKey, resourceKey) in ThemeKeys.ColorTokens)
        {
            var (a, r, g, b) = ThemeDefinition.HexToArgb(resolved.Colors[jsonKey]);
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            dict[resourceKey] = brush;
        }

        var fontName = _fontFamilyOverride.Length > 0 ? _fontFamilyOverride : resolved.Typography.FontFamily;
        dict[ThemeKeys.FontFamily] = SafeFontFamily(fontName, "Segoe UI");
        dict[ThemeKeys.FontFamilyMono] = SafeFontFamily(resolved.Typography.MonoFontFamily, "Consolas");

        var baseSize = Math.Round(resolved.Typography.BaseFontSize * _textScale, 1);
        dict[ThemeKeys.FontSizeBase] = baseSize;
        dict[ThemeKeys.FontSizeSmall] = Math.Max(9, baseSize - 2);
        dict[ThemeKeys.FontSizeLarge] = baseSize + 2;
        dict[ThemeKeys.FontSizeHeader] = baseSize + 5;

        dict[ThemeKeys.FocusThickness] = _thickFocus ? 4.0 : 2.0;

        return dict;
    }

    private static FontFamily SafeFontFamily(string name, string fallback)
    {
        try
        {
            return new FontFamily(name);
        }
        catch (ArgumentException)
        {
            return new FontFamily(fallback);
        }
    }

    // ── WebView2 bridge ───────────────────────────────────────────────────────

    public string BuildMessageCss(bool forceOnContent)
    {
        var sb = new StringBuilder();
        var t = _resolved;

        var fontName = _fontFamilyOverride.Length > 0 ? _fontFamilyOverride : t.Typography.FontFamily;
        var fontSize = Math.Round(t.Typography.BaseFontSize * _textScale, 1)
            .ToString(CultureInfo.InvariantCulture);

        sb.Append(":root{");
        sb.Append("--qm-font:'").Append(fontName.Replace("'", string.Empty)).Append("',system-ui,sans-serif;");
        sb.Append("--qm-font-size:").Append(fontSize).Append("px;");
        if (!_isHighContrast)
        {
            // Color variables are omitted under HC — WebView2's forced-colors
            // handling is correct natively and must win.
            sb.Append("--qm-bg:").Append(t.ColorOf("windowBackground")).Append(';');
            sb.Append("--qm-text:").Append(t.ColorOf("textPrimary")).Append(';');
            sb.Append("--qm-text-muted:").Append(t.ColorOf("textSecondary")).Append(';');
            sb.Append("--qm-surface:").Append(t.ColorOf("surfaceBackground")).Append(';');
            sb.Append("--qm-border:").Append(t.ColorOf("border")).Append(';');
            sb.Append("--qm-accent:").Append(t.ColorOf("accent")).Append(';');
            sb.Append("--qm-link:").Append(t.ColorOf("hyperlink")).Append(';');
        }
        sb.Append('}');

        if (_underlineLinks)
            sb.Append("a{text-decoration:underline !important;}");

        if (forceOnContent && !_isHighContrast)
        {
            sb.Append("*{background-color:var(--qm-bg) !important;color:var(--qm-text) !important;}");
            sb.Append("a{color:var(--qm-link) !important;}");
        }

        return sb.ToString();
    }

    // ── OS probes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when Windows "choose your default app mode" is Light. .NET 8 has no
    /// managed API (Application.ThemeMode is .NET 9+), so read the registry value
    /// the OS itself uses.
    /// </summary>
    private static bool ReadOsLightMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }
}
