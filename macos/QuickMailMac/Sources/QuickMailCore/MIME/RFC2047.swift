import Foundation

/// RFC 2047 encoded-word decoding/encoding for message headers
/// (=?utf-8?B?...?= and =?iso-8859-1?Q?...?=).
public enum RFC2047 {
    /// Decode all encoded-words in a header value.
    public static func decodeHeaderValue(_ value: String) -> String {
        var result = ""
        var remainder = Substring(value)
        var lastWasEncoded = false
        var pendingWhitespace = ""

        while let start = remainder.range(of: "=?") {
            let prefix = String(remainder[remainder.startIndex..<start.lowerBound])
            // Find charset?encoding?text?= after "=?"
            let afterStart = remainder[start.upperBound...]
            guard let firstQ = afterStart.firstIndex(of: "?") else { break }
            let charset = String(afterStart[..<firstQ])
            let afterCharset = afterStart[afterStart.index(after: firstQ)...]
            guard let secondQ = afterCharset.firstIndex(of: "?") else { break }
            let encoding = String(afterCharset[..<secondQ]).uppercased()
            let afterEncoding = afterCharset[afterCharset.index(after: secondQ)...]
            guard let end = afterEncoding.range(of: "?=") else { break }
            let payload = String(afterEncoding[..<end.lowerBound])

            let decoded: String?
            switch encoding {
            case "B":
                decoded = Data(base64Encoded: payload).flatMap { Charset.decode($0, charset: charset) }
            case "Q":
                decoded = decodeQ(payload).flatMap { Charset.decode($0, charset: charset) }
            default:
                decoded = nil
            }

            if let decoded {
                // Whitespace between two encoded-words is dropped (RFC 2047 §6.2).
                if !(lastWasEncoded && prefix.allSatisfy(\.isWhitespace)) {
                    result += pendingWhitespace + prefix
                }
                result += decoded
                lastWasEncoded = true
            } else {
                result += pendingWhitespace + prefix + "=?"
                remainder = afterStart
                lastWasEncoded = false
                pendingWhitespace = ""
                continue
            }
            pendingWhitespace = ""
            remainder = afterEncoding[end.upperBound...]
        }
        result += pendingWhitespace + String(remainder)
        return result
    }

    /// Decode Q-encoding (quoted-printable variant where _ = space).
    static func decodeQ(_ text: String) -> Data? {
        var out = Data()
        let chars = Array(text.utf8)
        var i = 0
        while i < chars.count {
            let c = chars[i]
            if c == UInt8(ascii: "_") {
                out.append(UInt8(ascii: " "))
                i += 1
            } else if c == UInt8(ascii: "="), i + 2 < chars.count,
                      let hi = hexValue(chars[i + 1]), let lo = hexValue(chars[i + 2]) {
                out.append(hi << 4 | lo)
                i += 3
            } else {
                out.append(c)
                i += 1
            }
        }
        return out
    }

    static func hexValue(_ byte: UInt8) -> UInt8? {
        switch byte {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): return byte - UInt8(ascii: "0")
        case UInt8(ascii: "A")...UInt8(ascii: "F"): return byte - UInt8(ascii: "A") + 10
        case UInt8(ascii: "a")...UInt8(ascii: "f"): return byte - UInt8(ascii: "a") + 10
        default: return nil
        }
    }

    /// Encode a header value, using an encoded-word only when needed.
    public static func encodeHeaderValue(_ value: String) -> String {
        if value.allSatisfy({ $0.isASCII && !$0.isNewline }) { return value }
        let b64 = Data(value.utf8).base64EncodedString()
        return "=?utf-8?B?\(b64)?="
    }

    public static func encodeAddressList(_ addresses: [MailAddress]) -> String {
        addresses.map { addr in
            if addr.name.isEmpty { return addr.address }
            let name = addr.name
            if name.allSatisfy({ $0.isASCII }) && !name.contains(where: { "\",<>@".contains($0) }) {
                return "\(name) <\(addr.address)>"
            }
            if name.allSatisfy({ $0.isASCII }) {
                let escaped = name.replacingOccurrences(of: "\\", with: "\\\\")
                    .replacingOccurrences(of: "\"", with: "\\\"")
                return "\"\(escaped)\" <\(addr.address)>"
            }
            return "\(encodeHeaderValue(name)) <\(addr.address)>"
        }.joined(separator: ", ")
    }
}

/// Charset conversion for the encodings that matter in real mail.
public enum Charset {
    public static func decode(_ data: Data, charset: String) -> String? {
        let name = charset.lowercased()
            .trimmingCharacters(in: .whitespaces)
            .components(separatedBy: "*").first ?? "" // strip RFC2231 language tag
        let encoding: String.Encoding
        switch name {
        case "utf-8", "utf8", "us-ascii", "ascii", "": encoding = .utf8
        case "iso-8859-1", "latin1", "latin-1": encoding = .isoLatin1
        case "iso-8859-2": encoding = .isoLatin2
        case "windows-1250":
            encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.windowsLatin2.rawValue)))
        case "windows-1251":
            encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.windowsCyrillic.rawValue)))
        case "windows-1252", "cp1252": encoding = .windowsCP1252
        case "utf-16", "utf16": encoding = .utf16
        case "shift_jis", "shift-jis", "sjis": encoding = .shiftJIS
        case "iso-2022-jp": encoding = .iso2022JP
        case "euc-jp": encoding = .japaneseEUC
        case "gb2312", "gbk", "gb18030":
            encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))
        case "big5":
            encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.big5.rawValue)))
        case "koi8-r":
            encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.KOI8_R.rawValue)))
        default: encoding = .utf8
        }
        if let s = String(data: data, encoding: encoding) { return s }
        // Fall back so a bad charset never blanks out a message.
        return String(data: data, encoding: .utf8) ?? String(data: data, encoding: .isoLatin1)
    }
}
