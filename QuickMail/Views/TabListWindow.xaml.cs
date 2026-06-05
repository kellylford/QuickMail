using System.Windows;
using System.Windows.Input;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class TabListWindow : Window
{
    private readonly MainViewModel _vm;

    public TabListWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        TabList.ItemsSource = vm.OpenTabs;
        TabList.SelectedItem = vm.ActiveTab;
        HeaderText.Text = $"Open tabs ({vm.OpenTabs.Count})";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (TabList.SelectedItem != null)
            TabList.ScrollIntoView(TabList.SelectedItem);
        TabList.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Return:
                ActivateSelected();
                e.Handled = true;
                break;

            case Key.Escape:
                DialogResult = false;
                Close();
                e.Handled = true;
                break;

            case Key.Delete:
                CloseSelected();
                e.Handled = true;
                break;
        }
    }

    private void TabList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ActivateSelected();
    }

    private void ActivateSelected()
    {
        if (TabList.SelectedItem is MessageTabViewModel tab)
        {
            _vm.ActiveTab = tab;
            DialogResult = true;
            Close();
        }
    }

    private void CloseSelected()
    {
        if (TabList.SelectedItem is not MessageTabViewModel tab) return;

        var idx = _vm.OpenTabs.IndexOf(tab);
        _vm.CloseTab(tab);

        // Update header
        HeaderText.Text = $"Open tabs ({_vm.OpenTabs.Count})";

        if (_vm.OpenTabs.Count == 0)
        {
            DialogResult = true;
            Close();
            return;
        }

        // Keep focus at the same or last position
        var newIdx = Math.Min(idx, _vm.OpenTabs.Count - 1);
        TabList.SelectedIndex = newIdx;
        TabList.ScrollIntoView(TabList.SelectedItem);
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: MessageTabViewModel tab })
        {
            TabList.SelectedItem = tab;
            CloseSelected();
        }
    }
}
