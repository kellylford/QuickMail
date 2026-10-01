// Start QuickMail at Windows sign-in — issue #770.
//
// Four parts. The pure helpers that decide what goes in the Run key and how Windows' own on/off mark
// is read. The registry behavior, against a scratch key under HKCU so a test run never touches the
// real Run key (or the developer's own startup entry). The Settings VM, against a stub service. And
// the Startup tab's access keys, which the dialog documents as a budget and a collision would break.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class LaunchAtSignInHelperTests
{
    private const string Exe = @"C:\Users\u\AppData\Local\QuickMail\current\QuickMail.exe";

    [Fact]
    public void DefaultProfile_CommandQuotesTheExe_AndPassesStartup()
    {
        Assert.Equal($"\"{Exe}\" --startup", LaunchAtSignInService.BuildCommand(Exe, null));
    }

    [Fact]
    public void CustomProfile_CommandCarriesItsProfileDir()
    {
        Assert.Equal($"\"{Exe}\" --startup --profileDir \"D:\\Mail Data\\Work\"",
            LaunchAtSignInService.BuildCommand(Exe, @"D:\Mail Data\Work"));
    }

    [Fact]
    public void ProfileDir_TrailingBackslash_IsDropped_SoItCannotEscapeTheClosingQuote()
    {
        // "D:\Work\" on a command line reads as D:\Work" — the quote swallowed into the argument.
        Assert.EndsWith("--profileDir \"D:\\Work\"", LaunchAtSignInService.BuildCommand(Exe, @"D:\Work\"));
    }

    [Fact]
    public void ProfileDir_DriveRoot_KeepsMeaningTheRoot()
    {
        // Trimming "D:\" to "D:" would mean the current directory on D:.
        var command = LaunchAtSignInService.BuildCommand(Exe, @"D:\");
        Assert.EndsWith("--profileDir \"D:\\.\"", command);
        Assert.Equal(@"D:\", Path.GetFullPath(@"D:\."));
    }

    [Fact]
    public void ValueName_DefaultProfile_IsPlainQuickMail()
    {
        Assert.Equal("QuickMail", LaunchAtSignInService.BuildValueName(null));
    }

    [Fact]
    public void ValueName_CustomProfile_IsStableAcrossCaseAndTrailingSeparator_AndDistinctPerDir()
    {
        var a = LaunchAtSignInService.BuildValueName(@"C:\Data\ProfileA");
        Assert.Equal(a, LaunchAtSignInService.BuildValueName(@"c:\data\profilea\"));
        Assert.NotEqual(a, LaunchAtSignInService.BuildValueName(@"C:\Data\ProfileB"));
        Assert.NotEqual("QuickMail", a);
        Assert.True(LaunchAtSignInService.IsOurValueName(a));
    }

    [Fact]
    public void ExplicitProfileDir_NamingTheDefaultProfile_IsTheDefaultProfile()
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        var svc = new LaunchAtSignInService(Exe, defaultDir + @"\", isSupported: true);

        Assert.Equal("QuickMail", svc.ValueName);
        Assert.Equal($"\"{Exe}\" --startup", svc.Command);
        Assert.Equal(@"C:\Data\Other", LaunchAtSignInService.NormalizeProfileDir(@"C:\Data\Other"));
    }

    [Theory]
    [InlineData("QuickMail", true)]
    [InlineData("quickmail", true)]
    [InlineData("QuickMail (0123ABCD)", true)]
    [InlineData("QuickMailer", false)]
    [InlineData("OneDrive", false)]
    public void IsOurValueName(string name, bool expected)
    {
        Assert.Equal(expected, LaunchAtSignInService.IsOurValueName(name));
    }

    [Theory]
    [InlineData("\"C:\\A B\\QuickMail.exe\" --startup", @"C:\A B\QuickMail.exe")]
    [InlineData("C:\\A\\QuickMail.exe --startup", @"C:\A\QuickMail.exe")]
    [InlineData("C:\\A\\QuickMail.exe", @"C:\A\QuickMail.exe")]
    [InlineData("  \"C:\\A\\QuickMail.exe\"", @"C:\A\QuickMail.exe")]
    public void ExecutableOf(string command, string expected)
    {
        Assert.Equal(expected, LaunchAtSignInService.ExecutableOf(command));
    }

    [Fact]
    public void PointsAt_ComparesTheExecutable_IgnoringCase()
    {
        Assert.True(LaunchAtSignInService.PointsAt($"\"{Exe.ToUpperInvariant()}\" --startup", Exe));
        Assert.False(LaunchAtSignInService.PointsAt("\"C:\\Portable\\QuickMail.exe\" --startup", Exe));
        Assert.False(LaunchAtSignInService.PointsAt($"\"{Exe}\"", null));
    }

    // Windows writes 02/06 (enabled) and 03/07 (disabled) followed by a timestamp; a missing value
    // means enabled.
    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, true)]
    public void IsDisabledByWindows(byte[]? approval, bool expected)
    {
        Assert.Equal(expected, LaunchAtSignInService.IsDisabledByWindows(approval));
    }
}

/// <summary>Registry behavior against a throwaway key under HKCU, deleted afterwards.</summary>
public sealed class LaunchAtSignInRegistryTests : IDisposable
{
    private const string Exe = @"C:\Users\u\AppData\Local\QuickMail\current\QuickMail.exe";
    private const string OtherExe = @"C:\Portable\QuickMail.exe";

    private readonly string _root = $@"Software\QuickMail.Tests\LaunchAtSignIn-{Guid.NewGuid():N}";
    private string RunPath => _root + @"\Run";
    private string ApprovedPath => _root + @"\StartupApproved\Run";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

        // Leave no empty parent behind once the last test class instance is done with it.
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\QuickMail.Tests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            try { Registry.CurrentUser.DeleteSubKey(@"Software\QuickMail.Tests", throwOnMissingSubKey: false); }
            catch (InvalidOperationException) { } // a parallel test created a child meanwhile
        }
    }

    private LaunchAtSignInService Make(string? exe = Exe, string? profileDir = null, bool supported = true) =>
        new(exe, profileDir, supported, RunPath, ApprovedPath);

    private object? RunValue(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunPath);
        return k?.GetValue(name);
    }

    private object? ApprovedValue(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(ApprovedPath);
        return k?.GetValue(name);
    }

    private void SetRun(string name, string command)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunPath);
        k.SetValue(name, command, RegistryValueKind.String);
    }

    private void SetApproved(string name, byte first)
    {
        using var k = Registry.CurrentUser.CreateSubKey(ApprovedPath);
        k.SetValue(name, new byte[] { first, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueKind.Binary);
    }

    [Fact]
    public void NothingRegistered_IsOff()
    {
        Assert.Equal(LaunchAtSignInState.Off, Make().GetState());
    }

    [Fact]
    public void Enable_WritesTheRunValue_AndReadsOn()
    {
        var svc = Make();
        svc.Enable();

        Assert.Equal($"\"{Exe}\" --startup", RunValue("QuickMail"));
        Assert.Equal(LaunchAtSignInState.On, svc.GetState());
    }

    [Fact]
    public void TurnedOffInTaskManager_ReadsDisabledInWindows_AndTheEntryIsLeftInPlace()
    {
        var svc = Make();
        svc.Enable();
        SetApproved("QuickMail", 0x03);

        Assert.Equal(LaunchAtSignInState.DisabledInWindows, svc.GetState());
        Assert.NotNull(RunValue("QuickMail"));
    }

    [Fact]
    public void TurnedBackOnInTaskManager_ReadsOn()
    {
        var svc = Make();
        svc.Enable();
        SetApproved("QuickMail", 0x02);

        Assert.Equal(LaunchAtSignInState.On, svc.GetState());
    }

    [Fact]
    public void Enable_AfterWindowsDisabledIt_ClearsWindowsMark()
    {
        var svc = Make();
        svc.Enable();
        SetApproved("QuickMail", 0x03);

        svc.Enable();

        Assert.Null(ApprovedValue("QuickMail"));
        Assert.Equal(LaunchAtSignInState.On, svc.GetState());
    }

    [Fact]
    public void Disable_RemovesTheRunValue_AndWindowsMark()
    {
        var svc = Make();
        svc.Enable();
        SetApproved("QuickMail", 0x03);

        svc.Disable();

        Assert.Null(RunValue("QuickMail"));
        Assert.Null(ApprovedValue("QuickMail"));
        Assert.Equal(LaunchAtSignInState.Off, svc.GetState());
    }

    [Fact]
    public void Disable_WhenNothingRegistered_DoesNotThrow()
    {
        Make().Disable();
    }

    [Fact]
    public void EntryForAnotherCopy_IsNotThisCopysEntry()
    {
        SetRun("QuickMail", $"\"{OtherExe}\" --startup");
        Assert.Equal(LaunchAtSignInState.Off, Make().GetState());
    }

    [Fact]
    public void CustomProfile_GetsItsOwnEntry_AndNeverReplacesTheDefaultProfiles()
    {
        var main = Make();
        var test = Make(profileDir: @"C:\Data\TestProfile");
        main.Enable();
        test.Enable();

        Assert.Equal($"\"{Exe}\" --startup", RunValue("QuickMail"));
        Assert.Equal($"\"{Exe}\" --startup --profileDir \"C:\\Data\\TestProfile\"", RunValue(test.ValueName));

        test.Disable();
        Assert.Equal(LaunchAtSignInState.On, main.GetState());
    }

    [Fact]
    public void Unsupported_ReportsSo_AndRefusesToRegister()
    {
        var svc = Make(supported: false);
        Assert.False(svc.IsSupported);
        Assert.Throws<InvalidOperationException>(svc.Enable);
        Assert.Null(RunValue("QuickMail"));
    }

    [Fact]
    public void UnknownExecutable_IsUnsupported()
    {
        Assert.False(Make(exe: null).IsSupported);
    }

    [Fact]
    public void RemoveAllFor_RemovesEveryProfilesEntryForThisCopy_AndNothingElse()
    {
        Make().Enable();
        var test = Make(profileDir: @"C:\Data\TestProfile");
        test.Enable();
        SetApproved(test.ValueName, 0x03);
        SetRun("QuickMail (FFFFFFFF)", $"\"{OtherExe}\" --startup");   // another copy's
        SetRun("SomeOtherApp", $"\"{Exe}\"");                           // not a QuickMail name

        LaunchAtSignInService.RemoveAllFor(Exe, RunPath, ApprovedPath);

        Assert.Null(RunValue("QuickMail"));
        Assert.Null(RunValue(test.ValueName));
        Assert.Null(ApprovedValue(test.ValueName));
        Assert.NotNull(RunValue("QuickMail (FFFFFFFF)"));
        Assert.NotNull(RunValue("SomeOtherApp"));
    }

    [Fact]
    public void RemoveAllFor_NoRunKey_DoesNotThrow()
    {
        LaunchAtSignInService.RemoveAllFor(Exe, RunPath, ApprovedPath);
    }
}

