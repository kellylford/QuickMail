import AppKit
import Foundation
import QuickMailCore
import SwiftUI

/// Central observable state for the app — the Mac counterpart of
/// MainViewModel. Owns accounts, per-account IMAP sessions, the current
/// folder/message selection, and all mail operations the UI invokes.
@MainActor
final class AppState: ObservableObject {
    @Published var accounts: [Account] = []
    @Published var selectedAccountID: UUID?
    @Published var folders: [MailFolder] = []
    @Published var selectedFolderName: String?
    @Published var messages: [MessageSummary] = []
    @Published var selectedMessageUID: UInt32?
    @Published var currentDetail: MessageDetail?
    @Published var statusText: String = "Ready"
    @Published var errorText: String?
    @Published var isLoadingMessages = false
    @Published var showAccountEditor = false
    @Published var editingAccount: Account?

    /// Drafts handed to compose windows, keyed by the window's value.
    @Published var composeDrafts: [UUID: ComposeDraft] = [:]

    /// Bumped when focus should move into the message body (Enter on a message).
    @Published var bodyFocusToken = 0
    /// Bumped when focus should return to the message list (Escape in the body).
    @Published var listFocusToken = 0

    /// Enter on the message list: open the selected message (if not already
    /// open) and move focus into the body so reading can start immediately.
    func readSelectedMessage() {
        guard let uid = selectedMessageUID else { return }
        if currentDetail?.uid != uid {
            openMessage(uid: uid)
        }
        bodyFocusToken += 1
    }

    func returnFocusToList() {
        listFocusToken += 1
    }

    // --profileDir <path> overrides the data directory, as on Windows.
    private let store: AccountStore = {
        let args = CommandLine.arguments
        if let index = args.firstIndex(of: "--profileDir"), index + 1 < args.count {
            return AccountStore(directory: URL(fileURLWithPath: args[index + 1], isDirectory: true))
        }
        return AccountStore()
    }()
    private var sessions: [UUID: IMAPSession] = [:]
    private var openTask: Task<Void, Never>?

    var selectedAccount: Account? {
        accounts.first { $0.id == selectedAccountID }
    }

    var selectedFolder: MailFolder? {
        folders.first { $0.fullName == selectedFolderName }
    }

    // MARK: - Startup

    func start() {
        accounts = store.load()
        Log.debug("start: loaded \(accounts.count) account(s)")
        if accounts.isEmpty {
            showAccountEditor = true
        } else {
            let initial = accounts.first { $0.isDefault } ?? accounts[0]
            selectAccount(initial.id)
        }
    }

    // MARK: - Accounts

    func saveAccount(_ account: Account, password: String) {
        if let index = accounts.firstIndex(where: { $0.id == account.id }) {
            accounts[index] = account
        } else {
            accounts.append(account)
        }
        if !password.isEmpty {
            let id = account.id
            Task.detached(priority: .userInitiated) {
                do {
                    try Keychain.setPassword(password, forAccountID: id)
                } catch {
                    await MainActor.run {
                        self.report(error: "Could not save password to Keychain: \(error.localizedDescription)")
                    }
                }
            }
        }
        persistAccounts()
        // Force a fresh session so changed settings take effect.
        if let old = sessions.removeValue(forKey: account.id) {
            Task { await old.logout() }
        }
        selectAccount(account.id)
    }

    func removeAccount(_ account: Account) {
        accounts.removeAll { $0.id == account.id }
        Keychain.deletePassword(forAccountID: account.id)
        if let old = sessions.removeValue(forKey: account.id) {
            Task { await old.logout() }
        }
        persistAccounts()
        if selectedAccountID == account.id {
            selectedAccountID = nil
            folders = []
            messages = []
            currentDetail = nil
            if let next = accounts.first { selectAccount(next.id) }
        }
    }

    private func persistAccounts() {
        do {
            try store.save(accounts)
        } catch {
            report(error: "Could not save accounts: \(error.localizedDescription)")
        }
    }

    func selectAccount(_ id: UUID) {
        selectedAccountID = id
        folders = []
        messages = []
        currentDetail = nil
        selectedFolderName = nil
        guard let account = selectedAccount else { return }
        status("Connecting to \(account.accountName)…")
        Log.debug("selectAccount: connecting to \(account.imapHost):\(account.imapPort)")
        Task {
            do {
                let session = try await self.session(for: account)
                let list = try await session.listFolders()
                Log.debug("selectAccount: \(list.count) folders")
                self.folders = list.filter(\.isSelectable)
                self.status("Connected")
                if let inbox = self.folders.first(where: { $0.specialUse == .inbox }) ?? self.folders.first {
                    self.selectFolder(inbox.fullName)
                }
            } catch {
                // A missing password is fixable right here — open the editor
                // for this account instead of a dead-end alert.
                if case MailError.authenticationFailed(let message) = error,
                   message.hasPrefix("No saved password") {
                    self.editingAccount = account
                    self.showAccountEditor = true
                    self.statusText = "Enter the password for \(account.accountName)"
                    Announcer.announce("Enter the password for \(account.accountName)")
                } else {
                    self.report(error: error.localizedDescription)
                }
            }
        }
    }

