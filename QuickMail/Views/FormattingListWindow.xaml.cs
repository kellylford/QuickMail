using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QuickMail.Views;

/// <summary>
/// Read-only list of the formatting in effect at the editor caret — one row
/// per fact ("Heading 2", "Bold on", …) so the user can arrow through them at
/// their own pace instead of hearing one long announcement. Escape or Enter
/// closes (Close button is both IsCancel and IsDefault); the caller restores
/// focus to the editor after ShowDialog returns.
/// </summary>
public partial class FormattingListWindow : Window
{
    public FormattingListWindow(IEnumerable<string> items)
    {
        InitializeComponent();
        FormattingList.ItemsSource = items.ToList();

        Loaded += (_, _) =>
        {
            FormattingList.SelectedIndex = 0;
            FormattingList.UpdateLayout();
            (FormattingList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        };
    }
}