public class LaunchAtSignInSettingsTests
{
    private sealed class StubLaunchAtSignIn : ILaunchAtSignInService
    {
        public bool IsSupported { get; init; } = true;
        public LaunchAtSignInState State { get; set; }
        public List<string> Calls { get; } = new();

        public LaunchAtSignInState GetState() => State;
        public void Enable() { Calls.Add("Enable"); State = LaunchAtSignInState.On; }
        public void Disable() { Calls.Add("Disable"); State = LaunchAtSignInState.Off; }
    }

    private static SettingsViewModel MakeVm(ILaunchAtSignInService? svc, StubConfigService? config = null) =>
        new(config ?? new StubConfigService(), new StubCommandRegistry(), launchAtSignIn: svc);

    [Fact]
    public void NoService_IsUnsupported()
    {
        var vm = MakeVm(null);
        Assert.False(vm.IsLaunchAtSignInSupported);
        Assert.True(vm.IsLaunchAtSignInUnsupported);
        Assert.False(vm.LaunchAtSignIn);
    }

    [Fact]
    public void UnsupportedCopy_NeverTouchesTheRegistry_EvenIfTheBoxIsSet()
    {
        var svc = new StubLaunchAtSignIn { IsSupported = false };
        var vm = MakeVm(svc);
        Assert.False(vm.IsLaunchAtSignInSupported);

        vm.LaunchAtSignIn = true;
        vm.SaveCommand.Execute(null);

        Assert.Empty(svc.Calls);
    }

