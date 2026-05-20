using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Persists saved views to <c>%AppData%\QuickMail\views.json</c>.</summary>
public class ViewService : IViewService
{
    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");

    private static readonly string ViewsFile = Path.Combine(DataFolder, "views.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<SavedView> Load()
    {
        if (!File.Exists(ViewsFile)) return [];
        try
        {
            var json = File.ReadAllText(ViewsFile);
            return JsonSerializer.Deserialize<List<SavedView>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(List<SavedView> views)
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(ViewsFile, JsonSerializer.Serialize(views, JsonOptions));
    }
}
