import Foundation

/// Minimal SMTP submission client.
///
/// Supports implicit TLS (port 465, useTLS=true) and plaintext (dev/local
/// servers, useTLS=false). STARTTLS upgrade on 587 is NOT yet supported —
/// Network.framework cannot add TLS mid-stream; doing this properly needs a
/// custom framer or SwiftNIO+NIOSSL. All major providers (Gmail, Fastmail,
/// iCloud, Yahoo) accept submission over implicit TLS on 465.
public struct SMTPClient: Sendable {
    public var host: String
    public var port: Int
    public var useTLS: Bool

    public init(host: String, port: Int, useTLS: Bool) {
        self.host = host
        self.port = port
        self.useTLS = useTLS
    }

    /// Send one message. Username/password empty → skip AUTH (dev servers).
    public func send(message: OutgoingMessage, username: String, password: String) async throws {
        let conn = MailConnection(host: host, port: port, useTLS: useTLS)
        try await conn.connect()
        defer { conn.closeSync() }

        func readReply() async throws -> (code: Int, text: String) {
            var lines: [String] = []
            while true {
                let line = try await conn.readLine()
                lines.append(line)
                // Multiline replies: "250-..." continues, "250 ..." ends.
                if line.count < 4 || line[line.index(line.startIndex, offsetBy: 3)] != "-" {
                    break
                }
            }
            let last = lines.last ?? ""
            let code = Int(last.prefix(3)) ?? 0
            return (code, lines.joined(separator: " "))
        }

        @discardableResult
        func command(_ text: String, expect: Int) async throws -> String {
            try await conn.send(line: text)
            let reply = try await readReply()
            guard reply.code / 100 == expect / 100 else {
                if text.hasPrefix("AUTH") || text == "" {
                    throw MailError.authenticationFailed(reply.text)
                }
                throw MailError.commandFailed("\(text.prefix(20)) → \(reply.text)")
            }
            return reply.text
        }

        let greeting = try await readReply()
        guard greeting.code == 220 else {
            throw MailError.protocolError("Unexpected SMTP greeting: \(greeting.text)")
        }

        let ehloReply = try await command("EHLO quickmail.local", expect: 250)

        if !username.isEmpty {
            if ehloReply.uppercased().contains("PLAIN") {
                let token = Data("\u{0}\(username)\u{0}\(password)".utf8).base64EncodedString()
                try await command("AUTH PLAIN \(token)", expect: 235)
            } else {
                try await command("AUTH LOGIN", expect: 334)
                try await command(Data(username.utf8).base64EncodedString(), expect: 334)
                let reply = try await { () async throws -> (Int, String) in
                    try await conn.send(line: Data(password.utf8).base64EncodedString())
                    return try await readReply()
                }()
                guard reply.0 == 235 else { throw MailError.authenticationFailed(reply.1) }
            }
        }

        try await command("MAIL FROM:<\(message.from.address)>", expect: 250)
        for rcpt in message.to + message.cc + message.bcc {
            try await command("RCPT TO:<\(rcpt.address)>", expect: 250)
        }
        try await command("DATA", expect: 354)

        // Dot-stuff and terminate.
        var body = message.rfc5322Data()
        body = Self.dotStuff(body)
        try await conn.send(body)
        try await command(".", expect: 250)
        _ = try? await command("QUIT", expect: 221)
    }

    /// Escape leading dots per RFC 5321 §4.5.2.
    static func dotStuff(_ data: Data) -> Data {
        var out = Data(capacity: data.count)
        var atLineStart = true
        for byte in data {
            if atLineStart && byte == UInt8(ascii: ".") {
                out.append(UInt8(ascii: "."))
            }
            out.append(byte)
            atLineStart = (byte == 0x0A)
        }
        if !out.isEmpty && out.last != 0x0A {
            out.append(contentsOf: [0x0D, 0x0A])
        }
        return out
    }
}
