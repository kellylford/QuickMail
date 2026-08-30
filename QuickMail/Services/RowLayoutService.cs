using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Persists the spoken field layouts to <c>%AppData%\QuickMail\rowlayout.json</c>.
/// </summary>
public class RowLayoutService : IRowLayoutService
{
    private readonly string _dataFolder;
    private readonly string _layoutFile;
    private readonly IConfigService _configService;

    // Enums as names, so a hand-edited file reads "WhenTrue" rather than "0" and a future
    // member reordering cannot silently change what an existing file means.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public event EventHandler? LayoutsChanged;

    public RowLayoutService(ProfileContext profile, IConfigService configService)
    {
        _dataFolder    = profile.ProfileDir;
        _layoutFile    = Path.Combine(profile.ProfileDir, "rowlayout.json");
        _configService = configService;
    }

    public RowLayouts Load()
    {
        if (!File.Exists(_layoutFile)) return Defaults();

        try
        {
            var json    = File.ReadAllText(_layoutFile);
            var layouts = JsonSerializer.Deserialize<RowLayouts>(json, JsonOptions);
            if (layouts is null) return Defaults();

            // Drop ids this build does not know, append ones the file predates. It leaves the
            // user's arrangement alone with one deliberate exception: IntroduceOnce front-inserts
            // a field a release needs them to meet, once, and records that it has (#637).
            layouts.Reconcile();
            return layouts;
        }
        catch
        {
            // Corrupt file — back it up rather than deleting, and fall back to defaults so the
            // lists keep speaking. Mirrors FlagService.LoadFlagDefinitionsAsync.
            try
            {
                File.Move(_layoutFile, _layoutFile + $".bak-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    overwrite: false);
            }
            catch { /* ignore backup failure */ }
            return Defaults();
        }
    }

    public void Save(RowLayouts layouts)
    {
        Directory.CreateDirectory(_dataFolder);
        // Atomic: a crash mid-write must not truncate the file and leave rows unspoken.
        Helpers.AtomicFile.WriteAllText(_layoutFile, JsonSerializer.Serialize(layouts, JsonOptions));
        LayoutsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Seeds from the catalog. The legacy <c>AnnounceFlagStatus</c> setting is honoured exactly
    /// once here, so a user who had flag announcements off does not suddenly start hearing them;
    /// after the first save the layout owns that choice and the config key is no longer read.
    /// </summary>
    private RowLayouts Defaults()
    {
        var announceFlag = true;
        try { announceFlag = _configService.Load().AnnounceFlagStatus; }
        catch { /* config unreadable — fall back to the historical default */ }
        return RowFieldCatalog.DefaultLayouts(announceFlag);
    }
}
