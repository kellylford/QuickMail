using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// app-kit's shared sound scheme test files (SharedSoundTestData/README.md): QuickMail must read
/// every one as expected.json says, so it reads scheme files and gallery lists the way every other
/// Idea Place app does. A file expected.json lists only for The Chat Place is read as QuickMail
/// would, following the spec: a scheme for another app is refused by that app's name, and the
/// refusals that come before the app is known (an unreadable file, a newer format) are QuickMail's.
/// </summary>
public sealed partial class SharedSoundTestDataTests
{
    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "SharedSoundTestData", name);

    private static JsonElement Expected(string section)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DataPath("expected.json")));
        return doc.RootElement.GetProperty(section).Clone();
    }

    private static string TestKey => File.ReadAllText(DataPath("test-key.pub.pem"));

    public static TheoryData<string> SchemeFiles() => [.. Expected("schemes").EnumerateObject().Select(p => p.Name)];

    public static TheoryData<string> IndexFiles() => [.. Expected("indexes").EnumerateObject().Select(p => p.Name)];

    [Fact]
    public void EveryTestFileIsInExpected()
    {
        var listed = Expected("schemes").EnumerateObject().Select(p => p.Name)
            .Concat(Expected("indexes").EnumerateObject().Select(p => p.Name)).ToHashSet(StringComparer.Ordinal);
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SharedSoundTestData"))
            .Select(Path.GetFileName)
            .Where(n => n!.EndsWith(".signed", StringComparison.Ordinal) || n.EndsWith("sounds", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.Contains(f!, listed));
        Assert.Contains("quickmail", Expected("schemes").GetProperty("qm-0869.qmsounds").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void QuickMailsEventsAreTheOnesAppKitLists()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DataPath("apps.json")));
        var app = doc.RootElement.GetProperty("apps").GetProperty("quickmail");
        Assert.Equal(SoundSchemeFile.FileExtension, app.GetProperty("extension").GetString());
        var events = app.GetProperty("events").EnumerateArray()
            .Select(e => (e.GetProperty("key").GetString(), e.GetProperty("label").GetString(), e.GetProperty("sound").GetString()))
            .ToList();
        Assert.Equal(events, SoundCatalog.Events.Select(e => ((string?)e.ConfigKey, (string?)e.Label, (string?)e.BuiltInSound)).ToList());
    }

    // ── Scheme files ───────────────────────────────────────────────────────────

    /// <summary>What QuickMail must get from a file expected.json gives only The Chat Place's result for.</summary>
    private static JsonElement ForQuickMail(JsonElement cases)
    {
        if (cases.TryGetProperty("quickmail", out var mine)) return mine;
        var theirs = cases.GetProperty("thechatplace");
        string refused;
        if (!theirs.TryGetProperty("refused", out var message))
        {
            // The Chat Place reads it, so it's The Chat Place's: QuickMail refuses it by that name.
            refused = SoundSchemeFile.ForAnotherApp("The Chat Place");
        }
        else if (OtherAppRefusal().Match(message.GetString()!) is { Success: true } other)
        {
            Assert.NotEqual("QuickMail", other.Groups["app"].Value);   // that would be a file QuickMail reads; expected.json lists those
            refused = SoundSchemeFile.ForAnotherApp(other.Groups["app"].Value);
        }
        else
        {
            // Refused before the app is known: unreadable, or newer. The same words, for QuickMail.
            refused = message.GetString()!.Replace("The Chat Place", "QuickMail", StringComparison.Ordinal);
        }
        return JsonDocument.Parse(JsonSerializer.Serialize(new { refused })).RootElement.Clone();
    }

    [GeneratedRegex("^This is a sound scheme for (?<app>.+), so The Chat Place can't use it\\.$")]
    private static partial Regex OtherAppRefusal();

    [Theory]
    [MemberData(nameof(SchemeFiles))]
    public void ASchemeFileReadsAsAppKitSays(string file)
    {
        var expected = ForQuickMail(Expected("schemes").GetProperty(file));

        if (expected.TryGetProperty("refused", out var refused))
        {
            var ex = Assert.Throws<SoundSchemeFileException>(() => SoundSchemeFile.Read(DataPath(file)));
            Assert.Equal(refused.GetString(), ex.Message);
            return;
        }

        var read = SoundSchemeFile.Read(DataPath(file));
        Assert.Equal(expected.GetProperty("name").GetString(), read.Name);
        Assert.Equal(Text(expected, "author"), read.Author);
        Assert.Equal(Text(expected, "license"), read.License);
        Assert.Equal(Text(expected, "description"), read.Description);
        var events = expected.GetProperty("events").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(events.OrderBy(e => e.Key, StringComparer.Ordinal), read.Events.OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.Equal(expected.GetProperty("sounds").EnumerateArray().Select(s => s.GetString()!).Order(StringComparer.Ordinal),
            read.Sounds.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expected.GetProperty("dropped").GetInt32(), read.Dropped);
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.GetProperty(name).ValueKind == JsonValueKind.Null ? null : obj.GetProperty(name).GetString();

    // ── Gallery lists ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(IndexFiles))]
    public void AGalleryListReadsAsAppKitSays(string file)
    {
        var cases = Expected("indexes").GetProperty(file);
        // Every list here is The Chat Place's unless it says "quickmail", so for QuickMail one it
        // can't trust is still one it can't trust, and one The Chat Place reads is another app's.
        // A newer format is found before whose list it is, so it still says to update.
        var expected = cases.TryGetProperty("quickmail", out var mine) ? mine
            : cases.GetProperty("thechatplace") is { ValueKind: JsonValueKind.String } theirs ? theirs
            : JsonDocument.Parse("\"untrusted\"").RootElement.Clone();
        var signed = File.ReadAllBytes(DataPath(file));

        if (expected.ValueKind == JsonValueKind.String)
        {
            var ex = Assert.Throws<SoundGalleryException>(() => SoundGalleryService.ReadIndex(signed, TestKey));
            var kind = expected.GetString() switch
            {
                "untrusted" => SoundGalleryProblem.Untrusted,
                "needsUpdate" => SoundGalleryProblem.NeedsUpdate,
                var other => throw new InvalidOperationException("expected.json has a refusal these tests don't know: " + other),
            };
            Assert.Equal(kind, ex.Kind);
            return;
        }

        var entries = SoundGalleryService.ReadIndex(signed, TestKey);
        Assert.Equal(expected.GetProperty("ids").EnumerateArray().Select(i => i.GetString()), entries.Select(e => e.Id));
        if (expected.TryGetProperty("eventsWithSound", out var counts))
            Assert.Equal(counts.EnumerateArray().Select(c => (int?)c.GetInt32()), entries.Select(e => e.EventsWithSound));
    }

    [Fact]
    public void TheListQuickMailReadsHasEveryCheckedField()
    {
        var entry = Assert.Single(SoundGalleryService.ReadIndex(File.ReadAllBytes(DataPath("index-other-app.signed")), TestKey));
        Assert.Equal("wrong-ext-aaaaaaaaaaaaaaaa.qmsounds", entry.File);
        Assert.Equal(SoundCatalog.Events.Count, entry.EventsWithSound);   // 99 in the list, kept to QuickMail's number of events
        Assert.Equal("Test Author", entry.Author);
        Assert.Equal("CC0-1.0", entry.License);
    }
}
