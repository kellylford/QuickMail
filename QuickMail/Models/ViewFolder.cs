namespace QuickMail.Models;

/// <summary>One folder reference stored inside a <see cref="SavedView"/>.</summary>
public class ViewFolder
{
    public Guid   AccountId          { get; set; }
    public string FolderFullName     { get; set; } = string.Empty;
    public string AccountDisplayName { get; set; } = string.Empty;
    public string FolderDisplayName  { get; set; } = string.Empty;
}
