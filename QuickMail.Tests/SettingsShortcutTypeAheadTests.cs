using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// First-letter navigation in Settings → Keyboard Shortcuts (#852). The prefix and matching rules
/// are covered by <see cref="TypeAheadLogicTests"/>; these check the list applies them to the
/// command names in the order it shows them, and that typed text never leaves the list, where it
/// would fire the dialog's access keys (Shift+S for Save, Shift+C for Cancel) without Alt.
/// The clock is fixed, so "letters typed quickly" means the same thing on a slow runner.
/// </summary>
[Collection("WpfTests")]
public class SettingsShortcutTypeAheadTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Rows are listed by category, then name: Mail's four, then View's two.
    private static (SettingsDialog Dialog, SettingsViewModel Vm, FakeClock Clock) MakeDialog()
    {
        WpfTestHost.EnsureApplication();
        var registry = new StubCommandRegistry();
        registry.RegisterTestCommand("mail.forward", "Mail", "Forward");
        registry.RegisterTestCommand("mail.reply", "Mail", "Reply");
        registry.RegisterTestCommand("mail.replyAll", "Mail", "Reply All");
        registry.RegisterTestCommand("mail.toggleWatch", "Mail", "Watch Conversation");
        registry.RegisterTestCommand("view.filterWatched", "View", "Watched Only");
        registry.RegisterTestCommand("view.zoom", "View", "Zoom");
        var vm = new SettingsViewModel(new StubConfigService(), registry);
        var clock = new FakeClock();
        var dialog = new SettingsDialog(vm) { HotkeyTypeAhead = new TypeAheadPrefixTracker(clock) };
        return (dialog, vm, clock);
    }

    private static string? Selected(SettingsViewModel vm) => vm.SelectedHotkey?.Title;

    private static bool Type(SettingsDialog dialog, string text)
    {
        var list = dialog.FindName("HotkeyListView") as ListView;
        Assert.NotNull(list);
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, list, text))
        {
            RoutedEvent = UIElement.PreviewTextInputEvent,
        };
        list.RaiseEvent(args);
        return args.Handled;
    }

    [StaFact]
    public void ALetterMovesToTheFirstCommandStartingWithIt()
    {
        var (dialog, vm, _) = MakeDialog();
        try
        {
            Assert.True(Type(dialog, "w"));
            Assert.Equal("Watch Conversation", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void TheSameLetterAgainMovesToTheNextAndWrapsAround()
    {
        var (dialog, vm, clock) = MakeDialog();
        try
        {
            Type(dialog, "w");
            Type(dialog, "w");
            Assert.Equal("Watched Only", Selected(vm));

            // After a pause too: a new search for "w" starts after the current command.
            clock.Now += TimeSpan.FromSeconds(5);
            Type(dialog, "w");
            Assert.Equal("Watch Conversation", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void LettersTypedQuicklyMatchMoreOfTheName()
    {
        var (dialog, vm, _) = MakeDialog();
        try
        {
            Assert.True(dialog.TryHotkeyTypeAhead("r"));
            Assert.Equal("Reply", Selected(vm));

            // "re" still matches Reply, so the list stays there rather than skipping to Reply All.
            Assert.True(dialog.TryHotkeyTypeAhead("e"));
            Assert.Equal("Reply", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void CapitalsNavigateToo()
    {
        var (dialog, vm, _) = MakeDialog();
        try
        {
            Assert.True(Type(dialog, "Z"));
            Assert.Equal("Zoom", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void TextThatMatchesNothingStaysInTheList()
    {
        // Not matching must not mean passing it on: "C" or "S" reaching the window would press
        // Cancel or Save, and "g" would switch to the General tab.
        var (dialog, vm, _) = MakeDialog();
        try
        {
            Assert.True(Type(dialog, "S"));
            Assert.True(Type(dialog, "!"));
            Assert.Null(vm.SelectedHotkey);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void ItMatchesTheCommandNameNotTheCategory()
    {
        var (dialog, vm, _) = MakeDialog();
        try
        {
            // "v" is View's category, but no command name starts with V.
            Assert.False(dialog.TryHotkeyTypeAhead("v"));
            Assert.Null(vm.SelectedHotkey);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void ControlCharactersAndSpacesAreLeftToTheList()
    {
        // Enter arrives as "\r" and opens the shortcut capture through KeyDown; Space selects.
        var (dialog, _, _) = MakeDialog();
        try
        {
            Assert.False(Type(dialog, "\r"));
            Assert.False(Type(dialog, " "));
        }
        finally { dialog.Close(); }
    }
}
