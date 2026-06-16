using System.Windows;
using System.Windows.Input;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ForwardAttachmentDialogWindow : Window
{
    public ForwardAttachmentDialogWindow(ForwardAttachmentDialogViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F6) return;

        var shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (shift)
        {
            if (ButtonPanel.IsKeyboardFocusWithin)
                AttachmentList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else
                ForwardButton.Focus();
        }
        else
        {
            if (AttachmentList.IsKeyboardFocusWithin)
                ForwardButton.Focus();
            else
                AttachmentList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        e.Handled = true;
    }
}
