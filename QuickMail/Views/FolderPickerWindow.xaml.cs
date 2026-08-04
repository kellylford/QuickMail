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
/// Modal folder picker with two presentations, chosen by the <c>useTreeView</c> constructor flag.
///
/// <para>A folder tree mirroring the shape of the main window's folder tree, wherever the user is
/// choosing a destination folder they already know from that tree: moving or copying messages
/// (issue #250), and moving or copying a folder (issue #431).</para>
///
/// <para>Or a virtualized flat list with a search box — the default, and what "Go to Folder" needs,
/// being the only caller that also offers the virtual folders (All Inboxes, All Mail, …), which have
/// no place in a hierarchy. The rule editors still use it as well; they pick a real folder and are
/// candidates for the tree.</para>
/// </summary>
public partial class FolderPickerWindow : Window
{
    // Shared empty map for IMAP accounts, whose folders never consult byId in BuildFolderPath.
    private static readonly Dictionary<string, MailFolderModel> EmptyFolderById = new();

    private readonly ObservableCollection<FolderPickerItem> _items = [];
    private readonly TypeAheadPrefixTracker _typeAhead = new();
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

    // Tree view only: a folder (and everything under it) to leave out of the destination tree.
    // Set when the thing being moved or copied is itself a folder — see ForFolderMoveCopy.
    private readonly MailFolderModel? _excludeFolder;

    // Tree view only: where the excluded folder sat, remembered before it was removed, so the
    // picker can open on the folder the user came out of rather than on nothing.
    private FolderTreeNode? _openingNode;

    public MailFolderModel? SelectedFolder { get; private set; }
    public AccountModel? SelectedAccount { get; private set; }

    /// <summary>
    /// Whether the tree holds a folder the user could actually pick. False means showing the picker
    /// would put up an empty dialog — which happens when scoping and exclusion between them leave
    /// nothing, e.g. moving the only folder an account has. Always true for the flat list, which is
    /// never filtered this way.
    /// </summary>
    private bool HasSelectableFolders =>
        !_useTreeView ||
        (FolderTreeView.ItemsSource is IEnumerable<FolderTreeNode> roots && AnySelectable(roots));

    // Deliberately not TreeViewFocusHelper.GetVisibleTreeNodes: that walks only expanded nodes, and
    // whether a folder exists to pick must not depend on what happens to be expanded.
    private static bool AnySelectable(IEnumerable<FolderTreeNode> nodes) =>
        nodes.Any(n => n is { IsHeader: false, Folder: not null } || AnySelectable(n.Children));

    public FolderPickerWindow(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        IEnumerable<MailFolderModel>? virtualFolders = null,
        string title = "Go to Folder",
        // The folder to open on: normally the one the user came from. Honoured by both
        // presentations. When it is not in the tree, SelectOpeningNode stands something in for it —
        // the picker never opens with nothing selected.
        MailFolderModel? initialFolder = null,
        IReadOnlyDictionary<Guid, MailFolderModel>? accountMailFolders = null,
        bool useTreeView = false,
        Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? folderCreator = null,
        MailFolderModel? excludeFolder = null)
    {
        _initialFolder = initialFolder;
        _useTreeView = useTreeView;
        _folderCreator = folderCreator;
        _excludeFolder = excludeFolder;

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

        // Open follows the selection: a header or an IMAP path segment carries no folder, so there
        // is nothing to open. Reporting that through the control itself reaches the user whatever
        // their announcement settings are, unlike the "Choose a folder." announcement in Commit,
        // which AnnounceResults can switch off.
        OpenButton.IsEnabled = false;
        FolderTreeView.SelectedItemChanged += (_, _) =>
            OpenButton.IsEnabled = FolderTreeView.SelectedItem is FolderTreeNode
                                   { IsHeader: false, Folder: not null };

        RebuildTreeView();

        // Item containers are generated on a later dispatcher pass, so the opening selection has to
        // wait for one; until then there is nothing to select or focus.
        Loaded += (_, _) => Dispatcher.InvokeAsync(SelectOpeningNode, DispatcherPriority.Input);
    }

