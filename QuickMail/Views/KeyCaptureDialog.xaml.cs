using System.Windows;
using System.Windows.Input;

namespace QuickMail.Views;

public partial class KeyCaptureDialog : Window
{
    private Key _capturedKey = Key.None;
    private ModifierKeys _capturedModifiers = ModifierKeys.None;

    public Key CapturedKey => _capturedKey;
    public ModifierKeys CapturedModifiers => _capturedModifiers;

    public KeyCaptureDialog()
    {
        InitializeComponent();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ignore modifier-only keys
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift)
        {
            return;
        }

        // Require at least one modifier
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            return;
        }

        // Capture the key
        _capturedKey = e.Key;
        _capturedModifiers = modifiers;

        // Update display
        UpdateCapturedKeyDisplay();
        OkButton.IsEnabled = true;

        e.Handled = true;
    }

    private void UpdateCapturedKeyDisplay()
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((_capturedModifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((_capturedModifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((_capturedModifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");

        var keyStr = _capturedKey.ToString();
        if (keyStr.Length == 2 && keyStr[0] == 'D' && char.IsDigit(keyStr[1]))
            keyStr = keyStr[1..];

        parts.Add(keyStr);
        CapturedKeyText.Text = string.Join("+", parts);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
