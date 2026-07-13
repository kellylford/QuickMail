import Foundation

/// Builds the sandboxed HTML shown in the reading pane. Mirrors the Windows
/// app's WebView2 rules: strict CSP, scripts/frames/forms/remote images
/// stripped or blocked, plain-text URLs rendered as links.
public enum HTMLRenderer {
    static let csp = "default-src 'none'; img-src data:; style-src 'unsafe-inline';"

    /// Wrap a sanitized HTML body for display.
    public static func page(forHTML html: String) -> String {
        let sanitized = sanitize(html)
        return wrap(body: sanitized)
    }

    /// Escape and linkify a plain-text body for display.
    public static func page(forPlainText text: String) -> String {
        let escaped = escapeHTML(text)
        let linkified = linkify(escaped)
        return wrap(body: "<div class=\"plain\">\(linkified)</div>")
    }

    static func wrap(body: String) -> String {
        """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="\(csp)">
        <style>
        :root { color-scheme: light dark; }
        body { font: 14px -apple-system, sans-serif; margin: 12px; overflow-wrap: break-word; }
        .plain { white-space: pre-wrap; font-family: -apple-system, sans-serif; }
        img { max-width: 100%; }
        blockquote { border-left: 3px solid #8884; margin-left: 4px; padding-left: 10px; }
        </style>
        </head>
        <body>\(body)</body>
        </html>
        """
    }

    /// Defense-in-depth tag stripping. The WKWebView host also disables
    /// JavaScript and blocks all network subresource loads, so this only has
    /// to keep the DOM tidy, not be a perfect sanitizer.
    static func sanitize(_ html: String) -> String {
        var out = html
        for tag in ["script", "iframe", "object", "embed", "form"] {
            out = stripElement(out, tag: tag)
        }
        // Remove inline event handlers and javascript: URLs.
        out = out.replacingOccurrences(
            of: #"\son\w+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)"#,
            with: "", options: [.regularExpression, .caseInsensitive])
        out = out.replacingOccurrences(
            of: #"(href|src)\s*=\s*(["']?)\s*javascript:[^"'>\s]*\2"#,
            with: "", options: [.regularExpression, .caseInsensitive])
        return out
    }

    static func stripElement(_ html: String, tag: String) -> String {
        html.replacingOccurrences(
            of: "<\(tag)\\b[^>]*>[\\s\\S]*?</\(tag)>",
            with: "", options: [.regularExpression, .caseInsensitive]
        ).replacingOccurrences(
            of: "<\(tag)\\b[^>]*/?>",
            with: "", options: [.regularExpression, .caseInsensitive]
        )
    }

    public static func escapeHTML(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }

    /// Turn bare http/https/mailto references in ESCAPED text into anchors.
    static func linkify(_ escapedText: String) -> String {
        let pattern = #"(https?://[^\s<>"']+|mailto:[^\s<>"']+)"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return escapedText }
        let ns = escapedText as NSString
        var result = ""
        var last = 0
        for match in regex.matches(in: escapedText, range: NSRange(location: 0, length: ns.length)) {
            result += ns.substring(with: NSRange(location: last, length: match.range.location - last))
            var url = ns.substring(with: match.range)
            // Trailing punctuation is almost never part of the URL.
            var trailer = ""
            while let lastChar = url.last, ".,;:!?)".contains(lastChar) {
                trailer = String(lastChar) + trailer
                url = String(url.dropLast())
            }
            result += "<a href=\"\(url)\">\(url)</a>\(trailer)"
            last = match.range.location + match.range.length
        }
        result += ns.substring(from: last)
        return result
    }
}
