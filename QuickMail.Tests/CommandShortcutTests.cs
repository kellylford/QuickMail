using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The shortcut shown after a menu item, read by a screen reader and put in a tooltip comes from the
/// command registry, and follows a key rebound in Settings (#849).
/// </summary>
[Collection("WpfTests")]
public class CommandShortcutTests
{
    private static CommandRegistry RegistryWithReply()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("mail.reply", "Mail", "Reply", () => { },
            Key.R, ModifierKeys.Control));
        registry.Register(new CommandDefinition("mail.markRead", "Mail", "Mark as Read", () => { }));
        return registry;
    }

    private static readonly HotkeyBinding[] ReplyOnCtrlShiftY =
        [new HotkeyBinding { CommandId = "mail.reply", Gesture = "Ctrl+Shift+Y" }];

    [Fact]
    public void TheRegistryGivesTheDefaultUntilTheUserRebindsIt()
    {
        var registry = RegistryWithReply();
        Assert.Equal("Ctrl+R", registry.GetGestureText("mail.reply"));

        registry.ApplyUserOverrides(ReplyOnCtrlShiftY);
        Assert.Equal("Ctrl+Shift+Y", registry.GetGestureText("mail.reply"));
        Assert.Equal("Ctrl+R", registry.FindById("mail.reply")!.DefaultGestureText);

        registry.ApplyUserOverrides([]);
        Assert.Equal("Ctrl+R", registry.GetGestureText("mail.reply"));
    }

    [Fact]
    public void UnknownCommandsAndCommandsWithoutAKeyGiveNothing()
    {
        var registry = RegistryWithReply();
        Assert.Equal(string.Empty, registry.GetGestureText("mail.markRead"));
        Assert.Equal(string.Empty, registry.GetGestureText("no.such.command"));
    }

    [Fact]
    public void ACommandRegisteredAfterTheUsersKeysWereLoadedGetsTheirKey()
    {
        // Saved views and themes register their commands after startup has applied hotkeys.json.
        var registry = new CommandRegistry();
        registry.ApplyUserOverrides(ReplyOnCtrlShiftY);
        registry.Register(new CommandDefinition("mail.reply", "Mail", "Reply", () => { },
            Key.R, ModifierKeys.Control));

        Assert.Equal("Ctrl+Shift+Y", registry.GetGestureText("mail.reply"));
    }

    [Fact]
    public void RebindingRegisteringAndRemovingAreAllAnnounced()
    {
        var registry = RegistryWithReply();
        var raised = 0;
        registry.BindingsChanged += (_, _) => raised++;

        registry.ApplyUserOverrides(ReplyOnCtrlShiftY);
        registry.Register(new CommandDefinition("mail.forward", "Mail", "Forward", () => { }));
        registry.Unregister("mail.forward");
        registry.Unregister("mail.forward"); // already gone: nothing changed

        Assert.Equal(3, raised);
    }

    [StaFact]
    public void AMenuItemShowsAndReportsTheKeyOfItsCommand()
    {
        var registry = RegistryWithReply();
        var item = new MenuItem { Header = "_Reply" };
        CommandShortcut.SetRegistry(item, registry);
        CommandShortcut.SetId(item, "mail.reply");

        Assert.Equal("Ctrl+R", item.InputGestureText);
        // What a screen reader is actually given: InputGestureText alone never reaches it.
        Assert.Equal("Ctrl+R", new MenuItemAutomationPeer(item).GetAcceleratorKey());
    }

    [StaFact]
    public void AMenuItemPicksUpARebindWithoutBeingRebuilt()
    {
        var registry = RegistryWithReply();
        var item = new MenuItem { Header = "_Reply" };
        var window = new Window
        {
            Content = new Menu { Items = { item } },
            ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000, Width = 200, Height = 100,
        };
        CommandShortcut.SetRegistry(window, registry);
        CommandShortcut.SetId(item, "mail.reply");
        try
        {
            window.Show();
            Assert.Equal("Ctrl+R", item.InputGestureText);

            registry.ApplyUserOverrides(ReplyOnCtrlShiftY);

            Assert.Equal("Ctrl+Shift+Y", item.InputGestureText);
            Assert.Equal("Ctrl+Shift+Y", AutomationProperties.GetAcceleratorKey(item));
        }
        finally { window.Close(); }

        // Once unloaded it no longer listens, so the registry does not keep a closed window alive.
        registry.ApplyUserOverrides([]);
        Assert.Equal("Ctrl+Shift+Y", item.InputGestureText);
    }

    [StaFact]
    public void AContextMenuItemUsesTheRegistryOfTheControlItOpensOn()
    {
        // A context menu is outside its window's tree, so nothing is inherited into it.
        var registry = RegistryWithReply();
        var list = new ListBox();
        CommandShortcut.SetRegistry(list, registry);
        var item = new MenuItem { Header = "_Reply" };
        var menu = new ContextMenu { Items = { item }, PlacementTarget = list };
        CommandShortcut.SetId(item, "mail.reply");

        CommandShortcut.Refresh(item);

        Assert.Same(menu, item.Parent);
        Assert.Equal("Ctrl+R", item.InputGestureText);
        Assert.Equal("Ctrl+R", AutomationProperties.GetAcceleratorKey(item));
    }

    [StaFact]
    public void AButtonReportsItsKeyAndItsTooltipIsNotAlsoItsHelpText()
    {
        // The toolbar used to read "New Message button New Message (Ctrl+N) Ctrl+N": the tooltip
        // string doubles as help text. Built only while open, the tooltip adds nothing to speech.
        var registry = RegistryWithReply();
        var button = new Button { Content = "Reply" };
        AutomationProperties.SetName(button, "Reply");
        CommandShortcut.SetRegistry(button, registry);
        CommandShortcut.SetId(button, "mail.reply");
        CommandShortcut.SetToolTip(button, "Reply");

        var peer = new ButtonAutomationPeer(button);
        Assert.Equal("Ctrl+R", peer.GetAcceleratorKey());
        Assert.Equal(string.Empty, peer.GetHelpText());
        Assert.IsType<ToolTip>(button.ToolTip);
    }

    [Fact]
    public void TheVisibleTooltipNamesTheKeyWhenThereIsOne()
    {
        Assert.Equal("Reply (Ctrl+R)", CommandShortcut.FormatToolTip("Reply", "Ctrl+R"));
        Assert.Equal("Accounts", CommandShortcut.FormatToolTip("Accounts", ""));
    }

    [StaFact]
    public void WithoutARegistryNothingIsTouched()
    {
        // XAML loaded in a test, or before the window has its registry, keeps whatever it had.
        var item = new MenuItem { Header = "_Reply", InputGestureText = "kept" };
        CommandShortcut.SetId(item, "mail.reply");
        Assert.Equal("kept", item.InputGestureText);
    }
}
