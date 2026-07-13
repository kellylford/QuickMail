import Foundation

/// A single authenticated IMAP connection with high-level operations.
/// One session per account; the UI layer serializes access through the actor.
public actor IMAPSession {
    private let host: String
    private let port: Int
    private let useTLS: Bool
    private var connection: MailConnection?
    private var tagCounter = 0
    private var selectedFolder: String?
    private var selectedExists: Int = 0
    public private(set) var capabilities: Set<String> = []

    public init(host: String, port: Int, useTLS: Bool) {
        self.host = host
        self.port = port
        self.useTLS = useTLS
    }

    public var isConnected: Bool { connection != nil }

    private struct CommandResult {
        var untagged: [IMAPLine]
        var status: String   // OK / NO / BAD
        var statusText: String
    }

    private func nextTag() -> String {
        tagCounter += 1
        return String(format: "A%03d", tagCounter)
    }

    private func requireConnection() throws -> MailConnection {
        guard let connection else { throw MailError.connectionFailed("Not connected") }
        return connection
    }

    /// Send a command and collect responses until tagged completion.
    /// If `literal` is provided, the command line must end with a {n} marker;
    /// the literal is sent after the server's continuation response.
    private func runCommand(_ command: String, literal: Data? = nil) async throws -> CommandResult {
        let conn = try requireConnection()
        let tag = nextTag()
        try await conn.send(line: "\(tag) \(command)")
        var untagged: [IMAPLine] = []
        var pendingLiteral = literal
        while true {
            let line = try await conn.readIMAPLine()
            let flat = line.flatText
            if flat.hasPrefix("+ ") || flat == "+" {
                if let data = pendingLiteral {
                    try await conn.send(data)
                    try await conn.send(line: "")
                    pendingLiteral = nil
                }
                continue
            }
            if flat.hasPrefix("* ") {
                untagged.append(line)
                continue
            }
            if flat.hasPrefix("\(tag) ") {
                let rest = flat.dropFirst(tag.count + 1)
                let parts = rest.split(separator: " ", maxSplits: 1)
                let status = parts.first.map(String.init)?.uppercased() ?? "BAD"
                let text = parts.count > 1 ? String(parts[1]) : ""
                return CommandResult(untagged: untagged, status: status, statusText: text)
            }
            // Unknown line (other tag or noise) — ignore.
        }
    }

    private func runChecked(_ command: String, literal: Data? = nil) async throws -> CommandResult {
        let result = try await runCommand(command, literal: literal)
        guard result.status == "OK" else {
            throw MailError.commandFailed(result.statusText.isEmpty ? command : result.statusText)
        }
        return result
    }

    // MARK: - Connection lifecycle

    public func connect() async throws {
        let conn = MailConnection(host: host, port: port, useTLS: useTLS)
        try await conn.connect()
        let greeting = try await conn.readIMAPLine()
        guard greeting.flatText.hasPrefix("* OK") || greeting.flatText.hasPrefix("* PREAUTH") else {
            conn.closeSync()
            throw MailError.protocolError("Unexpected IMAP greeting: \(greeting.flatText)")
        }
        self.connection = conn
        if let caps = try? await runCommand("CAPABILITY") {
            for line in caps.untagged where line.flatText.uppercased().hasPrefix("* CAPABILITY") {
                capabilities = Set(
                    line.flatText.dropFirst(2).split(separator: " ").dropFirst().map { $0.uppercased() }
                )
            }
        }
    }

    public func login(username: String, password: String) async throws {
        let result = try await runCommand(
            "LOGIN \(IMAPExtract.quote(username)) \(IMAPExtract.quote(password))")
        guard result.status == "OK" else {
            throw MailError.authenticationFailed(result.statusText)
        }
    }

    public func logout() async {
        _ = try? await runCommand("LOGOUT")
        if let connection { await connection.close() }
        connection = nil
        selectedFolder = nil
    }

    public func noop() async throws {
        _ = try await runChecked("NOOP")
    }

    // MARK: - Folders

    public func listFolders() async throws -> [MailFolder] {
        let result = try await runChecked("LIST \"\" \"*\"")
        var folders: [MailFolder] = []
        for line in result.untagged {
            var parser = IMAPValueParser(line: line)
            let values = parser.parseAll()
            // * LIST (\HasNoChildren \Sent) "/" "Sent Items"
            guard values.count >= 5,
                  values[1].text?.uppercased() == "LIST",
                  let attrs = values[2].listItems
            else { continue }
            let delimiter = values[3].text ?? "/"
            guard let fullName = values[4].text else { continue }
            let attributes = Set(attrs.compactMap { $0.text?.lowercased() })
            let displayName = fullName.components(separatedBy: delimiter).last ?? fullName
            folders.append(MailFolder(
                fullName: fullName,
                displayName: displayName,
                delimiter: delimiter,
                attributes: attributes
            ))
        }
        // INBOX first, then special folders, then the rest alphabetically.
        func rank(_ f: MailFolder) -> Int {
            switch f.specialUse {
            case .inbox: return 0
            case .drafts: return 1
            case .sent: return 2
            case .archive: return 3
            case .junk: return 4
            case .trash: return 5
            case nil: return 6
            }
        }
        return folders.sorted {
            (rank($0), $0.fullName.lowercased()) < (rank($1), $1.fullName.lowercased())
        }
    }

    /// SELECT a folder; returns the message count (EXISTS).
    @discardableResult
    public func select(folder: String) async throws -> Int {
        let result = try await runChecked("SELECT \(IMAPExtract.quote(folder))")
        var exists = 0
        for line in result.untagged {
            let flat = line.flatText
            let parts = flat.split(separator: " ")
            if parts.count >= 3, parts[2].uppercased() == "EXISTS", let n = Int(parts[1]) {
                exists = n
            }
        }
        selectedFolder = folder
        selectedExists = exists
        return exists
    }

    // MARK: - Messages

    /// Fetch summaries for the newest `limit` messages, offset from the top,
    /// in the currently selected folder. Returns newest first.
    public func fetchSummaries(offset: Int = 0, limit: Int = 100) async throws -> [MessageSummary] {
        guard selectedFolder != nil else { throw MailError.protocolError("No folder selected") }
        guard selectedExists > 0 else { return [] }
        let high = selectedExists - offset
        guard high >= 1 else { return [] }
        let low = max(1, high - limit + 1)
        let result = try await runChecked(
            "FETCH \(low):\(high) (UID FLAGS INTERNALDATE ENVELOPE)")
        var summaries: [MessageSummary] = []
        for line in result.untagged {
            var parser = IMAPValueParser(line: line)
            let values = parser.parseAll()
            // * 12 FETCH (UID 34 FLAGS (\Seen) INTERNALDATE "..." ENVELOPE (...))
            guard values.count >= 4,
                  values[2].text?.uppercased() == "FETCH",
                  let items = values[3].listItems
            else { continue }
            let pairs = IMAPExtract.fetchPairs(items)
            if let summary = IMAPExtract.summary(from: pairs) {
                summaries.append(summary)
            }
        }
        return summaries.sorted { ($0.date ?? .distantPast) > ($1.date ?? .distantPast) }
    }

    /// Fetch the raw RFC 5322 bytes of one message by UID.
    public func fetchRawMessage(uid: UInt32) async throws -> Data {
        let result = try await runChecked("UID FETCH \(uid) (BODY.PEEK[])")
        for line in result.untagged {
            var parser = IMAPValueParser(line: line)
            let values = parser.parseAll()
            guard values.count >= 4,
                  values[2].text?.uppercased() == "FETCH",
                  let items = values[3].listItems
            else { continue }
            let pairs = IMAPExtract.fetchPairs(items)
            if case .string(let data)? = pairs["BODY[]"] {
                return data
            }
        }
        throw MailError.protocolError("Server returned no body for UID \(uid)")
    }

    public func setFlag(_ flag: String, uid: UInt32, add: Bool) async throws {
        let op = add ? "+FLAGS" : "-FLAGS"
        _ = try await runChecked("UID STORE \(uid) \(op) (\(flag))")
    }

    public func markSeen(uid: UInt32, seen: Bool) async throws {
        try await setFlag("\\Seen", uid: uid, add: seen)
    }

    public func markFlagged(uid: UInt32, flagged: Bool) async throws {
        try await setFlag("\\Flagged", uid: uid, add: flagged)
    }

    /// Move a message to another folder; uses UID MOVE when the server
    /// advertises it, otherwise COPY + \Deleted + EXPUNGE.
    public func move(uid: UInt32, to destination: String) async throws {
        if capabilities.contains("MOVE") {
            _ = try await runChecked("UID MOVE \(uid) \(IMAPExtract.quote(destination))")
        } else {
            _ = try await runChecked("UID COPY \(uid) \(IMAPExtract.quote(destination))")
            _ = try await runChecked("UID STORE \(uid) +FLAGS (\\Deleted)")
            _ = try await runChecked("EXPUNGE")
        }
        selectedExists = max(0, selectedExists - 1)
    }

    /// Permanently delete (flag + expunge) — used when there is no trash folder.
    public func delete(uid: UInt32) async throws {
        _ = try await runChecked("UID STORE \(uid) +FLAGS (\\Deleted)")
        _ = try await runChecked("EXPUNGE")
        selectedExists = max(0, selectedExists - 1)
    }

    /// APPEND a message to a folder (e.g. saving to Sent).
    public func append(folder: String, message: Data, flags: String = "(\\Seen)") async throws {
        _ = try await runChecked(
            "APPEND \(IMAPExtract.quote(folder)) \(flags) {\(message.count)}",
            literal: message
        )
    }
}

extension MailConnection {
    /// Fire-and-forget close usable from non-async cleanup paths.
    nonisolated func closeSync() {
        Task { await self.close() }
    }
}