    /// Get or create an authenticated session for an account.
    private func session(for account: Account) async throws -> IMAPSession {
        if let existing = sessions[account.id], await existing.isConnected {
            return existing
        }
        Log.debug("session: reading keychain password")
        guard let password = await Self.lookupPassword(accountID: account.id, host: account.imapHost) else {
            throw MailError.authenticationFailed("No saved password — edit the account to set one.")
        }
        Log.debug("session: got password, connecting")
        let session = IMAPSession(host: account.imapHost, port: account.imapPort, useTLS: account.imapUseSSL)
        try await session.connect()
        Log.debug("session: connected, logging in")
        try await session.login(username: account.username, password: password)
        Log.debug("session: logged in")
        sessions[account.id] = session
        return session
    }

    /// Keychain reads run off the main actor: SecItemCopyMatching is a
    /// blocking IPC call and may show an authorization prompt — it must never
    /// be able to freeze the UI thread.
    ///
    /// Dev override: when launched with --profileDir (isolated test profile),
    /// the QM_DEV_PASSWORD environment variable supplies the password —
    /// but only for accounts pointing at a loopback server, so a real
    /// account added during a dev run still uses its Keychain password.
    private static func lookupPassword(accountID: UUID, host: String) async -> String? {
        if CommandLine.arguments.contains("--profileDir"),
           ["127.0.0.1", "localhost", "::1"].contains(host.lowercased()),
           let dev = ProcessInfo.processInfo.environment["QM_DEV_PASSWORD"], !dev.isEmpty {
            Log.debug("session: using QM_DEV_PASSWORD override (dev profile, loopback host)")
            return dev
        }
        // Race the read against a timeout: a Keychain authorization prompt
        // that can't display (or a wedged securityd) must surface as an error,
        // not an eternal "Connecting…".
        return await withTaskGroup(of: String??.self) { group in
            group.addTask {
                Keychain.password(forAccountID: accountID)
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: 15_000_000_000)
                return .some(nil)
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            if first == .some(nil) {
                Log.debug("session: keychain read timed out")
            }
            return first ?? nil
        }
    }

    // MARK: - Folders & messages

    func selectFolder(_ name: String) {
        guard let account = selectedAccount else { return }
        selectedFolderName = name
        messages = []
        currentDetail = nil
        selectedMessageUID = nil
        isLoadingMessages = true
        status("Loading \(name)…")
        Task {
            do {
                let session = try await self.session(for: account)
                let count = try await session.select(folder: name)
                let summaries = try await session.fetchSummaries(limit: 200)
                guard self.selectedFolderName == name else { return }
                Log.debug("selectFolder: \(name) has \(count) messages, showing \(summaries.count)")
                self.messages = summaries
                self.isLoadingMessages = false
                let shown = summaries.count < count ? "newest \(summaries.count) of \(count)" : "\(count)"
                self.status("\(name): \(shown) messages")
                Announcer.announce("\(name), \(count) messages")
            } catch {
                self.isLoadingMessages = false
                self.report(error: error.localizedDescription)
            }
        }
    }

    func refreshCurrentFolder() {
        if let name = selectedFolderName { selectFolder(name) }
    }

    func openMessage(uid: UInt32) {
        guard let account = selectedAccount else { return }
        openTask?.cancel()
        currentDetail = nil
        openTask = Task {
            do {
                let session = try await self.session(for: account)
                let raw = try await session.fetchRawMessage(uid: uid)
                guard !Task.isCancelled else { return }
                let detail = MIMEParser.parseMessage(raw, uid: uid)
                guard self.selectedMessageUID == uid else { return }
                self.currentDetail = detail
                if let index = self.messages.firstIndex(where: { $0.uid == uid }),
                   !self.messages[index].isSeen {
                    self.messages[index].isSeen = true
                    try? await session.markSeen(uid: uid, seen: true)
                }
            } catch is CancellationError {
            } catch {
                self.report(error: error.localizedDescription)
            }
        }
    }

