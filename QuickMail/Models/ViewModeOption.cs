using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

/// <summary>
/// One entry in the "View Mode" menu — a mail grouping (Messages / Conversations /
/// By Sender / By Recipient) or a calendar slice (Agenda / Day / Week / Month).
///
/// The two sets are mutually exclusive, and the menu used to hold all eight items at once
/// with <c>Visibility</c> hiding the inapplicable set. A collapsed <c>MenuItem</c> is still a
/// child of its parent's automation peer, so screen readers counted all eight and announced
/// "1 of 8" over a four-item menu (issue #663). Binding the menu to a collection that holds
/// only the applicable options removes the hidden items from the tree entirely, so the
/// position and count a screen reader reports match what is on screen.
/// </summary>
public sealed class ViewModeOption : ObservableObject
{
    public ViewModeOption(string id, string name, string header, bool isCalendarMode)
    {
        Id             = id;
        Name           = name;
        Header         = header;
        IsCalendarMode = isCalendarMode;
    }

    /// <summary>
    /// Matches the enum member this option selects — a <c>ViewMode</c> name for mail
    /// ("Messages", "Conversations", "From", "To") or a <c>CalendarViewMode</c> name for the
    /// calendar ("Agenda", "Day", "Week", "Month").
    /// </summary>
    public string Id { get; }

    /// <summary>The accessible name of the menu item — no mnemonic underscore.</summary>
    public string Name { get; }

    /// <summary>Menu text, including the mnemonic underscore.</summary>
    public string Header { get; }

    /// <summary>True for the calendar slices, false for the mail groupings.</summary>
    public bool IsCalendarMode { get; }

    private bool _isSelected;

    /// <summary>
    /// True for the mode currently in effect. Bound to the menu item's IsChecked so a screen
    /// reader announces which mode is active as the user arrows through the menu.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Re-raises PropertyChanged for <see cref="IsSelected"/> even when the value did not
    /// change. Activating a checkable menu item toggles its IsChecked locally (via
    /// SetCurrentValue, which leaves the binding intact); re-raising pushes the authoritative
    /// value back so re-choosing the already-active mode does not leave the check mark cleared.
    /// </summary>
    public void RefreshIsSelected() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>Display text for any Selector or menu this is bound into (see CLAUDE.md).</summary>
    public override string ToString() => Name;
}
