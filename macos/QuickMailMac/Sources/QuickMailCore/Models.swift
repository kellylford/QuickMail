import Foundation

/// A configured mail account. Mirrors the persistent fields of the Windows
/// app's AccountModel (accounts.json); passwords never live here — they are
/// stored in the macOS Keychain under the account's id.
public struct Account: Identifiable, Codable, Hashable, Sendable {
    public var id: UUID
    public var accountName: String
    public var displayName: String
    public var emailAddress: String
    public var username: String

    public var imapHost: String
    public var imapPort: Int
    public var imapUseSSL: Bool

    public var smtpHost: String
    public var smtpPort: Int
    /// true = implicit TLS (465); false = STARTTLS (587)
    public var smtpUseSSL: Bool

    public var isDefault: Bool
    public var signature: String

    public init(
        id: UUID = UUID(),
        accountName: String = "",
        displayName: String = "",
        emailAddress: String = "",
        username: String = "",
        imapHost: String = "",
        imapPort: Int = 993,
        imapUseSSL: Bool = true,
        smtpHost: String = "",
        smtpPort: Int = 587,
        smtpUseSSL: Bool = false,
        isDefault: Bool = false,
        signature: String = ""
    ) {
        self.id = id
        self.accountName = accountName
        self.displayName = displayName
        self.emailAddress = emailAddress
        self.username = username
        self.imapHost = imapHost
        self.imapPort = imapPort
        self.imapUseSSL = imapUseSSL
        self.smtpHost = smtpHost
        self.smtpPort = smtpPort
        self.smtpUseSSL = smtpUseSSL
        self.isDefault = isDefault
        self.signature = signature
    }
}

/// An email address with optional display name.
public struct MailAddress: Codable, Hashable, Sendable, CustomStringConvertible {
    public var name: String
    public var address: String

    public init(name: String = "", address: String) {
        self.name = name
        self.address = address
    }

    /// "Jane Doe <jane@example.com>" or "jane@example.com"
    public var description: String {
        name.isEmpty ? address : "\(name) <\(address)>"
    }

    /// Display text preferring the human name.
    public var displayText: String {
        name.isEmpty ? address : name
    }
}

/// An IMAP folder (mailbox).
public struct MailFolder: Identifiable, Hashable, Sendable {
    public var id: String { fullName }
    public var fullName: String
    public var displayName: String
    public var delimiter: String
    public var attributes: Set<String>   // lowercased, e.g. "\\noselect", "\\sent"
    public var isSelectable: Bool { !attributes.contains("\\noselect") }

    public init(fullName: String, displayName: String, delimiter: String, attributes: Set<String>) {
        self.fullName = fullName
        self.displayName = displayName
        self.delimiter = delimiter
        self.attributes = attributes
    }

    /// Special-use role derived from RFC 6154 attributes or common names.
    public var specialUse: SpecialUse? {
        if attributes.contains("\\inbox") || fullName.uppercased() == "INBOX" { return .inbox }
        if attributes.contains("\\sent") { return .sent }
        if attributes.contains("\\drafts") { return .drafts }
        if attributes.contains("\\trash") { return .trash }
        if attributes.contains("\\junk") { return .junk }
        if attributes.contains("\\archive") { return .archive }
        switch displayName.lowercased() {
        case "sent", "sent items", "sent messages": return .sent
        case "drafts": return .drafts
        case "trash", "deleted items", "deleted messages": return .trash
        case "junk", "spam", "junk e-mail": return .junk
        case "archive": return .archive
        default: return nil
        }
    }

    public enum SpecialUse: Sendable {
        case inbox, sent, drafts, trash, junk, archive
    }
}

/// Lightweight message summary for the message list (from ENVELOPE + FLAGS).
public struct MessageSummary: Identifiable, Hashable, Sendable {
    public var id: UInt32 { uid }
    public var uid: UInt32
    public var subject: String
    public var from: [MailAddress]
    public var to: [MailAddress]
    public var date: Date?
    public var isSeen: Bool
    public var isFlagged: Bool
    public var isAnswered: Bool

    public init(
        uid: UInt32, subject: String, from: [MailAddress], to: [MailAddress],
        date: Date?, isSeen: Bool, isFlagged: Bool, isAnswered: Bool
    ) {
        self.uid = uid
        self.subject = subject
        self.from = from
        self.to = to
        self.date = date
        self.isSeen = isSeen
        self.isFlagged = isFlagged
        self.isAnswered = isAnswered
    }
}