    // MARK: - Message actions

    private var currentSummary: MessageSummary? {
        guard let uid = selectedMessageUID else { return nil }
        return messages.first { $0.uid == uid }
    }

    func deleteSelectedMessage() {
        guard let account = selectedAccount, let summary = currentSummary else { return }
        let trash = folders.first { $0.specialUse == .trash }
        let deletingFromTrash = selectedFolder?.specialUse == .trash
        Task {
            do {
                let session = try await self.session(for: account)
                if let trash, !deletingFromTrash {
                    try await session.move(uid: summary.uid, to: trash.fullName)
                } else {
                    try await session.delete(uid: summary.uid)
                }
                self.removeFromList(uid: summary.uid)
                self.status("Message deleted")
                Announcer.announce("Deleted")
            } catch {
                self.report(error: "Delete failed: \(error.localizedDescription)")
            }
        }
    }

    func archiveSelectedMessage() {
        guard let account = selectedAccount, let summary = currentSummary,
              let archive = folders.first(where: { $0.specialUse == .archive })
        else {
            report(error: "No Archive folder on this account.")
            return
        }
        Task {
            do {
                let session = try await self.session(for: account)
                try await session.move(uid: summary.uid, to: archive.fullName)
                self.removeFromList(uid: summary.uid)
                self.status("Message archived")
                Announcer.announce("Archived")
            } catch {
                self.report(error: "Archive failed: \(error.localizedDescription)")
            }
        }
    }

    private func removeFromList(uid: UInt32) {
        guard let index = messages.firstIndex(where: { $0.uid == uid }) else { return }
        messages.remove(at: index)
        if selectedMessageUID == uid {
            // Keep focus in place: select what slid into this row.
            selectedMessageUID = index < messages.count ? messages[index].uid
                : messages.last?.uid
            currentDetail = nil
            if let next = selectedMessageUID { openMessage(uid: next) }
        }
    }

    func toggleReadSelectedMessage() {
        guard let account = selectedAccount, let summary = currentSummary,
              let index = messages.firstIndex(where: { $0.uid == summary.uid }) else { return }
        let newSeen = !summary.isSeen
        messages[index].isSeen = newSeen
        Task {
            do {
                let session = try await self.session(for: account)
                try await session.markSeen(uid: summary.uid, seen: newSeen)
                Announcer.announce(newSeen ? "Marked read" : "Marked unread")
            } catch {
                self.messages[index].isSeen = !newSeen
                self.report(error: error.localizedDescription)
            }
        }
    }

    func toggleFlagSelectedMessage() {
        guard let account = selectedAccount, let summary = currentSummary,
              let index = messages.firstIndex(where: { $0.uid == summary.uid }) else { return }
        let newFlagged = !summary.isFlagged
        messages[index].isFlagged = newFlagged
        Task {
            do {
                let session = try await self.session(for: account)
                try await session.markFlagged(uid: summary.uid, flagged: newFlagged)
                Announcer.announce(newFlagged ? "Flagged" : "Flag removed")
            } catch {
                self.messages[index].isFlagged = !newFlagged
                self.report(error: error.localizedDescription)
            }
        }
    }

    // MARK: - Compose

    /// Create a draft and return the key for openWindow.
    func makeDraft(mode: ComposeDraft.Mode) -> UUID? {
        guard let account = selectedAccount else { return nil }
        var draft = ComposeDraft(accountID: account.id)
        if mode != .new, let detail = currentDetail {
            let quoted = Self.quote(detail)
            let sender = detail.replyTo.isEmpty ? detail.from : detail.replyTo
            switch mode {
            case .reply:
                draft.to = sender
                draft.subject = Self.replySubject(detail.subject)
            case .replyAll:
                let mine = account.emailAddress.lowercased()
                draft.to = sender
                draft.cc = (detail.to + detail.cc).filter { $0.address.lowercased() != mine }
            case .forward:
                draft.subject = detail.subject.hasPrefix("Fwd:") ? detail.subject : "Fwd: \(detail.subject)"
            case .new:
                break
            }
            if mode != .forward {
                draft.subject = draft.subject.isEmpty ? Self.replySubject(detail.subject) : draft.subject
                draft.inReplyTo = detail.messageID
                draft.references = detail.messageID.isEmpty ? [] : [detail.messageID]
            }
            draft.body = "\n\n" + quoted
        }
        if !account.signature.isEmpty {
            draft.body = "\n\n" + account.signature + draft.body
        }
        let key = UUID()
        composeDrafts[key] = draft
        return key
    }

