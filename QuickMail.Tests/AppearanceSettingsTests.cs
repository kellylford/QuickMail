using System;
using System.IO;
using System.Linq;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>Appearance settings: config.ini round-trip and SettingsViewModel persistence.</summary>
public class AppearanceSettingsTests
{
    private static ProfileContext MakeTempProfile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QM-Appearance-{Guid.NewGuid():N}");
        return new ProfileContext(tempDir);
    }

    // ── ConfigService round-trip ──────────────────────────────────────────────

    [Fact]
    public void AppearanceKeys_RoundTripThroughConfigIni()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);

        var config = service.Load();
        config.AppearanceThemeId = "ember";
        config.AppearanceTextScale = 1.5;
        config.AppearanceFontFamily = "Verdana";
        config.AppearanceUnderlineLinks = true;
        config.AppearanceThickFocus = true;
        config.AppearanceForceMessageTheme = true;
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("ember", reloaded.AppearanceThemeId);
        Assert.Equal(1.5, reloaded.AppearanceTextScale);
        Assert.Equal("Verdana", reloaded.AppearanceFontFamily);
        Assert.True(reloaded.AppearanceUnderlineLinks);
        Assert.True(reloaded.AppearanceThickFocus);
        Assert.True(reloaded.AppearanceForceMessageTheme);
    }

    [Fact]
    public void FreshConfig_DefaultsToSystemThemeAtHundredPercent()
    {
        var profile = MakeTempProfile();
        var config = new ConfigService(profile).Load();

        Assert.Equal("system", config.AppearanceThemeId);
        Assert.Equal(1.0, config.AppearanceTextScale);
        Assert.Equal(string.Empty, config.AppearanceFontFamily);
        Assert.False(config.AppearanceUnderlineLinks);
        Assert.False(config.AppearanceThickFocus);
        Assert.False(config.AppearanceForceMessageTheme);

        // The written file documents the theme setting for hand-editing.
        var ini = File.ReadAllText(Path.Combine(profile.ProfileDir, "config.ini"));
        Assert.Contains("AppearanceThemeId = system", ini);
    }

    [Fact]
    public void TextScale_OutOfRangeValue_IsClampedOnLoad()
    {
        var profile = MakeTempProfile();
        var service = new ConfigService(profile);
        service.Save(service.Load()); // write defaults

        var path = Path.Combine(profile.ProfileDir, "config.ini");
        var ini = File.ReadAllText(path).Replace("AppearanceTextScale = 1", "AppearanceTextScale = 9");
        File.WriteAllText(path, ini);

        Assert.Equal(2.0, new ConfigService(profile).Load().AppearanceTextScale);
    }

    // ── SettingsViewModel ─────────────────────────────────────────────────────

    [Fact]
    public void SettingsViewModel_LoadsAppearanceFields()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.AppearanceThemeId = "dark";
        cfg.AppearanceTextScale = 1.25;
        cfg.AppearanceFontFamily = "Verdana";
        cfg.AppearanceUnderlineLinks = true;
        config.Save(cfg);

        var vm = new SettingsViewModel(config, new StubCommandRegistry(),
            new StubThemeService(), ["Arial", "Verdana"]);

        Assert.Equal("dark", vm.AppearanceThemeId);
        Assert.Equal(125, vm.AppearanceTextScalePercent);
        Assert.Equal("Verdana", vm.AppearanceFontOption);
        Assert.True(vm.AppearanceUnderlineLinks);
        Assert.Contains(vm.ThemeOptions, t => t.Id == "system");
        Assert.Equal(SettingsViewModel.ThemeDefaultFontLabel, vm.FontOptions[0]);
    }

    [Fact]
    public void SettingsViewModel_Save_PersistsAppearanceFields()
    {
        var config = new StubConfigService();
        var vm = new SettingsViewModel(config, new StubCommandRegistry(),
            new StubThemeService(), ["Arial", "Verdana"]);

        vm.AppearanceThemeId = "heather";
        vm.AppearanceTextScalePercent = 150;
        vm.AppearanceFontOption = "Arial";
        vm.AppearanceUnderlineLinks = true;
        vm.AppearanceThickFocus = true;
        vm.AppearanceForceMessageTheme = true;
        vm.SaveCommand.Execute(null);

        var saved = config.Load();
        Assert.Equal("heather", saved.AppearanceThemeId);
        Assert.Equal(1.5, saved.AppearanceTextScale);
        Assert.Equal("Arial", saved.AppearanceFontFamily);
        Assert.True(saved.AppearanceUnderlineLinks);
        Assert.True(saved.AppearanceThickFocus);
        Assert.True(saved.AppearanceForceMessageTheme);
    }

    [Fact]
    public void SettingsViewModel_ThemeDefaultFontOption_SavesEmptyString()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.AppearanceFontFamily = "Verdana";
        config.Save(cfg);

        var vm = new SettingsViewModel(config, new StubCommandRegistry(),
            new StubThemeService(), ["Verdana"]);
        vm.AppearanceFontOption = SettingsViewModel.ThemeDefaultFontLabel;
        vm.SaveCommand.Execute(null);

        Assert.Equal(string.Empty, config.Load().AppearanceFontFamily);
    }

    [Fact]
    public void SettingsViewModel_UninstalledConfiguredFont_StaysSelectable()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.AppearanceFontFamily = "Some Missing Font";
        config.Save(cfg);

        var vm = new SettingsViewModel(config, new StubCommandRegistry(),
            new StubThemeService(), ["Arial"]);

        Assert.Equal("Some Missing Font", vm.AppearanceFontOption);
        Assert.Contains("Some Missing Font", vm.FontOptions);
    }
}
