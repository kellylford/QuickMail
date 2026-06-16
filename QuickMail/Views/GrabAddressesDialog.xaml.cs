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
        Loaded += async (_, _) =>
        {
            await LoadGroupsAsync();
            FocusFirstAddress();
        };
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

    private async System.Threading.Tasks.Task LoadGroupsAsync()
    {
        var groups = await _contactService.LoadAllGroupsAsync();
        GroupComboBox.Items.Clear();
        foreach (var g in groups)
            GroupComboBox.Items.Add(new GroupPickerItem(g.Id, g.Name));
        GroupComboBox.Items.Add(GroupPickerItem.CreateNew);
        if (GroupComboBox.Items.Count > 0)
            GroupComboBox.SelectedIndex = 0;
    }

    private void AddToGroupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool isChecked = AddToGroupCheckBox.IsChecked == true;
        GroupComboBox.IsEnabled = isChecked;
        if (!isChecked)
        {
            SetNewGroupNameVisible(false);
        }
        else
        {
            SetNewGroupNameVisible(GroupComboBox.SelectedItem is GroupPickerItem { IsCreateNew: true });
        }
    }

    private void GroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetNewGroupNameVisible(
            AddToGroupCheckBox.IsChecked == true &&
            GroupComboBox.SelectedItem is GroupPickerItem { IsCreateNew: true });
    }

    private void SetNewGroupNameVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        NewGroupNameLabel.Visibility = vis;
        NewGroupNameBox.Visibility = vis;
        if (visible)
            NewGroupNameBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        bool addToGroup = AddToGroupCheckBox.IsChecked == true;
        var pickedGroup = addToGroup ? GroupComboBox.SelectedItem as GroupPickerItem : null;

        if (pickedGroup?.IsCreateNew == true && string.IsNullOrWhiteSpace(NewGroupNameBox.Text))
        {
            AccessibilityHelper.Announce(this, "Enter a name for the new group.", category: AnnouncementCategory.Result);
            NewGroupNameBox.Focus();
            return;
        }

        int? groupId = null;
        if (pickedGroup != null)
        {
            groupId = pickedGroup.IsCreateNew
                ? await _contactService.CreateGroupAsync(NewGroupNameBox.Text.Trim())
                : pickedGroup.Id;
        }

        foreach (var entry in _entries.Where(a => a.IsChecked))
        {
            var model = new ContactModel
            {
                DisplayName   = entry.DisplayName,
                EmailAddress  = entry.EmailAddress,
                LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
            };
            await _contactService.UpsertContactAsync(model);
            if (groupId.HasValue)
                await _contactService.AddMemberAsync(groupId.Value, model.Id);
        }

        Close();
    }

    private sealed class GroupPickerItem
    {
        public static readonly GroupPickerItem CreateNew = new(-1, "Create new group");

        public int Id { get; }
        public string Name { get; }
        public bool IsCreateNew => Id < 0;

        public GroupPickerItem(int id, string name)
        {
            Id   = id;
            Name = name;
        }

        public override string ToString() => Name;
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