    static func replySubject(_ subject: String) -> String {
        subject.lowercased().hasPrefix("re:") ? subject : "Re: \(subject)"
    }

    static func quote(_ detail: MessageDetail) -> String {
        let source = detail.textBody ?? Self.roughTextFromHTML(detail.htmlBody ?? "")
        let sender = detail.from.first?.description ?? "the sender"
        let header = "On \(detail.date.map { DateFormatter.localizedString(from: $0, dateStyle: .medium, timeStyle: .short) } ?? "an earlier date"), \(sender) wrote:"
        let quoted = source
            .replacingOccurrences(of: "\r\n", with: "\n")
            .split(separator: "\n", omittingEmptySubsequences: false)
            .map { "> \($0)" }
            .joined(separator: "\n")
        return header + "\n" + quoted
    }

    static func roughTextFromHTML(_ html: String) -> String {
        html.replacingOccurrences(of: "<br[^>]*>", with: "\n", options: [.regularExpression, .caseInsensitive])
            .replacingOccurrences(of: "</p>", with: "\n", options: .caseInsensitive)
            .replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression)
            .replacingOccurrences(of: "&nbsp;", with: " ")
            .replacingOccurrences(of: "&amp;", with: "&")
            .replacingOccurrences(of: "&lt;", with: "<")
            .replacingOccurrences(of: "&gt;", with: ">")
    }

    /// Send a composed message over SMTP, then try to save a copy to Sent.
    func send(draft: ComposeDraft, completion: @escaping (String?) -> Void) {
        guard let account = accounts.first(where: { $0.id == draft.accountID }) else {
            completion("The sending account no longer exists.")
            return
        }
        let message = OutgoingMessage(
            from: MailAddress(name: account.displayName, address: account.emailAddress),
            to: draft.to, cc: draft.cc,
            subject: draft.subject,
            textBody: draft.body,
            inReplyTo: draft.inReplyTo,
            references: draft.references
        )
        status("Sending…")
        Task {
            guard let password = await Self.lookupPassword(accountID: account.id, host: account.smtpHost) else {
                self.status("Send failed")
                completion("No saved password for \(account.accountName).")
                return
            }
            do {
                let client = SMTPClient(host: account.smtpHost, port: account.smtpPort, useTLS: account.smtpUseSSL)
                try await client.send(message: message, username: account.username, password: password)
                self.status("Message sent")
                Announcer.announce("Message sent")
                completion(nil)
                // Best-effort Sent copy; many servers do this automatically.
                if let sent = self.folders.first(where: { $0.specialUse == .sent }) {
                    let session = try await self.session(for: account)
                    try? await session.append(folder: sent.fullName, message: message.rfc5322Data())
                }
            } catch {
                self.status("Send failed")
                completion(error.localizedDescription)
            }
        }
    }

    // MARK: - Status

    private func status(_ text: String) {
        statusText = text
    }

    private func report(error: String) {
        Log.debug("error: \(error)")
        errorText = error
        statusText = error
        Announcer.announce(error)
    }
}

/// Minimal stderr logger, active when launched with /debug or --debug
/// (mirrors the Windows app's /debug verbose logging flag).
enum Log {
    static let enabled = CommandLine.arguments.contains { $0 == "/debug" || $0 == "--debug" }

    private static let fileURL: URL = {
        let dir = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("QuickMail", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("quickmail.log")
    }()

    static func debug(_ message: String) {
        guard enabled else { return }
        let line = "[quickmail] \(Date().formatted(date: .omitted, time: .standard)) \(message)\n"
        FileHandle.standardError.write(Data(line.utf8))
        if let handle = try? FileHandle(forWritingTo: fileURL) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            try? handle.close()
        } else {
            try? Data(line.utf8).write(to: fileURL)
        }
    }
}

/// A message being composed, owned by one compose window.
struct ComposeDraft {
    enum Mode { case new, reply, replyAll, forward }
    var accountID: UUID
    var to: [MailAddress] = []
    var cc: [MailAddress] = []
    var subject: String = ""
    var body: String = ""
    var inReplyTo: String?
    var references: [String] = []
}

/// Posts VoiceOver announcements for results and status changes — the Mac
/// analogue of AccessibilityHelper.Announce on Windows.
@MainActor
enum Announcer {
    static func announce(_ text: String) {
        guard let element = NSApp.mainWindow ?? NSApp.windows.first else { return }
        NSAccessibility.post(
            element: element,
            notification: .announcementRequested,
            userInfo: [
                .announcement: text,
                .priority: NSAccessibilityPriorityLevel.high.rawValue,
            ]
        )
    }
}
