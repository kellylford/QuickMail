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
        Assert.False(vm.ReadAsPlainText);
        Assert.Equal("messages", vm.ViewMode);
        Assert.Equal(30, vm.SyncDays);
        Assert.Equal(500, vm.InitialSyncCount);
        Assert.Empty(vm.HotkeyRows);
    }

    [Fact]
    public void ReadAsPlainText_RoundTripsThroughConfig()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();

        // Load reflects config.
        configService.Save(new ConfigModel { ReadAsPlainText = true });
        var vm = new SettingsViewModel(configService, registry);
        Assert.True(vm.ReadAsPlainText);

        // Save writes it back.
        vm.ReadAsPlainText = false;
        vm.SaveCommand.Execute(null);
        Assert.False(configService.Load().ReadAsPlainText);
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

    // ── CalDAV calendar source ──────────────────────────────────────────────────

    private sealed class RecordingCredentialService : ICredentialService
    {
        public Dictionary<string, string> Secrets { get; } = [];
        public void SavePassword(System.Guid accountId, string password) { }
        public string? GetPassword(System.Guid accountId) => null;
        public void DeletePassword(System.Guid accountId) { }
        public void SaveSecret(string key, string value) => Secrets[key] = value;
        public string? GetSecret(string key) => Secrets.TryGetValue(key, out var v) ? v : null;
        public void DeleteSecret(string key) => Secrets.Remove(key);
    }

    private sealed class StubCalDavClient : ICalDavCalendarClient
    {
        public string? LastUrl, LastUser, LastPassword;
        public bool Fail;
        public System.Threading.Tasks.Task<CalDavCalendarInfo> DiscoverCalendarAsync(
            string serverUrl, string username, string password, System.Threading.CancellationToken ct = default)
        {
            LastUrl = serverUrl; LastUser = username; LastPassword = password;
            if (Fail) throw new System.InvalidOperationException("No event calendar was found for this account.");
            return System.Threading.Tasks.Task.FromResult(new CalDavCalendarInfo("https://p42.example/cal/", "Home"));
        }
        public System.Threading.Tasks.Task<List<string>> FetchEventIcsAsync(
            string calendarUrl, string username, string password,
            System.DateTime startUtc, System.DateTime endUtc, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new List<string>());
    }

    [Fact]
    public void CalDav_LoadsFromConfig_AndSavePersists_UrlUsernameNameOnly()
    {
        var configService = new StubConfigService();
        configService.Save(new ConfigModel
        {
            CalDavUrl = "https://caldav.icloud.com", CalDavUsername = "kelly@example.com", CalDavDisplayName = "Apple",
        });
        var creds = new RecordingCredentialService();
        var vm = new SettingsViewModel(configService, new StubCommandRegistry(),
                                       credentialService: creds);

        Assert.Equal("https://caldav.icloud.com", vm.CalDavUrl);
        Assert.Equal("kelly@example.com", vm.CalDavUsername);
        Assert.Equal("Apple", vm.CalDavDisplayName);

        vm.CalDavUrl = " https://caldav.fastmail.com ";
        vm.CalDavDisplayName = "  "; // blank name falls back to the default
        vm.SaveCommand.Execute(null);

        var cfg = configService.Load();
        Assert.Equal("https://caldav.fastmail.com", cfg.CalDavUrl);
        Assert.Equal("kelly@example.com", cfg.CalDavUsername);
        Assert.Equal("iCloud", cfg.CalDavDisplayName);
        // No password typed → nothing written to the credential store (saved secret kept).
        Assert.Empty(creds.Secrets);
    }

    [Fact]
    public void CalDav_Save_WritesPasswordToCredentialStore_NeverConfig()
    {
        var configService = new StubConfigService();
        var creds = new RecordingCredentialService();
        var vm = new SettingsViewModel(configService, new StubCommandRegistry(),
                                       credentialService: creds)
        {
            CalDavUrl = "https://caldav.icloud.com",
            CalDavUsername = "kelly@example.com",
            CalDavPassword = "app-specific-pass",
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal("app-specific-pass",
            creds.Secrets[CalDavCalendarClient.SecretKeyFor("kelly@example.com")]);
    }

    [Fact]
    public async System.Threading.Tasks.Task CalDav_Test_RunsDiscovery_WithTypedPassword()
    {
        var client = new StubCalDavClient();
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry(),
                                       credentialService: new RecordingCredentialService(),
                                       calDavClient: client)
        {
            CalDavUrl = "https://caldav.icloud.com",
            CalDavUsername = "kelly@example.com",
            CalDavPassword = "typed-pass",
        };
        string? announced = null;
        vm.CalDavTestCompleted += t => announced = t;

        await vm.TestCalDavCommand.ExecuteAsync(null);

        Assert.Equal("typed-pass", client.LastPassword);
        Assert.Equal("Connected. Found calendar: Home.", vm.CalDavTestResult);
        Assert.Equal(vm.CalDavTestResult, announced);
    }

    [Fact]
    public async System.Threading.Tasks.Task CalDav_Test_FallsBackToSavedPassword_AndReportsFailure()
    {
        var creds = new RecordingCredentialService();
        creds.SaveSecret(CalDavCalendarClient.SecretKeyFor("kelly@example.com"), "saved-pass");
        var client = new StubCalDavClient { Fail = true };
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry(),
                                       credentialService: creds, calDavClient: client)
        {
            CalDavUrl = "https://caldav.icloud.com",
            CalDavUsername = "kelly@example.com",
        };

        await vm.TestCalDavCommand.ExecuteAsync(null);

        Assert.Equal("saved-pass", client.LastPassword);
        Assert.StartsWith("Could not connect.", vm.CalDavTestResult);
    }

    [Fact]
    public async System.Threading.Tasks.Task CalDav_Test_MissingFields_PromptsInsteadOfCalling()
    {
        var client = new StubCalDavClient();
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry(),
                                       credentialService: new RecordingCredentialService(),
                                       calDavClient: client);

        await vm.TestCalDavCommand.ExecuteAsync(null);

        Assert.Null(client.LastUrl); // no request attempted
        Assert.Contains("app-specific password", vm.CalDavTestResult);
    }

    [Fact]
    public void NotifyOnNewMail_LoadsAndSaves()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();

        // Default is off (opt-in).
        var vm = new SettingsViewModel(configService, registry);
        Assert.False(vm.NotifyOnNewMail);

        vm.NotifyOnNewMail = true;
        vm.SaveCommand.Execute(null);

        Assert.True(configService.Load().NotifyOnNewMail);
    }

    [Fact]
    public void CloseToTray_LoadsAndSaves()
    {
        var configService = new StubConfigService();
        var registry = new StubCommandRegistry();

        var vm = new SettingsViewModel(configService, registry);
        Assert.False(vm.CloseToTray); // default off

        vm.CloseToTray = true;
        vm.SaveCommand.Execute(null);

        Assert.True(configService.Load().CloseToTray);
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
    public void HotkeyRow_AccessibleName_ReflectsCurrentGesture()
    {
        var registry = new StubCommandRegistry();
        registry.Register(new CommandDefinition("mail.reply", "Mail", "Reply", () => { }, Key.R, ModifierKeys.Control));

        var vm = new SettingsViewModel(new StubConfigService(), registry);
        var row = vm.HotkeyRows[0];

        Assert.Equal("Reply, Mail, Ctrl+R", row.AccessibleName);

        row.SetCustomBinding(Key.K, ModifierKeys.Control);
        Assert.Equal("Reply, Mail, Ctrl+K", row.AccessibleName);

        row.ClearCustomBinding();
        Assert.Equal("Reply, Mail, Ctrl+R", row.AccessibleName);
    }

    [Fact]
    public void HotkeyRow_AccessibleName_NoShortcut_WhenNoneAssigned()
    {
        var registry = new StubCommandRegistry();
        registry.RegisterTestCommand("test.cmd", "Test", "Command");

        var vm = new SettingsViewModel(new StubConfigService(), registry);
        var row = vm.HotkeyRows[0];

        Assert.Equal("Command, Test, no shortcut", row.AccessibleName);
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

    // ── Logging ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnableLogging_DefaultsToFalse()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        Assert.False(vm.EnableLogging);
    }

    [Fact]
    public void EnableLogging_LoadAndSave_RoundTrips()
    {
        var configService = new StubConfigService();

        var vmSave = new SettingsViewModel(configService, new StubCommandRegistry());
        vmSave.EnableLogging = true;
        vmSave.SaveCommand.Execute(null);

        var vmLoad = new SettingsViewModel(configService, new StubCommandRegistry());
        Assert.True(vmLoad.EnableLogging);
    }

    // ── Spelling suggestions ───────────────────────────────────────────────────────

    [Fact]
    public void SpellingSuggestionsVerbosity_DefaultsToNumbersWithSuggestions()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        Assert.Equal("numbersWithSuggestions", vm.SpellingSuggestionsVerbosity);
    }

    [Fact]
    public void AnnounceSpellingSuggestions_DefaultsToTrue()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        Assert.True(vm.AnnounceSpellingSuggestions);
    }

    [Fact]
    public void IsVerbosityProperties_ReflectCurrentSetting()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());

        Assert.True(vm.IsVerbosityNumbersWithSuggestions);
        Assert.False(vm.IsVerbosityJustSuggestions);

        vm.IsVerbosityJustSuggestions = true;

        Assert.True(vm.IsVerbosityJustSuggestions);
        Assert.False(vm.IsVerbosityNumbersWithSuggestions);
        Assert.Equal("justSuggestions", vm.SpellingSuggestionsVerbosity);
    }

    [Fact]
    public void SpellingSuggestionsVerbosity_LoadAndSave_RoundTrips()
    {
        var configService = new StubConfigService();

        var vmSave = new SettingsViewModel(configService, new StubCommandRegistry());
        vmSave.SpellingSuggestionsVerbosity = "justSuggestions";
        vmSave.SaveCommand.Execute(null);

        var vmLoad = new SettingsViewModel(configService, new StubCommandRegistry());
        Assert.Equal("justSuggestions", vmLoad.SpellingSuggestionsVerbosity);
    }

    [Fact]
    public void AnnounceSpellingSuggestions_LoadAndSave_RoundTrips()
    {
        var configService = new StubConfigService();

        var vmSave = new SettingsViewModel(configService, new StubCommandRegistry());
        vmSave.AnnounceSpellingSuggestions = false;
        vmSave.SaveCommand.Execute(null);

        var vmLoad = new SettingsViewModel(configService, new StubCommandRegistry());
        Assert.False(vmLoad.AnnounceSpellingSuggestions);
    }
}
