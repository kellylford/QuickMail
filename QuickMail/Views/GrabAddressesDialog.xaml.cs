using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class GrabAddressesDialog : Window
{
    private readonly ILocalStoreService _store;
    private readonly List<AddressEntry> _entries;

    public GrabAddressesDialog(List<(string Name, string Address)> addresses, ILocalStoreService store)
    {
        _store   = store;
        _entries = addresses.Select(a => new AddressEntry(a.Name, a.Address)).ToList();
        InitializeComponent();
        AddressList.ItemsSource = _entries;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries.Where(a => a.IsChecked))
        {
            await _store.UpsertContactAsync(new ContactModel
            {
                DisplayName   = entry.DisplayName,
                EmailAddress  = entry.EmailAddress,
                LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
            });
        }
        Close();
    }

    private sealed class AddressEntry : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public string DisplayName  { get; }
        public string EmailAddress { get; }
        public string Display => string.IsNullOrWhiteSpace(DisplayName)
            ? EmailAddress
            : $"{DisplayName} <{EmailAddress}>";

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public AddressEntry(string name, string address)
        {
            DisplayName  = name;
            EmailAddress = address;
        }
    }
}
