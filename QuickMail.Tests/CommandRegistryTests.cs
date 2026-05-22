using System.Collections.Generic;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
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
}
