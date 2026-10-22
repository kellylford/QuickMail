using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// First-letter navigation in Settings → Keyboard Shortcuts (#852). Drives the dialog's
/// type-ahead entry point directly, without synthesized keys: the prefix and matching rules are
/// covered by <see cref="TypeAheadLogicTests"/>, and these check the list uses them on the command
/// names, in the order the list shows them.
/// </summary>
[Collection("WpfTests")]
public class SettingsShortcutTypeAheadTests
{
    // Rows are listed by category, then name: Mail's four, then View's two.
    private static (SettingsDialog Dialog, SettingsViewModel Vm) MakeDialog()
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
        return (new SettingsDialog(vm), vm);
    }

    private static string? Selected(SettingsViewModel vm) => vm.SelectedHotkey?.Title;

    [StaFact]
    public void ALetterMovesToTheFirstCommandStartingWithIt()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            Assert.True(dialog.TryHotkeyTypeAhead("w"));
            Assert.Equal("Watch Conversation", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void TheSameLetterAgainMovesToTheNextAndWrapsAround()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            dialog.TryHotkeyTypeAhead("w");
            dialog.TryHotkeyTypeAhead("w");
            Assert.Equal("Watched Only", Selected(vm));

            dialog.TryHotkeyTypeAhead("w");
            Assert.Equal("Watch Conversation", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void LettersTypedQuicklyMatchMoreOfTheName()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            dialog.TryHotkeyTypeAhead("r");
            Assert.Equal("Reply", Selected(vm));

            // "re" still matches Reply, so the list stays there rather than skipping to Reply All.
            dialog.TryHotkeyTypeAhead("e");
            Assert.Equal("Reply", Selected(vm));
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void ItMatchesTheCommandNameNotTheCategory()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            // "v" is View's category, but no command name starts with V.
            Assert.False(dialog.TryHotkeyTypeAhead("v"));
            Assert.Null(vm.SelectedHotkey);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void ALetterThatMatchesNothingLeavesTheSelectionAlone()
    {
        var (dialog, vm) = MakeDialog();
        try
        {
            dialog.TryHotkeyTypeAhead("z");
            Assert.False(dialog.TryHotkeyTypeAhead("q"));
            Assert.Equal("Zoom", Selected(vm));
        }
        finally { dialog.Close(); }
    }
}
