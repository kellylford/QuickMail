using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class TemplateService : ITemplateService, IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private List<MessageTemplate> _cache = [];
    private bool _loaded;

    public TemplateService(ProfileContext profile)
    {
        _filePath = Path.Combine(profile.ProfileDir, "templates.json");
    }

    public async Task<List<MessageTemplate>> LoadAllAsync()
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            return _cache.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<MessageTemplate> AddAsync(MessageTemplate item)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            item.Id = _cache.Count > 0 ? _cache.Max(t => t.Id) + 1 : 1;
            _cache.Add(item);
            await SaveAsyncLocked();
            return item;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task UpdateAsync(MessageTemplate item)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var existing = _cache.FirstOrDefault(t => t.Id == item.Id);
            if (existing != null)
            {
                existing.Title = item.Title;
                existing.Subject = item.Subject;
                existing.Body = item.Body;
                await SaveAsyncLocked();
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task DeleteAsync(int id)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            _cache.RemoveAll(t => t.Id == id);
            await SaveAsyncLocked();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void Dispose()
    {
        _loadLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _loadLock.WaitAsync();
        try
        {
            if (_loaded) return;
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    _cache = JsonSerializer.Deserialize<List<MessageTemplate>>(json) ?? [];
                }
                catch (Exception ex)
                {
                    LogService.Debug($"TemplateService: failed to load templates: {ex.Message}");
                }
            }
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task SaveAsyncLocked()
    {
        var json = JsonSerializer.Serialize(_cache, WriteOptions);
        await Helpers.AtomicFile.WriteAllTextAsync(_filePath, json);
    }
}
