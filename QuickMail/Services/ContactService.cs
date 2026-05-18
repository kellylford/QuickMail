using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class ContactService : IContactService
{
    private readonly string _filePath;
    private List<ContactModel> _cache = [];
    private bool _loaded = false;

    public ContactService(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "contacts.json");
    }

    public async Task UpsertContactAsync(ContactModel contact)
    {
        await EnsureLoadedAsync();
        var existing = _cache.FirstOrDefault(c => c.EmailAddress.Equals(contact.EmailAddress, StringComparison.OrdinalIgnoreCase));
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
            contact.Id = _cache.Count > 0 ? _cache.Max(c => c.Id) + 1 : 1;
            _cache.Add(contact);
        }
        await SaveAsync();
    }

    public async Task<List<ContactModel>> SearchContactsAsync(string prefix)
    {
        await EnsureLoadedAsync();
        var q = prefix.Trim();
        return string.IsNullOrEmpty(q)
            ? _cache.OrderByDescending(c => c.LastUsedTicks).ToList()
            : _cache.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.EmailAddress.Contains(q, StringComparison.OrdinalIgnoreCase))
              .OrderByDescending(c => c.LastUsedTicks)
              .Take(10)
              .ToList();
    }

    public async Task<List<ContactModel>> LoadAllContactsAsync()
    {
        await EnsureLoadedAsync();
        return _cache.OrderBy(c => c.DisplayName).ThenBy(c => c.EmailAddress).ToList();
    }

    public async Task DeleteContactAsync(int id)
    {
        await EnsureLoadedAsync();
        _cache.RemoveAll(c => c.Id == id);
        await SaveAsync();
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        if (File.Exists(_filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_filePath);
                _cache = JsonSerializer.Deserialize<List<ContactModel>>(json) ?? [];
            }
            catch { /* corrupt or invalid JSON — start fresh */ }
        }
        _loaded = true;
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json);
    }
}
