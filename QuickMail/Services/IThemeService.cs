using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Applies semantic theme tokens to the application and resolves the effective
/// theme from the configured id, the OS light/dark preference, and Windows High
/// Contrast. Exposes colors as hex strings only — never System.Windows.Media
/// types — so ViewModels can consume the theme without touching the View layer.
/// </summary>
public interface IThemeService : IDisposable
{
    /// <summary>The configured theme id, e.g. "system", "parchment", "dark", or a user theme id.</summary>
    string ConfiguredThemeId { get; }

    /// <summary>
    /// The display name to announce for the current selection. For the virtual
    /// System theme this reads "System, showing …" (qualified with the base it
    /// currently resolves to) so cycling to System never announces the resolved
    /// built-in name as if the user had selected it directly.
    /// </summary>
    string ConfiguredThemeName { get; }

    /// <summary>
    /// The effective theme after System/HC resolution — fully populated, hex strings only.
    /// Under High Contrast this reflects the live SystemColors palette.
    /// </summary>
    ThemeDefinition ResolvedTheme { get; }

    /// <summary>True while Windows High Contrast is active.</summary>
    bool IsHighContrastActive { get; }

    /// <summary>The user themes folder ({profile}\themes), for "Open themes folder".</summary>
    string UserThemesFolder { get; }

    /// <summary>
    /// All selectable themes in display order: System (virtual), the built-ins,
    /// then user themes.
    /// </summary>
    IReadOnlyList<ThemeDefinition> GetAvailableThemes();

    /// <summary>
    /// Fully resolves a theme by id (every token filled from its light/dark base)
    /// for display and description, without applying it or consulting High
    /// Contrast. The virtual "system" id resolves to whichever base the OS
    /// currently selects. An unknown id falls back to the system base.
    /// </summary>
    ThemeDefinition ResolveForPreview(string themeId);

    /// <summary>
    /// Applies a theme by id and re-publishes the token dictionary. An unknown id
    /// falls back to "system" without throwing. The caller persists the id to config.
    /// </summary>
    void ApplyTheme(string themeId);

    /// <summary>
    /// Imports a theme file, re-identifying on id collision and renaming on name
    /// collision, and saves it into the themes folder.
    /// Throws <see cref="ThemeFormatException"/> with a user-presentable message.
    /// </summary>
    ThemeDefinition ImportTheme(string filePath);

    /// <summary>Exports a theme (built-in or user) to a .quickmailtheme JSON file.</summary>
    void ExportTheme(string themeId, string filePath);

    /// <summary>Saves (creates or overwrites) a user theme and refreshes if it is active.</summary>
    void SaveUserTheme(ThemeDefinition theme);

    /// <summary>Deletes a user theme. If it was active, falls back to "system".</summary>
    void DeleteUserTheme(string themeId);

    /// <summary>
    /// Re-reads the vision-assist settings (text scale, font family override,
    /// underline links, thick focus) from config and re-publishes the tokens.
    /// Called after the Settings dialog saves.
    /// </summary>
    void ApplyVisionSettings(ConfigModel config);

    /// <summary>
    /// Applies the configured theme id and the vision-assist settings together in
    /// a single re-publish, so a combined Settings save raises <see cref="ThemeChanged"/>
    /// at most once (no double announcement, no double reading-pane re-render).
    /// The caller is responsible for persisting config.
    /// </summary>
    void ApplyAppearance(ConfigModel config);

    /// <summary>
    /// The token → CSS-variable bridge for WebView2 documents. Emits the
    /// <c>--qm-*</c> variables in a <c>:root</c> block; color variables are omitted
    /// under High Contrast so the browser's forced-colors handling wins. When
    /// <paramref name="forceOnContent"/> is true, appends rules that override
    /// sender-authored colors with theme colors.
    /// </summary>
    string BuildMessageCss(bool forceOnContent);

    /// <summary>
    /// Raised on the Dispatcher thread after the effective theme changes (theme
    /// switch, OS light/dark flip, or High Contrast transition).
    /// </summary>
    event EventHandler? ThemeChanged;
}