    /// <summary>
    /// Puts the selection — and keyboard focus — on the folder the user came from, so the picker
    /// opens somewhere meaningful instead of on an empty tree. When that folder is not in the tree
    /// (moving a folder excludes it, and an aggregate view is not a folder at all) the nearest
    /// standing-in node is used: the parent it was moved out of, else the first real folder. Landing
    /// on nothing is never the answer — a tree with no selection announces the tree and no item, and
    /// leaves the user to guess that Down is what starts things.
    /// </summary>
    private void SelectOpeningNode()
    {
        if (FolderTreeView.ItemsSource is not IEnumerable<FolderTreeNode> roots)
        {
            FolderTreeView.Focus();
            return;
        }

        var target = (_initialFolder != null
                         ? TreeViewFocusHelper.FindFolderTreeNode(roots, _initialFolder)
                         : null)
                     ?? _openingNode;

        // The stand-in can itself be unopenable: a parent that only exists as a path segment keeps
        // its node as long as another child survives the exclusion, and landing on a row with Open
        // disabled is the same failure as landing on nothing, one step in. Prefer the nearest real
        // folder beneath it, so the user still starts near where they were.
        if (target is not { IsHeader: false, Folder: not null })
            target = (target != null ? FirstSelectable(target.Children) : null) ?? FirstSelectable(roots);

        if (target == null || !TreeViewFocusHelper.SelectTreeViewNode(FolderTreeView, target))
            FolderTreeView.Focus();
    }

