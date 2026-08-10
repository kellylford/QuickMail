using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// One-time conversion of the old "mark a saved view as the default" startup mechanism into
/// <see cref="ConfigModel.StartupFolder"/> (#516).
///
/// <para><c>SavedView.IsDefault</c> no longer exists on the model, so the flag is read straight out
/// of the raw <c>views.json</c> — deserializing would silently drop it. After a successful
/// migration the file is rewritten through <see cref="IViewService"/>, which serializes the current
/// model and therefore leaves <c>IsDefault</c> behind for good. That rewrite is what makes this
/// genuinely one-time: without it, a user who later cleared their startup folder would have the old
/// default view resurrected on the next launch.</para>
/// </summary>
public static class StartupFolderMigration
{
    /// <summary>
    /// Converts a default saved view in <paramref name="viewsJson"/> into startup settings on
    /// <paramref name="config"/>. Returns true when something was migrated.
    ///
    /// Runs only when no startup folder is configured — an explicit choice always wins over an
    /// inherited one. Malformed JSON is treated as "nothing to migrate"; this must never be the
    /// reason an app fails to start.
    /// </summary>
    public static bool ApplyToConfig(ConfigModel config, string? viewsJson)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(config.StartupFolder)) return false;
        if (string.IsNullOrWhiteSpace(viewsJson)) return false;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(viewsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var view in root.EnumerateArray())
        {
            if (view.ValueKind != JsonValueKind.Object) continue;
            if (!view.TryGetProperty("IsDefault", out var isDefault) ||
                isDefault.ValueKind != JsonValueKind.True) continue;

            var name = view.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty : string.Empty;

            // A view over a virtual folder — All Inboxes and friends. The key is already stored
            // without the NUL sentinel prefix, which is exactly the form StartupFolder wants.
            if (view.TryGetProperty("VirtualFolderKey", out var vfk) &&
                vfk.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(vfk.GetString()))
            {
                config.StartupFolder        = vfk.GetString()!;
                config.StartupFolderAccount = string.Empty;   // a virtual folder spans every account
                config.StartupFolderLabel   = VirtualFolderLabel(config.StartupFolder, name);
                return true;
            }

            var folders = ReadFolders(view);

            // A single real folder maps cleanly onto the new setting.
            if (folders.Count == 1)
            {
                config.StartupFolder        = folders[0].FullName;
                config.StartupFolderAccount = folders[0].AccountId.ToString();
                config.StartupFolderLabel   = string.IsNullOrWhiteSpace(folders[0].DisplayName)
                    ? name : folders[0].DisplayName;
                return true;
            }

            // A multi-folder view has no single folder to point at. Keep it working by reference
            // rather than silently dropping a setup the user deliberately built — no picker offers
            // or can create this form.
            if (folders.Count > 1 &&
                view.TryGetProperty("Id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String &&
                Guid.TryParse(idEl.GetString(), out var id))
            {
                config.StartupFolder        = $"view:{id}";
                config.StartupFolderAccount = string.Empty;   // a view is not scoped to one account
                config.StartupFolderLabel   = name;
                return true;
            }

            // A default view with no folders and no virtual key is the legacy "All Mail" shape,
            // which is already the default. Nothing to store.
            return false;
        }

        return false;
    }

    private static List<(Guid AccountId, string FullName, string DisplayName)> ReadFolders(JsonElement view)
    {
        var result = new List<(Guid, string, string)>();
        if (!view.TryGetProperty("Folders", out var folders) ||
            folders.ValueKind != JsonValueKind.Array) return result;

        foreach (var f in folders.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            var full = f.TryGetProperty("FolderFullName", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString() : null;
            if (string.IsNullOrEmpty(full)) continue;

            Guid.TryParse(
                f.TryGetProperty("AccountId", out var aid) && aid.ValueKind == JsonValueKind.String
                    ? aid.GetString() : null,
                out var accountId);

            var display = f.TryGetProperty("FolderDisplayName", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? string.Empty : string.Empty;

            result.Add((accountId, full, display));
        }
        return result;
    }

    /// <summary>Friendly name for a virtual folder key, falling back to the view's own name.</summary>
    private static string VirtualFolderLabel(string key, string viewName) => key switch
    {
        "AllMail"    => "All Mail",
        "AllInboxes" => "All Inboxes",
        "AllDrafts"  => "All Drafts",
        "AllSent"    => "All Sent",
        "AllArchive" => "All Archive",
        "AllTrash"   => "All Trash",
        "AllFlagged" => "All Flagged",
        "AllWatched" => "Watched Conversations",
        _            => string.IsNullOrWhiteSpace(viewName) ? key : viewName,
    };

    /// <summary>
    /// Reads <c>views.json</c> from the profile, migrates a default view into
    /// <paramref name="config"/>, persists the config, and rewrites <c>views.json</c> so the now
    /// model-less <c>IsDefault</c> property is dropped. Best-effort: any IO failure is logged and
    /// swallowed, because a migration must never be the reason the app will not start.
    /// </summary>
    public static bool Run(ProfileContext profile, ConfigModel config,
                           IConfigService configService, IViewService viewService)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(viewService);
        try
        {
            var path = Path.Combine(profile.ProfileDir, "views.json");
            if (!File.Exists(path)) return false;

            if (!ApplyToConfig(config, File.ReadAllText(path))) return false;

            configService.Save(config);

            // Re-serialize through the current model so the retired IsDefault property is dropped
            // for good. Guarded, because ViewService.Load() swallows every failure and returns an
            // empty list — a type mismatch on any property, or a transient sharing violation from a
            // backup agent, would otherwise have us write [] over every saved view the user has,
            // atomically and silently, on the first launch after upgrade. ApplyToConfig returning
            // true proves the file held at least one view, so an empty load here is provably a read
            // failure rather than an empty file. Leaving IsDefault in place costs nothing: the
            // config gate above already makes the migration one-time.
            var views = viewService.Load();
            if (views.Count > 0)
                viewService.Save(views);
            else
                LogService.Log("Startup migration: views.json migrated but re-read as empty — " +
                               "leaving the file untouched rather than overwriting it.");
            LogService.Log($"Startup migration: default view converted to StartupFolder " +
                           $"'{config.StartupFolder}' ({config.StartupFolderLabel}).");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Log("Startup migration: converting the default saved view", ex);
            return false;
        }
    }
}
