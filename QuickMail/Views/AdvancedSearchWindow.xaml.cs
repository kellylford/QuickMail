using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Advanced Search (#717, phase 2). Modeless, per the CLAUDE.md modal rules — it has editable fields and
/// opens over the reading pane's WebView2 — so Escape and Close are wired explicitly. The window builds the
/// request; <see cref="SearchRunner"/>, supplied by the main window, runs it and says how many were found.
/// When nothing is found the window stays open with focus back in Words anywhere, so the search can be
/// changed and tried again; otherwise it closes and the main window takes focus to the results.
/// </summary>
public partial class AdvancedSearchWindow : Window
{
    private readonly AdvancedSearchViewModel _vm;
    private readonly CommandRegistry _registry = new();
    private bool _searching;

    /// <summary>Runs a request and says what it found. Set by the owner before showing.</summary>
    public Func<AdvancedSearchRequest, Task<MainViewModel.AdvancedSearchOutcome>>? SearchRunner { get; set; }

    /// <summary>True once a search found something and the window closed for it.</summary>
    public bool ClosedWithResults { get; private set; }

    /// <summary>What the search that closed the window came to, for the owner's announcement.</summary>
    public MainViewModel.AdvancedSearchOutcome? LastOutcome { get; private set; }

    public AdvancedSearchWindow(AdvancedSearchViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        _vm.SearchRequested += OnSearchRequested;
        RegisterPaletteCommands();

        Loaded += (_, _) => WordsBox.Focus();
    }

    private void RegisterPaletteCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "advancedSearch.search", category: "Mail", title: "Search",
            execute: RunSearch));
        _registry.Register(new CommandDefinition(
            id: "advancedSearch.clear", category: "Mail", title: "Clear Fields",
            execute: ClearFields));
        _registry.Register(new CommandDefinition(
            id: "advancedSearch.close", category: "Mail", title: "Close Advanced Search",
            execute: Close,
            defaultKey: Key.Escape, defaultModifiers: ModifierKeys.None));
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearFields();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RunSearch()
    {
        if (_searching) return;
        _vm.SearchCommand.Execute(null);
        // A request that was not worth running says why; the request itself arrives through SearchRequested.
        if (!string.IsNullOrEmpty(_vm.Problem))
            AccessibilityHelper.Announce(this, _vm.Problem, interrupt: true, category: AnnouncementCategory.Result);
    }

    private void ClearFields()
    {
        _vm.ClearCommand.Execute(null);
        WordsBox.Focus();
    }

    private async void OnSearchRequested(AdvancedSearchRequest request)
    {
        if (SearchRunner == null) return;
        _searching = true;
        SearchButton.IsEnabled = false;
        try
        {
            var outcome = await SearchRunner(request);
            LastOutcome = outcome;
            // Abandoned because the user moved to another folder meanwhile: nothing to report about it.
            if (outcome.Cancelled) return;
            if (outcome.Found > 0 && !outcome.Failed)
            {
                ClosedWithResults = true;
                _searching = false;
                Close();
                return;
            }
            AccessibilityHelper.Announce(this, NothingFoundText(request, outcome),
                interrupt: true, category: AnnouncementCategory.Result);
            WordsBox.Focus();
        }
        catch (Exception ex)
        {
            LogService.Log("Advanced search failed", ex);
            AccessibilityHelper.Announce(this, "Could not search.", interrupt: true, category: AnnouncementCategory.Result);
        }
        finally
        {
            _searching = false;
            SearchButton.IsEnabled = true;
        }
    }

    /// <summary>What to say when a search found nothing — including why the server did not help, if asked.</summary>
    internal static string NothingFoundText(AdvancedSearchRequest request, MainViewModel.AdvancedSearchOutcome outcome)
    {
        if (outcome.Failed) return "Could not search.";
        if (request.InCurrentFolder) return "No messages found.";
        if (!request.SearchServer)
            return "No messages found on this computer. Check Also search the mail server to look there too.";
        var server = outcome.Server;
        if (server == null || server.Asked == 0)
            return "No messages found. No account's mail server could be searched.";
        if (server.FailedAccounts.Count == server.Asked)
            return "No messages found on this computer, and could not search the server.";
        return "No messages found.";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+P — command palette (framework-level; cannot dispatch through itself).
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            var previous = Keyboard.FocusedElement as IInputElement;
            new CommandPaletteWindow(_registry) { Owner = this }.ShowDialog();
            (previous ?? WordsBox).Focus();
            return;
        }

        // F6 / Shift+F6 — cycle the logical stops (framework-level, like the palette).
        if (e.Key == Key.F6)
        {
            e.Handled = true;
            CycleFocus(forward: Keyboard.Modifiers != ModifierKeys.Shift);
            return;
        }

        // An open drop-down owns Escape: it closes the list, not the window.
        if (e.Key == Key.Escape && (ReadCombo.IsDropDownOpen || FlagCombo.IsDropDownOpen)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var cmd = _registry.FindByGesture(key, Keyboard.Modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            e.Handled = true;
            cmd.Execute();
        }
    }

    /// <summary>Cycles the logical stops: the fields, where to look, the accounts, the buttons.</summary>
    private void CycleFocus(bool forward)
    {
        var stops = new System.Collections.Generic.List<(Func<bool> Has, Action Focus)>
        {
            (() => FieldsPanel.IsKeyboardFocusWithin, () => WordsBox.Focus()),
            (() => LookInGroup.IsKeyboardFocusWithin, FocusLookIn),
        };
        if (_vm.SearchInAccounts && _vm.Accounts.Count > 0)
            stops.Add((() => AccountsList.IsKeyboardFocusWithin || AlsoSearchServerBox.IsKeyboardFocused, FocusFirstAccount));
        stops.Add((() => SearchButton.IsKeyboardFocused || ClearButton.IsKeyboardFocused || CloseButton.IsKeyboardFocused,
                   () => SearchButton.Focus()));

        var current = stops.FindIndex(s => s.Has());
        var next = current < 0
            ? 0
            : forward ? (current + 1) % stops.Count : (current - 1 + stops.Count) % stops.Count;
        stops[next].Focus();
    }

    private void FocusLookIn()
    {
        if (CurrentFolderRadio.IsChecked == true && CurrentFolderRadio.IsEnabled) CurrentFolderRadio.Focus();
        else AccountsRadio.Focus();
    }

    // The list puts focus on a row itself, and remembers which row it was on.
    private void FocusFirstAccount() => AccountsList.Focus();

    /// <summary>
    /// A search that is running holds the window open: closing it then would leave the result with nowhere to
    /// report to and focus with nowhere to go. Escape and Close work again as soon as it finishes.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_searching) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.SearchRequested -= OnSearchRequested;
        base.OnClosed(e);
    }
}
