import Foundation

/// Parses raw RFC 5322 / MIME message bytes into headers, text/HTML bodies,
/// and attachments. Handles nested multiparts, base64 and quoted-printable
/// transfer encodings, and charset conversion.
public enum MIMEParser {
    public struct Part {
        public var headers: [(String, String)]
        public var body: Data

        public func header(_ name: String) -> String? {
            headers.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }?.1
        }
    }

    // MARK: - Entry point

    public static func parseMessage(_ raw: Data, uid: UInt32) -> MessageDetail {
        let root = splitHeadersAndBody(raw)
        var textBody: String?
        var htmlBody: String?
        var attachments: [AttachmentInfo] = []
        collectParts(root, textBody: &textBody, htmlBody: &htmlBody, attachments: &attachments)

        func addressHeader(_ name: String) -> [MailAddress] {
            root.header(name).map { AddressParser.parse(RFC2047.decodeHeaderValue($0)) } ?? []
        }

        let subject = root.header("Subject").map { RFC2047.decodeHeaderValue($0) } ?? ""
        let date = root.header("Date").flatMap(parseRFC5322Date)

        return MessageDetail(
            uid: uid,
            subject: subject,
            from: addressHeader("From"),
            to: addressHeader("To"),
            cc: addressHeader("Cc"),
            replyTo: addressHeader("Reply-To"),
            date: date,
            messageID: root.header("Message-ID") ?? "",
            textBody: textBody,
            htmlBody: htmlBody,
            attachments: attachments,
            rawHeaders: root.headers
        )
    }

    // MARK: - Structure

    /// Split a part into unfolded headers and body bytes.
    static func splitHeadersAndBody(_ raw: Data) -> Part {
        let separator = Data("\r\n\r\n".utf8)
        let altSeparator = Data("\n\n".utf8)
        var headerData: Data
        var body: Data
        if let range = raw.range(of: separator) {
            headerData = raw.subdata(in: raw.startIndex..<range.lowerBound)
            body = raw.subdata(in: range.upperBound..<raw.endIndex)
        } else if let range = raw.range(of: altSeparator) {
            headerData = raw.subdata(in: raw.startIndex..<range.lowerBound)
            body = raw.subdata(in: range.upperBound..<raw.endIndex)
        } else {
            headerData = raw
            body = Data()
        }
        let headerText = String(data: headerData, encoding: .utf8)
            ?? String(data: headerData, encoding: .isoLatin1) ?? ""
        return Part(headers: unfoldHeaders(headerText), body: body)
    }

    static func unfoldHeaders(_ text: String) -> [(String, String)] {
        var headers: [(String, String)] = []
        var currentName: String?
        var currentValue = ""
        // "\r\n" is a single Character in Swift, so normalize CRLF before
        // splitting or the split on "\n" never fires.
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n")
        for rawLine in normalized.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = rawLine.hasSuffix("\r") ? String(rawLine.dropLast()) : String(rawLine)
            if line.isEmpty { continue }
            if line.first == " " || line.first == "\t" {
                currentValue += " " + line.trimmingCharacters(in: .whitespaces)
                continue
            }
            if let name = currentName {
                headers.append((name, currentValue))
            }
            if let colon = line.firstIndex(of: ":") {
                currentName = String(line[..<colon])
                currentValue = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
            } else {
                currentName = nil
                currentValue = ""
            }
        }
        if let name = currentName {
            headers.append((name, currentValue))
        }
        return headers
    }

    /// Parse a structured header like Content-Type into value + parameters.
    public static func parseStructuredHeader(_ value: String) -> (value: String, params: [String: String]) {
        var params: [String: String] = [:]
        let parts = splitOutsideQuotes(value, on: ";")
        let main = parts.first?.trimmingCharacters(in: .whitespaces).lowercased() ?? ""
        for part in parts.dropFirst() {
            let trimmed = part.trimmingCharacters(in: .whitespaces)
            guard let eq = trimmed.firstIndex(of: "=") else { continue }
            let key = String(trimmed[..<eq]).lowercased().trimmingCharacters(in: .whitespaces)
            var val = String(trimmed[trimmed.index(after: eq)...]).trimmingCharacters(in: .whitespaces)
            if val.hasPrefix("\""), val.hasSuffix("\""), val.count >= 2 {
                val = String(val.dropFirst().dropLast())
            }
            params[key] = val
        }
        return (main, params)
    }

    static func splitOutsideQuotes(_ s: String, on separator: Character) -> [String] {
        var parts: [String] = []
        var current = ""
        var inQuotes = false
        for ch in s {
            if ch == "\"" { inQuotes.toggle() }
            if ch == separator && !inQuotes {
                parts.append(current)
                current = ""
            } else {
                current.append(ch)
            }
        }
        parts.append(current)
        return parts
    }

    /// Recursively walk a part, filling in the best text/html bodies and attachments.
    static func collectParts(
        _ part: Part,
        textBody: inout String?,
        htmlBody: inout String?,
        attachments: inout [AttachmentInfo]
    ) {
        let contentTypeRaw = part.header("Content-Type") ?? "text/plain; charset=us-ascii"
        let (contentType, params) = parseStructuredHeader(contentTypeRaw)
        let disposition = part.header("Content-Disposition").map { parseStructuredHeader($0) }

        if contentType.hasPrefix("multipart/") {
            guard let boundary = params["boundary"] else { return }
            for sub in splitMultipart(part.body, boundary: boundary) {
                collectParts(sub, textBody: &textBody, htmlBody: &htmlBody, attachments: &attachments)
            }
            return
        }

        if contentType == "message/rfc822" {
            // Attached message: expose as an attachment.
            let subject = splitHeadersAndBody(part.body).header("Subject") ?? "message"
            attachments.append(AttachmentInfo(
                filename: RFC2047.decodeHeaderValue(subject) + ".eml",
                mimeType: contentType,
                data: decodeTransferEncoding(part)
            ))
            return
        }

        let isAttachment = disposition?.value == "attachment"
        let decoded = decodeTransferEncoding(part)

        if !isAttachment && contentType == "text/plain" && textBody == nil {
            textBody = Charset.decode(decoded, charset: params["charset"] ?? "utf-8")
            return
        }
        if !isAttachment && contentType == "text/html" && htmlBody == nil {
            htmlBody = Charset.decode(decoded, charset: params["charset"] ?? "utf-8")
            return
        }

        // Anything else with content is an attachment (or inline image).
        guard !decoded.isEmpty else { return }
        let filename = disposition?.params["filename"]
            ?? params["name"]
            ?? "attachment"
        attachments.append(AttachmentInfo(
            filename: RFC2047.decodeHeaderValue(filename),
            mimeType: contentType,
            data: decoded
        ))
    }

    /// Split a multipart body on its boundary into sub-parts.
    static func splitMultipart(_ body: Data, boundary: String) -> [Part] {
        let delimiter = Data("--\(boundary)".utf8)
        var parts: [Part] = []
        var searchStart = body.startIndex
        var sectionStart: Data.Index?
        while let range = body.range(of: delimiter, in: searchStart..<body.endIndex) {
            if let start = sectionStart {
                var end = range.lowerBound
                // Trim the CRLF that precedes the boundary.
                if end > start, body[body.index(end, offsetBy: -1)] == 0x0A { end = body.index(end, offsetBy: -1) }
                if end > start, body[body.index(end, offsetBy: -1)] == 0x0D { end = body.index(end, offsetBy: -1) }
                parts.append(splitHeadersAndBody(body.subdata(in: start..<end)))
            }
            // Move past the boundary line (skip "--" terminator or CRLF).
            var cursor = range.upperBound
            if body.distance(from: cursor, to: body.endIndex) >= 2,
               body[cursor] == UInt8(ascii: "-"), body[body.index(after: cursor)] == UInt8(ascii: "-") {
                sectionStart = nil
                break
            }
            while cursor < body.endIndex, body[cursor] == 0x0D || body[cursor] == 0x0A {
                cursor = body.index(after: cursor)
                if cursor > body.startIndex, body[body.index(before: cursor)] == 0x0A { break }
            }
            sectionStart = cursor
            searchStart = cursor
            if searchStart >= body.endIndex { break }
        }
        return parts
    }

    /// Apply Content-Transfer-Encoding.
    static func decodeTransferEncoding(_ part: Part) -> Data {
        let encoding = (part.header("Content-Transfer-Encoding") ?? "7bit")
            .lowercased().trimmingCharacters(in: .whitespaces)
        switch encoding {
        case "base64":
            let text = String(data: part.body, encoding: .ascii)?
                .components(separatedBy: .whitespacesAndNewlines).joined() ?? ""
            return Data(base64Encoded: text) ?? part.body
        case "quoted-printable":
            return decodeQuotedPrintable(part.body)
        default:
            return part.body
        }
    }

    static func decodeQuotedPrintable(_ data: Data) -> Data {
        var out = Data()
        let bytes = [UInt8](data)
        var i = 0
        while i < bytes.count {
            let b = bytes[i]
            if b == UInt8(ascii: "=") {
                // Soft line break: =\r\n or =\n
                if i + 2 < bytes.count, bytes[i + 1] == 0x0D, bytes[i + 2] == 0x0A {
                    i += 3
                    continue
                }
                if i + 1 < bytes.count, bytes[i + 1] == 0x0A {
                    i += 2
                    continue
                }
                if i + 2 < bytes.count,
                   let hi = RFC2047.hexValue(bytes[i + 1]),
                   let lo = RFC2047.hexValue(bytes[i + 2]) {
                    out.append(hi << 4 | lo)
                    i += 3
                    continue
                }
            }
            out.append(b)
            i += 1
        }
        return out
    }

    /// Parse an RFC 5322 Date header, tolerating common variants.
    public static func parseRFC5322Date(_ text: String) -> Date? {
        // Strip trailing "(UTC)"-style comments.
        var cleaned = text
        if let paren = cleaned.firstIndex(of: "(") {
            cleaned = String(cleaned[..<paren])
        }
        cleaned = cleaned.trimmingCharacters(in: .whitespaces)
        let formats = [
            "EEE, dd MMM yyyy HH:mm:ss Z",
            "dd MMM yyyy HH:mm:ss Z",
            "EEE, dd MMM yyyy HH:mm Z",
            "EEE, d MMM yyyy HH:mm:ss Z",
        ]
        let df = DateFormatter()
        df.locale = Locale(identifier: "en_US_POSIX")
        for format in formats {
            df.dateFormat = format
            if let date = df.date(from: cleaned) { return date }
        }
        return nil
    }
}

