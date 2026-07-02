using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Persists saved views to <c>%AppData%\QuickMail\views.json</c>.</summary>
public class ViewService : IViewService
{
    private readonly string _dataFolder;
    private readonly string _viewsFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ViewService(ProfileContext profile)
    {
        _dataFolder = profile.ProfileDir;
        _viewsFile  = Path.Combine(profile.ProfileDir, "views.json");
    }

    public List<SavedView> Load()
    {
        if (!File.Exists(_viewsFile)) return [];
        try
        {
            var json = File.ReadAllText(_viewsFile);
            return JsonSerializer.Deserialize<List<SavedView>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(List<SavedView> views)
    {
        Directory.CreateDirectory(_dataFolder);
        // Atomic: a crash mid-write must not truncate views.json (all saved views).
        Helpers.AtomicFile.WriteAllText(_viewsFile, JsonSerializer.Serialize(views, JsonOptions));
    }
}
