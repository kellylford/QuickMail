using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Modal folder picker backed by a real WPF TreeView so screen readers
/// announce role, level, expanded/collapsed state correctly.
/// </summary>
public partial class FolderPickerWindow : Window
{
    private readonly Dictionary<MailFolderModel, AccountModel> _folderToAccount = new();
    private MailFolderModel? _initialFolder;

    public MailFolderModel? SelectedFolder { get; private set; }
    public AccountModel? SelectedAccount { get; private set; }

    public FolderPickerWindow(IEnumerable<AccountModel> accounts, IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, IEnumerable<MailFolderModel>? virtualFolders = null, string title = "Go to Folder", MailFolderModel? initialFolder = null, IReadOnlyDictionary<Guid, MailFolderModel>? accountMailFolders = null)
    {
        _initialFolder = initialFolder;

        InitializeComponent();
        Title = title;

        var roots = new List<FolderTreeNode>();

        // "All Mail" group header with virtual sub-folder children at the top of the tree
        var virtualList = virtualFolders?.ToList();
        if (virtualList is { Count: > 0 })
        {
            var allMailGroup = new FolderTreeNode { IsHeader = true, Label = "All Mail", IsExpanded = true };
            foreach (var vf in virtualList)
                allMailGroup.Children.Add(new FolderTreeNode { Folder = vf, Label = vf.DisplayName });
            roots.Add(allMailGroup);
        }

        foreach (var account in accounts)
        {
            if (!cachedFolders.TryGetValue(account.Id, out var folders) || folders.Count == 0) continue;

            foreach (var f in folders)
                _folderToAccount[f] = account;

            var accountRoots = FolderTreeBuilder.Build(folders, account);

            // Inject the per-account "All Mail" virtual folder as the first child
            // of the account header node (navigation picker only — move/copy pickers
            // omit this by not passing accountMailFolders).
            if (accountRoots.Count > 0 &&
                accountMailFolders != null &&
                accountMailFolders.TryGetValue(account.Id, out var acctMail))
            {
                accountRoots[0].Children.Insert(0, new FolderTreeNode
                {
                    Folder = acctMail,
                    Label  = acctMail.DisplayName,
                });
            }

            foreach (var r in accountRoots)
                roots.Add(r);
        }

        FolderTreeView.ItemsSource = roots;

        Loaded += (_, _) =>
        {
            if (_initialFolder == null)
            {
                FolderTreeView.Focus();
                return;
            }

            // Ensure ancestors of the target node are expanded so their
            // item containers get generated before we try to select.
            FindAndExpandPath(roots, _initialFolder);

            // Defer selection until after WPF has generated the new containers.
            Dispatcher.InvokeAsync(() =>
            {
                var node = FindNode(roots, _initialFolder);
                if (node != null)
                    SelectTreeViewNode(FolderTreeView, node);
                else
                    FolderTreeView.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void FolderTreeView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryGetTypeAheadKeyText(e, out var searchText) && TryHandleFolderTreeTypeAhead(searchText))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
    }

    // First-letter navigation for the folder TreeView (TreeView has no built-in TextSearch).
    private void FolderTreeView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (TryHandleFolderTreeTypeAhead(e.Text))
            e.Handled = true;
    }

    private bool TryHandleFolderTreeTypeAhead(string? text)
    {
        if (string.IsNullOrEmpty(text) || char.IsControl(text[0]))
            return false;

        var allNodes = TreeViewFocusHelper.GetVisibleTreeNodes(FolderTreeView.Items.OfType<FolderTreeNode>()).ToList();
        if (allNodes.Count == 0)
            return false;

        var current = FolderTreeView.SelectedItem as FolderTreeNode;
        var startIdx = current != null ? allNodes.IndexOf(current) : -1;

        for (int i = 1; i <= allNodes.Count; i++)
        {
            var candidate = allNodes[(startIdx + i) % allNodes.Count];
            if (!candidate.Label.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                continue;

            return TreeViewFocusHelper.SelectTreeViewNode(FolderTreeView, candidate);
        }

        return false;
    }

    private static bool TryGetTypeAheadKeyText(KeyEventArgs e, out string text)
        => TreeViewFocusHelper.TryGetTypeAheadKeyText(e, out text);

    private static IEnumerable<FolderTreeNode> GetVisibleTreeNodes(IEnumerable<FolderTreeNode> nodes)
        => TreeViewFocusHelper.GetVisibleTreeNodes(nodes);

    // Finds a node whose Folder matches the target, searching all nodes regardless of expansion.
    private static FolderTreeNode? FindNode(IEnumerable<FolderTreeNode> nodes, MailFolderModel target)
        => TreeViewFocusHelper.FindFolderTreeNode(nodes, target);

    // Expands all ancestor nodes along the path to the target so their
    // children's item containers are generated before SelectTreeViewNode runs.
    private static bool FindAndExpandPath(IList<FolderTreeNode> nodes, MailFolderModel target)
        => TreeViewFocusHelper.FindAndExpandFolderPath(nodes, target);

    private static bool FoldersMatch(MailFolderModel a, MailFolderModel b)
        => TreeViewFocusHelper.FoldersMatch(a, b);

    private static bool SelectTreeViewNode(ItemsControl parent, FolderTreeNode target)
        => TreeViewFocusHelper.SelectTreeViewNode(parent, target, focusNode: true);

    private void Commit()
    {
        if (FolderTreeView.SelectedItem is FolderTreeNode node && node.Folder != null)
        {
            SelectedFolder = node.Folder;
            _folderToAccount.TryGetValue(node.Folder, out var account);
            SelectedAccount = account;
            DialogResult = true;
        }
    }
}
