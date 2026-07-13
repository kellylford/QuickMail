import Foundation

/// One logical IMAP response line: text runs interleaved with literal blobs.
/// The connection layer produces these; `{n}` markers in the text are
/// immediately followed by a `.literal` segment of exactly n bytes.
public struct IMAPLine: Sendable {
    public enum Segment: Sendable {
        case text(String)
        case literal(Data)
    }
    public var segments: [Segment]

    public init(segments: [Segment]) {
        self.segments = segments
    }

    /// Flattened text with literals decoded as strings — for logging and
    /// simple prefix checks only, never for parsing message bodies.
    public var flatText: String {
        segments.map {
            switch $0 {
            case .text(let t): return t
            case .literal(let d): return String(decoding: d, as: UTF8.self)
            }
        }.joined()
    }
}

/// A parsed IMAP data item.
public indirect enum IMAPValue: Sendable {
    case atom(String)        // unquoted token, including NIL and numbers
    case string(Data)        // quoted string or literal
    case list([IMAPValue])

    public var isNil: Bool {
        if case .atom(let a) = self, a.uppercased() == "NIL" { return true }
        return false
    }

    /// Best-effort text of this value (nil for NIL and lists).
    public var text: String? {
        switch self {
        case .atom(let a): return a.uppercased() == "NIL" ? nil : a
        case .string(let d):
            return String(data: d, encoding: .utf8) ?? String(data: d, encoding: .isoLatin1)
        case .list: return nil
        }
    }

    public var number: UInt32? {
        if case .atom(let a) = self { return UInt32(a) }
        return nil
    }

    public var listItems: [IMAPValue]? {
        if case .list(let items) = self { return items }
        return nil
    }
}

/// Parses the data items of one logical response line.
public struct IMAPValueParser {
    private let segments: [IMAPLine.Segment]
    private var segmentIndex = 0
    private var chars: [Character] = []
    private var charIndex = 0

    public init(line: IMAPLine) {
        self.segments = line.segments
        loadCurrentSegment()
    }

    private mutating func loadCurrentSegment() {
        chars = []
        charIndex = 0
        if segmentIndex < segments.count, case .text(let t) = segments[segmentIndex] {
            chars = Array(t)
        }
    }

    private var atEnd: Bool {
        segmentIndex >= segments.count
    }

    private mutating func peekChar() -> Character? {
        while charIndex >= chars.count {
            if atEnd { return nil }
            if case .literal = segments[segmentIndex] { return nil } // literal boundary
            segmentIndex += 1
            loadCurrentSegment()
            if atEnd { return nil }
        }
        return chars[charIndex]
    }

    private mutating func nextChar() -> Character? {
        guard let c = peekChar() else { return nil }
        charIndex += 1
        return c
    }

    private mutating func skipSpaces() {
        while let c = peekChar(), c == " " { charIndex += 1 }
    }

    /// Parse all values until the end of the line.
    public mutating func parseAll() -> [IMAPValue] {
        var values: [IMAPValue] = []
        while let v = parseValue() { values.append(v) }
        return values
    }

    /// Parse the next value, or nil at end of line.
    public mutating func parseValue() -> IMAPValue? {
        skipSpaces()
        guard let c = peekChar() else {
            // Possibly sitting at a literal segment boundary.
            if !atEnd, case .literal(let d) = segments[segmentIndex] {
                segmentIndex += 1
                loadCurrentSegment()
                return .string(d)
            }
            return nil
        }
        switch c {
        case "(":
            _ = nextChar()
            var items: [IMAPValue] = []
            while true {
                skipSpaces()
                if peekChar() == ")" { _ = nextChar(); break }
                guard let v = parseValue() else { break }
                items.append(v)
            }
            return .list(items)
        case ")":
            return nil // caller handles close paren
        case "\"":
            _ = nextChar()
            var out = ""
            while let ch = nextChar() {
                if ch == "\\" {
                    if let escaped = nextChar() { out.append(escaped) }
                } else if ch == "\"" {
                    break
                } else {
                    out.append(ch)
                }
            }
            return .string(Data(out.utf8))
        case "{":
            // Literal marker: consume "{n}" then the following literal segment.
            while let ch = nextChar(), ch != "}" {}
            // Advance past exhausted text; next segment must be the literal.
            if charIndex >= chars.count {
                segmentIndex += 1
                if !atEnd, case .literal(let d) = segments[segmentIndex] {
                    segmentIndex += 1
                    loadCurrentSegment()
                    return .string(d)
                }
                loadCurrentSegment()
            }
            return .string(Data())
        case "[":
            // Section spec in FETCH results, e.g. BODY[HEADER.FIELDS (...)] —
            // capture the bracketed run as part of an atom.
            var out = ""
            var depth = 0
            while let ch = peekChar() {
                if ch == "[" { depth += 1 }
                if ch == "]" { depth -= 1 }
                out.append(ch)
                charIndex += 1
                if depth == 0 { break }
            }
            return .atom(out)
        default:
            var out = ""
            while let ch = peekChar(), ch != " ", ch != "(", ch != ")", ch != "[" {
                out.append(ch)
                charIndex += 1
            }
            // Attach a section spec directly following an atom (BODY[...]).
            if peekChar() == "[" {
                var depth = 0
                while let ch = peekChar() {
                    if ch == "[" { depth += 1 }
                    if ch == "]" { depth -= 1 }
                    out.append(ch)
                    charIndex += 1
                    if depth == 0 { break }
                }
            }
            return out.isEmpty ? nil : .atom(out)
        }
    }
}

