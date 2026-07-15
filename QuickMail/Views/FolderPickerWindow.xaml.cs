using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Fast modal folder picker backed by a virtualized flat list.
/// </summary>
public partial class FolderPickerWindow : Window
{
    // Shared empty map for IMAP accounts, whose folders never consult byId in BuildFolderPath.
    private static readonly Dictionary<string, MailFolderModel> EmptyFolderById = new();

    private readonly ObservableCollection<FolderPickerItem> _items = [];
    private readonly ICollectionView? _view;
    private readonly MailFolderModel? _initialFolder;
    private readonly bool _useTreeView;

    // When non-null, a "New Folder" button is shown and this callback creates the folder,
    // refreshes the owning account, and returns that account's refreshed folder list so the
    // picker can rebuild in place and select the new folder (issue #250, move/copy-message flow).
    private readonly Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? _folderCreator;

    // Retained for the tree view so it can be rebuilt after a folder is created. Not needed by the
    // flat list, which reuses its own ObservableCollection (_items) directly.
    private List<AccountModel>? _treeAccounts;
    private Dictionary<Guid, List<MailFolderModel>>? _treeFolders;

    public MailFolderModel? SelectedFolder { get; private set; }
    public AccountModel? SelectedAccount { get; private set; }

    public FolderPickerWindow(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        IEnumerable<MailFolderModel>? virtualFolders = null,
        string title = "Go to Folder",
        MailFolderModel? initialFolder = null,
        IReadOnlyDictionary<Guid, MailFolderModel>? accountMailFolders = null,
        bool useTreeView = false,
        Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? folderCreator = null)
    {
        _initialFolder = initialFolder;
        _useTreeView = useTreeView;
        _folderCreator = folderCreator;

        InitializeComponent();
        Title = title;

        // Alt+N → New Folder (see FolderPicker_PreviewKeyDown). Window-level so it fires from the
        // tree, the buttons, or anywhere else in the picker.
        PreviewKeyDown += FolderPicker_PreviewKeyDown;

        // The New Folder button is only meaningful when the caller supplied a way to create one.
        // Scoped to the tree view (the move/copy-message picker); the flat list has no in-place
        // repopulation path wired, so it never offers creation.
        NewFolderButton.Visibility = folderCreator != null && useTreeView
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_useTreeView)
        {
            BuildTreeView(accounts, cachedFolders);
            return;
        }

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
            // readable path from DisplayNames. IMAP carries a separator path in FullName already and
            // never consults byId, so don't build it for an all-IMAP account.
            var byId = folders.Any(f => f.ParentId != null)
                ? folders.ToDictionary(f => f.FullName, StringComparer.Ordinal)
                : EmptyFolderById;

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

    private void BuildTreeView(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders)
    {
        SearchBox.Visibility = Visibility.Collapsed;
        FolderListBox.Visibility = Visibility.Collapsed;
        FolderTreeView.Visibility = Visibility.Visible;

        // Retain a private, mutable copy so RebuildTreeView can regenerate the tree after a folder
        // is created without depending on the caller's snapshot (which may be a filtered copy).
        _treeAccounts = accounts.ToList();
        _treeFolders  = _treeAccounts
            .Where(a => cachedFolders.ContainsKey(a.Id))
            .ToDictionary(a => a.Id, a => cachedFolders[a.Id].ToList());

        RebuildTreeView();

        Loaded += (_, _) => Dispatcher.InvokeAsync(
            () => FolderTreeView.Focus(), DispatcherPriority.Input);
    }

    private void RebuildTreeView()
    {
        if (_treeAccounts == null || _treeFolders == null) return;

        var roots = new List<FolderTreeNode>();
        foreach (var account in _treeAccounts)
        {
            if (!_treeFolders.TryGetValue(account.Id, out var folders) || folders.Count == 0)
                continue;

            var nodes = FolderTreeBuilder.Build(folders, _treeAccounts.Count > 1 ? account : null);
            ExpandAll(nodes);
            roots.AddRange(nodes);
        }

        FolderTreeView.ItemsSource = roots;
    }

    private static void ExpandAll(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            ExpandAll(node.Children);
        }
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
        int guard = 0;
        while (current.ParentId != null && byId.TryGetValue(current.ParentId, out var parent) && guard < 64)
        {
            segments.Add(parent.DisplayName);
            current = parent;
            guard++;
        }
        // The guard only trips on a ParentId cycle (which Graph shouldn't produce). Surface it in
        // /debug so a subtly-truncated path is discoverable rather than silent.
        if (guard >= 64)
            LogService.Debug($"FolderPickerWindow: path for '{folder.DisplayName}' hit the 64-deep guard — possible ParentId cycle.");
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
        _view?.Refresh();
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

