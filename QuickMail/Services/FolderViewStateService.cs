using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Persists per-folder message-list presentation to <c>%AppData%\QuickMail\folderviews.json</c>.
/// Modelled on <see cref="ViewService"/>: profile-dir JSON, atomic write, unreadable file treated
/// as empty rather than fatal.
/// </summary>
public class FolderViewStateService : IFolderViewStateService
{
    private readonly string _dataFolder;
    private readonly string _stateFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private Dictionary<string, Entry>? _cache;

    /// <summary>
    /// On-disk shape. String-valued rather than enum ordinals so that reordering
    /// <see cref="ViewMode"/>, <see cref="MessageFilter"/> or <see cref="MessageSort"/> cannot
    /// silently repoint existing entries. These are the same strings <c>views.json</c> uses.
    /// </summary>
    private sealed class Entry
    {
        public string  Mode         { get; set; } = "messages";
        public string  Filter       { get; set; } = "all";
        public string? FlagFilterId { get; set; }
        public string  Sort         { get; set; } = "dateDesc";
        public int?    DayLimit     { get; set; }
    }

    public FolderViewStateService(ProfileContext profile)
    {
        _dataFolder = profile.ProfileDir;
        _stateFile  = Path.Combine(profile.ProfileDir, "folderviews.json");
    }

    /// <summary>
    /// Account id is part of the key because virtual folders (All Mail, All Inboxes) carry
    /// <see cref="Guid.Empty"/> and could otherwise collide with a real folder of the same name.
    /// View sentinels (<c>\0View:{id}</c>) key like anything else, which is what lets a
    /// multi-folder view's folder set carry its own remembered presentation with no special case.
    /// </summary>
    private static string Key(Guid accountId, string folderFullName) =>
        $"{accountId:N}|{folderFullName}";

    private Dictionary<string, Entry> Cache
    {
        get
        {
            if (_cache != null) return _cache;

            if (!File.Exists(_stateFile))
                return _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);

            try
            {
                var json = File.ReadAllText(_stateFile);
                _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json)
                         ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
            catch
            {
                // Malformed or unreadable — every folder falls back to the global default, which
                // is a working app rather than a startup failure. Matches ViewService.Load.
                _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
            }

            return _cache;
        }
    }

    public ListState? Recall(Guid accountId, string folderFullName)
    {
        if (string.IsNullOrEmpty(folderFullName)) return null;
        if (!Cache.TryGetValue(Key(accountId, folderFullName), out var e)) return null;

        return new ListState(
            ConfigModel.ParseViewMode(e.Mode),
            ConfigModel.ParseFilter(e.Filter),
            string.IsNullOrEmpty(e.FlagFilterId) ? null : e.FlagFilterId,
            ConfigModel.ParseSort(e.Sort),
            e.DayLimit);
    }

    public void Remember(Guid accountId, string folderFullName, ListState state)
    {
        if (string.IsNullOrEmpty(folderFullName)) return;

        Cache[Key(accountId, folderFullName)] = new Entry
        {
            Mode         = ConfigModel.ToConfigString(state.Mode),
            Filter       = ConfigModel.ToConfigString(state.Filter),
            FlagFilterId = state.FlagFilterId,
            Sort         = ConfigModel.ToConfigString(state.Sort),
            DayLimit     = state.DayLimit,
        };
        Persist();
    }

    public void Forget(Guid accountId, string folderFullName)
    {
        if (string.IsNullOrEmpty(folderFullName)) return;
        if (Cache.Remove(Key(accountId, folderFullName)))
            Persist();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(_dataFolder);
            Helpers.AtomicFile.WriteAllText(_stateFile, JsonSerializer.Serialize(Cache, JsonOptions));
        }
        catch
        {
            // A presentation preference is not worth taking the app down for. The in-memory cache
            // still holds the change, so the session behaves correctly; only the restart is lost.
        }
    }
}
