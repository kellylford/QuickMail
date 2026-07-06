using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// "Report a Bug" window. Modeless by design (<see cref="Window.Show"/>, never
/// <see cref="Window.ShowDialog"/>) — it has multiple editable TextBoxes and is opened over
/// MainWindow, which hosts a live WebView2 reading pane. That combination is the exact
/// profile documented in CLAUDE.md's Modal Dialog Rules as able to deadlock the UI thread
/// (the GrabAddressesDialog incident); see docs/planning/bug-reporting-pm-dev-spec.md,
/// Decision C.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "_vm.Dispose() is called explicitly in OnClosed below; WPF does not call Dispose on Window instances, so implementing IDisposable on the Window itself would never be invoked.")]
public partial class ReportBugWindow : Window
{
    private readonly ReportBugViewModel _vm;
    private readonly CommandRegistry _registry = new();

    public ReportBugWindow(IBugReportService bugReportService)
    {
        _vm = new ReportBugViewModel(bugReportService);
        InitializeComponent();
        DataContext = _vm;

        _vm.AnnouncementRequested += OnAnnouncementRequested;
        _vm.CloseRequested        += OnCloseRequested;
        _vm.SendSucceeded         += OnSendSucceeded;
        _vm.SendFailed            += OnSendFailed;

        RegisterCommands();

        Loaded += (_, _) => SummaryBox.Focus();
    }

    private void OnAnnouncementRequested(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, category: category);

    // Named handlers (not lambdas) so OnClosed can unsubscribe them individually. Without this,
    // a Send still in flight when the window closes resumes after Dispose() and — with the
    // lambda still subscribed — calls Focus() on a control belonging to an already-closed window.
    private void OnCloseRequested(object? sender, EventArgs e) => Close();
    private void OnSendSucceeded(object? sender, EventArgs e) => IssueLinkButton.Focus();
    private void OnSendFailed(object? sender, EventArgs e) => CopyAndOpenButton.Focus();

    private void RegisterCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "reportbug.send", category: "Help", title: "Send Report",
            execute: () => _vm.SendCommand.Execute(null),
            defaultKey: Key.Enter, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "reportbug.copyAndOpen", category: "Help", title: "Copy Report and Open GitHub",
            execute: () => _vm.CopyAndOpenCommand.Execute(null)));

        _registry.Register(new CommandDefinition(
            id: "reportbug.cancel", category: "Help", title: "Cancel / Close",
            execute: () => _vm.CancelCommand.Execute(null),
            defaultKey: Key.Escape, defaultModifiers: ModifierKeys.None));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            CycleFocus();
            e.Handled = true;
            return;
        }

        var cmd = _registry.FindByGesture(e.Key, Keyboard.Modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            cmd.Execute();
            e.Handled = true;
        }
    }

    // Two logical F6 stops: the form fields, and the read-only preview. With exactly two
    // stops, F6 and Shift+F6 both just toggle — there is no distinct "forward"/"reverse".
    private void CycleFocus()
    {
        System.Windows.Controls.TextBox target = PreviewBox.IsKeyboardFocusWithin ? SummaryBox : PreviewBox;
        target.Focus();
    }

    private void OpenCommandPalette()
    {
        var previousFocus = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_registry) { Owner = this };
        palette.ShowDialog();
        (previousFocus ?? SummaryBox).Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        _vm.CloseRequested        -= OnCloseRequested;
        _vm.SendSucceeded         -= OnSendSucceeded;
        _vm.SendFailed            -= OnSendFailed;
        _vm.Dispose();
        base.OnClosed(e);
    }
}
