using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ForwardAttachmentDialogWindow : Window
{
    public ForwardAttachmentDialogWindow(ForwardAttachmentDialogViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();

        // Focus the first CheckBox after layout completes.
        Loaded += (_, _) =>
            Dispatcher.BeginInvoke(
                () => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                DispatcherPriority.Input);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (e.Key == Key.F6)
        {
            if (shift)
            {
                // Shift+F6: buttons → list
                if (ButtonPanel.IsKeyboardFocusWithin)
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                else
                    ForwardButton.Focus();
            }
            else
            {
                // F6: list → Forward button
                if (AttachmentList.IsKeyboardFocusWithin)
                    ForwardButton.Focus();
                else
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Return && (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            // Alt+Enter: show properties for the focused attachment.
            if (Keyboard.FocusedElement is CheckBox cb &&
                cb.DataContext is ForwardAttachmentSelectionItem item)
            {
                ShowAttachmentProperties(item.Attachment);
                e.Handled = true;
            }
        }
    }

    private void ShowAttachmentProperties(AttachmentModel attachment)
    {
        MessageBox.Show(
            this,
            $"File name: {attachment.FileName}\nSize: {attachment.FileSizeDisplay}\nType: {attachment.ContentType}",
            "Attachment Properties",
            MessageBoxButton.OK);
    }
}
