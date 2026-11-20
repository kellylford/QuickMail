using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickMail.Services;

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
/// <see cref="Refresh"/> resets the cached children of the editor's own peer and, for each change,
/// of the existing peers of the elements enclosing it (the cell and table an Enter added a row to).
/// Only peers that already exist are touched: a new table or cell has no cache to go stale, and
/// creating peers nobody asked for would raise a burst of structure-changed events on the UI
/// thread. Call it from <c>TextChanged</c>, which is raised synchronously at the end of a change,
/// before any automation query can be dispatched. It does nothing until an automation client has
/// asked about the editor.
/// </para>
/// </summary>
public static class RichTextAutomationTree
{
    public static void Refresh(RichTextBox editor, TextChangedEventArgs e)
    {
        if (UIElementAutomationPeer.FromElement(editor) is not { } peer) return;
        try
        {
            peer.ResetChildrenCache();
            var start = editor.Document.ContentStart;
            foreach (var change in e.Changes)
            {
                ResetEnclosing(start.GetPositionAtOffset(change.Offset));
                ResetEnclosing(start.GetPositionAtOffset(change.Offset + change.AddedLength));
            }
        }
        catch (InvalidOperationException ex)
        {
            // A re-entrant GetChildren on the same peer throws. Never let an accessibility refresh
            // turn a keystroke into an unhandled exception.
            LogService.Debug($"RichTextAutomationTree: refresh skipped: {ex.Message}");
        }
    }

    private static void ResetEnclosing(TextPointer? position)
    {
        for (var element = position?.Parent as TextElement; element != null; element = element.Parent as TextElement)
            if (ContentElementAutomationPeer.FromElement(element) is { } peer)
                peer.ResetChildrenCache();
    }
}
