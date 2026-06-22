using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Calendar pane UserControl. Hosts a virtualized event list.
/// Code-behind is limited to UI-only concerns: keyboard routing and focus management.
/// All business logic lives in <see cref="CalendarViewModel"/>.
/// </summary>
public partial class CalendarPane : UserControl
{
    public CalendarPane()
    {
        InitializeComponent();
    }

    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

    /// <summary>
    /// Moves focus to the event list. Called by MainWindow when the pane opens
    /// or when F6 cycles to this pane.
    /// </summary>
    public void FocusEventList()
    {
        EventList.Focus();
        if (EventList.Items.Count > 0 && EventList.SelectedIndex < 0)
            EventList.SelectedIndex = 0;
    }

    private void EventList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Ensure the first item is selected when focus arrives and nothing is selected.
        if (EventList.Items.Count > 0 && EventList.SelectedIndex < 0)
            EventList.SelectedIndex = 0;
    }

    private void CalendarPane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (EventList.IsKeyboardFocusWithin && vm.SelectedEvent != null)
                {
                    vm.OpenSourceMessageCommand.Execute(vm.SelectedEvent);
                    e.Handled = true;
                }
                break;

            case Key.T:
                if (EventList.IsKeyboardFocusWithin)
                {
                    vm.ToggleTodayFilterCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.F5:
                if (EventList.IsKeyboardFocusWithin)
                {
                    vm.RefreshCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                // Let MainWindow handle Escape-to-close via its own PreviewKeyDown.
                // The event bubbles up so MainWindow can close the pane.
                break;
        }
    }
}