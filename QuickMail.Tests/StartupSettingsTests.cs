// Settings -> Startup — issue #516.
//
// Two halves. The VM half pins that the tab's fields survive a save/load and that Choose/Clear do
// what their labels say. The call-site half reads MainWindow's source, because the picker cannot be
// stood up without a live main window — and the property that matters most about it (that it is
// UNSCOPED, unlike every other destination picker) is invisible to a presentation test: scoping it
// by accident would still produce a tree, just one missing most of the user's folders.

using System;
using System.IO;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class StartupSettingsTests
{
    private static SettingsViewModel MakeVm(IConfigService config)
        => new(config, new StubCommandRegistry());

    [Fact]
    public void FreshSettings_ShowAllMail_AndTheRecommendedScope()
    {
        var vm = MakeVm(new StubConfigService());

        Assert.Equal("All Mail", vm.StartupFolderDisplay);   // empty StartupFolder == All Mail
        Assert.True(vm.IsStartupSyncScopeStartupFolder);
        Assert.False(vm.IsStartupSyncScopeInboxes);
        Assert.False(vm.IsStartupSyncScopeAll);
    }

    private static readonly Guid WorkId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static MailFolderModel RealFolder(string full, string display) =>
        new() { AccountId = WorkId, FullName = full, DisplayName = display };

    [Fact]
    public void StartupFolder_RoundTripsThroughConfig()
    {
        var config = new StubConfigService();
        var save = MakeVm(config);
        save.PickStartupFolderRequested = () => RealFolder("INBOX/Projects", "Projects");

        save.ChooseStartupFolderCommand.Execute(null);
        save.SaveCommand.Execute(null);

        var load = MakeVm(config);
        Assert.Equal("INBOX/Projects", load.StartupFolder);
        Assert.Equal(WorkId.ToString(), load.StartupFolderAccount);
        Assert.Equal("Projects", load.StartupFolderDisplay);
    }

    [Fact]
    public void PickingAVirtualAggregate_StripsTheSentinelAndDropsTheAccount()
    {
        // The VM owns this conversion, not the code-behind — one rule, one place. A virtual folder
        // spans every account, so carrying an account id would make it resolve as a real folder name.
        var config = new StubConfigService();
        var vm = MakeVm(config);
        vm.PickStartupFolderRequested = () => MainViewModel.AllInboxesFolder;

        vm.ChooseStartupFolderCommand.Execute(null);

        Assert.Equal("AllInboxes", vm.StartupFolder);
        Assert.Equal(string.Empty, vm.StartupFolderAccount);
        Assert.Equal("All Inboxes", vm.StartupFolderDisplay);
    }

    [Fact]
    public void CancellingThePicker_LeavesTheCurrentChoiceAlone()
    {
        var config = new StubConfigService();
        var vm = MakeVm(config);
        vm.PickStartupFolderRequested = () => MainViewModel.AllInboxesFolder;
        vm.ChooseStartupFolderCommand.Execute(null);

        vm.PickStartupFolderRequested = () => null;    // user pressed Escape
        vm.ChooseStartupFolderCommand.Execute(null);

        Assert.Equal("AllInboxes", vm.StartupFolder);
        Assert.Equal("All Inboxes", vm.StartupFolderDisplay);
    }

    [Fact]
    public void Clear_ResetsToAllMail_AndPersists()
    {
        var config = new StubConfigService();
        var vm = MakeVm(config);
        vm.PickStartupFolderRequested = () => RealFolder("INBOX", "Inbox");
        vm.ChooseStartupFolderCommand.Execute(null);

        vm.ClearStartupFolderCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        var reloaded = MakeVm(config);
        Assert.Equal(string.Empty, reloaded.StartupFolder);
        Assert.Equal(string.Empty, reloaded.StartupFolderAccount);
        Assert.Equal("All Mail", reloaded.StartupFolderDisplay);
    }

    [Fact]
    public void ChoosingAVirtualFolderAfterARealOne_ClearsTheAccount()
    {
        // Otherwise the virtual key would be read back as a real folder name on that account.
        var config = new StubConfigService();
        var vm = MakeVm(config);
        vm.PickStartupFolderRequested = () => RealFolder("INBOX", "Inbox");
        vm.ChooseStartupFolderCommand.Execute(null);

        vm.PickStartupFolderRequested = () => MainViewModel.AllInboxesFolder;
        vm.ChooseStartupFolderCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(string.Empty, MakeVm(config).StartupFolderAccount);
    }

    [Theory]
    [InlineData(ConfigModel.StartupSyncScopeInboxes)]
    [InlineData(ConfigModel.StartupSyncScopeAll)]
    [InlineData(ConfigModel.StartupSyncScopeStartupFolder)]
    public void SyncScope_RoundTripsThroughConfig(string scope)
    {
        var config = new StubConfigService();
        var save = MakeVm(config);
        save.StartupSyncScope = scope;
        save.SaveCommand.Execute(null);

        Assert.Equal(scope, MakeVm(config).StartupSyncScope);
    }

    [Fact]
    public void SyncScopeRadios_StayMutuallyExclusive()
    {
        var vm = MakeVm(new StubConfigService());

        vm.IsStartupSyncScopeAll = true;

        Assert.True(vm.IsStartupSyncScopeAll);
        Assert.False(vm.IsStartupSyncScopeInboxes);
        Assert.False(vm.IsStartupSyncScopeStartupFolder);
    }

    [Fact]
    public void AnUnrecognisedPersistedScope_NormalizesToTheDefault()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.StartupSyncScope = "whatever";
        config.Save(cfg);

        Assert.True(MakeVm(config).IsStartupSyncScopeStartupFolder);
    }

    // ── Call-site guards ─────────────────────────────────────────────────────────

    [Fact]
    public void TheSettingsPickerIsBuiltThroughForStartupFolder()
    {
        var body = MethodBody("Views/MainWindow.xaml.cs", "MenuSettings_Click");

        Assert.Contains("FolderPickerWindow.ForStartupFolder", body, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(body, @"new\s+FolderPickerWindow\s*\("),
                     "MenuSettings_Click constructs a FolderPickerWindow directly — go through the factory.");
    }

    [Fact]
    public void TheSettingsPickerOffersTheVirtualFolders()
    {
        // "Open me in All Inboxes" is the single most-requested form of this setting, and the
        // aggregates reach the picker only by being passed in — nothing else would fail without it.
        var body = MethodBody("Views/MainWindow.xaml.cs", "MenuSettings_Click");

        Assert.Contains("AllVirtualFolders", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPickerIsNotScopedToOneAccount()
    {
        // The opposite of the rule-target and move/copy pickers, deliberately: a startup folder is
        // one global choice stored with its owning account, so there is no name-collision hazard to
        // scope against, and scoping would hide most of the tree.
        var body = MethodBody("Views/MainWindow.xaml.cs", "MenuSettings_Click");
        var call = Regex.Match(body, @"ForStartupFolder\s*\((?<args>[^;]*?)\)\s*;", RegexOptions.Singleline);

        Assert.True(call.Success, "could not find the ForStartupFolder call.");
        Assert.Contains("_vm.Accounts", call.Groups["args"].Value, StringComparison.Ordinal);
        Assert.Contains("_vm.CachedFolders", call.Groups["args"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(", call.Groups["args"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text of one method, from its DECLARATION to the first closing brace at the same indentation.
    /// Anchored on the modifier rather than the bare name: MainWindow also has the one-line probe
    /// wrapper <c>ShowSettingsDialogForProbe() => MenuSettings_Click(...)</c>, and a name-only match
    /// finds that first and returns a "body" containing nothing this file asserts about.
    /// </summary>
    private static string MethodBody(string relativePath, string methodName)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{path} not found.");

        var source = File.ReadAllText(path);
        var start  = Regex.Match(
            source,
            $@"^(?<indent>[ \t]*)(?:private|internal|public|protected)[^\r\n(]*\b{Regex.Escape(methodName)}\s*\([^)]*\)\s*[\r\n]",
            RegexOptions.Multiline);
        Assert.True(start.Success, $"{methodName} declaration not found in {relativePath} — renamed or removed?");

        var end = Regex.Match(
            source[start.Index..], $@"^{start.Groups["indent"].Value}}}", RegexOptions.Multiline);
        Assert.True(end.Success, $"could not find the end of {methodName} in {relativePath}.");

        return source.Substring(start.Index, end.Index + end.Length);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
