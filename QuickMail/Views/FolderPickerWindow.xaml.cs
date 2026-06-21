using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Fast modal folder picker backed by a virtualized flat list.
/// </summary>
public partial class FolderPickerWindow : Window
{
    private readonly ObservableCollection<FolderPickerItem> _items = [];
    private readonly ICollectionView _view;
    private readonly MailFolderModel? _initialFolder;

    public MailFolderModel? SelectedFolder { get; private set; }
    public AccountModel? SelectedAccount { get; private set; }

    public FolderPickerWindow(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        IEnumerable<MailFolderModel>? virtualFolders = null,
        string title = "Go to Folder",
        MailFolderModel? initialFolder = null,
        IReadOnlyDictionary<Guid, MailFolderModel>? accountMailFolders = null)
    {
        _initialFolder = initialFolder;

        InitializeComponent();
        Title = title;

        if (virtualFolders != null)
        {
            foreach (var vf in virtualFolders)
                _items.Add(new FolderPickerItem(vf, null, vf.DisplayName, vf.DisplayName));
        }

        foreach (var account in accounts)
        {
            if (!cachedFolders.TryGetValue(account.Id, out var folders) || folders.Count == 0)
                continue;

            if (accountMailFolders != null &&
                accountMailFolders.TryGetValue(account.Id, out var accountMailFolder))
            {
                _items.Add(new FolderPickerItem(
                    accountMailFolder,
                    account,
                    accountMailFolder.DisplayName,
                    $"{account.AccountLabel} - {accountMailFolder.DisplayName}"));
            }

            // Graph references parents by id, so a folder's FullName is an opaque id — build a
            // readable path from DisplayNames. IMAP already carries a separator path in FullName.
            var byId = folders.ToDictionary(f => f.FullName, StringComparer.Ordinal);

            foreach (var (folder, folderPath) in folders
                         .Where(f => !f.IsHeader)
                         .Select(f => (Folder: f, Path: BuildFolderPath(f, byId)))
                         .OrderBy(x => IsInbox(x.Folder) ? 0 : 1)
                         .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(new FolderPickerItem(
                    folder,
                    account,
                    folderPath,
                    $"{account.AccountLabel} - {folderPath}"));
            }
        }

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;
        FolderListBox.ItemsSource = _view;

        Loaded += (_, _) =>
        {
            if (!TrySelectInitialFolder())
                SelectFirstVisibleItem();

            Dispatcher.InvokeAsync(FocusSelectedFolder, DispatcherPriority.Input);
        };
    }

    // Backwards-compatible single-virtual-folder convenience constructor.
    public FolderPickerWindow(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        MailFolderModel? allMailFolder,
        string title = "Go to Folder")
        : this(accounts, cachedFolders,
               allMailFolder != null ? new[] { allMailFolder } : null,
               title,
               initialFolder: null)
    {
    }

    private static bool IsInbox(MailFolderModel folder) =>
        folder.Kind == SpecialFolderKind.Inbox ||
        folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Human-readable folder path for display. IMAP keeps its separator-delimited FullName; Graph
    /// (whose FullName is an opaque id) is reconstructed from DisplayNames up the ParentId chain,
    /// e.g. "Inbox/Projects/2026", so the picker never shows a raw folder id.
    /// </summary>
    internal static string BuildFolderPath(
        MailFolderModel folder, IReadOnlyDictionary<string, MailFolderModel> byId)
    {
        if (folder.ParentId == null)
            return string.IsNullOrWhiteSpace(folder.FullName) ? folder.DisplayName : folder.FullName;

        var segments = new List<string> { folder.DisplayName };
        var current = folder;
        for (int guard = 0;
             current.ParentId != null && byId.TryGetValue(current.ParentId, out var parent) && guard < 64;
             guard++)
        {
            segments.Add(parent.DisplayName);
            current = parent;
        }
        segments.Reverse();
        return string.Join('/', segments);
    }

    private bool FilterItem(object item)
    {
        if (item is not FolderPickerItem folder)
            return false;

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query))
            return true;

        return folder.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        SelectFirstVisibleItem();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Down:
                e.Handled = true;
                FocusSelectedFolder();
                break;
            case Key.Escape:
                e.Handled = true;
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Clear();
                    FocusSelectedFolder();
                }
                else
                {
                    DialogResult = false;
                }
                break;
        }
    }

    private void FolderListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
            return;
        }

        if (IsSearchGesture(e))
        {
            e.Handled = true;
            BeginSearch();
        }
    }

    private static bool IsSearchGesture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return (key == Key.Oem2 || key == Key.Divide) && Keyboard.Modifiers == ModifierKeys.None;
    }

    private void BeginSearch()
    {
        SearchBox.Clear();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void FolderListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void OpenButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void SelectFirstVisibleItem()
    {
        var first = _view.Cast<FolderPickerItem>().FirstOrDefault();
        FolderListBox.SelectedItem = first;
        if (first != null)
            FolderListBox.ScrollIntoView(first);
    }

    private void FocusSelectedFolder()
    {
        if (FolderListBox.SelectedIndex < 0)
            SelectFirstVisibleItem();

        if (FolderListBox.SelectedItem is { } item)
            FolderListBox.ScrollIntoView(item);

        FolderListBox.Focus();
    }

    private bool TrySelectInitialFolder()
    {
        if (_initialFolder == null)
            return false;

        var match = _items.FirstOrDefault(i => FoldersMatch(i.Folder, _initialFolder));
        if (match == null)
            return false;

        FolderListBox.SelectedItem = match;
        FolderListBox.ScrollIntoView(match);
        return true;
    }

    private static bool FoldersMatch(MailFolderModel a, MailFolderModel b) =>
        a.FullName.Equals(b.FullName, StringComparison.OrdinalIgnoreCase) &&
        (a.AccountId == b.AccountId || a.AccountId == Guid.Empty || b.AccountId == Guid.Empty);

    private void Commit()
    {
        if (FolderListBox.SelectedItem is not FolderPickerItem item)
            return;

        SelectedFolder = item.Folder;
        SelectedAccount = item.Account;
        DialogResult = true;
    }

    private sealed class FolderPickerItem
    {
        public FolderPickerItem(
            MailFolderModel folder,
            AccountModel? account,
            string folderPath,
            string displayName)
        {
            Folder = folder;
            Account = account;
            FolderPath = folderPath;
            DisplayName = displayName;
            AccountName = account?.AccountLabel ?? string.Empty;
            SearchText = $"{DisplayName} {Folder.DisplayName} {Folder.FullName} {AccountName}";
        }

        public MailFolderModel Folder { get; }
        public AccountModel? Account { get; }
        public string FolderPath { get; }
        public string DisplayName { get; }
        public string AccountName { get; }
        public string SearchText { get; }
    }
}
