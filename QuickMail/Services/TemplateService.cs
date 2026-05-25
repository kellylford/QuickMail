using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class TemplateService : ITemplateService
{
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

    public async Task<MessageTemplate> AddAsync(MessageTemplate template)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            template.Id = _cache.Count > 0 ? _cache.Max(t => t.Id) + 1 : 1;
            _cache.Add(template);
            await SaveAsyncLocked();
            return template;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task UpdateAsync(MessageTemplate template)
    {
        await EnsureLoadedAsync();
        await _loadLock.WaitAsync();
        try
        {
            var existing = _cache.FirstOrDefault(t => t.Id == template.Id);
            if (existing != null)
            {
                existing.Title = template.Title;
                existing.Subject = template.Subject;
                existing.Body = template.Body;
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
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