    private static FolderTreeNode? FirstSelectable(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is { IsHeader: false, Folder: not null }) return node;
            if (FirstSelectable(node.Children) is { } found) return found;
        }
        return null;
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

        if (_excludeFolder != null)
        {
            // Where the excluded folder sat, captured before it is removed: for a move or copy that
            // is the folder the user came from, and the closest thing to it that survives into the
            // destination tree. Null for a top-level folder — its parent is the account root, which
            // this picker does not offer — and SelectOpeningNode falls back from there.
            _openingNode = FindParentOfFolder(roots, _excludeFolder);

            RemoveFolderSubtree(roots, _excludeFolder);
            PruneEmptySyntheticNodes(roots);

            // The parent may itself have been a path segment that the exclusion just emptied.
            if (_openingNode != null && !Contains(roots, _openingNode))
                _openingNode = null;
        }

        FolderTreeView.ItemsSource = roots;
    }

    private static FolderTreeNode? FindParentOfFolder(IEnumerable<FolderTreeNode> nodes, MailFolderModel folder)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Any(c => c.Folder is { } f &&
                                       f.AccountId == folder.AccountId &&
                                       string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal)))
                return node;

            if (FindParentOfFolder(node.Children, folder) is { } found) return found;
        }
        return null;
    }

    private static bool Contains(IEnumerable<FolderTreeNode> nodes, FolderTreeNode target) =>
        nodes.Any(n => ReferenceEquals(n, target) || Contains(n.Children, target));

    /// <summary>
    /// Drops the node for <paramref name="folder"/>, and with it every subfolder underneath it,
    /// from the destination tree. A folder cannot be moved or copied into itself or into one of
    /// its own descendants, so offering those destinations only gives the user a way to reach a
    /// server error — and in a tree the source folder sits inline among the valid destinations,
    /// one mis-arrow away.
    /// </summary>
    private static void RemoveFolderSubtree(IList<FolderTreeNode> nodes, MailFolderModel folder)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            // Ordinal, not TreeViewFocusHelper.FoldersMatch: that compares FullName
            // case-insensitively, which would drop a sibling "archive" alongside "Archive" from the
            // destinations — and a Graph FullName is an opaque, case-sensitive id.
            if (nodes[i].Folder is { } f &&
                f.AccountId == folder.AccountId &&
                string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal))
                nodes.RemoveAt(i);
            else
                RemoveFolderSubtree(nodes[i].Children, folder);
        }
    }

    /// <summary>
    /// Removes intermediate nodes left childless by <see cref="RemoveFolderSubtree"/>. An IMAP
    /// hierarchy synthesizes a node for each path segment that is not itself a mailbox; once the
    /// only folder beneath such a segment is gone, the node is a row that can be arrowed onto and
    /// never opened.
    /// </summary>
    private static void PruneEmptySyntheticNodes(IList<FolderTreeNode> nodes)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            PruneEmptySyntheticNodes(nodes[i].Children);
            if (nodes[i] is { Folder: null, IsHeader: false, Children.Count: 0 })
                nodes.RemoveAt(i);
        }
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

    /// <summary>
    /// Picker for the folder tree's "Move Folder To" / "Copy Folder To" commands. The destination
    /// is a real folder in a hierarchy the user already knows from the folder tree, so it is shown
    /// as a tree rather than the flat list this dialog used to open with (issue #431). No search
    /// box: arrow keys and type-ahead are how the folder tree is navigated everywhere else.
    ///
    /// <para>Scoped to <paramref name="source"/>'s own account, for the same reason
    /// <c>MainWindow.BuildMessageFolderPicker</c> is: the backends move and copy by name over the
    /// <em>source</em> account's connection and never look at the destination's account, so a
    /// destination on another account either errors or — when both accounts have a folder of that
    /// name, "Archive" being the obvious case — silently acts on the wrong one. The flat list spelled
    /// the account into every row ("Work - Archive"); a tree carries it only on a header several rows
    /// up, so scoping is what keeps the two apart.</para>
    ///
    /// <para><paramref name="source"/> and its subfolders are left out of the tree — a folder cannot
    /// be moved or copied inside itself.</para>
    ///
    /// <para>Returns <see langword="null"/> when scoping and exclusion leave no folder to pick —
    /// moving the only folder an account has, say. The caller must say why instead of putting up an
    /// empty dialog. Returning null rather than an unusable window is deliberate: a WPF
    /// <see cref="Window"/> joins <c>Application.Current.Windows</c> at construction and only leaves
    /// it on <see cref="Window.Close"/>, so a picker the caller decided not to show and then dropped
    /// keeps the app's <c>OnLastWindowClose</c> shutdown from ever firing — the zombie process that
    /// holds the single-instance mutex (issue #252). Closing it here makes that unforgettable.</para>
    ///
    /// <para>A named factory rather than a call-site flag so the presentation is testable without
    /// standing up a MainWindow — see <c>FolderPickerTreeTests</c>.</para>
    /// </summary>
    public static FolderPickerWindow? ForFolderMoveCopy(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        MailFolderModel source,
        string title)
    {
        var picker = new FolderPickerWindow(
            accounts.Where(a => a.Id == source.AccountId),
            cachedFolders.Where(kv => kv.Key == source.AccountId)
                         .ToDictionary(kv => kv.Key, kv => kv.Value),
            title: title,
            useTreeView: true,
            excludeFolder: source);

        if (picker.HasSelectableFolders)
            return picker;

        picker.Close();
        return null;
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

    // Type-ahead over the visible tree, same behavior as the main window's folder tree
    // (accumulating prefix, wrap-around). See the XAML note on FolderTreeView for why this is
    // hand-rolled rather than WPF TextSearch.
    private void FolderTreeView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!TreeViewFocusHelper.ModifiersAllowTypeAhead)
            return;

        if (FolderTreeView.ItemsSource is IEnumerable<FolderTreeNode> roots &&
            _typeAhead.TryAppend(e.Text, FolderTreeView, out var prefix) &&
            TreeViewFocusHelper.TrySelectNextMatch(FolderTreeView, roots, prefix))
            e.Handled = true;
    }

    // Alt+N (New Folder), Alt+O (Open), Alt+C (Cancel) are wired explicitly rather than as
    // button mnemonics, because a bare mnemonic letter fires without Alt when focus isn't in a
    // text field and steals type-ahead (see the XAML notes on the buttons). Handled window-wide
    // so they work whatever picker control has focus.
    private void FolderPicker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // With Alt held, the character arrives as a System key; the real key is in SystemKey.
        // Exactly Alt, not "Alt among others": AltGr reports as Ctrl+Alt, and on layouts where
        // AltGr+O/C/N produce letters those keystrokes must reach type-ahead, not the buttons.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.Alt)
            return;

        switch (key)
        {
            case Key.N when NewFolderButton.Visibility == Visibility.Visible:
                e.Handled = true;
                NewFolderButton_Click(NewFolderButton, new RoutedEventArgs());
                break;
            case Key.O:
                e.Handled = true;
                Commit();
                break;
            case Key.C:
                e.Handled = true;
                DialogResult = false;
                break;
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
            {
                // Account headers, and the path-only nodes an IMAP hierarchy produces for a parent
                // that is not itself a mailbox, carry no folder — there is nothing to open. Say so
                // rather than swallowing Enter and leaving the dialog looking stuck.
                AccessibilityHelper.Announce(this, "Choose a folder.", category: AnnouncementCategory.Result);
                return;
            }

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

    // Internal (not private) so TypeAheadWiringTests can assert the flat list's
    // TextSearch.TextPath resolves to a real property on this type.
    internal sealed class FolderPickerItem
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
