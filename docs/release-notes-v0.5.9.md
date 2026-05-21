# QuickMail v0.5.9 Release Notes

## New Features

### Message sorting

A new **View → Sort** menu lets you control the order of the message list. The selected sort persists across sessions and applies independently of the active view mode or filter.

| Sort order | What it does |
|------------|-------------|
| Newest First | Most recent messages at the top (default) |
| Oldest First | Oldest messages at the top |
| A → Z | Alphabetical by sender or group name |
| Z → A | Reverse alphabetical by sender or group name |
| Most Messages | Groups with the most messages at the top |
| Fewest Messages | Groups with the fewest messages at the top |

Date and alphabetical sorts work in all view modes. The two message-count sorts are only meaningful in grouped views (Conversations, By Sender, By Recipient) and are greyed out when the flat Messages view is active.

All six sort options are also registered in the command palette and can be assigned custom keyboard shortcuts in **Settings → Keyboard Shortcuts**.

---

### Saved views

A **saved view** is a named snapshot of a folder (or set of folders), view mode, filter, and sort order that you can jump back to instantly.

**Creating a view**

Open **View → Views → Save View…**. The dialog opens focused on the name field with a suggested name based on the current folder and settings — type to replace it or press Tab to accept it. Set an optional keyboard shortcut and mark the view as the default if you want it applied automatically when the app starts. Press **Save View** when done. Press **Cancel** or Escape to discard.

**Applying a view**

Four ways to switch to a saved view:
- **View menu** — select the view from the **View → Views** submenu (a checkmark appears next to the active view)
- **Folder tree** — a **Views** section appears below your accounts; expand it and activate the view
- **Keyboard shortcut** — press the shortcut assigned in the view manager or in Settings → Keyboard Shortcuts
- **Apply View button** — select the view in Manage Views and press **Apply View** to activate it and close the dialog

**Managing views**

Open **View → Views → Manage Views…** to rename, reassign shortcuts, change the default, set a day limit, update the saved state, or delete any view. Select a view to see its details in read-only mode; press **Edit** to make changes.

**Multi-folder views**

If you press **Save** in the manager while the current folder differs from the one already stored in a view, you can choose to replace the existing folder, add the new one alongside it, or cancel. A view that spans multiple folders shows an **All** child in the folder tree (shows all messages together) plus individual folder nodes.

**Keyboard shortcuts**

Shortcuts assigned to saved views appear in the folder tree and the View menu alongside the view name. They are also registered in the command palette and visible in **Settings → Keyboard Shortcuts**.

---

### Day limit for saved views

A saved view can include an optional **day limit** that restricts the message list to only messages received within the last N days. Older messages stay in the local cache and reappear immediately when the view is cleared or you navigate to the folder directly — the limit affects display only, not how much mail is downloaded in the background.

To set a day limit, open **Manage Views…**, select a view, press **Edit**, and enter a number in the **Day Limit** field. Leave it blank to show all cached messages. When a day limit is active it stacks with any filter that is also part of the view, so a view can be both "last 7 days" and "unread only."

---

### Clear View command

**View → Views → Clear View** deactivates the current saved view and returns to normal folder navigation, leaving the selected folder and all other settings unchanged. Pressing **F5** (Refresh) while a view is active re-applies the view rather than reverting to the underlying folder.

---

## Improvements

### Save View dialog focus

When **View → Views → Save View…** is chosen, the dialog opens in a streamlined create mode:

- The saved-views list is hidden so the editing panel fills the full width
- Focus lands immediately in the **Name** field with the suggested name selected — start typing to replace it
- Bottom buttons are **Save View** (enabled once the name is not blank) and **Cancel**

The **Manage Views…** dialog is unchanged.

---

### View Manager: read-only and edit states

The right-hand panel in **Manage Views…** now has three distinct states:

- **Empty** — shown when no view is selected; includes a **Create New View from Current State** button
- **Read-only** — shown when a view is selected; displays the name, keyboard shortcut, saved folders, and saved settings, with **Apply View**, **Edit**, **Delete**, and **Save As New** buttons
- **Edit** — shown when you press **Edit** or are creating a new view; exposes the name field, keyboard shortcut picker, default-view checkbox, and day-limit field, with **Save** and **Cancel** buttons

Focus moves automatically to the Name field when entering edit mode.

---

### Folder picker uses the account nickname

