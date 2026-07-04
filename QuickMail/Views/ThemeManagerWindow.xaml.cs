using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Theme Manager — list, apply, duplicate, rename, delete, export, import.
///
/// Deliberately MODELESS (Show, not ShowDialog): applying a theme restyles the
/// owner window (including its live WebView2 reading pane) immediately, and the
/// duplicate/rename flow needs an editable text field — both are the documented
/// modal-dialog hazard patterns. Because it is modeless, Escape and the Close
/// button call Close() explicitly, and the opener restores focus on Closed.
/// </summary>
public partial class ThemeManagerWindow : Window
{
    private readonly ThemeManagerViewModel _vm;
    private bool _listHintAnnounced;

    public ThemeManagerWindow(ThemeManagerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        vm.AnnouncementRequested += (text, category) =>
            AccessibilityHelper.Announce(this, text, interrupt: true, category: category);
        vm.NameEditStarted += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Input);
        vm.FocusListItemRequested += _ => Dispatcher.InvokeAsync(
            FocusSelectedListItem, DispatcherPriority.Input);
        vm.ErrorRequested += message =>
            MessageBox.Show(this, message, "Theme Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        vm.ConfirmDeleteRequested = (message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        vm.ExportPathRequested = suggestedName =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Export Theme",
                FileName = suggestedName,
                Filter   = "QuickMail theme (*.quickmailtheme)|*.quickmailtheme|All files (*.*)|*.*",
                DefaultExt = ".quickmailtheme",
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
        vm.ImportPathRequested = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Import Theme",
                Filter = "QuickMail theme (*.quickmailtheme;*.json)|*.quickmailtheme;*.json|All files (*.*)|*.*",
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
        vm.OpenFolderRequested += folder =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                LogService.Log("OpenThemesFolder", ex);
            }
        };

        AccessibilityHelper.RegisterDebugInputTrace(this);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ThemesList.Focus();
        FocusSelectedListItem();
    }

    private void FocusSelectedListItem()
    {
        if (ThemesList.SelectedItem is null)
        {
            ThemesList.Focus();
            return;
        }
        ThemesList.UpdateLayout();
        if (ThemesList.ItemContainerGenerator.ContainerFromItem(ThemesList.SelectedItem)
            is ListBoxItem item)
            item.Focus();
        else
            ThemesList.Focus();
    }

    private void ThemesList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_listHintAnnounced) return;
        _listHintAnnounced = true;
        AccessibilityHelper.Announce(this, "Press Tab for actions on the selected theme.",
            category: Models.AnnouncementCategory.Hint);
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            _vm.ConfirmNameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape: close the name panel first if open, otherwise the window.
        // (Modeless window — IsCancel/DialogResult do not apply.)
        if (e.Key == Key.Escape)
        {
            if (_vm.IsNamePanelOpen)
                _vm.CancelNameCommand.Execute(null);
            else
                Close();
            e.Handled = true;
            return;
        }

        // F6 / Shift+F6: two-stop ring, theme list ↔ action buttons.
        if (e.Key == Key.F6)
        {
            if (ButtonColumn.IsKeyboardFocusWithin)
                FocusSelectedListItem();
            else
                MoveFocusToButtons();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+P: local command palette with the window-scoped actions.
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenLocalCommandPalette();
            e.Handled = true;
        }
    }

    private void MoveFocusToButtons()
    {
        ButtonColumn.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OpenLocalCommandPalette()
    {
        var previousFocus = Keyboard.FocusedElement as IInputElement;
        var registry = new CommandRegistry();
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.apply", category: "Settings", title: "Apply Selected Theme",
            execute: () => _vm.ApplyCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.duplicate", category: "Settings", title: "Duplicate Selected Theme",
            execute: () => _vm.DuplicateCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.rename", category: "Settings", title: "Rename Selected Theme",
            execute: () => _vm.RenameCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.delete", category: "Settings", title: "Delete Selected Theme",
            execute: () => _vm.DeleteCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.export", category: "Settings", title: "Export Theme…",
            execute: () => _vm.ExportCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.import", category: "Settings", title: "Import Theme…",
            execute: () => _vm.ImportCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.openFolder", category: "Settings", title: "Open Themes Folder",
            execute: () => _vm.OpenThemesFolderCommand.Execute(null)));
        registry.Register(new Models.CommandDefinition(
            id: "thememanager.close", category: "Settings", title: "Close Theme Manager",
            execute: Close));

        var palette = new CommandPaletteWindow(registry) { Owner = this };
        palette.ShowDialog();
        (previousFocus ?? ThemesList).Focus();
    }
}
