import SwiftUI
import QuickMailCore

@main
struct QuickMailApp: App {
    @StateObject private var state = AppState()
    @Environment(\.openWindow) private var openWindow

    var body: some Scene {
        WindowGroup("QuickMail", id: "main") {
            MainView()
                .environmentObject(state)
                .onAppear { state.start() }
        }
        .commands {
            CommandGroup(after: .appSettings) {
                SettingsLink {
                    Text("Manage Accounts…")
                }
                .keyboardShortcut(",", modifiers: [.command, .shift])
            }
            CommandGroup(after: .newItem) {
                Button("New Message") {
                    compose(.new)
                }
                .keyboardShortcut("n", modifiers: [.command])
                .disabled(state.selectedAccount == nil)
            }
            CommandMenu("Message") {
                Button("Reply") { compose(.reply) }
                    .keyboardShortcut("r", modifiers: [.command])
                    .disabled(state.currentDetail == nil)
                Button("Reply All") { compose(.replyAll) }
                    .keyboardShortcut("r", modifiers: [.command, .shift])
                    .disabled(state.currentDetail == nil)
                Button("Forward") { compose(.forward) }
                    .keyboardShortcut("f", modifiers: [.command, .shift])
                    .disabled(state.currentDetail == nil)
                Divider()
                Button(markReadTitle) { state.toggleReadSelectedMessage() }
                    .keyboardShortcut("u", modifiers: [.command, .shift])
                    .disabled(state.selectedMessageUID == nil)
                Button(flagTitle) { state.toggleFlagSelectedMessage() }
                    .keyboardShortcut("l", modifiers: [.command, .shift])
                    .disabled(state.selectedMessageUID == nil)
                Divider()
                Button("Archive") { state.archiveSelectedMessage() }
                    .keyboardShortcut("a", modifiers: [.control, .command])
                    .disabled(state.selectedMessageUID == nil)
                Button("Delete") { state.deleteSelectedMessage() }
                    .keyboardShortcut(.delete, modifiers: [.command])
                    .disabled(state.selectedMessageUID == nil)
            }
            CommandGroup(after: .toolbar) {
                Button("Get New Mail") { state.refreshCurrentFolder() }
                    .keyboardShortcut("n", modifiers: [.command, .shift])
                    .disabled(state.selectedFolderName == nil)
            }
        }

        WindowGroup("New Message", id: "compose", for: UUID.self) { $draftKey in
            if let key = draftKey, let draft = state.composeDrafts[key] {
                ComposeView(draftKey: key, draft: draft)
                    .environmentObject(state)
            } else {
                Text("This compose window has no draft.")
                    .padding()
            }
        }

        Settings {
            AccountsSettingsView()
                .environmentObject(state)
        }
    }

    private var markReadTitle: String {
        let summary = state.messages.first { $0.uid == state.selectedMessageUID }
        return summary?.isSeen == true ? "Mark as Unread" : "Mark as Read"
    }

    private var flagTitle: String {
        let summary = state.messages.first { $0.uid == state.selectedMessageUID }
        return summary?.isFlagged == true ? "Remove Flag" : "Flag"
    }

    private func compose(_ mode: ComposeDraft.Mode) {
        if let key = state.makeDraft(mode: mode) {
            openWindow(id: "compose", value: key)
        }
    }
}
