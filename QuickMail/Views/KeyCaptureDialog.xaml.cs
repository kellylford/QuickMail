using System.Windows;
using System.Windows.Input;
using QuickMail.Helpers;

namespace QuickMail.Views;

public partial class KeyCaptureDialog : Window
{
    private Key _capturedKey = Key.None;
    private ModifierKeys _capturedModifiers = ModifierKeys.None;

    public Key CapturedKey      => _capturedKey;
    public ModifierKeys CapturedModifiers => _capturedModifiers;

    public KeyCaptureDialog()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MainGrid.Focus();
        Keyboard.Focus(MainGrid);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => CaptureKey(e);
    private void Grid_KeyDown(object sender, KeyEventArgs e)          => CaptureKey(e);
    private void Window_KeyDown(object sender, KeyEventArgs e)        => CaptureKey(e);

    private void CaptureKey(KeyEventArgs e)
    {
        // Ignore modifier-only keys
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        _capturedKey       = e.Key;
        _capturedModifiers = modifiers;

        var gesture = GestureHelper.Format(_capturedKey, _capturedModifiers);
        CapturedKeyText.Text = gesture;
        OkButton.IsEnabled   = true;

        AccessibilityHelper.Announce(MainGrid, gesture);

        e.Handled = true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
