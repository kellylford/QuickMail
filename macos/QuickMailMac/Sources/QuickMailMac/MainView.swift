import SwiftUI
import QuickMailCore

struct MainView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 180, ideal: 220)
        } content: {
            MessageListView()
                .navigationSplitViewColumnWidth(min: 280, ideal: 360)
        } detail: {
            ReadingPaneView()
        }
        .frame(minWidth: 900, minHeight: 500)
        .safeAreaInset(edge: .bottom) {
            // Status line: visible state for sighted users; changes are also
            // announced through the Announcer for screen reader users.
            Text(state.statusText)
                .font(.callout)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(.bar)
                .accessibilityAddTraits(.updatesFrequently)
                .accessibilityLabel("Status: \(state.statusText)")
        }
        .alert(
            "QuickMail",
            isPresented: Binding(
                get: { state.errorText != nil },
                set: { if !$0 { state.errorText = nil } }
            )
        ) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(state.errorText ?? "")
        }
        .sheet(isPresented: $state.showAccountEditor) {
            AccountEditorView(account: state.editingAccount ?? Account())
                .environmentObject(state)
        }
    }
}

struct SidebarView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        List(selection: folderSelection) {
            ForEach(state.accounts) { account in
                Section(account.accountName) {
                    if account.id == state.selectedAccountID {
                        ForEach(state.folders) { folder in
                            Label(folder.displayName, systemImage: icon(for: folder))
                                .tag(folder.fullName)
                                .accessibilityLabel(folder.displayName)
                        }
                    } else {
                        Button("Open \(account.accountName)") {
                            state.selectAccount(account.id)
                        }
                        .buttonStyle(.link)
                    }
                }
            }
        }
        .accessibilityLabel("Folders")
        .navigationTitle("QuickMail")
        .toolbar {
            ToolbarItem {
                Button {
                    state.editingAccount = nil
                    state.showAccountEditor = true
                } label: {
                    Label("Add Account", systemImage: "plus")
                }
                .accessibilityLabel("Add Account")
            }
        }
    }

    private var folderSelection: Binding<String?> {
        Binding(
            get: { state.selectedFolderName },
            set: { if let name = $0 { state.selectFolder(name) } }
        )
    }

    private func icon(for folder: MailFolder) -> String {
        switch folder.specialUse {
        case .inbox: return "tray"
        case .sent: return "paperplane"
        case .drafts: return "doc"
        case .trash: return "trash"
        case .junk: return "xmark.bin"
        case .archive: return "archivebox"
        case nil: return "folder"
        }
    }
}

struct MessageListView: View {
    @EnvironmentObject var state: AppState
    @FocusState private var listFocused: Bool

    var body: some View {
        Group {
            if state.isLoadingMessages {
                ProgressView("Loading messages…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if state.messages.isEmpty {
                Text(state.selectedFolderName == nil ? "Select a folder" : "No messages")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(state.messages, selection: messageSelection) { message in
                    MessageRow(message: message, folder: state.selectedFolder)
                        .tag(message.uid)
                }
                .accessibilityLabel("Messages")
                .focused($listFocused)
                .onKeyPress(.return) {
                    guard state.selectedMessageUID != nil else { return .ignored }
                    state.readSelectedMessage()
                    return .handled
                }
                .onChange(of: state.listFocusToken) {
                    listFocused = true
                }
            }
        }
        .navigationTitle(state.selectedFolder?.displayName ?? "Messages")
        .onDeleteCommand { state.deleteSelectedMessage() }
    }

    private var messageSelection: Binding<UInt32?> {
        Binding(
            get: { state.selectedMessageUID },
            set: { uid in
                state.selectedMessageUID = uid
                if let uid { state.openMessage(uid: uid) }
            }
        )
    }
}

struct MessageRow: View {
    let message: MessageSummary
    let folder: MailFolder?

    private var correspondent: String {
        // In Sent/Drafts the interesting party is the recipient.
        if folder?.specialUse == .sent || folder?.specialUse == .drafts {
            let names = message.to.map(\.displayText).joined(separator: ", ")
            return names.isEmpty ? "(no recipient)" : "To: \(names)"
        }
        return message.from.first?.displayText ?? "(unknown sender)"
    }

    private var dateText: String {
        guard let date = message.date else { return "" }
        return DateFormatter.localizedString(from: date, dateStyle: .short, timeStyle: .short)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(correspondent)
                    .font(.body.weight(message.isSeen ? .regular : .bold))
                    .lineLimit(1)
                Spacer()
                if message.isFlagged {
                    Image(systemName: "flag.fill")
                        .foregroundStyle(.orange)
                        .accessibilityHidden(true)
                }
                Text(dateText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Text(message.subject.isEmpty ? "(no subject)" : message.subject)
                .font(.callout.weight(message.isSeen ? .regular : .semibold))
                .lineLimit(1)
                .foregroundStyle(message.isSeen ? .secondary : .primary)
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibleText)
    }

    private var accessibleText: String {
        var parts: [String] = []
        if !message.isSeen { parts.append("Unread") }
        if message.isFlagged { parts.append("Flagged") }
        parts.append(correspondent)
        parts.append(message.subject.isEmpty ? "no subject" : message.subject)
        if !dateText.isEmpty { parts.append(dateText) }
        return parts.joined(separator: ", ")
    }
}
