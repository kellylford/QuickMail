using System;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Persistence for the spoken field layouts. The failure modes matter more than the happy path:
/// a layout that silently resets, or a corrupt file that takes the message list's accessible names
/// down with it, are both worse than having no feature at all.
/// </summary>
public class RowLayoutServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly RowLayoutService _service;
    private readonly string _file;

    public RowLayoutServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qm-rowlayout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file    = Path.Combine(_dir, "rowlayout.json");
        _service = new RowLayoutService(new ProfileContext(_dir), new StubConfigService());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    // ── load ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MissingFile_YieldsCatalogDefaults()
    {
        var layouts = _service.Load();

        Assert.Equal(
            RowFieldCatalog.DefaultLayout(RowKind.Message).Select(f => f.Id),
            layouts.Message.Select(f => f.Id));
        Assert.True(layouts.Message.First(f => f.Id == "from").Enabled);
    }

    [Fact]
    public void RoundTrips_OrderEnabledAndSpeakMode()
    {
        var layouts = _service.Load();
        var message = layouts.Message;

        // Move "date" to the front, disable preview, set unread to Always.
        var date = message.First(f => f.Id == "date");
        message.Remove(date);
        message.Insert(0, date);
        message.First(f => f.Id == "preview").Enabled = false;
        var unread = message.First(f => f.Id == "unread");
        unread.Enabled   = true;
        unread.SpeakMode = SpeakMode.Always;

        _service.Save(layouts);
        var reloaded = _service.Load();

        Assert.Equal("date", reloaded.Message[0].Id);
        Assert.False(reloaded.Message.First(f => f.Id == "preview").Enabled);
        Assert.Equal(SpeakMode.Always, reloaded.Message.First(f => f.Id == "unread").SpeakMode);
        Assert.Equal(message.Select(f => f.Id), reloaded.Message.Select(f => f.Id));
    }

    [Fact]
    public void SpeakModeIsPersistedByName_NotOrdinal()
    {
        var layouts = _service.Load();
        layouts.Message.First(f => f.Id == "unread").SpeakMode = SpeakMode.Always;
        _service.Save(layouts);

        // Reordering the enum in a future version must not silently change existing files.
        Assert.Contains("\"Always\"", File.ReadAllText(_file), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveRaisesLayoutsChanged()
    {
        var fired = 0;
        _service.LayoutsChanged += (_, _) => fired++;

        _service.Save(_service.Load());

        Assert.Equal(1, fired);
    }

    // ── forward and backward compatibility ────────────────────────────────────

    [Fact]
    public void UnknownFieldId_IsDroppedSilently()
    {
        File.WriteAllText(_file, """
        {
          "Message": [
            { "Id": "from", "Enabled": true, "SpeakMode": "WhenTrue" },
            { "Id": "a-field-from-the-future", "Enabled": true, "SpeakMode": "WhenTrue" }
          ],
          "Conversation": [],
          "SenderGroup": []
        }
        """);

        var layouts = _service.Load();

        Assert.DoesNotContain(layouts.Message, f => f.Id == "a-field-from-the-future");
        Assert.Equal("from", layouts.Message[0].Id);
    }

    [Fact]
    public void FieldAddedSinceTheFileWasWritten_IsAppendedDisabled()
    {
        // A file that only knows about three fields must not lose the rest, and the new ones must
        // not barge into the middle of an order the user arranged.
        File.WriteAllText(_file, """
        {
          "Message": [
            { "Id": "subject", "Enabled": true },
            { "Id": "from", "Enabled": true },
            { "Id": "date", "Enabled": true }
          ],
          "Conversation": [],
          "SenderGroup": []
        }
        """);

        var layouts = _service.Load();

        Assert.Equal(["subject", "from", "date"], layouts.Message.Take(3).Select(f => f.Id));
        Assert.Equal(RowFieldCatalog.For(RowKind.Message).Count, layouts.Message.Count);
        foreach (var appended in layouts.Message.Skip(3))
            Assert.False(appended.Enabled, $"{appended.Id} should have been appended disabled");
    }

    [Fact]
    public void DuplicateIds_AreCollapsedToTheFirst()
    {
        File.WriteAllText(_file, """
        {
          "Message": [
            { "Id": "from", "Enabled": true },
            { "Id": "from", "Enabled": false }
          ],
          "Conversation": [],
          "SenderGroup": []
        }
        """);

        var layouts = _service.Load();

        Assert.Single(layouts.Message, f => f.Id == "from");
        Assert.True(layouts.Message.First(f => f.Id == "from").Enabled);
    }

    [Fact]
    public void EmptyLayoutsInFile_AreRepopulatedFromTheCatalogDisabled()
    {
        File.WriteAllText(_file, """{ "Message": [], "Conversation": [], "SenderGroup": [] }""");

        var layouts = _service.Load();

        Assert.Equal(RowFieldCatalog.For(RowKind.Message).Count, layouts.Message.Count);
        Assert.All(layouts.Message, f => Assert.False(f.Enabled));
    }

    // ── corruption ────────────────────────────────────────────────────────────

    [Fact]
    public void CorruptFile_IsBackedUpAndDefaultsReturned()
    {
        File.WriteAllText(_file, "{ this is not json");

        var layouts = _service.Load();

        Assert.True(layouts.Message.First(f => f.Id == "from").Enabled);
        Assert.NotEmpty(Directory.GetFiles(_dir, "rowlayout.json.bak-*"));
        // Backed up, not deleted — the user's arrangement is recoverable by hand.
        Assert.False(File.Exists(_file));
    }

    // ── the legacy AnnounceFlagStatus migration ───────────────────────────────

    [Fact]
    public void LegacyAnnounceFlagStatusOff_SeedsTheFlagFieldDisabled()
    {
        var service = new RowLayoutService(
            new ProfileContext(_dir), new StubConfigService { AnnounceFlagStatus = false });

        var layouts = service.Load();

        Assert.False(layouts.Message.First(f => f.Id == "flag").Enabled);
        // Only the flag is affected; everything else keeps the historical order.
        Assert.True(layouts.Message.First(f => f.Id == "from").Enabled);
        Assert.Equal("flag", layouts.Message[0].Id);
    }

    [Fact]
    public void OnceSaved_TheLayoutOwnsTheFlagChoice_NotTheLegacySetting()
    {
        var cfg     = new StubConfigService { AnnounceFlagStatus = false };
        var service = new RowLayoutService(new ProfileContext(_dir), cfg);

        var layouts = service.Load();
        layouts.Message.First(f => f.Id == "flag").Enabled = true;
        service.Save(layouts);

        // Legacy key still says "off", but the saved layout wins from here on.
        Assert.True(service.Load().Message.First(f => f.Id == "flag").Enabled);
    }

    private sealed class StubConfigService : IConfigService
    {
        public bool AnnounceFlagStatus { get; init; } = true;
        public ConfigModel Load() => new() { AnnounceFlagStatus = AnnounceFlagStatus };
        public void Save(ConfigModel config) { }
    }
}
