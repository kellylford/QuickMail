using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class TemplatePickerWindow : Window
{
    private readonly TemplatePickerViewModel _vm;

    public MessageTemplate? SelectedTemplate { get; private set; }

    public TemplatePickerWindow(TemplatePickerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync();
            SearchBox.Focus();
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TemplatePickerViewModel.Templates))
                AccessibilityHelper.Announce(this, _vm.MatchCountText, category: AnnouncementCategory.Result);
        };
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Down:
                e.Handled = true;
                TemplateListBox.Focus();
                if (TemplateListBox.SelectedIndex < 0 && TemplateListBox.Items.Count > 0)
                    TemplateListBox.SelectedIndex = 0;
                break;
            case Key.Escape:
                e.Handled = true;
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Clear();
                }
                else
                {
                    DialogResult = false;
                }
                break;
        }
    }

    private void TemplateListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void TemplateListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void InsertButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (TemplateListBox.SelectedItem is MessageTemplate template)
        {
            SelectedTemplate = template;
            DialogResult = true;
        }
    }
}
