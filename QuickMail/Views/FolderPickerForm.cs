using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Modal folder picker backed by a native Win32 TreeView so screen readers
/// announce role, level, and expanded/collapsed state natively.
/// </summary>
public sealed class FolderPickerForm : Form
{
    private readonly TreeView _treeView;
    private readonly Button _openButton;
    private readonly Dictionary<TreeNode, AccountModel> _nodeToAccount = new();

    public MailFolderModel? SelectedFolder { get; private set; }
    public AccountModel?   SelectedAccount { get; private set; }

    public FolderPickerForm(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        MailFolderModel? allMailFolder = null,
        string title = "Go to Folder")
    {
        Text            = title;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        ClientSize      = new System.Drawing.Size(380, 460);
        StartPosition   = FormStartPosition.CenterParent;

        _treeView = new TreeView
        {
            Dock              = DockStyle.Fill,
            HideSelection     = false,
            FullRowSelect     = true,
            AccessibleName    = "Folders",
        };

        _openButton = new Button
        {
            Text   = "&Open",
            Dock   = DockStyle.Bottom,
            Height = 30,
        };
        var cancelButton = new Button
        {
            Text         = "Cancel",
            Dock         = DockStyle.Bottom,
            Height       = 30,
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = _openButton;
        CancelButton = cancelButton;

        Controls.Add(_treeView);
        Controls.Add(_openButton);
        Controls.Add(cancelButton);

        PopulateTree(accounts, cachedFolders, allMailFolder);

        _openButton.Click += (_, _) => Commit();
        _treeView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; Commit(); }
        };
        _treeView.NodeMouseDoubleClick += (_, _) => Commit();
        _treeView.KeyPress += OnTreeKeyPress;

        Load += (_, _) => _treeView.Focus();
    }

    private void PopulateTree(
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        MailFolderModel? allMailFolder)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();

        if (allMailFolder != null)
        {
            var allNode = new TreeNode(allMailFolder.DisplayName) { Tag = allMailFolder };
            _treeView.Nodes.Add(allNode);
        }

        foreach (var account in accounts)
        {
            if (!cachedFolders.TryGetValue(account.Id, out var folders) || folders.Count == 0)
                continue;

            var accountRoots = FolderTreeBuilder.Build(folders, account);
            foreach (var root in accountRoots)
                AddFolderNode(_treeView.Nodes, root, account);
        }

        _treeView.ExpandAll();
        _treeView.EndUpdate();
    }

    private void AddFolderNode(TreeNodeCollection parent, FolderTreeNode folderNode, AccountModel account)
    {
        var tn = new TreeNode(folderNode.Label) { Tag = folderNode, Name = folderNode.AutomationName ?? folderNode.Label };
        parent.Add(tn);
        _nodeToAccount[tn] = account;

        foreach (var child in folderNode.Children)
            AddFolderNode(tn.Nodes, child, account);
    }

    private void OnTreeKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar)) return;

        var allNodes = GetVisibleNodes(_treeView.Nodes).ToList();
        if (allNodes.Count == 0) return;

        var current  = _treeView.SelectedNode;
        var startIdx = current != null ? allNodes.IndexOf(current) : -1;

        for (int i = 1; i <= allNodes.Count; i++)
        {
            var candidate = allNodes[(startIdx + i) % allNodes.Count];
            var label     = (candidate.Tag as FolderTreeNode)?.Label ?? candidate.Text;
            if (label.StartsWith(e.KeyChar.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _treeView.SelectedNode = candidate;
                candidate.EnsureVisible();
                e.Handled = true;
                return;
            }
        }
    }

    private static IEnumerable<TreeNode> GetVisibleNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            if (node.IsExpanded)
                foreach (var child in GetVisibleNodes(node.Nodes))
                    yield return child;
        }
    }

    private void Commit()
    {
        var selected = _treeView.SelectedNode;
        if (selected == null) return;

        MailFolderModel? folder = null;
        if (selected.Tag is FolderTreeNode fn && fn.Folder != null)
            folder = fn.Folder;
        else if (selected.Tag is MailFolderModel mf)
            folder = mf;

        if (folder == null) return;

        SelectedFolder  = folder;
        _nodeToAccount.TryGetValue(selected, out var account);
        SelectedAccount = account;
        DialogResult    = DialogResult.OK;
    }
}
