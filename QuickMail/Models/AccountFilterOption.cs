using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

/// <summary>Which slice of the address book an <see cref="AccountFilterOption"/> selects.</summary>
public enum AccountFilterKind
{
    /// <summary>Every contact, local and synced. The default.</summary>
    All,

    /// <summary>Only user-created contacts (<see cref="ContactModel.IsLocal"/>).</summary>
    Local,

    /// <summary>Only contacts synced from one specific account.</summary>
    Account,
}

/// <summary>
/// One entry in the address book's "Filter" menu — "All accounts", "Local address book",
/// or a single mail account. The address book keeps a list of these and shows only the
/// contacts the selected one matches.
/// </summary>
public sealed class AccountFilterOption : ObservableObject
{
    public AccountFilterOption(AccountFilterKind kind, string name, Guid? accountId = null)
    {
        Kind      = kind;
        Name      = name;
        AccountId = accountId;
    }

    public AccountFilterKind Kind { get; }

    /// <summary>The owning account, for <see cref="AccountFilterKind.Account"/> only.</summary>
    public Guid? AccountId { get; }

    /// <summary>Menu text, and the accessible name of the menu item.</summary>
    public string Name { get; }

    private bool _isSelected;

    /// <summary>
    /// True for the option currently in effect. Bound to the menu item's IsChecked so a
    /// screen reader announces which filter is active as the user arrows through the menu.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Re-raises PropertyChanged for <see cref="IsSelected"/> even when the value did not
    /// change. Activating a menu item toggles its IsChecked locally (via SetCurrentValue,
    /// which leaves the binding intact); re-raising pushes the authoritative value back so
    /// re-choosing the already-active filter does not leave the check mark cleared.
    /// </summary>
    public void RefreshIsSelected() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>
    /// True when this filter should show the given contact. The address book shows one row
    /// per address, so a person the user saved locally *and* syncs from two accounts is a
    /// single row; the filter matches if any contributing copy belongs to this account (see
    /// <see cref="ContactModel.MergedAccountIds"/>). The surviving row's own provenance is
    /// checked too, so a contact that never went through the collapse still matches.
    /// </summary>
    public bool Matches(ContactModel contact) => Kind switch
    {
        AccountFilterKind.All   => true,
        AccountFilterKind.Local => contact.IsLocal || contact.MergedIncludesLocal,
        _                       => AccountId is { } id
                                   && ((!contact.IsLocal && contact.OwnerAccountId == id)
                                       || contact.MergedAccountIds.Contains(id)),
    };

    /// <summary>Display text for any Selector or menu this is bound into (see CLAUDE.md).</summary>
    public override string ToString() => Name;
}
