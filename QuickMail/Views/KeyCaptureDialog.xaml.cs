using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Views;

public partial class KeyCaptureDialog : Window
{
    private Key _capturedKey = Key.None;
    private ModifierKeys _capturedModifiers = ModifierKeys.None;
    private bool _inConfirmState;

    public Key CapturedKey => _capturedKey;
    public ModifierKeys CapturedModifiers => _capturedModifiers;

    /// <summary>
    /// Optional. Invoked after a key combination is captured to check whether it conflicts
    /// with an existing binding. Return a short description of the conflict (e.g. the
    /// conflicting command/view name), or null if there is no conflict. The dialog displays
    /// the message inline; the caller is responsible for resolving the conflict if the user
    /// presses OK (typically by clearing the other binding before applying the new one).
    /// </summary>
    public Func<Key, ModifierKeys, string?>? ConflictChecker { get; set; }

    /// <summary>True when the user accepted a combination that the ConflictChecker flagged.</summary>
    public bool HasConflict { get; private set; }

    public KeyCaptureDialog()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MainGrid.Focus();
        Keyboard.Focus(MainGrid);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Once we're showing OK/Change/Cancel, let normal keyboard handling run
        // (Enter/Escape/Tab, button accelerators) instead of re-capturing.
        if (_inConfirmState) return;
        CaptureKey(e);
    }

    private void CaptureKey(KeyEventArgs e)
    {
        // Ignore modifier-only keys.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        _capturedKey       = e.Key;
        _capturedModifiers = modifiers;
        e.Handled = true;

        EnterConfirmState();
    }

    private void EnterConfirmState()
    {
        _inConfirmState = true;

        var gesture = GestureHelper.Format(_capturedKey, _capturedModifiers);
        CapturedKeyText.Text = gesture;
        CapturedKeyText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            Theming.ThemeKeys.TextPrimary);

        ListeningHeader.Text = "Captured shortcut";
        ListeningHint.Visibility = Visibility.Collapsed;

        var conflict = ConflictChecker?.Invoke(_capturedKey, _capturedModifiers);
        HasConflict = !string.IsNullOrEmpty(conflict);
        if (HasConflict)
        {
            ConflictText.Text = $"⚠ Already assigned to: {conflict}. " +
                                "Select OK to reassign, Change to pick another, or Cancel to keep the existing binding.";
            ConflictText.Visibility = Visibility.Visible;
        }
        else
        {
            ConflictText.Visibility = Visibility.Collapsed;
        }

        OkButton.Visibility     = Visibility.Visible;
        ChangeButton.Visibility = Visibility.Visible;

        // Defer focus + announce until after the visibility changes settle so screen readers
        // pick up the new state.
        Dispatcher.InvokeAsync(() =>
        {
            OkButton.Focus();
            var announce = HasConflict
                ? $"{gesture}. {ConflictText.Text}"
                : gesture;
            AccessibilityHelper.Announce(OkButton, announce);
        }, DispatcherPriority.Input);
    }

    private void ChangeButton_Click(object sender, RoutedEventArgs e)
    {
        // Return to listening state so the user can press a different combination.
        _inConfirmState     = false;
        _capturedKey        = Key.None;
        _capturedModifiers  = ModifierKeys.None;
        HasConflict         = false;

        CapturedKeyText.Text = "(waiting for input...)";
        CapturedKeyText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            Theming.ThemeKeys.TextSecondary);
        ListeningHeader.Text       = "Press your desired key combination";
        ListeningHint.Visibility   = Visibility.Visible;
        ConflictText.Visibility    = Visibility.Collapsed;
        OkButton.Visibility        = Visibility.Collapsed;
        ChangeButton.Visibility    = Visibility.Collapsed;

        MainGrid.Focus();
        Keyboard.Focus(MainGrid);
        AccessibilityHelper.Announce(MainGrid, "Listening for a new shortcut.", category: AnnouncementCategory.Hint);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
