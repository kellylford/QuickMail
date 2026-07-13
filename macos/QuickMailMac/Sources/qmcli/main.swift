import Foundation
import QuickMailCore

// Headless smoke tool for the mail engine. Not shipped; used to exercise
// IMAP/SMTP against a dev server (or a real one) without launching the GUI.
//
//   qmcli imap <host> <port> <user> <pass> [--tls]
//   qmcli smtp <host> <port> <from> <to> [--tls]

let args = CommandLine.arguments

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

guard args.count >= 2 else {
    fail("usage: qmcli imap <host> <port> <user> <pass> [--tls] | qmcli smtp <host> <port> <from> <to> [--tls]")
}

let useTLS = args.contains("--tls")

switch args[1] {
case "imap":
    guard args.count >= 6, let port = Int(args[3]) else { fail("usage: qmcli imap <host> <port> <user> <pass> [--tls]") }
    let semaphore = DispatchSemaphore(value: 0)
    Task {
        do {
            let session = IMAPSession(host: args[2], port: port, useTLS: useTLS)
            try await session.connect()
            print("connected; capabilities: \(await session.capabilities.sorted().joined(separator: " "))")
            try await session.login(username: args[4], password: args[5])
            print("logged in")
            let folders = try await session.listFolders()
            for f in folders {
                print("folder: \(f.fullName) [\(f.attributes.sorted().joined(separator: ","))]")
            }
            let count = try await session.select(folder: "INBOX")
            print("INBOX: \(count) messages")
            let summaries = try await session.fetchSummaries(limit: 10)
            for s in summaries {
                let from = s.from.first?.displayText ?? "?"
                print("uid=\(s.uid) seen=\(s.isSeen) from=\(from) subject=\(s.subject)")
            }
            if let first = summaries.first {
                let raw = try await session.fetchRawMessage(uid: first.uid)
                let detail = MIMEParser.parseMessage(raw, uid: first.uid)
                print("--- message uid=\(first.uid) ---")
                print("subject: \(detail.subject)")
                print("from: \(detail.from.map(\.description).joined(separator: ", "))")
                print("text: \(detail.textBody?.prefix(200) ?? "(none)")")
                print("html: \(detail.htmlBody == nil ? "(none)" : "present")")
                print("attachments: \(detail.attachments.map(\.filename).joined(separator: ", "))")
                try await session.markSeen(uid: first.uid, seen: true)
                print("marked seen")
            }
            await session.logout()
            print("OK")
            semaphore.signal()
        } catch {
            fail("FAILED: \(error.localizedDescription)")
        }
    }
    semaphore.wait()

case "smtp":
    guard args.count >= 6, let port = Int(args[3]) else { fail("usage: qmcli smtp <host> <port> <from> <to> [--tls]") }
    let semaphore = DispatchSemaphore(value: 0)
    Task {
        do {
            let client = SMTPClient(host: args[2], port: port, useTLS: useTLS)
            let message = OutgoingMessage(
                from: MailAddress(name: "QuickMail CLI", address: args[4]),
                to: [MailAddress(address: args[5])],
                subject: "qmcli test message — ünïcode ✓",
                textBody: "This is a test message sent by qmcli.\n\nSecond paragraph.\n.leading dot line\n"
            )
            try await client.send(message: message, username: "", password: "")
            print("OK sent")
            semaphore.signal()
        } catch {
            fail("FAILED: \(error.localizedDescription)")
        }
    }
    semaphore.wait()

default:
    fail("unknown subcommand \(args[1])")
}
