using System.IO;

namespace QuickMail.Models;

public class AttachmentModel
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Size in bytes (from IMAP BODYSTRUCTURE octets field, or actual file size on compose).</summary>
    public long FileSize { get; set; }

    /// <summary>IMAP body-part specifier (e.g. "2", "2.1"). Null for compose-only attachments.</summary>
    public string? PartSpecifier { get; set; }

    /// <summary>Raw bytes. Null for received attachments until explicitly downloaded.</summary>
    public byte[]? Content { get; set; }

    public bool IsLoaded => Content != null;

    public string AccessibleName =>
        string.IsNullOrEmpty(FileName) ? "(unnamed attachment)" : $"{FileName}, {FileSizeDisplay}";

    public string FileSizeDisplay
    {
        get
        {
            if (FileSize >= 1_048_576) return $"{FileSize / 1_048_576.0:F1} MB";
            if (FileSize >= 1_024)     return $"{FileSize / 1_024.0:F0} KB";
            return $"{FileSize} B";
        }
    }

    /// <summary>Maps a file extension to a MIME content-type string.</summary>
    public static string ContentTypeFromFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf"  => "application/pdf",
            ".doc"  => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls"  => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt"  => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".zip"  => "application/zip",
            ".gz"   => "application/gzip",
            ".tar"  => "application/x-tar",
            ".7z"   => "application/x-7z-compressed",
            ".rar"  => "application/vnd.rar",
            ".txt"  => "text/plain",
            ".csv"  => "text/csv",
            ".xml"  => "application/xml",
            ".json" => "application/json",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            ".bmp"  => "image/bmp",
            ".svg"  => "image/svg+xml",
            ".webp" => "image/webp",
            ".mp3"  => "audio/mpeg",
            ".mp4"  => "video/mp4",
            ".mov"  => "video/quicktime",
            ".avi"  => "video/x-msvideo",
            ".eml"  => "message/rfc822",
            _       => "application/octet-stream",
        };
}