The move/copy folder picker was labelling each folder with the account display name (e.g. "Kelly Ford"), which made folders indistinguishable when multiple accounts share the same name. It now uses the account nickname (the email address or label shown everywhere else in the app), which is unique per account.

---

### Move/copy picker shows only the source account's folders

The move and copy pickers used to list folders from all accounts. Selecting a folder from a different account than the message's source caused a "folder not found" error. The picker now filters to only folders that belong to the same account as the messages being moved or copied.

---

### Suggested view name for virtual folders

When saving a view while a virtual folder (All Mail, All Inboxes, etc.) is selected, the suggested name no longer includes the account nickname prefix. Virtual folder views span all accounts, so "theideaplace All Mail" was misleading. The name is now just "All Mail" or "All Inboxes, Unread" as appropriate.

---

### Compose window focus

When replying to or forwarding a message, focus now lands in the message body rather than the To field, so you can start typing immediately.

---

## Bug Fixes

- **Views created from virtual folders loaded no messages** — Views saved while All Mail, All Inboxes, or another virtual folder was active stored the folder reference in a way that could not be resolved on apply, resulting in an empty message list. Virtual folder views now record which virtual folder they were created from and navigate to it correctly when applied.

- **Crash when pressing Escape to cancel the Save View dialog** — Cancelling the Save View dialog (via Escape, the X button, or Cancel) triggered a re-entrant event that mutated the main window's menu while the dialog's message loop was still unwinding, causing a COM apartment violation crash. The cleanup now runs after the dialog is fully closed.

- **Crash when deleting or saving a view in the View Manager** — The same COM apartment crash occurred when deleting or saving a view inside the manager. `ViewsChanged` was being fired while `ShowDialog()` was still blocking; it is now handled only after the dialog closes.

- **Crash when deleting a view** — Removing a view from the collection fired a change notification that cleared `SelectedView` before the deletion code could finish using it, causing a `NullReferenceException`. The selected view is now captured to a local variable before any collection mutation.

- **"Folder not found" when moving or copying messages** — Fixed by the improved move/copy picker described above.

- **Sent messages not appearing in Sent folder** — QuickMail was delivering messages via SMTP but never copying them to the IMAP Sent folder. Providers that automatically copy sent mail (Gmail) appeared to work; all others silently dropped the sent copy. The app now appends each sent message to the Sent folder via IMAP immediately after a successful SMTP delivery. The append runs in the background so a slow server response does not delay the compose window closing.

- **Drafts opening for reading in grouped views** — In Conversations, By Sender, and By Recipient views, opening a draft message displayed it in the reading pane instead of opening it in the compose window. The draft detection logic now applies in all view modes.

- **SMTP connection failures** — Connections to some SMTP servers were failing silently or being cancelled before the server had time to respond. The connect timeout is now separate from the folder-listing timeout (30 s and 120 s respectively), and connection errors are logged with enough detail to diagnose them. The SSL option label in the account dialog has been clarified: checked means implicit SSL on connect (port 465); unchecked means STARTTLS (port 587).

---

## Internal

- Added `SavedViewsTests.cs` with 106 tests covering the full view lifecycle: apply, refresh, clear, folder navigation, day-limit filtering, sentinel string round-tripping, window title updates, and switching between views with different settings.
- Fixed a `\x00` vs ` ` escape bug in virtual-folder sentinel constants. The C# `\x` escape greedily consumes up to four hex digits, so `"\x00AllMail"` compiled to a line-feed character followed by `"llMail"` instead of a NUL followed by `"AllMail"`. All sentinel constants now use ` `, which always takes exactly four digits.
- `ViewManagerViewModel` and `ViewManagerWindow` visibility bindings have full unit-test coverage. Tests use `Dispatcher.PushFrame` at Background priority to drain the WPF DataBind queue before asserting.
- `ViewManagerWindow` is included in the XAML parse test suite.
- Added `WarningsAsErrors` for CS8602, CS8604, and CS8618 in the test project so nullable dereference risks fail the build rather than silently shipping.
- `EnsureApplication()` in the test project uses `lock (typeof(Application))` so parallel `[StaFact]` threads from different test classes cannot race to create a second WPF `Application` instance.
- `CLAUDE.md`: documented the modal dialog / COM apartment crash pattern, event subscription cleanup rules, and `FindName` null-safety requirements for tests.
