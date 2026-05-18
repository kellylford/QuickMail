using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class GrabAddressesDialog : Window
{
    private readonly IContactService _contactService;
    private readonly List<AddressEntry> _entries;

    public GrabAddressesDialog(List<(string Name, string Address)> addresses, IContactService contactService)
    {
        _contactService = contactService;
        _entries = addresses.Select(a => new AddressEntry(a.Name, a.Address)).ToList();
        InitializeComponent();
        AddressList.ItemsSource = _entries;
        Loaded += (_, _) => FocusFirstAddress();
    }

    private void FocusFirstAddress()
    {
        if (_entries.Count > 0)
        {
            var container = AddressList.ItemContainerGenerator.ContainerFromIndex(0);
            if (container is ContentPresenter presenter && FindFirstChild<CheckBox>(presenter) is CheckBox checkbox)
                checkbox.Focus();
        }
    }

    private static T? FindFirstChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindFirstChild<T>(child) is T descendant) return descendant;
        }
        return null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries.Where(a => a.IsChecked))
        {
            await _contactService.UpsertContactAsync(new ContactModel
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