// MARK: - Envelope / FETCH extraction

public enum IMAPExtract {
    /// Parse the addresses in an ENVELOPE address list:
    /// ((name adl mailbox host) ...)
    public static func addresses(_ value: IMAPValue) -> [MailAddress] {
        guard let items = value.listItems else { return [] }
        var result: [MailAddress] = []
        for item in items {
            guard let parts = item.listItems, parts.count >= 4 else { continue }
            let name = parts[0].text.map(RFC2047.decodeHeaderValue) ?? ""
            let mailbox = parts[2].text ?? ""
            let host = parts[3].text ?? ""
            guard !mailbox.isEmpty else { continue }
            let address = host.isEmpty ? mailbox : "\(mailbox)@\(host)"
            result.append(MailAddress(name: name, address: address))
        }
        return result
    }

    /// Build key/value pairs from a FETCH item list:
    /// (UID 5 FLAGS (\Seen) ENVELOPE (...)) → ["UID": .atom("5"), ...]
    public static func fetchPairs(_ items: [IMAPValue]) -> [String: IMAPValue] {
        var pairs: [String: IMAPValue] = [:]
        var i = 0
        while i + 1 < items.count {
            if case .atom(let key) = items[i] {
                // Normalize BODY[...] keys to just "BODY[]" style prefix.
                let upper = key.uppercased()
                let normalized = upper.hasPrefix("BODY[") ? "BODY[]" : upper
                pairs[normalized] = items[i + 1]
            }
            i += 2
        }
        return pairs
    }

    public static func flags(_ value: IMAPValue?) -> Set<String> {
        guard let items = value?.listItems else { return [] }
        return Set(items.compactMap { $0.text?.lowercased() })
    }

    /// Parse an IMAP INTERNALDATE: "13-Jul-2026 10:21:00 -0500"
    public static func internalDate(_ text: String?) -> Date? {
        guard let text else { return nil }
        let df = DateFormatter()
        df.locale = Locale(identifier: "en_US_POSIX")
        df.dateFormat = "dd-MMM-yyyy HH:mm:ss Z"
        return df.date(from: text.trimmingCharacters(in: .whitespaces))
    }

    /// Build a MessageSummary from parsed FETCH pairs, if envelope data exists.
    public static func summary(from pairs: [String: IMAPValue]) -> MessageSummary? {
        guard let uid = pairs["UID"]?.number else { return nil }
        let flags = Self.flags(pairs["FLAGS"])
        var subject = ""
        var from: [MailAddress] = []
        var to: [MailAddress] = []
        if let env = pairs["ENVELOPE"]?.listItems, env.count >= 10 {
            subject = env[1].text.map(RFC2047.decodeHeaderValue) ?? ""
            from = addresses(env[2])
            to = addresses(env[5])
        }
        let date = internalDate(pairs["INTERNALDATE"]?.text)
        return MessageSummary(
            uid: uid,
            subject: subject,
            from: from,
            to: to,
            date: date,
            isSeen: flags.contains("\\seen"),
            isFlagged: flags.contains("\\flagged"),
            isAnswered: flags.contains("\\answered")
        )
    }

    /// Quote a string for use as an IMAP quoted-string argument.
    public static func quote(_ s: String) -> String {
        "\"" + s.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}
