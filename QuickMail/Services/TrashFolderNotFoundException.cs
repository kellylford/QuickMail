using System;

namespace QuickMail.Services;

/// <summary>
/// The account has no folder this app recognizes as its trash. Not the same as an empty trash, which
/// the messages listed as trash would otherwise be taken to mean, and they'd be dropped from the list.
/// </summary>
public sealed class TrashFolderNotFoundException : Exception
{
    public TrashFolderNotFoundException() : base("No trash folder found.") { }
    public TrashFolderNotFoundException(string message) : base(message) { }
    public TrashFolderNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
