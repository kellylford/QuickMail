using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace QuickMail.Helpers;

/// <summary>
/// Keeps a <see cref="RichTextBox"/>'s automation tree in step with its document, so a
/// screen reader's text-pattern queries cannot reach a WPF assertion that ends the process
/// (issue #782).
///
/// <para>
/// WPF caches each automation peer's children, and does not rebuild that cache for the peers of
/// document content when the document changes. A table and its cells have peers of their own; when the document gains a
/// new cell — Enter at the end of a table adds a row — the table's peer still lists only the
/// cells it had. A text range in the new cell then reports that cell's peer as its enclosing
/// element, WPF cannot connect that peer to the window's automation tree, and
/// <c>TextRangeAdaptor.GetEnclosingElement</c> fails <c>Invariant.Assert(provider != null)</c>,
/// which calls <c>Environment.FailFast</c>. Nothing can catch that; the window is simply gone.
/// </para>
///
/// <para>
/// <see cref="Refresh"/> rebuilds the cached children of the editor's peer and of every
/// document-content peer beneath it. Call it when the document changes: <c>TextChanged</c> is
/// raised synchronously at the end of the change, before any automation query can be
/// dispatched, so no query ever sees the stale tree. It does nothing when no peer exists, which
/// is the case until an automation client has asked about the editor.
/// </para>
/// </summary>
public static class RichTextAutomationTree
{
    public static void Refresh(RichTextBox editor)
    {
        if (UIElementAutomationPeer.FromElement(editor) is { } peer)
            Refresh(peer);
    }

    private static void Refresh(AutomationPeer peer)
    {
        peer.ResetChildrenCache();
        var children = peer.GetChildren();
        if (children is null) return;
        // Only content peers (tables, cells, hyperlinks) have document content beneath them;
        // an embedded control's peer, such as a picture's Image, has visuals.
        foreach (var child in children)
            if (child is ContentTextAutomationPeer)
                Refresh(child);
    }
}
