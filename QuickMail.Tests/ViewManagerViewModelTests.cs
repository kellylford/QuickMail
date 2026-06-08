using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ViewManagerViewModel behaviour and the ViewManagerWindow XAML.
///
/// Why these tests exist
/// ─────────────────────
/// A previous bug: the "Create New View from Current State" button used
///   Visibility="{Binding HasSelectedView, ConverterParameter=Inverse}"
/// BooleanToVisibilityConverter silently ignores ConverterParameter, so the button
/// was always Collapsed — it only showed up when a view WAS selected, which is the
/// opposite of the intended behaviour.
///
/// The ViewModel-property tests (Fact) verify the boolean contract the XAML relies on.
/// The window-visibility tests (StaFact) construct the real window and assert that the
/// bound Visibility values are correct — the kind of test that would have caught the bug
/// before it shipped.
/// </summary>
public class ViewManagerViewModelTests
{
    // ── Factory ──────────────────────────────────────────────────────────────────────

    private static ViewManagerViewModel MakeVm(
        IEnumerable<SavedView>? views = null,
        SavedView? selectedView = null)
    {
        var vm = new ViewManagerViewModel(
            new StubViewService(),
            new StubConfigService(),
            new StubCommandRegistry(),
            views ?? [],
            currentFolder:  null,
            currentAccount: null,
            currentViewMode: ViewMode.Messages,
            currentFilter:  MessageFilter.All,
            currentSort:    MessageSort.DateDescending);

        if (selectedView != null)
            vm.SelectedView = selectedView;

        return vm;
    }

    // ── HasSelectedView / HasNoSelectedView ──────────────────────────────────────────
    // These are the properties the XAML Visibility bindings depend on.
    // If the contract breaks, the buttons show/hide at the wrong time.

    [Fact]
    public void HasNoSelectedView_TrueWhenNothingSelected()
    {
        var vm = MakeVm();
        Assert.True(vm.HasNoSelectedView);
        Assert.False(vm.HasSelectedView);
    }

    [Fact]
    public void HasSelectedView_TrueAfterSelectingAView()
    {
        var view = new SavedView { Name = "Inbox Unread" };
        var vm   = MakeVm(views: [view], selectedView: view);

        Assert.True(vm.HasSelectedView);
        Assert.False(vm.HasNoSelectedView);
    }

    [Fact]
    public void HasNoSelectedView_FlipsBackWhenSelectionCleared()
    {
        var view = new SavedView { Name = "Test" };
        var vm   = MakeVm(views: [view], selectedView: view);

        Assert.False(vm.HasNoSelectedView);   // sanity check

        vm.SelectedView = null;

        Assert.True(vm.HasNoSelectedView);
        Assert.False(vm.HasSelectedView);
    }

    [Fact]
    public void HasSelectedView_AndHasNoSelectedView_AreAlwaysOpposite()
    {
        var view = new SavedView { Name = "A" };
        var vm   = MakeVm(views: [view]);

        // unselected
        Assert.NotEqual(vm.HasSelectedView, vm.HasNoSelectedView);

        vm.SelectedView = view;
        Assert.NotEqual(vm.HasSelectedView, vm.HasNoSelectedView);

        vm.SelectedView = null;
        Assert.NotEqual(vm.HasSelectedView, vm.HasNoSelectedView);
    }