    private void FolderTreeView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
    }

    // Alt+N opens New Folder. Wired explicitly (rather than a button mnemonic) so a bare "n" in the
    // folder tree stays available for type-ahead instead of triggering the button (see the XAML note
    // on NewFolderButton). Handled window-wide so it works whatever picker control has focus.
    private void FolderPicker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // With Alt held, the character arrives as a System key; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.N
            && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt
            && NewFolderButton.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            NewFolderButton_Click(NewFolderButton, new RoutedEventArgs());
        }
    }

    private void FolderTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void OpenButton_Click(object sender, RoutedEventArgs e) => Commit();

    // Create a folder from within the picker (move/copy-message flow, tree view only). The parent
    // is the currently selected folder, or the account root when a header / nothing is selected.
    // After creation the tree is rebuilt from the refreshed folder list and the new folder selected
    // so the user can immediately Open it as the move/copy destination.
    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folderCreator == null || !_useTreeView || _treeFolders == null) return;
        if (!TryResolveTreeCreateTarget(out var accountId, out var parentFullName, out var parentLabel))
            return;

        var dlg = new NewFolderDialog { Owner = this, ParentFolderName = parentLabel };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.FolderName;
        var updated = await _folderCreator(accountId, parentFullName, name);
        if (updated == null) return; // failure is surfaced by the caller (main-window status text)

        _treeFolders[accountId] = updated.ToList();
        RebuildTreeView();

        // Container generation for the freshly-assigned ItemsSource completes on a later dispatcher
        // pass; select the new node once it exists so focus lands on it for the screen reader.
        // Fire-and-forget: the selection is a UI side effect with nothing to await on.
        _ = Dispatcher.InvokeAsync(
            () => SelectCreatedTreeNode(accountId, parentFullName, name),
            DispatcherPriority.Input);
    }

    private bool TryResolveTreeCreateTarget(out Guid accountId, out string? parentFullName, out string parentLabel)
    {
        accountId = Guid.Empty;
        parentFullName = null;
        parentLabel = string.Empty;

        var node = FolderTreeView.SelectedItem as FolderTreeNode;

        // A real folder is selected → create a subfolder beneath it.
        if (node is { IsHeader: false, Folder: { } folder })
        {
            accountId = folder.AccountId;
            parentFullName = folder.FullName;
            parentLabel = folder.DisplayName;
            return true;
        }

        // A header (account) node is selected → create at that account's root.
        if (node is { IsHeader: true } && _treeAccounts != null)
        {
            var acct = _treeAccounts.FirstOrDefault(a => a.AccountLabel == node.Label);
            if (acct != null)
            {
                accountId = acct.Id;
                parentLabel = acct.AccountLabel;
                return true;
            }
        }

        // Nothing (usable) selected → fall back to the sole account's root. The move/copy-message
        // picker is scoped to the accounts owning the messages, so this is unambiguous when there
        // is only one. With several accounts and no folder selected we can't guess a destination.
        if (_treeFolders != null && _treeFolders.Count == 1 && _treeAccounts != null)
        {
            var acct = _treeAccounts.FirstOrDefault(a => _treeFolders.ContainsKey(a.Id));
            if (acct != null)
            {
                accountId = acct.Id;
                parentLabel = acct.AccountLabel;
                return true;
            }
        }

        return false;
    }

    private void SelectCreatedTreeNode(Guid accountId, string? parentFullName, string name)
    {
        if (FolderTreeView.ItemsSource is not IEnumerable<FolderTreeNode> roots) return;

        // Siblings of the new folder: the parent's children, the account header's children, or the
        // top level (single-account tree).
        IEnumerable<FolderTreeNode> siblings;
        if (parentFullName != null)
        {
            var parent = FindNodeByFolder(roots, accountId, parentFullName);
            siblings = parent?.Children ?? roots;
        }
        else
        {
            var header = roots.FirstOrDefault(n => n.IsHeader &&
                n.Children.Any(c => c.Folder?.AccountId == accountId));
            siblings = header?.Children ?? roots;
        }

        var created = siblings.FirstOrDefault(n =>
            !n.IsHeader && n.Folder?.AccountId == accountId &&
            string.Equals(n.Folder?.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        var container = created != null ? ContainerFromNode(FolderTreeView, created) : null;
        if (container != null)
        {
            container.IsSelected = true;
            container.BringIntoView();
            container.Focus();
        }
        else
        {
            FolderTreeView.Focus();
        }
    }

    private static FolderTreeNode? FindNodeByFolder(
        IEnumerable<FolderTreeNode> nodes, Guid accountId, string fullName)
    {
        foreach (var node in nodes)
        {
            if (!node.IsHeader && node.Folder?.AccountId == accountId &&
                string.Equals(node.Folder?.FullName, fullName, StringComparison.Ordinal))
                return node;

            var found = FindNodeByFolder(node.Children, accountId, fullName);
            if (found != null) return found;
        }
        return null;
    }

    private static TreeViewItem? ContainerFromNode(ItemsControl parent, FolderTreeNode target)
    {
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem tvi)
                continue;
            if (ReferenceEquals(parent.Items[i], target))
                return tvi;
            var found = ContainerFromNode(tvi, target);
            if (found != null) return found;
        }
        return null;
    }

    private void SelectFirstVisibleItem()
    {
        var first = _view?.Cast<FolderPickerItem>().FirstOrDefault();
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
        if (_useTreeView)
        {
            if (FolderTreeView.SelectedItem is not FolderTreeNode node || node.Folder == null || node.IsHeader)
                return;

            SelectedFolder = node.Folder;
            DialogResult = true;
            return;
        }

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
