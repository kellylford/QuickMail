using System.Collections.Generic;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();

        var vm = new SettingsViewModel(configService, registry);

        Assert.Equal(3, vm.PreviewLines);
        Assert.True(vm.ShowMessageStatus);
        Assert.Equal("messages", vm.ViewMode);
        Assert.Equal(30, vm.SyncDays);
        Assert.Equal(500, vm.InitialSyncCount);
        Assert.Empty(vm.HotkeyRows);
    }

    [Fact]
    public void Save_PersistsChanges()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();
        var vm = new SettingsViewModel(configService, registry);

        vm.PreviewLines = 5;
        vm.ShowMessageStatus = false;
        vm.ViewMode = "conversations";
        vm.SyncDays = 7;
        vm.InitialSyncCount = 100;

        vm.SaveCommand.Execute(null);

        var loadedConfig = configService.Load();
        Assert.Equal(5, loadedConfig.PreviewLines);
        Assert.False(loadedConfig.ShowMessageStatus);
        Assert.Equal("conversations", loadedConfig.ViewMode);
        Assert.Equal(7, loadedConfig.SyncDays);
        Assert.Equal(100, loadedConfig.InitialSyncCount);
    }

    [Fact]
    public void SaveWithHotkeys_PersistsHotkeysInConfig()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();
        registry.RegisterTestCommand("test.cmd1", "Test", "Command 1");

        var vm = new SettingsViewModel(configService, registry);

        Assert.Single(vm.HotkeyRows);
        var row = vm.HotkeyRows[0];
        row.SetCustomBinding(Key.K, ModifierKeys.Control | ModifierKeys.Shift);

        vm.SaveCommand.Execute(null);

        var loadedConfig = configService.Load();
        Assert.Single(loadedConfig.CustomHotkeys);
        var binding = loadedConfig.CustomHotkeys[0];
        Assert.Equal("test.cmd1", binding.CommandId);
        Assert.Equal("Ctrl+Shift+K", binding.Gesture);
    }

    [Fact]
    public void ClearHotkey_RemovesCustomBinding()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();
        registry.RegisterTestCommand("test.cmd", "Test", "Command");

        var vm = new SettingsViewModel(configService, registry);
        var row = vm.HotkeyRows[0];

        row.SetCustomBinding(Key.A, ModifierKeys.Control);
        Assert.True(row.HasCustomBinding);
        Assert.Equal("Ctrl+A", row.CustomGesture);

        vm.ClearHotkeyCommand.Execute(row);

        Assert.False(row.HasCustomBinding);
        Assert.Empty(row.CustomGesture);
    }

    [Fact]
    public void HotkeyRow_ToBinding_ReturnsCorrectBinding()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();
        registry.RegisterTestCommand("test.cmd", "Test", "Command");

        var vm = new SettingsViewModel(configService, registry);
        var row = vm.HotkeyRows[0];
        row.SetCustomBinding(Key.S, ModifierKeys.Control | ModifierKeys.Shift);

        var binding = row.ToBinding();

        Assert.Equal("test.cmd", binding.CommandId);
        Assert.Equal("Ctrl+Shift+S", binding.Gesture);
    }

    [Fact]
    public void LoadExistingHotkeys_PopulatesCustomBindings()
    {
        var configService = new StubConfigService();
        var cfg = configService.Load();
        cfg.CustomHotkeys = new List<HotkeyBinding>
        {
            new() { CommandId = "test.cmd", Gesture = "Ctrl+G" }
        };
        configService.Save(cfg);

        var registry = new StubCommandRegistry();
        registry.RegisterTestCommand("test.cmd", "Test", "Command");

        var vm = new SettingsViewModel(configService, registry);

        Assert.Single(vm.HotkeyRows);
        var row = vm.HotkeyRows[0];
        Assert.True(row.HasCustomBinding);
        Assert.Equal("Ctrl+G", row.CustomGesture);
    }

    [Fact]
    public void AnnounceFlagStatus_DefaultsToTrue()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        Assert.True(vm.AnnounceFlagStatus);
    }

    [Fact]
    public void AnnounceFlagStatus_LoadAndSave_RoundTrips()
    {
        var configService = new StubConfigService();

        var vmSave = new SettingsViewModel(configService, new StubCommandRegistry());
        vmSave.AnnounceFlagStatus = false;
        vmSave.SaveCommand.Execute(null);

        var vmLoad = new SettingsViewModel(configService, new StubCommandRegistry());
        Assert.False(vmLoad.AnnounceFlagStatus);
    }
}
