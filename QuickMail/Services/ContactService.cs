using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class ContactService : IContactService
{
    private readonly string _contactsFilePath;
    private readonly string _groupsFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private List<ContactModel> _contactsCache = [];
    private List<GroupModel>   _groupsCache   = [];
    private bool _loaded = false;

    public ContactService(ProfileContext profile)
    {
        _contactsFilePath = Path.Combine(profile.ProfileDir, "contacts.json");
        _groupsFilePath   = Path.Combine(profile.ProfileDir, "groups.json");
    }

    // ── Contacts ─────────────────────────────────────────────────────────────

    public async Task UpsertContactAsync(ContactModel contact)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var existing = _contactsCache.FirstOrDefault(c => c.EmailAddress.Equals(contact.EmailAddress, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // Update: preserve non-empty display name if new one is empty
                if (!string.IsNullOrWhiteSpace(contact.DisplayName))
                    existing.DisplayName = contact.DisplayName;
                existing.LastUsedTicks = contact.LastUsedTicks;
            }
            else
            {
                // New contact: assign an ID (max + 1)
                contact.Id = _contactsCache.Count > 0 ? _contactsCache.Max(c => c.Id) + 1 : 1;
                _contactsCache.Add(contact);
            }
            LogService.Debug($"[ContactService] UpsertContactAsync: email={contact.EmailAddress} assignedId={contact.Id} cacheCount={_contactsCache.Count}");
            await SaveContactsAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default)
    {
        await EnsureLoadedAsync();
        ct.ThrowIfCancellationRequested();
        var q = prefix.Trim();
        await _loadLock.WaitAsync(ct);
        try
        {
            return string.IsNullOrEmpty(q)
                ? _contactsCache.OrderByDescending(c => c.LastUsedTicks).ToList()
                : _contactsCache.Where(c =>
                    c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.EmailAddress.Contains(q, StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(c => c.LastUsedTicks)
                  .Take(10)
                  .ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<List<ContactModel>> LoadAllContactsAsync()
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            return _contactsCache.OrderBy(c => c.DisplayName).ThenBy(c => c.EmailAddress).ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task DeleteContactAsync(int id)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            // Remove the contact from the in-memory contact list and write the
            // contact file. Group memberships (MemberContactIds) that point at
            // this id are intentionally left in place — the picker surfaces the
            // mismatch via ResolvedMemberCount / MissingContactCount so the user
            // can repair it manually. Removing them automatically would lose the
            // membership history if the contact is later restored.
            _contactsCache.RemoveAll(c => c.Id == id);
            await SaveContactsAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    // ── Groups ───────────────────────────────────────────────────────────────

    public async Task<List<GroupModel>> LoadAllGroupsAsync()
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            // Recompute ResolvedMemberCount against the live contact list before
            // returning so the picker always reflects the current state. A group
            // is NOT rewritten to disk — stale MemberContactIds are kept so an
            // undo of a contact delete (if the user used Ctrl+Z elsewhere) can
            // re-resolve the membership.
            var byId = _contactsCache.ToDictionary(c => c.Id);
            foreach (var g in _groupsCache)
            {
                g.ResolvedMemberCount = g.MemberContactIds.Count(id => byId.ContainsKey(id));
            }
            return _groupsCache
                .OrderByDescending(g => g.LastUsedTicks)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<int> CreateGroupAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Group name cannot be empty.", nameof(name));

        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var group = new GroupModel
            {
                Id = _groupsCache.Count > 0 ? _groupsCache.Max(g => g.Id) + 1 : 1,
                Name = name.Trim(),
            };
            _groupsCache.Add(group);
            await SaveGroupsAsyncLocked();
            return group.Id;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task RenameGroupAsync(int id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Group name cannot be empty.", nameof(newName));

        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var group = _groupsCache.FirstOrDefault(g => g.Id == id)
                ?? throw new InvalidOperationException($"Group with id {id} not found.");
            group.Name = newName.Trim();
            await SaveGroupsAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task DeleteGroupAsync(int id)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            _groupsCache.RemoveAll(g => g.Id == id);
            await SaveGroupsAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task AddMemberAsync(int groupId, int contactId)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var group = _groupsCache.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            if (!group.MemberContactIds.Contains(contactId))
            {
                group.MemberContactIds.Add(contactId);
                await SaveGroupsAsyncLocked();
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task RemoveMemberAsync(int groupId, int contactId)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var group = _groupsCache.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            if (group.MemberContactIds.Remove(contactId))
                await SaveGroupsAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<List<int>> ListGroupsForContactAsync(int contactId)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            return _groupsCache
                .Where(g => g.MemberContactIds.Contains(contactId))
                .Select(g => g.Id)
                .ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<List<GroupModel>> SearchGroupsAsync(string prefix, CancellationToken ct = default)
    {
        await EnsureLoadedAsync();
        ct.ThrowIfCancellationRequested();
        var q = prefix.Trim();
        await _loadLock.WaitAsync(ct);
        try
        {
            var byId = _contactsCache.ToDictionary(c => c.Id);
            var matches = (string.IsNullOrEmpty(q)
                ? _groupsCache.AsEnumerable()
                : _groupsCache.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var g in matches)
                g.ResolvedMemberCount = g.MemberContactIds.Count(id => byId.ContainsKey(id));

            return matches
                .Where(g => g.ResolvedMemberCount > 0)   // skip empty groups — nothing to insert
                .OrderByDescending(g => g.LastUsedTicks)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task TouchGroupAsync(int groupId)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var group = _groupsCache.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            group.LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;
            await SaveGroupsAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    // ── Persistence helpers ──────────────────────────────────────────────────

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _loadLock.WaitAsync();
        try
        {
            // Check again after acquiring lock in case another thread loaded while we were waiting
            if (_loaded) return;
            _contactsCache = await LoadJsonAsync(_contactsFilePath, () => new List<ContactModel>());
            _groupsCache   = await LoadGroupsWithRecoveryAsync();
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<List<T>> LoadJsonAsync<T>(string path, Func<List<T>> emptyFactory) where T : class
    {
        if (!File.Exists(path)) return emptyFactory();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<T>>(json) ?? emptyFactory();
        }
        catch (Exception ex)
        {
            LogService.Debug($"ContactService: failed to load {Path.GetFileName(path)}: {ex.Message}");
            return emptyFactory();
        }
    }

    /// <summary>
    /// Loads groups.json. If the file is corrupt, the bad file is moved aside
    /// to <c>groups.json.bak-{timestamp}</c> and an empty list is returned so
    /// the app continues to function. The screen reader is not notified here
    /// because the call site is far from the UI thread; the spec message
    /// ("Address book groups file was unreadable…") is logged at /debug so
    /// users can opt in to see it.
    /// </summary>
    private async Task<List<GroupModel>> LoadGroupsWithRecoveryAsync()
    {
        if (!File.Exists(_groupsFilePath)) return new List<GroupModel>();
        try
        {
            var json = await File.ReadAllTextAsync(_groupsFilePath);
            return JsonSerializer.Deserialize<List<GroupModel>>(json) ?? new List<GroupModel>();
        }
        catch (Exception ex)
        {
            try
            {
                var backup = _groupsFilePath + $".bak-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                File.Move(_groupsFilePath, backup);
                LogService.Debug($"ContactService: groups.json was unreadable, moved to {backup}: {ex.Message}");
            }
            catch (Exception mvEx)
            {
                LogService.Debug($"ContactService: groups.json unreadable and backup move failed: {mvEx.Message}");
            }
            return new List<GroupModel>();
        }
    }

    /// <summary>
    /// Caller must already hold <see cref="_loadLock"/>. Without that gate, two concurrent
    /// upserts could each compute the same max(Id)+1 and produce duplicate IDs, and the
    /// temp-file rename could race.
    /// </summary>
    private async Task SaveContactsAsyncLocked()
    {
        await WriteJsonAtomicallyAsync(_contactsFilePath, _contactsCache);
    }

    private async Task SaveGroupsAsyncLocked()
    {
        await WriteJsonAtomicallyAsync(_groupsFilePath, _groupsCache);
    }

    private async Task WriteJsonAtomicallyAsync<T>(string path, T payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
