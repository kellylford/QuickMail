# Plan: Context Menus Throughout the App (Issue #3)

## Status

Implemented in the current codebase. The context-menu work added account, folder, message, and conversation actions. As of v0.5.3 those commands run through the pooled IMAP connection layer, so moving/copying/deleting mail or folders should not collide with message opening or background sync on the same account.

## TL;DR
Add right-click / Shift+F10 context menus to all four interactive lists (account, folder, message, conversation) with contextually appropriate commands. Requires new IMAP folder/message operations (copy messages, move messages, folder CRUD), new VM commands, a reusable folder-picker dialog adapted for destination selection, and a small new-folder dialog. Naturally decomposes into 5 sequential-but-partially-parallelizable phases.

---

## Phase 1 — New IMAP Operations (`ImapService.cs`)

These are the server-side primitives needed by all higher phases. Implement first.

1. `CopyMessagesAsync(Guid accountId, string folderName, IReadOnlyList<UniqueId> uids, string destinationFolder, CancellationToken ct)` — IMAP COPY command
2. `MoveMessagesAsync(Guid accountId, string folderName, IReadOnlyList<UniqueId> uids, string destinationFolder, CancellationToken ct)` — IMAP MOVE (RFC 6851) or COPY + UID STORE \Deleted + EXPUNGE fallback
3. `CreateFolderAsync(Guid accountId, string parentFolder, string name, CancellationToken ct)` — IMAP CREATE
4. `DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct)` — move all messages to trash, then IMAP DELETE
5. `RenameFolderAsync(Guid accountId, string folderName, string newFullName, CancellationToken ct)` — IMAP RENAME (also serves as move-folder)
6. `CopyFolderAsync(Guid accountId, string folderName, string destinationParent, CancellationToken ct)` — recursive CreateFolder + CopyMessages per subfolder

Pattern reference: `MoveToTrashBatchAsync` in `ImapService.cs` for the move pattern; MailKit's `IMailFolder.MoveToAsync()` / `CopyToAsync()` / `CreateAsync()` / `RenameAsync()` / `DeleteAsync()`. Every method must lease an IMAP client from `ImapService`'s per-account pool and return it after the operation; do not store or reuse `ImapClient` instances in view models.

---

## Phase 2 — New VM Commands (`MainViewModel.cs`)

*Depends on Phase 1.* New `[RelayCommand]` async methods:

**Message operations:**
- `MoveMessagesToFolderAsync()` — opens folder-picker dialog, calls `ImapService.MoveMessagesAsync`, removes from `Messages` collection, refreshes if still in same folder
- `CopyMessagesToFolderAsync()` — same but `CopyMessagesAsync`, no removal from current view

**Conversation operations** (operate on `SelectedConversation` of type `ConversationGroup`):
- `MoveConversationToFolderAsync()` — collects all `ConversationGroup.Messages`, calls same move logic
- `CopyConversationToFolderAsync()` — same for copy
- `ExpandConversationCommand(ConversationGroup g)` / `CollapseConversationCommand(ConversationGroup g)` — toggle `g.IsExpanded`
- `ExpandAllConversationsCommand` / `CollapseAllConversationsCommand` — set all `Conversations` items' `IsExpanded`

**Folder operations** (parameter = `FolderTreeNode`):
- `NewFolderAsync(FolderTreeNode? parent)` — opens `NewFolderDialog` with parent pre-selected, calls `ImapService.CreateFolderAsync`, refreshes folder tree
- `MoveFolderAsync(FolderTreeNode node)` — opens folder-picker for destination, calls `ImapService.RenameFolderAsync`, refreshes tree
- `CopyFolderAsync(FolderTreeNode node)` — same with `ImapService.CopyFolderAsync`
- `DeleteFolderAsync(FolderTreeNode node)` — confirm MessageBox ("Delete '{name}' and move all messages to Trash?"), calls `ImapService.DeleteFolderAsync`, refreshes folder tree

**Account operations:**
- `DeleteAccountAsync(AccountModel account)` — delegate to `AccountManagerViewModel.RemoveAccount` logic (already exists); confirm dialog first
- `OpenAccountSettingsAsync(AccountModel account)` — open `AccountManagerDialog` pre-selected to that account

Register new commands in `RegisterCommands()` where keyboard shortcuts apply (Move/Copy do not need default hotkeys; Expand/Collapse can register for the View menu).

---

## Phase 3 — Dialog Adaptations

*Can run in parallel with Phase 2 once Phase 1 is done.*

**`FolderPickerWindow` extensions:**
- Add a `string Title` constructor parameter (sets `Window.Title`, e.g. "Move to Folder", "Copy to Folder")
- Add optional `bool ShowNewFolderButton` — if true, show "New Folder…" button at the bottom that calls `NewFolderDialog` inline and refreshes the destination list
- Current implementation note: `FolderPickerWindow` is now a flat virtualized searchable list for responsiveness with large Gmail label sets. Keep future destination-picking changes compatible with that fast path.

**New `NewFolderDialog`** (new small Window):
- Single `TextBox` for folder name + OK / Cancel
- Optional `TreeView` (reuse `FolderPickerWindow` pattern) to select parent folder — default to currently selected folder's account
- Returns `(FolderTreeNode parent, string name)` to caller

---

## Phase 4 — Context Menu XAML (`MainWindow.xaml`)

