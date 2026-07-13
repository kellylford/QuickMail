import SwiftUI
import QuickMailCore

/// Add/edit one account. Password goes straight to the Keychain, never to disk.
struct AccountEditorView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss

    @State private var account: Account
    @State private var password = ""
    @State private var validationError: String?
    private let isNew: Bool

    init(account: Account) {
        _account = State(initialValue: account)
        isNew = account.accountName.isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Form {
                Section("Account") {
                    TextField("Account name", text: $account.accountName)
                        .accessibilityLabel("Account name")
                    TextField("Your name", text: $account.displayName)
                        .accessibilityLabel("Your name")
                    TextField("Email address", text: $account.emailAddress)
                        .accessibilityLabel("Email address")
                    TextField("Username", text: $account.username)
                        .accessibilityLabel("Username")
                    SecureField(isNew ? "Password" : "Password (leave blank to keep current)", text: $password)
                        .accessibilityLabel("Password")
                }
                Section("Incoming mail (IMAP)") {
                    TextField("IMAP server", text: $account.imapHost)
                        .accessibilityLabel("IMAP server")
                    TextField("IMAP port", value: $account.imapPort, format: .number.grouping(.never))
                        .accessibilityLabel("IMAP port")
                    Toggle("Use TLS", isOn: $account.imapUseSSL)
                        .accessibilityLabel("IMAP use TLS")
                }
                Section("Outgoing mail (SMTP)") {
                    TextField("SMTP server", text: $account.smtpHost)
                        .accessibilityLabel("SMTP server")
                    TextField("SMTP port", value: $account.smtpPort, format: .number.grouping(.never))
                        .accessibilityLabel("SMTP port")
                    Toggle("Use TLS (port 465)", isOn: $account.smtpUseSSL)
                        .accessibilityLabel("SMTP use TLS")
                }
                Section("Options") {
                    Toggle("Default account for new messages", isOn: $account.isDefault)
                    TextField("Signature", text: $account.signature, axis: .vertical)
                        .lineLimit(3...6)
                        .accessibilityLabel("Signature")
                }
            }
            .formStyle(.grouped)

            if let validationError {
                Text(validationError)
                    .foregroundStyle(.red)
                    .padding(.horizontal)
                    .accessibilityLabel("Error: \(validationError)")
            }

            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(isNew ? "Add Account" : "Save") { save() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(minWidth: 460, minHeight: 560)
        .navigationTitle(isNew ? "Add Account" : "Edit Account")
    }

    private func save() {
        let trimmedName = account.accountName.trimmingCharacters(in: .whitespaces)
        guard !trimmedName.isEmpty else {
            validationError = "Account name is required."
            return
        }
        guard account.emailAddress.contains("@") else {
            validationError = "Enter a valid email address."
            return
        }
        guard !account.imapHost.isEmpty, !account.smtpHost.isEmpty else {
            validationError = "IMAP and SMTP servers are required."
            return
        }
        if isNew && password.isEmpty {
            validationError = "Password is required for a new account."
            return
        }
        if account.username.isEmpty {
            account.username = account.emailAddress
        }
        state.saveAccount(account, password: password)
        dismiss()
    }
}

/// Settings scene: list, edit, and remove accounts.
struct AccountsSettingsView: View {
    @EnvironmentObject var state: AppState
    @State private var editing: Account?
    @State private var pendingRemoval: Account?

    var body: some View {
        VStack(alignment: .leading) {
            List(state.accounts) { account in
                HStack {
                    VStack(alignment: .leading) {
                        Text(account.accountName)
                        Text(account.emailAddress)
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Edit") { editing = account }
                        .accessibilityLabel("Edit \(account.accountName)")
                    Button("Remove", role: .destructive) { pendingRemoval = account }
                        .accessibilityLabel("Remove \(account.accountName)")
                }
                .accessibilityElement(children: .contain)
            }
            .accessibilityLabel("Accounts")
            Button("Add Account") {
                editing = Account()
            }
            .padding()
        }
        .frame(minWidth: 480, minHeight: 320)
        .sheet(item: $editing) { account in
            AccountEditorView(account: account)
                .environmentObject(state)
        }
        .confirmationDialog(
            "Remove \(pendingRemoval?.accountName ?? "account")? The account's password is deleted from the Keychain; mail on the server is not affected.",
            isPresented: Binding(
                get: { pendingRemoval != nil },
                set: { if !$0 { pendingRemoval = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Remove Account", role: .destructive) {
                if let account = pendingRemoval { state.removeAccount(account) }
            }
            Button("Cancel", role: .cancel) {}
        }
    }
}