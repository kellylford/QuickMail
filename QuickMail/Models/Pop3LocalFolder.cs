namespace QuickMail.Models;

/// <summary>
/// A folder the user made on a POP3 account (#794). <paramref name="Id"/> is what messages are filed
/// under and what rules target, so it never changes — renaming or moving the folder changes only
/// <paramref name="DisplayName"/> and <paramref name="ParentId"/>. <paramref name="ParentId"/> is null
/// at the top level, otherwise a built-in folder's name or another local folder's id.
/// </summary>
public sealed record Pop3LocalFolder(string Id, string DisplayName, string? ParentId);
