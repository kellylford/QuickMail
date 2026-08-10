// One-time migration off SavedView.IsDefault — issue #516.
//
// The flag is read from raw views.json because it no longer exists on the model, so these tests
// feed JSON text rather than objects. That is deliberate: a test built from SavedView instances
// could not express the very thing being migrated.

using System;
using System.Collections.Generic;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class StartupFolderMigrationTests
{
    private const string WorkAccount = "11111111-1111-1111-1111-111111111111";

    private static string Views(string body) => $"[{body}]";

    private static string VirtualView(string key, bool isDefault = true, string name = "My Inboxes") => $$"""
        { "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Name": "{{name}}",
          "Folders": [], "VirtualFolderKey": "{{key}}", "IsDefault": {{(isDefault ? "true" : "false")}} }
        """;

    private static string SingleFolderView(bool isDefault = true) => $$"""
        { "Id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Name": "Work Projects",
          "Folders": [ { "AccountId": "{{WorkAccount}}", "FolderFullName": "INBOX/Projects",
                         "AccountDisplayName": "Work", "FolderDisplayName": "Projects" } ],
          "IsDefault": {{(isDefault ? "true" : "false")}} }
        """;

    private const string MultiFolderView = """
        { "Id": "cccccccc-cccc-cccc-cccc-cccccccccccc", "Name": "Both Inboxes",
          "Folders": [ { "AccountId": "11111111-1111-1111-1111-111111111111", "FolderFullName": "INBOX",
                         "AccountDisplayName": "Work", "FolderDisplayName": "Inbox" },
                       { "AccountId": "22222222-2222-2222-2222-222222222222", "FolderFullName": "INBOX",
                         "AccountDisplayName": "Home", "FolderDisplayName": "Inbox" } ],
          "IsDefault": true }
        """;

    [Fact]
    public void VirtualDefaultView_BecomesTheVirtualStartupKey()
    {
        // The common shape: someone made a saved view over All Inboxes purely to open there.
        var cfg = new ConfigModel();

        Assert.True(StartupFolderMigration.ApplyToConfig(cfg, Views(VirtualView("AllInboxes"))));

        Assert.Equal("AllInboxes", cfg.StartupFolder);
        Assert.Equal("All Inboxes", cfg.StartupFolderLabel);
        Assert.Equal(string.Empty, cfg.StartupFolderAccount);
    }

    [Fact]
    public void SingleFolderDefaultView_BecomesAnAccountScopedFolder()
    {
        var cfg = new ConfigModel();

        Assert.True(StartupFolderMigration.ApplyToConfig(cfg, Views(SingleFolderView())));

        Assert.Equal("INBOX/Projects", cfg.StartupFolder);
        Assert.Equal(WorkAccount, cfg.StartupFolderAccount);
        Assert.Equal("Projects", cfg.StartupFolderLabel);
    }

    [Fact]
    public void MultiFolderDefaultView_IsKeptByReference_NotDropped()
    {
        // No single folder can express it. Preserving the reference beats silently losing a setup
        // the user deliberately built; nothing in the UI can create this form.
        var cfg = new ConfigModel();

        Assert.True(StartupFolderMigration.ApplyToConfig(cfg, Views(MultiFolderView)));

        Assert.Equal("view:cccccccc-cccc-cccc-cccc-cccccccccccc", cfg.StartupFolder);
        Assert.Equal("Both Inboxes", cfg.StartupFolderLabel);
    }

    [Fact]
    public void NoDefaultView_ChangesNothing()
    {
        var cfg = new ConfigModel();

        Assert.False(StartupFolderMigration.ApplyToConfig(
            cfg, Views(VirtualView("AllInboxes", isDefault: false))));

        Assert.Equal(string.Empty, cfg.StartupFolder);
    }

    [Fact]
    public void AnExplicitStartupFolder_IsNeverOverwritten()
    {
        // The user's own choice always wins over an inherited one — and this is what keeps the
        // migration one-time for anyone who already ran it.
        var cfg = new ConfigModel { StartupFolder = "AllMail", StartupFolderLabel = "All Mail" };

        Assert.False(StartupFolderMigration.ApplyToConfig(cfg, Views(VirtualView("AllInboxes"))));

        Assert.Equal("AllMail", cfg.StartupFolder);
    }

    [Fact]
    public void OnlyTheFirstDefaultViewWins()
    {
        // IsDefault was supposed to be exclusive, but a hand-edited views.json need not be.
        var cfg = new ConfigModel();

        Assert.True(StartupFolderMigration.ApplyToConfig(
            cfg, Views($"{VirtualView("AllInboxes")},{SingleFolderView()}")));

        Assert.Equal("AllInboxes", cfg.StartupFolder);
    }

    [Fact]
    public void LegacyDefaultView_WithNoFoldersAndNoKey_MigratesNothing()
    {
        // That shape already means All Mail, which is the default.
        var cfg = new ConfigModel();

        Assert.False(StartupFolderMigration.ApplyToConfig(cfg, Views("""
            { "Id": "dddddddd-dddd-dddd-dddd-dddddddddddd", "Name": "Legacy",
              "Folders": [], "IsDefault": true }
            """)));

        Assert.Equal(string.Empty, cfg.StartupFolder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"notAnArray\": true }")]
    [InlineData("[ \"a string, not an object\" ]")]
    [InlineData("[]")]
    public void MalformedOrEmptyViewsFile_IsTreatedAsNothingToMigrate(string? json)
    {
        // A migration must never be the reason the app will not start.
        var cfg = new ConfigModel();

        Assert.False(StartupFolderMigration.ApplyToConfig(cfg, json));
        Assert.Equal(string.Empty, cfg.StartupFolder);
    }

    [Fact]
    public void DefaultViewMissingItsId_FallsThroughWithoutThrowing()
    {
        var cfg = new ConfigModel();

        Assert.False(StartupFolderMigration.ApplyToConfig(cfg, Views("""
            { "Name": "No id", "IsDefault": true,
              "Folders": [ { "AccountId": "11111111-1111-1111-1111-111111111111", "FolderFullName": "A" },
                           { "AccountId": "11111111-1111-1111-1111-111111111111", "FolderFullName": "B" } ] }
            """)));

        Assert.Equal(string.Empty, cfg.StartupFolder);
    }

    [Fact]
    public void SingleFolderViewWithoutADisplayName_FallsBackToTheViewName()
    {
        var cfg = new ConfigModel();

        Assert.True(StartupFolderMigration.ApplyToConfig(cfg, Views("""
            { "Id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "Name": "Nameless Folder View",
              "Folders": [ { "AccountId": "11111111-1111-1111-1111-111111111111",
                             "FolderFullName": "INBOX/Deep/Nested" } ],
              "IsDefault": true }
            """)));

        Assert.Equal("INBOX/Deep/Nested", cfg.StartupFolder);
        Assert.Equal("Nameless Folder View", cfg.StartupFolderLabel);
    }

    [Fact]
    public void ApplyToConfig_RejectsANullConfig()
        => Assert.Throws<ArgumentNullException>(() => StartupFolderMigration.ApplyToConfig(null!, "[]"));

    [Fact]
    public void VirtualAndViewForms_LeaveNoStaleAccountBehind()
    {
        // A stale account id paired with a virtual key would send ResolveStartupFolder down the
        // real-folder branch, where the key is not a folder name and never resolves.
        var virtualCfg = new ConfigModel { StartupFolderAccount = WorkAccount };
        Assert.True(StartupFolderMigration.ApplyToConfig(virtualCfg, Views(VirtualView("AllInboxes"))));
        Assert.Equal(string.Empty, virtualCfg.StartupFolderAccount);

        var viewCfg = new ConfigModel { StartupFolderAccount = WorkAccount };
        Assert.True(StartupFolderMigration.ApplyToConfig(viewCfg, Views(MultiFolderView)));
        Assert.Equal(string.Empty, viewCfg.StartupFolderAccount);
    }

    /// <summary>Fails every Load, the way ViewService does when views.json cannot be deserialized —
    /// it swallows the exception and returns an empty list.</summary>
    private sealed class FailingViewService : IViewService
    {
        public int SaveCount { get; private set; }
        public List<SavedView> Load() => [];              // "read failed", indistinguishable from empty
        public void Save(List<SavedView> views) => SaveCount++;
    }

    [Fact]
    public void AFailedReReadOfViewsJson_DoesNotOverwriteIt()
    {
        // ViewService.Load() has a bare catch returning []. Blindly doing Save(Load()) would write
        // [] over every saved view the user has — atomically, silently, on the first launch after
        // upgrade. ApplyToConfig succeeding proves the file held at least one view, so an empty
        // re-read is provably a read failure, not an empty file.
        var tempDir = Path.Combine(Path.GetTempPath(), $"qm-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "views.json"), Views(VirtualView("AllInboxes")));
            var views = new FailingViewService();

            var migrated = StartupFolderMigration.Run(
                new ProfileContext(tempDir), new ConfigModel(), new StubConfigService(), views);

            Assert.True(migrated);
            Assert.Equal(0, views.SaveCount);   // the file was left alone
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
