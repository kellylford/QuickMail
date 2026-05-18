using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AddressBookViewModel : ObservableObject
{
    private readonly ILocalStoreService _store;
    private List<ContactModel> _allContacts = [];

    public AddressBookViewModel(ILocalStoreService store)
    {
        _store = store;
    }

    public ObservableCollection<ContactModel> FilteredContacts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedContact))]
    private ContactModel? _selectedContact;

    public bool HasSelectedContact => SelectedContact != null;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newEmail = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter(value);

    [RelayCommand]
    public async Task LoadAsync()
    {
        _allContacts = await _store.LoadAllContactsAsync();
        ApplyFilter(SearchText);
    }

    [RelayCommand]
    private async Task AddContactAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEmail)) return;
        await _store.UpsertContactAsync(new ContactModel
        {
            DisplayName   = NewName.Trim(),
            EmailAddress  = NewEmail.Trim(),
            LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
        });
        NewName  = string.Empty;
        NewEmail = string.Empty;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteContactAsync()
    {
        if (SelectedContact is not { } contact) return;
        var result = MessageBox.Show(
            $"Delete {contact.Display}?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        await _store.DeleteContactAsync(contact.Id);
        _allContacts.Remove(contact);
        FilteredContacts.Remove(contact);
        SelectedContact = null;
    }

    private void ApplyFilter(string query)
    {
        FilteredContacts.Clear();
        var q = query.Trim();
        var matches = string.IsNullOrEmpty(q)
            ? _allContacts
            : _allContacts.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.EmailAddress.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var c in matches)
            FilteredContacts.Add(c);
    }
}
