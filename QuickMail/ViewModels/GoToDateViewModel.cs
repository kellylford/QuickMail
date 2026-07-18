using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Tiny authoring ViewModel for the Go To Date picker: holds the chosen date and raises
/// <see cref="Saved"/> / <see cref="Cancelled"/>. Pure VM — no View types, no window references.
/// The View subscribes to <see cref="Saved"/> (apply the date and close) and <see cref="Cancelled"/>
/// (close), and to <see cref="AnnouncementRequested"/> for validation feedback.
/// </summary>
public partial class GoToDateViewModel : ObservableObject
{
    public GoToDateViewModel(DateTime initial) => _selectedDate = initial.Date;

    /// <summary>The date the user has chosen. Bound two-way to the DatePicker.</summary>
    [ObservableProperty] private DateTime? _selectedDate;

    /// <summary>Raised with the chosen date when the user confirms.</summary>
    public event Action<DateTime>? Saved;

    /// <summary>Raised when the user cancels.</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Raised to request a screen-reader announcement. The View subscribes and calls
    /// <c>AccessibilityHelper.Announce(text, category)</c>.
    /// </summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>Confirms the chosen date. Bound to the Go button and Enter.</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (SelectedDate is not { } date)
        {
            AnnouncementRequested?.Invoke("Choose a date first.", AnnouncementCategory.Result);
            return;
        }
        Saved?.Invoke(date.Date);
    }

    /// <summary>Dismisses the picker without changing the calendar. Bound to Cancel and Escape.</summary>
    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
