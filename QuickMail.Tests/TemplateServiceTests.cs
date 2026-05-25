using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for TemplateService CRUD operations and edge cases.
/// Uses a real TemplateService pointed at a temp directory so we exercise
/// the full JSON serialize/deserialize path.
/// </summary>
public class TemplateServiceTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-TemplateTests-{Guid.NewGuid():N}");

    private static (TemplateService service, string dir) MakeService()
    {
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var service = new TemplateService(profile);
        return (service, dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    // ── LoadAllAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_ReturnsEmptyList_WhenNoFileExists()
    {
        var (service, dir) = MakeService();
        try
        {
            var templates = await service.LoadAllAsync();
            Assert.NotNull(templates);
            Assert.Empty(templates);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllAsync_ReturnsTemplatesSortedByTitle()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.AddAsync(new MessageTemplate { Title = "Zebra", Body = "z" });
            await service.AddAsync(new MessageTemplate { Title = "Apple", Body = "a" });
            await service.AddAsync(new MessageTemplate { Title = "Mango", Body = "m" });

            var templates = await service.LoadAllAsync();

            Assert.Equal(3, templates.Count);
            Assert.Equal("Apple", templates[0].Title);
            Assert.Equal("Mango", templates[1].Title);
            Assert.Equal("Zebra", templates[2].Title);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllAsync_SurvivesCorruptJson()
    {
        var (service, dir) = MakeService();
        try
        {
            // Write garbage to the templates file
            var filePath = Path.Combine(dir, "templates.json");
            await File.WriteAllTextAsync(filePath, "this is not valid json {{{");

            var templates = await service.LoadAllAsync();

            // Should return empty list, not throw
            Assert.NotNull(templates);
            Assert.Empty(templates);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllAsync_SurvivesEmptyFile()
    {
        var (service, dir) = MakeService();
        try
        {
            var filePath = Path.Combine(dir, "templates.json");
            await File.WriteAllTextAsync(filePath, "");

            var templates = await service.LoadAllAsync();

            Assert.NotNull(templates);
            Assert.Empty(templates);
        }
        finally { Cleanup(dir); }
    }

    // ── AddAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_AssignsIdAndPersists()
    {
        var (service, dir) = MakeService();
        try
        {
            var template = new MessageTemplate { Title = "Test", Subject = "Sub", Body = "Body text" };
            var result = await service.AddAsync(template);

            Assert.Equal(1, result.Id);
            Assert.Equal("Test", result.Title);

            // Verify persistence by loading from a fresh service instance
            var profile2 = new ProfileContext(dir);
            var service2 = new TemplateService(profile2);
            var all = await service2.LoadAllAsync();

            Assert.Single(all);
            Assert.Equal(1, all[0].Id);
            Assert.Equal("Test", all[0].Title);
            Assert.Equal("Sub", all[0].Subject);
            Assert.Equal("Body text", all[0].Body);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddAsync_AssignsIncrementalIds()
    {
        var (service, dir) = MakeService();
        try
        {
            var t1 = await service.AddAsync(new MessageTemplate { Title = "First" });
            var t2 = await service.AddAsync(new MessageTemplate { Title = "Second" });
            var t3 = await service.AddAsync(new MessageTemplate { Title = "Third" });

            Assert.Equal(1, t1.Id);
            Assert.Equal(2, t2.Id);
            Assert.Equal(3, t3.Id);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddAsync_HandlesMultipleAddsAndReloads()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.AddAsync(new MessageTemplate { Title = "A" });
            await service.AddAsync(new MessageTemplate { Title = "B" });

            // Reload from disk
            var profile2 = new ProfileContext(dir);
            var service2 = new TemplateService(profile2);
            var all = await service2.LoadAllAsync();

            Assert.Equal(2, all.Count);

            // Add more through second instance
            await service2.AddAsync(new MessageTemplate { Title = "C" });

            var all2 = await service2.LoadAllAsync();
            Assert.Equal(3, all2.Count);
        }
        finally { Cleanup(dir); }
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_UpdatesExistingTemplate()
    {
        var (service, dir) = MakeService();
        try
        {
            var original = await service.AddAsync(new MessageTemplate
            {
                Title = "Original",
                Subject = "Original Subject",
                Body = "Original Body"
            });

            original.Title = "Updated";
            original.Subject = "Updated Subject";
            original.Body = "Updated Body";
            await service.UpdateAsync(original);

            var all = await service.LoadAllAsync();
            Assert.Single(all);
            Assert.Equal("Updated", all[0].Title);
            Assert.Equal("Updated Subject", all[0].Subject);
            Assert.Equal("Updated Body", all[0].Body);
            Assert.Equal(original.Id, all[0].Id);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task UpdateAsync_DoesNothingForNonExistentId()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.AddAsync(new MessageTemplate { Title = "Real" });

            // Try to update a template with an ID that doesn't exist
            await service.UpdateAsync(new MessageTemplate
            {
                Id = 999,
                Title = "Ghost",
                Body = "Should not appear"
            });

            var all = await service.LoadAllAsync();
            Assert.Single(all);
            Assert.Equal("Real", all[0].Title);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task UpdateAsync_PersistsAcrossInstances()
    {
        var (service, dir) = MakeService();
        try
        {
            var t = await service.AddAsync(new MessageTemplate { Title = "T1", Body = "B1" });

            // Update through a second instance
            var profile2 = new ProfileContext(dir);
            var service2 = new TemplateService(profile2);
            t.Title = "T1 Modified";
            await service2.UpdateAsync(t);

            // Verify through first instance
            var all = await service.LoadAllAsync();
            Assert.Equal("T1 Modified", all[0].Title);
        }
        finally { Cleanup(dir); }
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesTemplate()
    {
        var (service, dir) = MakeService();
        try
        {
            var t1 = await service.AddAsync(new MessageTemplate { Title = "Keep" });
            var t2 = await service.AddAsync(new MessageTemplate { Title = "Delete Me" });

            await service.DeleteAsync(t2.Id);

            var all = await service.LoadAllAsync();
            Assert.Single(all);
            Assert.Equal("Keep", all[0].Title);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteAsync_DoesNothingForNonExistentId()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.AddAsync(new MessageTemplate { Title = "Only" });

            await service.DeleteAsync(999);

            var all = await service.LoadAllAsync();
            Assert.Single(all);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteAsync_HandlesDeleteAllAndReAdd()
    {
        var (service, dir) = MakeService();
        try
        {
            var t = await service.AddAsync(new MessageTemplate { Title = "Solo" });
            await service.DeleteAsync(t.Id);

            var all = await service.LoadAllAsync();
            Assert.Empty(all);

            // Re-add after deleting everything — cache is empty, so ID starts at 1
            var t2 = await service.AddAsync(new MessageTemplate { Title = "New" });
            Assert.Equal(1, t2.Id);
        }
        finally { Cleanup(dir); }
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAdds_DoNotCorruptData()
    {
        var (service, dir) = MakeService();
        try
        {
            var tasks = Enumerable.Range(0, 10).Select(i =>
                service.AddAsync(new MessageTemplate { Title = $"Template {i}", Body = $"Body {i}" })
            ).ToArray();

            await Task.WhenAll(tasks);

            var all = await service.LoadAllAsync();
            Assert.Equal(10, all.Count);
            // All IDs should be unique
            var ids = all.Select(t => t.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllAsync_ReturnsDefensiveCopy()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.AddAsync(new MessageTemplate { Title = "Original" });

            var list = await service.LoadAllAsync();
            list.Clear(); // Mutate the returned list

            // Original data should be unaffected
            var reloaded = await service.LoadAllAsync();
            Assert.Single(reloaded);
        }
        finally { Cleanup(dir); }
    }
}
