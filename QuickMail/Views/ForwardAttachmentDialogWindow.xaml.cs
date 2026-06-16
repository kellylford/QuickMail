using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ForwardAttachmentDialogWindow : Window
{
    private readonly ForwardAttachmentDialogViewModel _vm;

    public ForwardAttachmentDialogWindow(ForwardAttachmentDialogViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        Loaded += (_, _) => FocusFirstCheckBox();
    }

    private void FocusFirstCheckBox()
    {
        AttachmentList.UpdateLayout();
        var first = AttachmentList.ItemContainerGenerator.ContainerFromIndex(0) as ContentPresenter;
        if (first?.FindName("") is CheckBox cb)
        {
            cb.Focus();
        }
        else
        {
            // Fallback: move focus to the first focusable child in the ItemsControl.
            AttachmentList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F6) return;

        var shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (shift)
        {
            // Shift+F6: buttons → list
            if (ButtonPanel.IsKeyboardFocusWithin)
                AttachmentList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else
                IncludeSelectedButton.Focus();
        }
        else
        {
            // F6: list → buttons
            if (AttachmentList.IsKeyboardFocusWithin)
                IncludeSelectedButton.Focus();
            else
                AttachmentList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        e.Handled = true;
    }
}
