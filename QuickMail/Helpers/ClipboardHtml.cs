using System;
using System.Globalization;
using System.Text;

namespace QuickMail.Helpers;

/// <summary>
/// The Windows clipboard's "HTML Format": a short header of byte offsets, then an HTML document
/// with the copied part between <c>&lt;!--StartFragment--&gt;</c> and
/// <c>&lt;!--EndFragment--&gt;</c>. Used by the new message editor (#1099) so formatted text
/// pasted from a browser or a document comes in as headings, lists and links, and text copied
/// out of a message can be pasted back with its formatting and pictures.
/// </summary>
internal static class ClipboardHtml
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    /// <summary>
    /// The copied HTML out of clipboard data: what is between the fragment markers, or failing
    /// those, the document after the header. Null when there is nothing usable.
    /// </summary>
    public static string? ExtractFragment(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        int start = data.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += StartMarker.Length;
            int end = data.IndexOf(EndMarker, start, StringComparison.OrdinalIgnoreCase);
            var fragment = end >= 0 ? data[start..end] : data[start..];
            return fragment.Trim().Length == 0 ? null : fragment;
        }

        // No markers: skip the "Name:value" header lines and take the markup.
        int markup = data.IndexOf('<', StringComparison.Ordinal);
        if (markup < 0) return null;
        var rest = data[markup..];
        return rest.Trim().Length == 0 ? null : rest;
    }

    /// <summary>Clipboard data for a piece of HTML. The offsets are counted in UTF-8 bytes.</summary>
    public static string Build(string fragment)
    {
        const string headerFormat =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body>" + StartMarker;
        const string after = EndMarker + "</body></html>";

        int headerLength = string.Format(CultureInfo.InvariantCulture, headerFormat, 0, 0, 0, 0).Length;
        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(after);

        return string.Format(CultureInfo.InvariantCulture, headerFormat, startHtml, endHtml, startFragment, endFragment)
               + before + fragment + after;
    }
}
