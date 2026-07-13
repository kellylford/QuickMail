import Foundation

/// Persists accounts (without passwords) to
/// ~/Library/Application Support/QuickMail/accounts.json, mirroring the
/// Windows app's AccountService. A custom directory can be supplied for
/// tests or isolated profiles (the Mac analogue of --profileDir).
public final class AccountStore: @unchecked Sendable {
    private let fileURL: URL
    private let queue = DispatchQueue(label: "quickmail.accountstore")

    public init(directory: URL? = nil) {
        let dir = directory ?? FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("QuickMail", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        self.fileURL = dir.appendingPathComponent("accounts.json")
    }

    public func load() -> [Account] {
        queue.sync {
            guard let data = try? Data(contentsOf: fileURL) else { return [] }
            return (try? JSONDecoder().decode([Account].self, from: data)) ?? []
        }
    }

    public func save(_ accounts: [Account]) throws {
        try queue.sync {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(accounts)
            try data.write(to: fileURL, options: .atomic)
        }
    }
}