/// A fully fetched and MIME-parsed message.
public struct MessageDetail: Sendable {
    public var uid: UInt32
    public var subject: String
    public var from: [MailAddress]
    public var to: [MailAddress]
    public var cc: [MailAddress]
    public var replyTo: [MailAddress]
    public var date: Date?
    public var messageID: String
    public var textBody: String?
    public var htmlBody: String?
    public var attachments: [AttachmentInfo]
    public var rawHeaders: [(String, String)]

    public init(
        uid: UInt32, subject: String, from: [MailAddress], to: [MailAddress],
        cc: [MailAddress], replyTo: [MailAddress], date: Date?, messageID: String,
        textBody: String?, htmlBody: String?, attachments: [AttachmentInfo],
        rawHeaders: [(String, String)]
    ) {
        self.uid = uid
        self.subject = subject
        self.from = from
        self.to = to
        self.cc = cc
        self.replyTo = replyTo
        self.date = date
        self.messageID = messageID
        self.textBody = textBody
        self.htmlBody = htmlBody
        self.attachments = attachments
        self.rawHeaders = rawHeaders
    }
}

public struct AttachmentInfo: Identifiable, Sendable {
    public var id = UUID()
    public var filename: String
    public var mimeType: String
    public var data: Data

    public init(filename: String, mimeType: String, data: Data) {
        self.filename = filename
        self.mimeType = mimeType
        self.data = data
    }
}

/// A message being composed for send.
public struct OutgoingMessage: Sendable {
    public var from: MailAddress
    public var to: [MailAddress]
    public var cc: [MailAddress]
    public var bcc: [MailAddress]
    public var subject: String
    public var textBody: String
    public var inReplyTo: String?
    public var references: [String]

    public init(
        from: MailAddress, to: [MailAddress], cc: [MailAddress] = [], bcc: [MailAddress] = [],
        subject: String, textBody: String, inReplyTo: String? = nil, references: [String] = []
    ) {
        self.from = from
        self.to = to
        self.cc = cc
        self.bcc = bcc
        self.subject = subject
        self.textBody = textBody
        self.inReplyTo = inReplyTo
        self.references = references
    }

    /// Serialize to an RFC 5322 message for SMTP DATA / IMAP APPEND.
    public func rfc5322Data(date: Date = Date()) -> Data {
        var lines: [String] = []
        let df = DateFormatter()
        df.locale = Locale(identifier: "en_US_POSIX")
        df.dateFormat = "EEE, dd MMM yyyy HH:mm:ss Z"
        lines.append("Date: \(df.string(from: date))")
        lines.append("From: \(RFC2047.encodeAddressList([from]))")
        if !to.isEmpty { lines.append("To: \(RFC2047.encodeAddressList(to))") }
        if !cc.isEmpty { lines.append("Cc: \(RFC2047.encodeAddressList(cc))") }
        lines.append("Subject: \(RFC2047.encodeHeaderValue(subject))")
        lines.append("Message-ID: <\(UUID().uuidString.lowercased())@quickmail.mac>")
        if let inReplyTo, !inReplyTo.isEmpty {
            lines.append("In-Reply-To: \(inReplyTo)")
        }
        if !references.isEmpty {
            lines.append("References: \(references.joined(separator: " "))")
        }
        lines.append("MIME-Version: 1.0")
        lines.append("Content-Type: text/plain; charset=utf-8")
        lines.append("Content-Transfer-Encoding: base64")
        lines.append("")
        let bodyB64 = Data(textBody.utf8).base64EncodedString(options: [.lineLength76Characters, .endLineWithCarriageReturn])
        lines.append(bodyB64)
        return Data((lines.joined(separator: "\r\n") + "\r\n").utf8)
    }
}

public enum MailError: Error, LocalizedError, Sendable {
    case connectionFailed(String)
    case authenticationFailed(String)
    case protocolError(String)
    case commandFailed(String)
    case cancelled

    public var errorDescription: String? {
        switch self {
        case .connectionFailed(let m): return "Connection failed: \(m)"
        case .authenticationFailed(let m): return "Sign-in failed: \(m)"
        case .protocolError(let m): return "Protocol error: \(m)"
        case .commandFailed(let m): return "Server refused the operation: \(m)"
        case .cancelled: return "Operation cancelled"
        }
    }
}
