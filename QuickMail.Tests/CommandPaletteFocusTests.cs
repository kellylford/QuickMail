// Where the palette's keyboard focus actually lands when the window opens for real, and what
// happens to characters that are typed rather than assigned.
//
// CommandPaletteSpeechTests cannot answer either question: its helper calls FilterBox.Focus()
// itself before asserting (it has to — it shows the window with ShowActivated = false), so every
// focus assertion there is self-fulfilling, and it sets TextBox.Text directly, which bypasses
// input entirely. Reported from a real build: typing in the palette narrowed nothing and the
// selection jumped to a command starting with the last letter typed — the signature of the
// keystrokes reaching the LIST rather than the filter box.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class CommandPaletteFocusTests
{
    [StaFact]
    public void OnOpen_TheFilterBoxTakesFocusWithoutHelp()
    {
        var (window, box, list) = OpenActivated();
        try
        {
            Assert.True(box.IsKeyboardFocused,
                $"The filter box must hold keyboard focus when the palette opens. "
              + $"Focus is on: {Describe(Keyboard.FocusedElement)}");
            Assert.False(list.IsKeyboardFocusWithin);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TypedCharacters_ReachTheFilterBoxAndNarrowTheList()
    {
        // This is also the test that separates filtering from type-ahead, which is the failure
        // that was reported: WPF's built-in TextSearch on the list would leave all four items in
        // place and merely move the selection. Four going to one can only be the filter.
        var (window, box, list) = OpenActivated();
        try
        {
            Assert.Equal(4, list.Items.Count);

            Type(window, "mov");

            Assert.Equal("mov", box.Text);
            Assert.Single(list.Items);
            Assert.Equal("Move to Archive", ((CommandDefinition)list.SelectedItem).Title);
        }
        finally { window.Close(); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (CommandPaletteWindow window, TextBox box, ListBox list) OpenActivated()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var registry = new CommandRegistry();
        foreach (var (id, category, title) in new[]
                 {
                     ("mail.archive",   "Mail",     "Move to Archive"),
                     ("cal.editAppt",   "Calendar", "Edit Appointment"),
                     ("view.goFolder",  "View",     "Go to Folder"),
                     ("mail.reply",     "Mail",     "Reply"),
                 })
        {
            registry.Register(new CommandDefinition(id, category, title, execute: () => { }));
        }

        // Activated, and nothing here touches focus: that is the whole point.
        var window = new CommandPaletteWindow(registry)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false,
        };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Drain();

        var box = window.FindName("FilterBox") as TextBox;
        Assert.NotNull(box);
        var list = window.FindName("CommandList") as ListBox;
        Assert.NotNull(list);

        return (window, box!, list!);
    }

    /// <summary>
    /// Types into whatever currently has focus, the way a keyboard would — not by assigning
    /// <c>TextBox.Text</c>, which is what hid this.
    /// </summary>
    private static void Type(Window window, string text)
    {
        foreach (var ch in text)
        {
            var target = Keyboard.FocusedElement as UIElement ?? window;
            var composition = new TextComposition(InputManager.Current, target, ch.ToString());
            target.RaiseEvent(new TextCompositionEventArgs(
                InputManager.Current.PrimaryKeyboardDevice, composition)
            {
                RoutedEvent = UIElement.TextInputEvent,
            });
            Drain();
        }
    }

    private static string Describe(IInputElement? element) => element switch
    {
        null => "nothing",
        FrameworkElement fe when !string.IsNullOrEmpty(fe.Name) => $"{fe.GetType().Name}#{fe.Name}",
        _ => element.GetType().Name,
    };

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
