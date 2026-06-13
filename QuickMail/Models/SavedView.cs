using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>
/// A user-defined view that bundles one or more folders with a specific
/// view mode, filter, sort order, and optional keyboard shortcut.
/// </summary>
public class SavedView
{
    public Guid            Id        { get; set; } = Guid.NewGuid();
    public string          Name      { get; set; } = string.Empty;
    public List<ViewFolder> Folders  { get; set; } = [];

    /// <summary>Values: "messages" | "conversations" | "from" | "to".</summary>
    public string ViewMode { get; set; } = "messages";

    /// <summary>Values: "all" | "unread" | "read" | "attachments" | "replied" | "forwarded".</summary>
    public string Filter   { get; set; } = "all";

    /// <summary>Values: "dateDesc" | "dateAsc" | "alphaAsc" | "alphaDesc" | "countDesc" | "countAsc".</summary>
    public string Sort     { get; set; } = "dateDesc";

    /// <summary>Gesture string for the default keyboard shortcut, e.g. "Ctrl+1". Null = no shortcut.</summary>
    public string? Hotkey  { get; set; }

    /// <summary>When true this view is applied automatically on startup.</summary>
    public bool IsDefault  { get; set; }

    /// <summary>
    /// When set, only messages received within this many days are shown.
    /// Null means no day limit (show all available mail).
    /// </summary>
    public int? DaysOfMail { get; set; }

    /// <summary>
    /// Set when the view was created from a virtual folder (All Mail, All Inboxes, etc.).
    /// Stores the key without the NUL sentinel prefix, e.g. "AllMail". Null for real-folder views.
    /// </summary>
    public string? VirtualFolderKey { get; set; }

    /// <summary>
    /// When set, the view shows only messages flagged with this specific flag name.
    /// Null = no named-flag filter (MessageFilter.Flagged shows any flag).
    /// If the named flag is deleted, MainViewModel treats this as no filter.
    /// </summary>
    public string? FlagFilter { get; set; }
}
