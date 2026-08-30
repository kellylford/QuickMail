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
        // "from" is the first field the FILE named, and keeps that standing relative to every
        // other field the file named. "not on server" leads the row ahead of it by design (#637).
        var lead = RowFieldCatalog.MessageForceEnabledOnUpgrade;
        Assert.Equal("from", layouts.Message.First(f => !lead.Contains(f.Id)).Id);
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

        var introduced = RowFieldCatalog.MessageForceEnabledOnUpgrade;
        Assert.Equal(["subject", "from", "date"],
            layouts.Message.Where(f => !introduced.Contains(f.Id)).Take(3).Select(f => f.Id));
        Assert.Equal(RowFieldCatalog.For(RowKind.Message).Count, layouts.Message.Count);
        // Everything the file did not name is appended disabled — except the location field,
        // which leads the row switched on (the test below).
        var known = new[] { "subject", "from", "date" };
        foreach (var appended in layouts.Message.Where(f => !introduced.Contains(f.Id) && !known.Contains(f.Id)))
            Assert.False(appended.Enabled, $"{appended.Id} should have been appended disabled");
    }

    [Fact]
    public void TheLocationField_LeadsTheRow_AndIsSwitchedOnForAnExistingLayout()
    {
        // New catalog fields normally arrive disabled, which is right for optional extras: the
        // file represents choices the user made. It is wrong for a field whose whole job is to say
        // a draft has not left this computer — off by default there means the signal never reaches
        // anyone who already has a layout, which is everyone who has opened the chooser. And where
        // it lands is where it is HEARD: appended, it came after the preview and the date for
        // exactly those users, while a fresh layout says it first (#637).
        File.WriteAllText(_file, """
        {
          "Message": [
            { "Id": "unread", "Enabled": true },
            { "Id": "from", "Enabled": true },
            { "Id": "subject", "Enabled": true }
          ],
          "Conversation": [],
          "SenderGroup": []
        }
        """);

        var layouts = _service.Load();
        var order = layouts.Message.Select(f => f.Id).ToList();

        Assert.True(layouts.Message.Single(f => f.Id == "notonserver").Enabled);
        Assert.Equal("notonserver", order[0]);
        Assert.True(order.IndexOf("notonserver") < order.IndexOf("from"));
    }

    [Fact]
    public void ALayoutUsingTheCombinedStatusField_IsNotMadeToSayItTwice()
    {
        // The legacy "status" field speaks the same state through ReadStatusLabel — "saved on this
        // computer, not yet on the server" — so switching this on as well makes every such row say
        // the same thing twice in a row, and having the information is the whole reason to force
        // it on. That user already has it.
        File.WriteAllText(_file, """
        {
          "Message": [
            { "Id": "status", "Enabled": true },
            { "Id": "from", "Enabled": true }
          ],
          "Conversation": [],
          "SenderGroup": []
        }
        """);

        var layouts = _service.Load();

        Assert.False(layouts.Message.Single(f => f.Id == "notonserver").Enabled);
    }

    [Fact]
    public void OnceIntroduced_TurningTheFieldOffAgainSticks()
    {
        // The one-time enable must be exactly that. A user who hears "not on server" and decides
        // they would rather not must not have it switched back on at every launch.
        var first = _service.Load();
        Assert.True(first.Message.Single(f => f.Id == "notonserver").Enabled);

        first.Message.Single(f => f.Id == "notonserver").Enabled = false;
        _service.Save(first);

        var second = _service.Load();
        Assert.False(second.Message.Single(f => f.Id == "notonserver").Enabled);
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

        // Everything disabled, except the location field switched on (#637) — a draft that has not
        // left this computer has to say so even in a layout that mentions nothing at all.
        var introduced = RowFieldCatalog.MessageForceEnabledOnUpgrade;
        Assert.All(layouts.Message.Where(f => !introduced.Contains(f.Id)), f => Assert.False(f.Enabled));
        Assert.All(layouts.Message.Where(f => introduced.Contains(f.Id)), f => Assert.True(f.Enabled));
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
        // Flag leads the fields that describe the MESSAGE. The one that says where the message
        // is comes before it, because where it is outranks anything about it (#637).
        var lead = RowFieldCatalog.MessageForceEnabledOnUpgrade;
        Assert.Equal("flag", layouts.Message.First(f => !lead.Contains(f.Id)).Id);
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