/// Parses RFC 5322 address lists from raw header text:
/// `"Doe, Jane" <jane@example.com>, bob@example.com`
public enum AddressParser {
    public static func parse(_ headerValue: String) -> [MailAddress] {
        var addresses: [MailAddress] = []
        var current = ""
        var inQuotes = false
        var depth = 0
        for ch in headerValue {
            switch ch {
            case "\"": inQuotes.toggle(); current.append(ch)
            case "(" where !inQuotes: depth += 1
            case ")" where !inQuotes: depth = max(0, depth - 1)
            case "," where !inQuotes && depth == 0:
                if let a = parseSingle(current) { addresses.append(a) }
                current = ""
            default:
                if depth == 0 { current.append(ch) }
            }
        }
        if let a = parseSingle(current) { addresses.append(a) }
        return addresses
    }

    static func parseSingle(_ text: String) -> MailAddress? {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return nil }
        if let lt = trimmed.lastIndex(of: "<"), let gt = trimmed.lastIndex(of: ">"), lt < gt {
            let address = String(trimmed[trimmed.index(after: lt)..<gt]).trimmingCharacters(in: .whitespaces)
            var name = String(trimmed[..<lt]).trimmingCharacters(in: .whitespaces)
            if name.hasPrefix("\""), name.hasSuffix("\""), name.count >= 2 {
                name = String(name.dropFirst().dropLast())
                    .replacingOccurrences(of: "\\\"", with: "\"")
                    .replacingOccurrences(of: "\\\\", with: "\\")
            }
            guard !address.isEmpty else { return nil }
            return MailAddress(name: name, address: address)
        }
        guard trimmed.contains("@") else { return nil }
        return MailAddress(address: trimmed)
    }
}
