using System;

namespace QuickMail.Models;

/// <summary>What kind of content a tab holds.</summary>
public enum TabKind
{
    Message,
    Unknown,
}

/// <summary>
/// In-memory representation of a single open tab.
/// Not persisted in v1 (TabsRememberAcrossRestart is reserved for v2).
/// Identity is by Guid; titles are derived from the content's state.
/// </summary>
public sealed class TabSessionModel
{
    public Guid    Id         { get; init; } = Guid.NewGuid();
    public TabKind Kind       { get; init; }
    public string  Title      { get; set; } = string.Empty;
    public string  Tooltip    { get; set; } = string.Empty;
    public bool    IsDirty    { get; set; }
    public bool    CanClose   { get; set; } = true;

    /// <summary>E.g. the message UniqueId for message tabs.</summary>
    public object? ContentKey { get; init; }
}
