import SwiftUI
import QuickMailCore

struct ComposeView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss

    let draftKey: UUID
    @State private var toText: String
    @State private var ccText: String
    @State private var subject: String
    @State private var bodyText: String
    @State private var isSending = false
    @State private var sendError: String?
    private let accountID: UUID
    private let inReplyTo: String?
    private let references: [String]

    init(draftKey: UUID, draft: ComposeDraft) {
        self.draftKey = draftKey
        self.accountID = draft.accountID
        self.inReplyTo = draft.inReplyTo
        self.references = draft.references
        _toText = State(initialValue: draft.to.map(\.address).joined(separator: ", "))
        _ccText = State(initialValue: draft.cc.map(\.address).joined(separator: ", "))
        _subject = State(initialValue: draft.subject)
        _bodyText = State(initialValue: draft.body)
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                TextField("To", text: $toText)
                    .accessibilityLabel("To")
                TextField("Cc", text: $ccText)
                    .accessibilityLabel("Cc")
                TextField("Subject", text: $subject)
                    .accessibilityLabel("Subject")
            }
            .formStyle(.columns)
            .textFieldStyle(.roundedBorder)
            .padding(10)
            Divider()
            TextEditor(text: $bodyText)
                .font(.body)
                .accessibilityLabel("Message body")
                .padding(4)
        }
        .frame(minWidth: 560, minHeight: 420)
        .navigationTitle(subject.isEmpty ? "New Message" : subject)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    send()
                } label: {
                    Label("Send", systemImage: "paperplane.fill")
                }
                .keyboardShortcut("d", modifiers: [.command, .shift])
                .disabled(isSending || toText.trimmingCharacters(in: .whitespaces).isEmpty)
                .accessibilityLabel("Send")
                .help("Send (⇧⌘D)")
            }
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { close() }
                    .accessibilityLabel("Cancel")
            }
        }
        .alert("Could not send", isPresented: Binding(
            get: { sendError != nil },
            set: { if !$0 { sendError = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(sendError ?? "")
        }
    }

    private func send() {
        let to = AddressParser.parse(toText)
        guard !to.isEmpty else {
            sendError = "Enter at least one valid recipient address."
            return
        }
        isSending = true
        var draft = ComposeDraft(accountID: accountID)
        draft.to = to
        draft.cc = AddressParser.parse(ccText)
        draft.subject = subject
        draft.body = bodyText
        draft.inReplyTo = inReplyTo
        draft.references = references
        state.send(draft: draft) { error in
            isSending = false
            if let error {
                sendError = error
            } else {
                close()
            }
        }
    }

    private func close() {
        state.composeDrafts.removeValue(forKey: draftKey)
        dismiss()
    }
}
