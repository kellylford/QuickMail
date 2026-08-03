using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Persists watched conversations to <c>%AppData%\QuickMail\watches.json</c>.
/// <para>Shape deliberately mirrors <see cref="ViewService"/>: one JSON file in the profile
/// directory, atomic write, a corrupt or absent file degrades to an empty list rather than
/// throwing at startup.</para>
/// </summary>
public class WatchService : IWatchService
{
    private readonly string _dataFolder;
    private readonly string _watchesFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<WatchedConversation> _watches;

    // Match index over NormalizedSubject. IsWatched runs once per message per fetch — up to tens of
    // thousands of times on a large load — so the lookup must not be a scan. Rebuilt wholesale on
    // every mutation: the list is tiny, and rebuilding removes any chance of index drift.
    private HashSet<string> _index = new(StringComparer.OrdinalIgnoreCase);

    public WatchService(ProfileContext profile)
    {
        _dataFolder  = profile.ProfileDir;
        _watchesFile = Path.Combine(profile.ProfileDir, "watches.json");
        _watches     = Load();
        RebuildIndex();
    }

    /// <summary>
    /// Newest first. Two watches added in the same clock tick would otherwise order arbitrarily —
    /// <c>DateTimeOffset.UtcNow</c> has coarser resolution than consecutive calls — so insertion
    /// order breaks the tie, later insertion being newer.
    /// </summary>
    public IReadOnlyList<WatchedConversation> GetAll() =>
        _watches
            .Select((w, i) => (Watch: w, Index: i))
            .OrderByDescending(x => x.Watch.CreatedUtc)
            .ThenByDescending(x => x.Index)
            .Select(x => x.Watch)
            .ToList();

    public bool IsWatched(string subject)
    {
        // Early-out before normalizing. This runs once per message on every folder load — up to
        // 50,000 times on a large All Mail load, on the UI thread — and NormalizeSubject is a regex
        // loop. A user who has never watched anything must not pay for it.
        if (_index.Count == 0) return false;
        var key = ConversationBuilder.NormalizeSubject(subject ?? string.Empty);
        return key.Length > 0 && _index.Contains(key);
    }

    public bool Watch(string subject)
    {
        var key = ConversationBuilder.NormalizeSubject(subject ?? string.Empty);
        // An empty key would match every blank-subject message in every account, which reads as the
        // feature being broken rather than as a refusal. Store nothing; the caller announces why.
        if (key.Length == 0) return false;
        if (_index.Contains(key)) return false;

        _watches.Add(new WatchedConversation
        {
            NormalizedSubject = key,
            Label             = (subject ?? string.Empty).Trim(),
        });
        RebuildIndex();
        Save();
        return true;
    }

    public bool Unwatch(string subject)
    {
        var key = ConversationBuilder.NormalizeSubject(subject ?? string.Empty);
        if (key.Length == 0) return false;

        var removed = _watches.RemoveAll(
            w => string.Equals(w.NormalizedSubject, key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        RebuildIndex();
        Save();
        return true;
    }

    private void RebuildIndex() =>
        _index = new HashSet<string>(
            _watches.Select(w => w.NormalizedSubject).Where(s => !string.IsNullOrEmpty(s)),
            StringComparer.OrdinalIgnoreCase);

    private List<WatchedConversation> Load()
    {
        if (!File.Exists(_watchesFile)) return [];
        try
        {
            var json = File.ReadAllText(_watchesFile);
            var list = JsonSerializer.Deserialize<List<WatchedConversation>>(json) ?? [];
            // Drop entries whose key is empty or was hand-edited to an un-normalized form, so the
            // in-memory index can never hold a key that IsWatched would fail to match.
            foreach (var w in list)
                w.NormalizedSubject = ConversationBuilder.NormalizeSubject(w.NormalizedSubject ?? string.Empty);
            return list.Where(w => w.NormalizedSubject.Length > 0).ToList();
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_dataFolder);
        // Atomic: a crash mid-write must not truncate watches.json (every watched conversation).
        Helpers.AtomicFile.WriteAllText(_watchesFile, JsonSerializer.Serialize(_watches, JsonOptions));
    }
}
