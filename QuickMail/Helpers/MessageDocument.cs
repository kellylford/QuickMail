using System.Collections.Generic;

namespace QuickMail.Helpers;

/// <summary>
/// Which pictures a message document may show, and where the host serves them from.
/// </summary>
/// <param name="EmbeddedBase">
/// The address the host serves this message's own (<c>cid:</c>) pictures from, with the Content-ID
/// appended; null leaves them out (#729).
/// </param>
/// <param name="WebBase">
/// The address the host serves pictures from the web from, with the picture's number appended;
/// null leaves them out (#508). The host fetches the real address; the document never names it.
/// </param>
/// <param name="NoteBlockedWebPictures">
/// When web pictures are left out, whether to say so at the top of the message, with a link that
/// loads them.
/// </param>
public sealed record PictureSources(string? EmbeddedBase, string? WebBase, bool NoteBlockedWebPictures)
{
    public static readonly PictureSources None = new(null, null, false);

    internal bool Any => EmbeddedBase is not null || WebBase is not null || NoteBlockedWebPictures;
}

/// <summary>A built message document, with what its pictures need from the host.</summary>
/// <param name="Html">The document to navigate to.</param>
/// <param name="WebPictures">
/// The web addresses the document's pictures stand for, in the order they are numbered; empty when
/// web pictures were not asked for.
/// </param>
/// <param name="BlockedWebPictures">How many pictures from the web were left out.</param>
public sealed record MessageDocument(string Html, IReadOnlyList<string> WebPictures, int BlockedWebPictures);
