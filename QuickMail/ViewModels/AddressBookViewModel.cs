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
    private readonly IContactService _contactService;
    private List<ContactModel> _allContacts = [];

    public AddressBookViewModel(IContactService contactService)
    {
        _contactService = contactService;
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
        _allContacts = await _contactService.LoadAllContactsAsync();
        ApplyFilter(SearchText);
    }

    [RelayCommand]
    private async Task AddContactAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEmail)) return;
        await _contactService.UpsertContactAsync(new ContactModel
        {
            DisplayName   = NewName.Trim(),
            EmailAddress  = NewEmail.Trim(),
            LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
        });
        NewName  = string.Empty;
        NewEmail = string.Empty;
        await LoadAsync();
    }

    public async Task DeleteConfirmedAsync()
    {
        if (SelectedContact is not { } contact) return;
        await _contactService.DeleteContactAsync(contact.Id);
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
