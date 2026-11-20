namespace QuickMail.Models;

/// <summary>
/// The file formats a message can be saved in (issue #728). The persisted form in config.ini is
/// <see cref="MessageSaveFormats.ToConfigValue"/>, not the enum's integer, so reordering this is safe.
/// </summary>
public enum MessageSaveFormat
{
    /// <summary>The original message exactly as the server holds it, attachments included.</summary>
    Eml,
    /// <summary>A plain text file: a block of human-readable details, then the body.</summary>
    Text,
    /// <summary>A self-contained, sanitized web page that keeps the message's formatting.</summary>
    Html,
    /// <summary>A PDF made from the same page as <see cref="Html"/>.</summary>
    Pdf,
}

public static class MessageSaveFormats
{
    /// <summary>Every format, in the order the Save As dialog lists them.</summary>
    public static readonly MessageSaveFormat[] All =
        [MessageSaveFormat.Eml, MessageSaveFormat.Text, MessageSaveFormat.Html, MessageSaveFormat.Pdf];

    public static string Extension(MessageSaveFormat format) => format switch
    {
        MessageSaveFormat.Text => ".txt",
        MessageSaveFormat.Html => ".html",
        MessageSaveFormat.Pdf  => ".pdf",
        _                      => ".eml",
    };

    /// <summary>The name a person would use for the format — the Save As type list and Settings.</summary>
    public static string DisplayName(MessageSaveFormat format) => format switch
    {
        MessageSaveFormat.Text => "Text file",
        MessageSaveFormat.Html => "Web page",
        MessageSaveFormat.Pdf  => "PDF",
        _                      => "Email message",
    };

    public static string ToConfigValue(MessageSaveFormat format) => format switch
    {
        MessageSaveFormat.Text => "txt",
        MessageSaveFormat.Html => "html",
        MessageSaveFormat.Pdf  => "pdf",
        _                      => "eml",
    };

    /// <summary>Parses a config.ini value; anything unrecognized is the default, <see cref="MessageSaveFormat.Eml"/>.</summary>
    public static MessageSaveFormat FromConfigValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "txt" or "text"            => MessageSaveFormat.Text,
        "html" or "htm" or "web"   => MessageSaveFormat.Html,
        "pdf"                      => MessageSaveFormat.Pdf,
        _                          => MessageSaveFormat.Eml,
    };

    /// <summary>The format a file name's extension names, or null when it names none of them.</summary>
    public static MessageSaveFormat? FromExtension(string? extension) => extension?.Trim().ToLowerInvariant() switch
    {
        ".eml"            => MessageSaveFormat.Eml,
        ".txt"            => MessageSaveFormat.Text,
        ".html" or ".htm" => MessageSaveFormat.Html,
        ".pdf"            => MessageSaveFormat.Pdf,
        _                 => null,
    };
}

/// <summary>
/// A format choice for the Settings combo box. <see cref="ToString"/> returns the display name
/// because a screen reader reads a bound item's name from <c>ToString()</c>, not from
/// <c>DisplayMemberPath</c> (see SelectorItemAccessibilityTests).
/// </summary>
public sealed record MessageSaveFormatOption(MessageSaveFormat Format, string Name)
{
    public override string ToString() => Name;
}
