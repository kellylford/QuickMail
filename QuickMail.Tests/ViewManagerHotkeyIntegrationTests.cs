using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// End-to-end-ish tests that simulate the View Manager flow using the REAL
/// CommandRegistry and a real ConfigService against a temp directory. The Stub
/// versions don't honour overrides (StubCommandRegistry.FindByGesture only
/// looks at DefaultKey/Modifiers; StubConfigService is in-memory only) which
/// is why the unit-level tests passed while the actual app bug existed.
/// </summary>
public class ViewManagerHotkeyIntegrationTests
{
    private static ProfileContext MakeTempProfile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"QM-VM-{Guid.NewGuid():N}");
        return new ProfileContext(tempDir);
    }

    /// <summary>Mimics what MainViewModel.RegisterOneViewCommand does at startup —
    /// reads the gesture from cfg.CustomHotkeys (falling back to view.Hotkey) and
    /// registers a command whose DefaultKey/Modifiers match.</summary>
    private static void RegisterViewCommand(
        CommandRegistry registry, IConfigService config, SavedView view, Action onExecute)
    {
        var commandId = $"view.saved.{view.Id}";
        var cfg       = config.Load();
        var binding   = cfg.CustomHotkeys.FirstOrDefault(h => h.CommandId == commandId);
        var gesture   = binding?.Gesture ?? view.Hotkey;

        Key defaultKey = Key.None;
        ModifierKeys defaultMods = ModifierKeys.None;
        if (!string.IsNullOrEmpty(gesture))
            QuickMail.Helpers.GestureHelper.TryParse(gesture, out defaultKey, out defaultMods);

        registry.Register(new CommandDefinition(
            id:               commandId,
            category:         "Views",
            title:            view.Name,
            execute:          onExecute,
            defaultKey:       defaultKey,
            defaultModifiers: defaultMods));
    }

    [Fact]
    public void AssigningHotkeyViaViewManager_TakesEffectAfterDialogCloses()
    {
        var config   = new ConfigService(MakeTempProfile());
        var registry = new CommandRegistry();
        var views    = new StubViewService();

        // Initial state: one saved view, no hotkey, registered at startup.
        var view = new SavedView { Name = "Inbox Unread", Id = Guid.NewGuid() };
        int executeCount = 0;
        RegisterViewCommand(registry, config, view, () => executeCount++);

        // === Simulate opening View Manager and assigning a hotkey ===
        var vmVm = new ViewManagerViewModel(
            views, config, registry,
            savedViews:      new[] { view },
            currentFolder:   null,
            currentAccount:  null,
            currentViewMode: ViewMode.Messages,
            currentFilter:   MessageFilter.All,
            currentSort:     MessageSort.DateDescending);

        vmVm.SelectedView = view;
        vmVm.StartEditCommand.Execute(null);
        vmVm.ApplyHotkey(Key.D1, ModifierKeys.Control);

        Assert.Equal("Ctrl+1", vmVm.EditHotkey);

        // Save. CommitEdits → PersistHotkey → cfg.CustomHotkeys updated, ApplyUserOverrides called.
        vmVm.SaveCommand.Execute(null);

        // === Simulate the post-close UpdateSavedViews → RegisterViewCommands ===
        // (UpdateSavedViews in MainViewModel reloads from disk and re-registers each
        // view command. The new view command should have the new gesture as its default.)
        registry.Unregister($"view.saved.{view.Id}");
        var reloadedViews = views.Load(); // stub returns []; we use the in-memory view
        var reloadedView = view;
        RegisterViewCommand(registry, config, reloadedView, () => executeCount++);

        // === The actual assertion: pressing Ctrl+1 must fire the view command ===
        var cmd = registry.FindByGesture(Key.D1, ModifierKeys.Control);
        Assert.NotNull(cmd);
        Assert.Equal($"view.saved.{view.Id}", cmd!.Id);

        cmd.Execute();
        Assert.Equal(1, executeCount);
    }

    [Fact]
    public void ChangingViewHotkeyClearsTheOldGesture()
    {
        var config   = new ConfigService(MakeTempProfile());
        var registry = new CommandRegistry();
        var views    = new StubViewService();

        // View starts with Ctrl+1 already assigned via the SavedView.Hotkey field.
        var view = new SavedView { Name = "X", Id = Guid.NewGuid(), Hotkey = "Ctrl+1" };
        int executeCount = 0;
        RegisterViewCommand(registry, config, view, () => executeCount++);

        Assert.NotNull(registry.FindByGesture(Key.D1, ModifierKeys.Control));

        // User opens View Manager and re-assigns to Ctrl+2.
        var vmVm = new ViewManagerViewModel(
            views, config, registry,
            savedViews:      new[] { view },
            currentFolder:   null,
            currentAccount:  null,
            currentViewMode: ViewMode.Messages,
            currentFilter:   MessageFilter.All,
            currentSort:     MessageSort.DateDescending);
        vmVm.SelectedView = view;
        vmVm.StartEditCommand.Execute(null);
        vmVm.ApplyHotkey(Key.D2, ModifierKeys.Control);
        vmVm.SaveCommand.Execute(null);

        // Post-close: re-register from updated state.
        registry.Unregister($"view.saved.{view.Id}");
        RegisterViewCommand(registry, config, view, () => executeCount++);

        Assert.NotNull(registry.FindByGesture(Key.D2, ModifierKeys.Control));
        // Old Ctrl+1 must no longer fire the view command.
        Assert.Null(registry.FindByGesture(Key.D1, ModifierKeys.Control));
    }
}
