// Issue #300: WPF inserts Space through an editing command and marks the KeyDown handled, so no
// WM_CHAR is generated and screen readers watching for it never see the end of a word.
// TypedSpaceFix gives each editor a NotACommand binding for Space so the key stays unhandled.

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class NvdaTypingFixSettingTests
{
    [Fact]
    public void DefaultsToOn()
    {
        Assert.True(new ConfigModel().NvdaTypingFix);
    }

    [Fact]
    public void RoundTripsThroughConfigIni()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-typing-fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new ConfigService(new ProfileContext(dir));
            var cfg = service.Load();
            Assert.True(cfg.NvdaTypingFix);   // default in a fresh profile

            cfg.NvdaTypingFix = false;
            service.Save(cfg);
            Assert.False(new ConfigService(new ProfileContext(dir)).Load().NvdaTypingFix);

            cfg.NvdaTypingFix = true;
            service.Save(cfg);
            Assert.True(new ConfigService(new ProfileContext(dir)).Load().NvdaTypingFix);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SettingsViewModel_LoadsAndSavesIt()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.NvdaTypingFix = false;
        config.Save(cfg);

        var vm = new SettingsViewModel(config, new StubCommandRegistry(), new StubThemeService(), ["Arial"]);
        Assert.False(vm.NvdaTypingFix);

        vm.NvdaTypingFix = true;
        vm.SaveCommand.Execute(null);
        Assert.True(config.Load().NvdaTypingFix);
    }
}

[Collection("WpfTests")]
public class TypedSpaceFixTests
{
    [StaFact]
    public void Apply_AddsBothGesturesOnce_AndRemovesOnlyItsOwn()
    {
        var box = new TextBox();
        var mine = new KeyBinding(ApplicationCommands.Copy, Key.Space, ModifierKeys.Control);
        box.InputBindings.Add(mine);

        TypedSpaceFix.Apply(box, true);
        TypedSpaceFix.Apply(box, true);   // idempotent
        Assert.True(TypedSpaceFix.IsApplied(box));
        Assert.Equal(3, box.InputBindings.Count);
        Assert.Contains(box.InputBindings.OfType<KeyBinding>(), b =>
            b.Key == Key.Space && b.Modifiers == ModifierKeys.Shift && b.Command == ApplicationCommands.NotACommand);

        TypedSpaceFix.Apply(box, false);
        Assert.False(TypedSpaceFix.IsApplied(box));
        Assert.Same(mine, Assert.Single(box.InputBindings.Cast<InputBinding>()));
    }

    // The mechanism itself: without the fix WPF's Space command handles KeyDown (so no WM_CHAR
    // would follow); with it, KeyDown stays unhandled for both TextBox and RichTextBox.
    [StaFact]
    public void SpaceKeyDown_IsHandledByWpf_UnlessTheFixIsApplied()
    {
        var wasEnabled = TypedSpaceFix.Enabled;
        var text = new TextBox { AcceptsReturn = true };
        var rich = new RichTextBox();
        var window = NewWindow(new StackPanel { Children = { text, rich } });
        try
        {
            TypedSpaceFix.SetEnabled(false);
            foreach (TextBoxBase box in new TextBoxBase[] { text, rich })
            {
                Assert.True(PressSpace(box, ModifierKeys.None), $"{box.GetType().Name}: WPF no longer handles Space itself");
                TypedSpaceFix.Apply(box, true);
                Assert.False(PressSpace(box, ModifierKeys.None), $"{box.GetType().Name}: Space still handled with the fix");
            }
        }
        finally
        {
            window.Close();
            TypedSpaceFix.SetEnabled(wasEnabled);
        }
    }

    [StaFact]
    public void SetEnabled_UpdatesLoadedEditors_AndNewEditorsGetItOnLoad()
    {
        var wasEnabled = TypedSpaceFix.Enabled;
        TypedSpaceFix.Register();
        var panel = new StackPanel();
        var first = new TextBox();
        panel.Children.Add(first);
        var window = NewWindow(panel);
        try
        {
            // In the app SetEnabled walks Application.Windows; the test host's Application
            // belongs to another thread, so apply to this window the same way.
            TypedSpaceFix.SetEnabled(true);
            TypedSpaceFix.ApplyToWindow(window);
            Assert.True(TypedSpaceFix.IsApplied(first));

            var later = new TextBox();
            panel.Children.Add(later);
            window.UpdateLayout();
            Drain();
            Assert.True(TypedSpaceFix.IsApplied(later));

            TypedSpaceFix.SetEnabled(false);
            TypedSpaceFix.ApplyToWindow(window);
            Assert.False(TypedSpaceFix.IsApplied(first));
            Assert.False(TypedSpaceFix.IsApplied(later));

            var afterOff = new TextBox();
            panel.Children.Add(afterOff);
            window.UpdateLayout();
            Drain();
            Assert.False(TypedSpaceFix.IsApplied(afterOff));
        }
        finally
        {
            window.Close();
            TypedSpaceFix.SetEnabled(wasEnabled);
        }
    }

    private static bool PressSpace(TextBoxBase box, ModifierKeys modifiers)
    {
        box.Focus();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Space)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
        box.RaiseEvent(args);
        return args.Handled;
    }

    private static Window NewWindow(UIElement content)
    {
        var window = new Window
        {
            Content = content,
            Width = 300, Height = 200,
            Left = -10000, Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        Drain();
        return window;
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