    [Fact]
    public void LoadsWhatWindowsWillDo()
    {
        Assert.False(MakeVm(new StubLaunchAtSignIn { State = LaunchAtSignInState.Off }).LaunchAtSignIn);
        Assert.True(MakeVm(new StubLaunchAtSignIn { State = LaunchAtSignInState.On }).LaunchAtSignIn);
    }

    [Fact]
    public void DisabledInWindows_ReadsUnchecked_AndSaysWhy()
    {
        var vm = MakeVm(new StubLaunchAtSignIn { State = LaunchAtSignInState.DisabledInWindows });
        Assert.False(vm.LaunchAtSignIn);
        Assert.Contains("Windows", vm.LaunchAtSignInHelp);

        Assert.Equal("", MakeVm(new StubLaunchAtSignIn { State = LaunchAtSignInState.On }).LaunchAtSignInHelp);
    }

    [Fact]
    public void DisabledInWindows_SavingWithoutTouchingTheBox_LeavesWindowsChoiceAlone()
    {
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.DisabledInWindows };
        MakeVm(svc).SaveCommand.Execute(null);

        Assert.Empty(svc.Calls);
        Assert.Equal(LaunchAtSignInState.DisabledInWindows, svc.State);
    }

    [Fact]
    public void Checking_AndSaving_Enables()
    {
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.DisabledInWindows };
        var vm = MakeVm(svc);

        vm.LaunchAtSignIn = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(new[] { "Enable" }, svc.Calls);
        Assert.Equal("", vm.LaunchAtSignInHelp);
    }

    [Fact]
    public void Unchecking_AndSaving_Disables()
    {
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.On };
        var vm = MakeVm(svc);

        vm.LaunchAtSignIn = false;
        vm.SaveCommand.Execute(null);

        Assert.Equal(new[] { "Disable" }, svc.Calls);
    }

    [Fact]
    public void SavingUnchanged_DoesNothing()
    {
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.On };
        MakeVm(svc).SaveCommand.Execute(null);
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public void TurnedOffInTaskManagerWhileTheDialogWasOpen_SavingOtherSettings_LeavesItOff()
    {
        // Opened while on; meanwhile the user disables QuickMail in Task Manager, then saves some
        // unrelated setting. The box still shows "on" from when the dialog opened — that is not a
        // request to undo what they just did in Windows.
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.On };
        var vm = MakeVm(svc);
        svc.State = LaunchAtSignInState.DisabledInWindows;

        vm.StartMinimizedAtSignIn = false;
        vm.SaveCommand.Execute(null);

        Assert.Empty(svc.Calls);
        Assert.Equal(LaunchAtSignInState.DisabledInWindows, svc.State);
    }

    [Fact]
    public void UserChecksTheBox_AfterWindowsAlreadyTurnedItOn_DoesNothingMore()
    {
        // Opened while off; turned on elsewhere meanwhile; the user checks the box. Already there.
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.Off };
        var vm = MakeVm(svc);
        svc.State = LaunchAtSignInState.On;

        vm.LaunchAtSignIn = true;
        vm.SaveCommand.Execute(null);

        Assert.Empty(svc.Calls);
        Assert.True(vm.LaunchAtSignIn);
    }

    [Fact]
    public void UserUnchecksTheBox_AfterTaskManagerDisabledIt_RemovesTheEntry()
    {
        // The user asked for off; an entry Windows is merely skipping is still an entry.
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.On };
        var vm = MakeVm(svc);
        svc.State = LaunchAtSignInState.DisabledInWindows;

        vm.LaunchAtSignIn = false;
        vm.SaveCommand.Execute(null);

        Assert.Equal(new[] { "Disable" }, svc.Calls);
    }

    [Fact]
    public void SavingTwice_AfterAChange_ActsOnce()
    {
        // After a save the box is the new baseline; the dialog's Save may run more than once.
        var svc = new StubLaunchAtSignIn { State = LaunchAtSignInState.Off };
        var vm = MakeVm(svc);

        vm.LaunchAtSignIn = true;
        vm.SaveCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(new[] { "Enable" }, svc.Calls);
    }

    [Fact]
    public void StartMinimized_DefaultsOn_AndIsSaved()
    {
        var config = new StubConfigService();
        var vm = MakeVm(new StubLaunchAtSignIn(), config);
        Assert.True(vm.StartMinimizedAtSignIn);

        vm.StartMinimizedAtSignIn = false;
        vm.SaveCommand.Execute(null);

        Assert.False(config.Load().StartMinimizedAtSignIn);
    }

    [Fact]
    public void StartMinimized_RoundTripsThroughConfigIni()
    {
        var profile = new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-SignIn-{Guid.NewGuid():N}"));
        var service = new ConfigService(profile);
        Assert.True(service.Load().StartMinimizedAtSignIn);

        var cfg = service.Load();
        cfg.StartMinimizedAtSignIn = false;
        service.Save(cfg);

        Assert.False(new ConfigService(profile).Load().StartMinimizedAtSignIn);
    }
}