    // ── CanSave ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CanSave_FalseWhenNoViewSelected()
    {
        var vm = MakeVm();
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_FalseWhenNameIsBlank()
    {
        var view = new SavedView { Name = "Test" };
        var vm   = MakeVm(views: [view], selectedView: view);
        vm.EditName = string.Empty;
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_FalseWhenNameIsWhitespaceOnly()
    {
        var view = new SavedView { Name = "Test" };
        var vm   = MakeVm(views: [view], selectedView: view);
        vm.EditName = "   ";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_TrueWhenViewSelectedAndNameNotEmpty()
    {
        var view = new SavedView { Name = "Test" };
        var vm   = MakeVm(views: [view], selectedView: view);
        vm.EditName = "My Inbox";
        Assert.True(vm.CanSave);
    }

    // ── Edit field sync ───────────────────────────────────────────────────────────────

    [Fact]
    public void SelectingAView_PopulatesEditFields()
    {
        var view = new SavedView { Name = "Work Inbox", IsDefault = true };
        var vm   = MakeVm(views: [view], selectedView: view);

        Assert.Equal("Work Inbox", vm.EditName);
        Assert.True(vm.EditIsDefault);
    }

    [Fact]
    public void ClearingSelection_ResetsEditFields()
    {
        var view = new SavedView { Name = "Work Inbox", IsDefault = true };
        var vm   = MakeVm(views: [view], selectedView: view);

        vm.SelectedView = null;

        Assert.Equal(string.Empty, vm.EditName);
        Assert.Equal(string.Empty, vm.EditHotkey);
        Assert.False(vm.EditIsDefault);
    }

    // ── Commands ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveAsNew_AddsViewAndSelectsIt()
    {
        var vm = MakeVm();
        vm.SaveAsNewCommand.Execute(null);

        Assert.Single(vm.SavedViews);
        Assert.NotNull(vm.SelectedView);
    }

    [Fact]
    public void Delete_RemovesViewAndClearsSelection()
    {
        var view = new SavedView { Name = "Delete Me" };
        var vm   = MakeVm(views: [view], selectedView: view);

        vm.DeleteCommand.Execute(null);

        Assert.Empty(vm.SavedViews);
        Assert.Null(vm.SelectedView);
    }

    [Fact]
    public void ClearHotkey_EmptiesEditHotkey()
    {
        var view = new SavedView { Name = "Test", Hotkey = "Ctrl+1" };
        var vm   = MakeVm(views: [view], selectedView: view);

        // EditHotkey is populated from view.Hotkey by OnSelectedViewChanged.
        Assert.Equal("Ctrl+1", vm.EditHotkey);

        vm.ClearHotkeyCommand.Execute(null);

        Assert.Equal(string.Empty, vm.EditHotkey);
    }

    // ── Summary helpers ───────────────────────────────────────────────────────────────

    [Fact]
    public void SelectedFoldersSummary_EmptyWhenNoViewSelected()
    {
        var vm = MakeVm();
        Assert.Equal(string.Empty, vm.SelectedFoldersSummary);
    }

    [Fact]
    public void SelectedFoldersSummary_ShowsNoFoldersPlaceholder()
    {
        var view = new SavedView { Name = "Empty" };  // no folders added
        var vm   = MakeVm(views: [view], selectedView: view);

        Assert.Equal("(no folders)", vm.SelectedFoldersSummary);
    }

    [Fact]
    public void SelectedModeSummary_EmptyWhenNoViewSelected()
    {
        var vm = MakeVm();
        Assert.Equal(string.Empty, vm.SelectedModeSummary);
    }
}

/// <summary>
/// XAML parse + element-visibility tests for ViewManagerWindow.
///
/// These tests construct the real window and check that key elements have the
/// correct Visibility based on the ViewModel state.  A test like
/// <see cref="CreateNewViewButton_VisibleWhenNoViewSelected"/> would have directly
/// caught the ConverterParameter=Inverse bug before it shipped.
/// </summary>
[Collection("WpfTests")]
public class ViewManagerWindowTests
{
    private static void EnsureApplication()
    {
        // Lock on the Application type object — the same lock used by XamlParseTests —
        // so that parallel [StaFact] threads from different classes don't race to create
        // a second Application (WPF only allows one per AppDomain).
        lock (typeof(Application))
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        const string stylesUri = "pack://application:,,,/QuickMail;component/Styles/AccessibleStyles.xaml";
        var uri = new Uri(stylesUri, UriKind.Absolute);
        // Capture to local so nullable analysis knows it's non-null (it was just created above).
        var app = Application.Current!;
        if (app.Resources.MergedDictionaries.All(d => d.Source != uri))
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>
    /// WPF schedules binding updates at <see cref="System.Windows.Threading.DispatcherPriority.DataBind"/>
    /// rather than evaluating them synchronously when DataContext is set.  Without a running
    /// message loop the queue never drains, so tests that check a property which requires a
    /// binding to change its value from the WPF default (Visible) to something else (Collapsed)
    /// will fail.  Pushing a dispatcher frame at Background priority (lower than DataBind)
    /// causes the dispatcher to drain all pending DataBind operations before our empty action
    /// runs, ensuring binding values are committed before we assert.
    /// </summary>
    private static void FlushBindings()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static ViewManagerViewModel MakeVm(
        IEnumerable<SavedView>? views = null,
        SavedView? selectedView = null)
    {
        var vm = new ViewManagerViewModel(
            new StubViewService(),
            new StubConfigService(),
            new StubCommandRegistry(),
            views ?? [],
            currentFolder:  null,
            currentAccount: null,
            currentViewMode: ViewMode.Messages,
            currentFilter:  MessageFilter.All,
            currentSort:    MessageSort.DateDescending);

        if (selectedView != null)
            vm.SelectedView = selectedView;

        return vm;
    }

    // ── XAML parse ────────────────────────────────────────────────────────────────────

    [StaFact]
    public void ViewManagerWindow_XamlParsesWithoutException()
    {
        EnsureApplication();
        var vm     = MakeVm();
        var window = new ViewManagerWindow(vm);
        Assert.NotNull(window);
        window.Close();
    }

    // ── Visibility: no view selected ──────────────────────────────────────────────────
    //
    // When nothing is selected in the list:
    //   • "Create New View from Current State" button  → Visible
    //   • Save / Save As New / Delete panel            → Collapsed

    [StaFact]
    public void CreateNewViewButton_VisibleWhenNoViewSelected()
    {
        EnsureApplication();
        var vm     = MakeVm();                          // no views, nothing selected
        var window = new ViewManagerWindow(vm);
        FlushBindings();                                // drain DataBind queue

        var button = window.FindName("CreateNewViewButton") as Button;
        Assert.NotNull(button);   // fails clearly if x:Name is ever removed from XAML
        Assert.Equal(Visibility.Visible, button!.Visibility);

        window.Close();
    }

    [StaFact]
    public void ReadOnlyActionsPanel_CollapsedWhenNoViewSelected()
    {
        EnsureApplication();
        var vm     = MakeVm();
        var window = new ViewManagerWindow(vm);
        FlushBindings();

        var panel = window.FindName("ReadOnlyActionsPanel") as StackPanel;
        Assert.NotNull(panel);    // fails clearly if x:Name is ever removed from XAML
        Assert.Equal(Visibility.Collapsed, panel!.Visibility);

        window.Close();
    }

    // ── Visibility: a view is selected ────────────────────────────────────────────────
    //
    // When a saved view is selected:
    //   • "Create New View from Current State" button  → Collapsed
    //   • Read-only actions panel (Edit/Delete/Save As New)  → Visible

    [StaFact]
    public void CreateNewViewButton_CollapsedWhenViewSelected()
    {
        EnsureApplication();
        var view   = new SavedView { Name = "Work" };
        var vm     = MakeVm(views: [view], selectedView: view);
        var window = new ViewManagerWindow(vm);
        FlushBindings();

        var button = window.FindName("CreateNewViewButton") as Button;
        Assert.NotNull(button);
        Assert.Equal(Visibility.Collapsed, button!.Visibility);

        window.Close();
    }

    [StaFact]
    public void ReadOnlyActionsPanel_VisibleWhenViewSelected()
    {
        EnsureApplication();
        var view   = new SavedView { Name = "Work" };
        var vm     = MakeVm(views: [view], selectedView: view);
        var window = new ViewManagerWindow(vm);
        FlushBindings();

        var panel = window.FindName("ReadOnlyActionsPanel") as StackPanel;
        Assert.NotNull(panel);
        Assert.Equal(Visibility.Visible, panel!.Visibility);

        window.Close();
    }

    // ── Visibility: selection changes at runtime ──────────────────────────────────────

    [StaFact]
    public void Visibility_UpdatesWhenSelectionChanges()
    {
        EnsureApplication();
        var view   = new SavedView { Name = "Dynamic" };
        var vm     = MakeVm(views: [view]);              // starts with nothing selected
        var window = new ViewManagerWindow(vm);
        FlushBindings();

        var createBtn    = window.FindName("CreateNewViewButton") as Button;
        var actionsPanel = window.FindName("ReadOnlyActionsPanel") as StackPanel;
        Assert.NotNull(createBtn);
        Assert.NotNull(actionsPanel);

        // Initial: nothing selected
        Assert.Equal(Visibility.Visible,   createBtn.Visibility);
        Assert.Equal(Visibility.Collapsed, actionsPanel.Visibility);

        // Select a view — flush so the binding change propagates
        vm.SelectedView = view;
        FlushBindings();
        Assert.Equal(Visibility.Collapsed, createBtn.Visibility);
        Assert.Equal(Visibility.Visible,   actionsPanel.Visibility);

        // Clear selection — flush again
        vm.SelectedView = null;
        FlushBindings();
        Assert.Equal(Visibility.Visible,   createBtn.Visibility);
        Assert.Equal(Visibility.Collapsed, actionsPanel.Visibility);

        window.Close();
    }
}
