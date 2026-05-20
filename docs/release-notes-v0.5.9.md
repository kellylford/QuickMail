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

Three ways to switch to a saved view:
- **View menu** — select the view from the **View → Views** submenu
- **Folder tree** — a **Views** section appears below your accounts; expand it and activate the view
- **Keyboard shortcut** — press the shortcut assigned in the view manager or in Settings → Keyboard Shortcuts

**Managing views**

Open **View → Views → Manage Views…** to rename, reassign shortcuts, change the default, update the saved state, or delete any view.

**Multi-folder views**

If you press **Save** in the manager while the current folder differs from the one already stored in a view, you can choose to replace the existing folder, add the new one alongside it, or cancel. A view that spans multiple folders shows an **All** child in the folder tree (shows all messages together) plus individual folder nodes.

**Keyboard shortcuts**

Shortcuts assigned to saved views appear in the folder tree and the View menu alongside the view name. They are also registered in the command palette and visible in **Settings → Keyboard Shortcuts**.

---

## Improvements

### Save View dialog focus

When **View → Views → Save View…** is chosen, the dialog now opens in a streamlined create mode:

- The saved-views list is hidden so the editing panel fills the full width
- Focus lands immediately in the **Name** field with the suggested name selected — start typing to replace it
- Bottom buttons are **Save View** (enabled once the name is not blank) and **Cancel**

The **Manage Views…** dialog is unchanged.

---

## Bug Fixes

- **Sent messages not appearing in Sent folder** — QuickMail was delivering messages via SMTP but never copying them to the IMAP Sent folder. Providers that automatically copy sent mail (Gmail) appeared to work; all others silently dropped the sent copy. The app now appends each sent message to the Sent folder via IMAP immediately after a successful SMTP delivery. The append runs in the background so a slow server response does not delay the compose window closing.

- **Drafts opening for reading in grouped views** — In Conversations, By Sender, and By Recipient views, opening a draft message displayed it in the reading pane instead of opening it in the compose window. The draft detection logic now applies in all view modes.

- **SMTP connection failures** — Connections to some SMTP servers were failing silently or being cancelled before the server had time to respond. The connect timeout is now separate from the folder-listing timeout (30 s and 120 s respectively), and connection errors are logged with enough detail to diagnose them. The SSL option label in the account dialog has been clarified: checked means implicit SSL on connect (port 465); unchecked means STARTTLS (port 587).

---

## Internal

- `ViewManagerViewModel` and `ViewManagerWindow` visibility bindings now have full unit-test coverage. Tests use `Dispatcher.PushFrame` at Background priority to drain the WPF DataBind queue before asserting, making visibility assertions reliable without a running message loop.
- `ViewManagerWindow` added to the XAML parse test suite.
- `EnsureApplication()` in the test project uses `lock (typeof(Application))` so parallel `[StaFact]` threads from different test classes cannot race to create a second WPF `Application` instance.
