import Foundation
import Network

/// A buffered TCP/TLS connection used by both the IMAP and SMTP clients.
/// Wraps NWConnection with async line- and byte-oriented reads.
public actor MailConnection {
    private let connection: NWConnection
    private var buffer = Data()
    private var isClosed = false

    public init(host: String, port: Int, useTLS: Bool) {
        let params: NWParameters
        if useTLS {
            params = NWParameters(tls: NWProtocolTLS.Options())
        } else {
            params = NWParameters.tcp
        }
        self.connection = NWConnection(
            host: NWEndpoint.Host(host),
            port: NWEndpoint.Port(integerLiteral: UInt16(clamping: port)),
            using: params
        )
    }

    public func connect(timeout: TimeInterval = 20) async throws {
        let conn = connection
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                    // stateUpdateHandler can fire multiple times; resume once.
                    let resumed = ResumeGuard()
                    conn.stateUpdateHandler = { state in
                        switch state {
                        case .ready:
                            if resumed.tryResume() { cont.resume() }
                        case .failed(let error):
                            if resumed.tryResume() { cont.resume(throwing: MailError.connectionFailed(error.localizedDescription)) }
                        case .cancelled:
                            if resumed.tryResume() { cont.resume(throwing: MailError.cancelled) }
                        default:
                            break
                        }
                    }
                    conn.start(queue: DispatchQueue(label: "quickmail.connection"))
                }
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                throw MailError.connectionFailed("Timed out connecting")
            }
            try await group.next()
            group.cancelAll()
        }
    }

    public func send(_ data: Data) async throws {
        guard !isClosed else { throw MailError.connectionFailed("Connection closed") }
        let conn = connection
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            conn.send(content: data, completion: .contentProcessed { error in
                if let error {
                    cont.resume(throwing: MailError.connectionFailed(error.localizedDescription))
                } else {
                    cont.resume()
                }
            })
        }
    }

    public func send(line: String) async throws {
        try await send(Data((line + "\r\n").utf8))
    }

    private func receiveMore() async throws {
        guard !isClosed else { throw MailError.connectionFailed("Connection closed") }
        let conn = connection
        let chunk: Data = try await withCheckedThrowingContinuation { cont in
            conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, isComplete, error in
                if let error {
                    cont.resume(throwing: MailError.connectionFailed(error.localizedDescription))
                } else if let data, !data.isEmpty {
                    cont.resume(returning: data)
                } else if isComplete {
                    cont.resume(throwing: MailError.connectionFailed("Connection closed by server"))
                } else {
                    cont.resume(returning: Data())
                }
            }
        }
        buffer.append(chunk)
    }

    /// Read one CRLF-terminated line (CRLF stripped).
    public func readLine() async throws -> String {
        while true {
            if let range = buffer.range(of: Data([0x0D, 0x0A])) {
                let lineData = buffer.subdata(in: buffer.startIndex..<range.lowerBound)
                buffer.removeSubrange(buffer.startIndex..<range.upperBound)
                return String(data: lineData, encoding: .utf8)
                    ?? String(data: lineData, encoding: .isoLatin1) ?? ""
            }
            try await receiveMore()
        }
    }

    /// Read exactly n bytes.
    public func readBytes(_ n: Int) async throws -> Data {
        while buffer.count < n {
            try await receiveMore()
        }
        let out = buffer.prefix(n)
        buffer.removeFirst(n)
        return Data(out)
    }

    /// Read one logical IMAP line: text, plus any literals announced by
    /// trailing {n} markers, continuing until a line ends without a marker.
    public func readIMAPLine() async throws -> IMAPLine {
        var segments: [IMAPLine.Segment] = []
        while true {
            let text = try await readLine()
            segments.append(.text(text))
            guard let size = Self.trailingLiteralSize(text) else {
                return IMAPLine(segments: segments)
            }
            let literal = try await readBytes(size)
            segments.append(.literal(literal))
        }
    }

    /// If the line ends with "{n}" or "{n+}", return n.
    static func trailingLiteralSize(_ line: String) -> Int? {
        guard line.hasSuffix("}") else { return nil }
        guard let open = line.lastIndex(of: "{") else { return nil }
        var digits = line[line.index(after: open)..<line.index(before: line.endIndex)]
        if digits.hasSuffix("+") { digits = digits.dropLast() }
        guard !digits.isEmpty, digits.allSatisfy(\.isNumber) else { return nil }
        return Int(digits)
    }

    public func close() {
        isClosed = true
        connection.cancel()
    }
}

/// One-shot latch for continuations that may be signalled from multiple
/// NWConnection state transitions.
final class ResumeGuard: @unchecked Sendable {
    private let lock = NSLock()
    private var resumed = false
    func tryResume() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if resumed { return false }
        resumed = true
        return true
    }
}