public class LaunchAtSignInDialogTests
{
    [Fact]
    public void StartupTab_AccessKeys_DoNotCollide_WithEachOther_OrTheDialogWideKeys()
    {
        // WPF cycles between duplicate access keys instead of activating, so a collision costs the
        // user the keystroke entirely. Dialog-wide: the tab headers and Save/Cancel.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "SettingsDialog.xaml"));

        var start = xaml.IndexOf("<TabItem Header=\"Start_up\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Startup tab not found");
        var end = xaml.IndexOf("<TabItem ", start + 1, StringComparison.Ordinal);
        var tab = xaml[start..end];

        static IEnumerable<char> Keys(string text) =>
            Regex.Matches(text, "(?:Content|Header)=\"[^\"]*?_([A-Za-z0-9])")
                 .Select(m => char.ToUpperInvariant(m.Groups[1].Value[0]));

        var dialogWide = Regex.Matches(xaml, "<TabItem Header=\"[^\"]*?_([A-Za-z])")
            .Select(m => char.ToUpperInvariant(m.Groups[1].Value[0]))
            .Concat(new[] { 'S', 'C' })   // _Save, _Cancel
            .ToList();
        var tabKeys = Keys(tab).Skip(1).ToList();   // the first is the tab's own header, counted above

        var all = dialogWide.Concat(tabKeys).ToList();
        var duplicates = all.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"Duplicate access keys on the Startup tab: {string.Join(", ", duplicates)}");

        // And the new controls are really there.
        Assert.Contains('N', tabKeys);
        Assert.Contains('O', tabKeys);
        Assert.Contains('M', tabKeys);
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
