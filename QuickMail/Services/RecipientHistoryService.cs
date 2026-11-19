using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// <see cref="IRecipientHistory"/> kept in <c>recipients.json</c> in the profile folder (#1037).
/// Holds at most <see cref="MaxRemembered"/> people; past that the least recently used are dropped,
/// matching the cap on prior recipients synced from Google and Microsoft.
/// </summary>
public sealed class RecipientHistoryService : IRecipientHistory, IDisposable
{
    internal const int MaxRemembered = 1000;

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private List<ContactModel> _people = [];
    private bool _loaded;

    public RecipientHistoryService(ProfileContext profile)
        : this(Path.Combine(profile.ProfileDir, "recipients.json")) { }

    internal RecipientHistoryService(string path) { _path = path; }

    /// <summary>Supplies the time a record is stamped with; replaced by tests.</summary>
    internal Func<long> UtcTicks { get; set; } = () => DateTimeOffset.UtcNow.UtcTicks;

    public async Task RecordAsync(IReadOnlyList<(string DisplayName, string EmailAddress)> recipients)
    {
        if (recipients.Count == 0) return;
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedLockedAsync();
            var now = UtcTicks();
            var changed = false;
            foreach (var (name, rawEmail) in recipients)
            {
                var email = rawEmail?.Trim() ?? string.Empty;
                if (email.Length == 0 || !email.Contains('@')) continue;
                var cleanName = CleanName(name, email);
                var existing = _people.FirstOrDefault(p => p.EmailAddress.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    _people.Add(new ContactModel { DisplayName = cleanName, EmailAddress = email, LastUsedTicks = now });
                }
                else
                {
                    if (cleanName.Length > 0) existing.DisplayName = cleanName;
                    existing.LastUsedTicks = now;
                }
                changed = true;
            }
            if (!changed) return;
            if (_people.Count > MaxRemembered)
                _people = _people.OrderByDescending(p => p.LastUsedTicks).Take(MaxRemembered).ToList();
            await SaveLockedAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ContactModel>> SearchAsync(string text, int max, CancellationToken ct = default)
    {
        var q = text.Trim();
        if (q.Length == 0 || max <= 0) return [];
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedLockedAsync();
            ct.ThrowIfCancellationRequested();
            // Copies, so a caller holding a result never sees a later record change it underneath.
            return _people
                .Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || p.EmailAddress.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastUsedTicks)
                .Take(max)
                .Select(p => new ContactModel { DisplayName = p.DisplayName, EmailAddress = p.EmailAddress, LastUsedTicks = p.LastUsedTicks })
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedLockedAsync();
            return _people.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // The file first: if it cannot be deleted, nothing is forgotten, rather than everyone
            // vanishing now and coming back at the next start.
            if (File.Exists(_path)) File.Delete(_path);
            _people = [];
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A name worth keeping: trimmed, and not just the address again (some clients put the address in
    /// the name), so the suggestion does not read the address twice.
    /// </summary>
    internal static string CleanName(string? name, string email)
    {
        var n = (name ?? string.Empty).Trim().Trim('"').Trim();
        return n.Equals(email, StringComparison.OrdinalIgnoreCase) ? string.Empty : n;
    }

    private async Task EnsureLoadedLockedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(_path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(_path);
            // Valid JSON can still hold nulls (a hand edit, a half-written file from an old build); a
            // null entry or name would throw on every search and take the address book's suggestions
            // down with it, so keep only usable entries.
            _people = (JsonSerializer.Deserialize<List<ContactModel?>>(json) ?? [])
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.EmailAddress))
                .Select(p => { p!.DisplayName ??= string.Empty; p.EmailAddress = p.EmailAddress.Trim(); return p; })
                .ToList();
        }
        catch (Exception ex)
        {
            // Nothing here is irreplaceable — it refills as messages are sent — so an unreadable file
            // is logged and replaced on the next record rather than blocking compose.
            LogService.Log("RecipientHistory: recipients.json could not be read; starting empty", ex);
            _people = [];
        }
    }

    private async Task SaveLockedAsync()
    {
        // Only what a suggestion needs; the contact's other fields are address-book provenance.
        var payload = _people.Select(p => new { p.DisplayName, p.EmailAddress, p.LastUsedTicks });
        await Helpers.AtomicFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(payload, _json));
    }

    public void Dispose() => _lock.Dispose();
}
