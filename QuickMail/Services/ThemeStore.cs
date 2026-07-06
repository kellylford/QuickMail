using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Loads the built-in themes from embedded resources and persists user themes as
/// one JSON file per theme in {profile}\themes\ — a theme is an individually
/// shareable artifact, unlike views.json which holds the whole collection.
/// A missing or corrupt user theme file is skipped with a log line and never
/// blocks startup.
/// </summary>
public class ThemeStore
{
    /// <summary>Embedded built-in theme resources, in display order.</summary>
    private static readonly string[] BuiltInResourceNames =
    {
        "QuickMail.Themes.BuiltIn.light.json",
        "QuickMail.Themes.BuiltIn.dark.json",
        "QuickMail.Themes.BuiltIn.ember.json",
        "QuickMail.Themes.BuiltIn.fjord.json",
        "QuickMail.Themes.BuiltIn.heather.json",
    };

    private readonly string _themesFolder;
    private IReadOnlyList<ThemeDefinition>? _builtIns;
    private IReadOnlyList<ThemeDefinition>? _userThemes;

    public ThemeStore(ProfileContext profile)
    {
        _themesFolder = Path.Combine(profile.ProfileDir, "themes");
    }

    /// <summary>The user themes folder ({profile}\themes). Created on first write.</summary>
    public string ThemesFolder => _themesFolder;

    /// <summary>The five built-in themes, fully parsed. Cached after first load.</summary>
    public IReadOnlyList<ThemeDefinition> LoadBuiltIns()
    {
        if (_builtIns != null) return _builtIns;

        var assembly = typeof(ThemeStore).Assembly;
        var list = new List<ThemeDefinition>(BuiltInResourceNames.Length);
        foreach (var resourceName in BuiltInResourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded theme resource missing: {resourceName}");
            using var reader = new StreamReader(stream);
            var theme = ThemeDefinition.Parse(reader.ReadToEnd());
            theme.IsBuiltIn = true;
            list.Add(theme);
        }
        _builtIns = list;
        return _builtIns;
    }

    /// <summary>
    /// All user themes from {profile}\themes\*.json, sorted by name.
    /// Files that fail to parse are skipped and logged. Cached after first load
    /// (mirroring <see cref="LoadBuiltIns"/>) and invalidated by
    /// <see cref="SaveUserTheme"/> / <see cref="DeleteUserTheme"/>, so the hot
    /// paths (theme apply, Next/Previous cycling, OS refresh) do no disk I/O.
    /// </summary>
    public IReadOnlyList<ThemeDefinition> LoadUserThemes()
    {
        if (_userThemes != null) return _userThemes;

        var result = new List<ThemeDefinition>();
        if (!Directory.Exists(_themesFolder))
            return _userThemes = result;

        foreach (var file in Directory.EnumerateFiles(_themesFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var theme = ThemeDefinition.Parse(ReadThemeFile(file));
                theme.IsBuiltIn = false;
                // A user file must not shadow a built-in id or duplicate another user id.
                if (LoadBuiltIns().Any(b => string.Equals(b.Id, theme.Id, StringComparison.OrdinalIgnoreCase))
                    || result.Any(t => string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    LogService.Log($"Theme file skipped (duplicate id \"{theme.Id}\"): {file}");
                    continue;
                }
                result.Add(theme);
            }
            catch (Exception ex)
            {
                LogService.Log($"Theme file skipped ({ex.Message}): {file}");
            }
        }
        return _userThemes = result.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Writes a user theme to {profile}\themes\{id}.json atomically.</summary>
    public void SaveUserTheme(ThemeDefinition theme)
    {
        if (theme.IsBuiltIn)
            throw new InvalidOperationException("Built-in themes cannot be saved to the themes folder.");
        Directory.CreateDirectory(_themesFolder);
        Helpers.AtomicFile.WriteAllText(PathForId(theme.Id), theme.ToJson());
        _userThemes = null; // invalidate cache; next load re-reads the folder
    }

    /// <summary>Deletes a user theme file. No-op if absent.</summary>
    public void DeleteUserTheme(string themeId)
    {
        var path = PathForId(themeId);
        if (File.Exists(path))
            File.Delete(path);
        _userThemes = null; // invalidate cache; next load re-reads the folder
    }

    private string PathForId(string id)
    {
        // Theme ids come from JSON files; sanitize so an id can't escape the folder.
        var safe = string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var result = Path.Combine(_themesFolder, safe + ".json");

        // Belt-and-suspenders containment: the resolved path must be a direct child
        // of the themes folder. The sanitization above already strips separators and
        // ':', so this can't fire today — it's here so a future change to id handling
        // can't silently reintroduce path traversal.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_themesFolder));
        var resolvedParent = Path.GetDirectoryName(Path.GetFullPath(result));
        if (!string.Equals(resolvedParent, folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Theme id \"{id}\" resolves to a path outside the themes folder.");

        return result;
    }

    /// <summary>
    /// Reads a theme file, rejecting anything larger than
    /// <see cref="ThemeDefinition.MaxFileBytes"/> before allocating. Shared by the
    /// folder loader and by import so an untrusted file can't force a large read.
    /// </summary>
    public static string ReadThemeFile(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > ThemeDefinition.MaxFileBytes)
            throw new ThemeFormatException(
                $"The theme file is too large ({length / 1024} KB). " +
                $"Theme files must be under {ThemeDefinition.MaxFileBytes / 1024} KB.");
        return File.ReadAllText(path);
    }
}
