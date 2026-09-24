namespace QuickMail.Helpers;

/// <summary>
/// A picture in the HTML-mode editor (#729). The editor shows it as a real <c>Image</c> inside an
/// <c>InlineUIContainer</c> whose <c>Tag</c> is this record, so the picture round-trips to HTML
/// and Markdown without guessing.
/// </summary>
/// <param name="Src">The <c>src</c>: <c>cid:</c> plus a Content-ID for a picture that travels with
/// the message, or the original address of one that came in by address (a forward).</param>
/// <param name="Alt">The alt text. Empty means decorative (sent as <c>alt=""</c>); null means the
/// picture arrived with no alt attribute at all and nobody has described it yet.</param>
/// <param name="Width">Width in pixels to send, when known.</param>
/// <param name="Height">Height in pixels to send, when known.</param>
public sealed record ComposeImage(string Src, string? Alt, int? Width = null, int? Height = null)
{
    public bool IsDecorative => Alt is { Length: 0 };

    /// <summary>
    /// What a screen reader says for the picture in the editor: its description, or which of the
    /// two states without one it is in.
    /// </summary>
    public string AccessibleName => Alt switch
    {
        null => "Image with no description",
        { Length: 0 } => "Decorative image",
        _ => Alt,
    };

    /// <summary>The Content-ID when the picture travels with the message; otherwise null.</summary>
    public string? ContentId =>
        Src.StartsWith("cid:", System.StringComparison.OrdinalIgnoreCase) ? Src[4..] : null;
}
