using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>A scheme from someone else keeps their credit through import, saving and export (#937).</summary>
public sealed class SoundSchemeCreditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"QM-CRED-{Guid.NewGuid():N}");

    public SoundSchemeCreditTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CreditSurvivesConfigIni()
    {
        var config = new ConfigModel { SoundSchemeId = "user-a" };
        config.SoundSchemes["user-a"] = new SoundScheme("user-a", "Rain", isBuiltIn: false,
            events: new Dictionary<string, string> { ["NewMail"] = "none" }, author: "Pat Lee", license: "CC-BY-4.0");
        var text = ConfigService.BuildFileText(config);
        var reloaded = ConfigService.ParseFile(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        var scheme = reloaded.SoundSchemes["user-a"];
        Assert.Equal("Pat Lee", scheme.Author);
        Assert.Equal("CC-BY-4.0", scheme.License);
        Assert.False(scheme.Events.ContainsKey("Author"));
        Assert.False(scheme.Events.ContainsKey("License"));
    }

    [Fact]
    public void ASchemeWithNoCreditWritesNone()
    {
        var config = new ConfigModel();
        config.SoundSchemes["user-a"] = new SoundScheme("user-a", "Mine", isBuiltIn: false);
        var text = ConfigService.BuildFileText(config);
        Assert.DoesNotContain("Author =", text, StringComparison.Ordinal);
        Assert.DoesNotContain("License =", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreditTravelsThroughImportSaveAndExport()
    {
        var sounds = new RecordingSoundService();
        var file = Path.Combine(_root, "in.qmsounds");
        SoundSchemeFile.Export(file, "Rain", new Dictionary<string, string> { ["NewMail"] = "builtin:wood/arrived" }, sounds,
            author: "Pat Lee", license: "CC-BY-4.0");

        var config = new StubConfigService();
        var vm = new SettingsViewModel(config, new StubCommandRegistry(), sounds: sounds);
        Assert.True(vm.ImportSchemeFrom(file));
        Assert.Equal("Pat Lee", vm.SelectedSoundScheme!.Author);

        // A copy keeps the credit too.
        Assert.True(vm.CreateScheme("Rain mine", vm.SelectedSoundScheme.Id));
        Assert.Equal("CC-BY-4.0", vm.SelectedSoundScheme!.License);

        vm.SaveCommand.Execute(null);
        Assert.All(config.Load().SoundSchemes.Values.Where(s => !s.IsBuiltIn), s => Assert.Equal("Pat Lee", s.Author));

        var exported = Path.Combine(_root, "out.qmsounds");
        vm.PickSchemeExportPathRequested = _ => exported;
        vm.ExportSchemeCommand.Execute(null);
        var again = SoundSchemeFile.Import(exported, sounds);
        Assert.Equal("Pat Lee", again.Author);
        Assert.Equal("CC-BY-4.0", again.License);
    }

    [Fact]
    public void TextIsNeverCutBetweenTheHalvesOfAnEmoji()
    {
        var text = new string('a', 59) + "\U0001F327";   // 61 UTF-16 units
        var cut = SoundSchemeFile.Cut(text, 60);
        Assert.Equal(59, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        Assert.Equal("abc", SoundSchemeFile.Cut("abc", 60));
    }
}
