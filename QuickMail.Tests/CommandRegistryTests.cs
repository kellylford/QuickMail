using System.Collections.Generic;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests the real <see cref="CommandRegistry"/> (not the stub). Regression: the override
/// loop previously read the legacy integer fields on <see cref="HotkeyBinding"/>, which
/// are always 0 for bindings written by the Settings UI, so customised hotkeys were
/// silently inert. See QuickMail-Review-2026-05-21.md §1.1.
/// </summary>
public class CommandRegistryTests
{
    private static CommandDefinition MakeCmd(string id, Key key = Key.None, ModifierKeys mods = ModifierKeys.None)
        => new(id, category: "Mail", title: id, execute: () => { }, defaultKey: key, defaultModifiers: mods);

    [Fact]
    public void FindByGesture_ReturnsCommand_WhenDefaultMatches()
    {
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        var hit = reg.FindByGesture(Key.R, ModifierKeys.Control);

        Assert.NotNull(hit);
        Assert.Equal("mail.reply", hit!.Id);
    }

    [Fact]
    public void FindByGesture_ReturnsNull_WhenNoMatch()
    {
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        Assert.Null(reg.FindByGesture(Key.Q, ModifierKeys.Control));
    }

    [Fact]
    public void FindByGesture_RespectsUserOverride_FromGestureString()
    {
        // Regression for §1.1: SettingsViewModel.ToBinding() writes only Gesture;
        // the legacy Key/Modifiers integer fields stay at 0.  Before the fix,
        // FindByGesture compared 0 == Key.K and never matched.
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "mail.reply", Gesture = "Ctrl+Shift+K" }
        });

        var hit = reg.FindByGesture(Key.K, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.NotNull(hit);
        Assert.Equal("mail.reply", hit!.Id);
    }

    [Fact]
    public void FindByGesture_OverrideSuppressesDefaultGesture()
    {
        // After remapping mail.reply to Ctrl+Shift+K, Ctrl+R should NOT still fire
        // mail.reply — the user intentionally moved the binding.
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "mail.reply", Gesture = "Ctrl+Shift+K" }
        });

        Assert.Null(reg.FindByGesture(Key.R, ModifierKeys.Control));
    }

    [Fact]
    public void FindByGesture_MigratesLegacyIntegerBindings()
    {
        // Old hotkeys.json files saved Key/Modifiers as integers without a Gesture string.
        // Those entries must still resolve.
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding
            {
                CommandId = "mail.reply",
                Gesture   = string.Empty,
                Key       = (int)Key.K,
                Modifiers = (int)(ModifierKeys.Control | ModifierKeys.Shift),
            }
        });

        var hit = reg.FindByGesture(Key.K, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.NotNull(hit);
        Assert.Equal("mail.reply", hit!.Id);
    }

    [Fact]
    public void FindByGesture_KeyNone_ReturnsNull()
    {
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        Assert.Null(reg.FindByGesture(Key.None, ModifierKeys.None));
    }

    [Fact]
    public void ApplyUserOverrides_IgnoresMalformedGesture()
    {
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        // Bad input shouldn't throw, shouldn't suppress the default, shouldn't bind anything.
        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "mail.reply", Gesture = "not-a-gesture" }
        });

        // Malformed override is ignored: default still fires.
        var hit = reg.FindByGesture(Key.R, ModifierKeys.Control);
        Assert.NotNull(hit);
        Assert.Equal("mail.reply", hit!.Id);
    }

    [Fact]
    public void ApplyUserOverrides_IsIdempotentAndReplaces()
    {
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.R, ModifierKeys.Control));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "mail.reply", Gesture = "Ctrl+Shift+K" }
        });

        // Second call replaces the first — no leaking from prior set.
        reg.ApplyUserOverrides(new List<HotkeyBinding>());

        Assert.Null(reg.FindByGesture(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.NotNull(reg.FindByGesture(Key.R, ModifierKeys.Control));
    }

    [Fact]
    public void FindByGesture_IgnoresOrphanOverride_FallsThroughToDefault()
    {
        // Regression: when the user deletes a saved view but its hotkey binding remains
        // in hotkeys.json (orphan), pressing that gesture used to swallow the keypress —
        // FindByGesture returned null instead of falling through to the default-gesture
        // loop. With this fix, the orphan is ignored entirely so a live default with the
        // same gesture still fires.
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply", Key.K, ModifierKeys.Control | ModifierKeys.Shift));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "view.saved.deleted-view-id", Gesture = "Ctrl+Shift+K" }
        });

        var hit = reg.FindByGesture(Key.K, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.NotNull(hit);
        Assert.Equal("mail.reply", hit!.Id);
    }

    [Fact]
    public void FindByGesture_OrphanOverride_DoesNotBlockNewerLiveOverrideOnSameGesture()
    {
        // Concrete user scenario: hotkeys.json has an orphan for view.saved.{deleted-id}
        // → Ctrl+Shift+8. The user then creates a new saved view and assigns Ctrl+Shift+8.
        // Both bindings end up in the override list. The orphan must be ignored so the
        // new view's command fires.
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("view.saved.live-view-id"));   // no default gesture — only the override

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "view.saved.dead-view-id", Gesture = "Ctrl+Shift+8" },
            new HotkeyBinding { CommandId = "view.saved.live-view-id", Gesture = "Ctrl+Shift+8" },
        });

        var hit = reg.FindByGesture(Key.D8, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.NotNull(hit);
        Assert.Equal("view.saved.live-view-id", hit!.Id);
    }

    [Fact]
    public void GetOrphanOverrideCommandIds_ReturnsBindingsWithoutCommands()
    {
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply"));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "mail.reply",                Gesture = "Ctrl+R" },
            new HotkeyBinding { CommandId = "view.saved.gone",           Gesture = "Ctrl+1" },
            new HotkeyBinding { CommandId = "view.saved.also-gone",      Gesture = "Ctrl+2" },
        });

        var orphans = reg.GetOrphanOverrideCommandIds();

        Assert.Equal(2, orphans.Count);
        Assert.Contains("view.saved.gone",      orphans);
        Assert.Contains("view.saved.also-gone", orphans);
        Assert.DoesNotContain("mail.reply", orphans);
    }

    [Fact]
    public void FindByGesture_OrphanOverride_DoesNotSuppressUnrelatedDefault()
    {
        // Subtle bug: my first fix used `_userOverrides.Select(...).ToHashSet()` to suppress
        // defaults. That set included orphan CommandIds, but since orphans have no command
        // to be suppressed there was no observable effect for OTHER commands. Still, only
        // *live* overrides should populate the suppression set, so we test that here.
        var reg = new CommandRegistry();
        reg.Register(MakeCmd("mail.reply",   Key.R, ModifierKeys.Control));
        reg.Register(MakeCmd("mail.forward", Key.F, ModifierKeys.Control));

        reg.ApplyUserOverrides(new[]
        {
            new HotkeyBinding { CommandId = "view.saved.gone", Gesture = "Ctrl+1" },
        });

        // Both live defaults must still fire — the orphan binding shouldn't suppress them.
        Assert.Equal("mail.reply",   reg.FindByGesture(Key.R, ModifierKeys.Control)!.Id);
        Assert.Equal("mail.forward", reg.FindByGesture(Key.F, ModifierKeys.Control)!.Id);
    }

    [Fact]
    public void MainViewModel_RegistersReportBugCommand_InHelpCategoryWithNoDefaultHotkey()
    {
        var registry = new CommandRegistry();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService());
        Assert.NotNull(vm);

        var cmd = registry.FindById("help.reportBug");
        Assert.NotNull(cmd);
        Assert.Equal("Help", cmd!.Category);
        Assert.Equal(Key.None, cmd.DefaultKey);

        // Adjacent existing Help commands still resolve — the new registration didn't disturb them.
        Assert.Equal("help.userGuide", registry.FindByGesture(Key.F1, ModifierKeys.None)!.Id);
        Assert.NotNull(registry.FindById("help.about"));
    }
}
