using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickMail.Views;

/// <summary>
/// A list whose rows are check boxes and nothing else.
///
/// <para>Both stock items controls insert a wrapper element per row that carries its own name,
/// taken from the item's <c>ToString()</c>: a <c>ListBox</c> contributes a ListItem, and a plain
/// <c>ItemsControl</c> contributes a DataItem. Either way the row's name is in the tree twice, once
/// on the wrapper and once on the check box inside it, and it is spoken twice — "Date, Date check
/// box, unchecked".</para>
///
/// <para>This control keeps the list role (so a row still has position and count) but reports the
/// row check boxes as its direct children, so each row is exactly one named element.
/// <c>RowFieldsWindowCheckBoxTests</c> asserts that shape against the live automation tree.</para>
/// </summary>
public sealed class FieldCheckList : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FieldCheckListPeer(this);

    /// <summary>The row check boxes, in display order.</summary>
    internal IEnumerable<CheckBox> RowCheckBoxes()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                FindCheckBox(container) is { } box)
                yield return box;
        }
    }

    internal static CheckBox? FindCheckBox(DependencyObject root)
    {
        if (root is CheckBox cb) return cb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindCheckBox(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private sealed class FieldCheckListPeer : FrameworkElementAutomationPeer
    {
        public FieldCheckListPeer(FieldCheckList owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(FieldCheckList);

        /// <summary>
        /// The check boxes themselves, skipping the per-item wrapper peers that would otherwise
        /// repeat each row's name.
        /// </summary>
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var owner = (FieldCheckList)Owner;
            var children = new List<AutomationPeer>();
            foreach (var box in owner.RowCheckBoxes())
            {
                // Reuse the element's existing peer when WPF has already made one, so identity
                // stays stable across calls and focus/state events keep matching up.
                var peer = FromElement(box) ?? CreatePeerForElement(box);
                if (peer != null) children.Add(peer);
            }
            return children;
        }
    }
}