*Depends on Phase 2. Can be written in parallel with Phase 3.*

Use the existing attachment `ContextMenu` (lines 253–269 of `MainWindow.xaml`) as the binding pattern template:
```xml
Command="{Binding PlacementTarget.DataContext.XxxCommand,
          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
```

**AccountList ContextMenu** (add to `<ListBox x:Name="AccountList">`):
- "Delete Account" → `DeleteAccountCommand`, CommandParameter = bound item
- "Account Settings…" → `OpenAccountSettingsCommand`, CommandParameter = bound item

**FolderList ContextMenu** (add to `<TreeView x:Name="FolderList">`):
- "New Folder…" → `NewFolderCommand`, CommandParameter = right-clicked `FolderTreeNode`
- Separator
- "Move Folder…" → `MoveFolderCommand`, CommandParameter = right-clicked `FolderTreeNode`
- "Copy Folder…" → `CopyFolderCommand`, CommandParameter = right-clicked `FolderTreeNode`
- Separator
- "Delete Folder" → `DeleteFolderCommand`, CommandParameter = right-clicked `FolderTreeNode`
- Disable Move/Copy/Delete when node is a system folder (Inbox, Sent, etc.) — use `CanExecute` or `IsEnabled` binding

**MessageList ContextMenu** (add to `<ListView x:Name="MessageList">`):
- "Reply" → existing `ReplyCommand` (disabled if multi-select via `CanExecute`)
- "Reply All" → existing `ReplyAllCommand`
- "Forward" → existing `ForwardCommand`
- Separator
- "Move to Folder…" → `MoveMessagesToFolderCommand`
- "Copy to Folder…" → `CopyMessagesToFolderCommand`
- Separator
- "Delete" → existing `DeleteMessageCommand`

**ConversationTree ContextMenu** — two-level (group header + child items):
- Group level: Reply, Reply All, Forward (on most recent msg), separator, Move Conversation to Folder…, Copy Conversation to Folder…, separator, Delete Conversation, separator, Expand Conversation, Collapse Conversation, Expand All, Collapse All
- Child level: same as MessageList

---

## Phase 5 — Keyboard Support & Accessibility

*Parallel with Phase 4.*

- WPF opens `ContextMenu` on Shift+F10 automatically once the property is set — verify that `AccountList_PreviewKeyDown`, `FolderList_PreviewKeyDown`, `MessageList_PreviewKeyDown`, `ConversationTree_PreviewKeyDown` do **not** consume `Key.F10`.
- Set `AutomationProperties.Name` on each `ContextMenu` and `MenuItem` for screen reader support.
- Confirm that `ItemContainerStyle` on `TreeView` items does not block context menu propagation.

---

## Relevant Files

| File | Change |
|------|--------|
| `QuickMail/Services/ImapService.cs` | Add 6 new methods (Phase 1) |
| `QuickMail/ViewModels/MainViewModel.cs` | Add ~12 new commands (Phase 2) |
| `QuickMail/Views/MainWindow.xaml` | Add 4 ContextMenu blocks (Phase 4) |
| `QuickMail/Views/FolderPickerWindow.xaml` + `.cs` | Extend with Title param + New Folder button (Phase 3) |
| `QuickMail/Views/NewFolderDialog.xaml` + `.cs` | **New file** (Phase 3) |
| `QuickMail/Models/ConversationGroup.cs` | Verify `IsExpanded` property exists |
| `QuickMail/Models/FolderTreeNode.cs` | Verify system-folder guard property |

## Follow-up Note: Issue #6

Issue #6 reported MailKit's "ImapClient is currently busy processing a command in another thread" error while opening messages during other IMAP work. The fix is not in the context-menu XAML; it is the v0.5.2+ IMAP pool. Context-menu commands should continue to call `IImapService` methods and let the service handle connection leasing. Foreground commands use reserved connection capacity in v0.5.3.

---

## Verification Checklist

1. Right-click each list → correct menu items appear; disabled items are not clickable
2. Shift+F10 on focused item → same menu opens at item position
3. Move message → disappears from source folder, appears in destination on refresh
4. Copy message → remains in source, appears in destination on refresh
5. Delete folder → confirmation shown, folder removed from tree
6. New folder → appears in tree under correct parent
7. Move folder → relocated in tree
8. Expand/Collapse All → all conversation groups update visually
9. Reply/Reply All/Forward on conversation → acts on the most recent message only
10. Delete conversation → all messages in group moved to trash
11. Multi-select 3 messages → Move/Copy/Delete all operate on full selection
12. Screen reader announces all menu item names (UIA accessibility audit)

---

## Decisions & Scope Boundaries

- **Move folder = IMAP RENAME** (standard). Cross-account folder moves are out of scope.
- **Copy folder** = recursive operation: create destination subtree + IMAP COPY each batch. Warn user if folder is large.
- **Delete folder** always moves messages to Trash first — never hard-deletes data.
- **New Folder** defaults parent to currently selected folder's account root; tree picker lets user choose any parent.
- Conversation commands collect `ConversationGroup.Messages` — the existing `List<MailMessageSummary>` field.
- Reply/ReplyAll/Forward on a conversation group → `group.Messages.OrderByDescending(m => m.Date).First()`.
- **Excluded:** drag-and-drop move, in-place folder rename, cross-account message moving (future features).
- **Excluded:** "Mark as Read/Unread" on context menu (separate enhancement).
