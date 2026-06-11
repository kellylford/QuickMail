# QuickMail v0.7.2 Release Notes

## Download

Two options are available for v0.7.2:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.2-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New Features

### Rich Compose: Plain Text, Markdown, and HTML Modes

Composing email gets its largest update yet. Every compose window now offers three editing modes:

- **Plain Text** — the classic experience, unchanged.
- **Markdown** — write Markdown source in the familiar text editor; QuickMail renders it to formatted HTML when the message is sent. Press **F8** to open a rendered preview in a separate window.
- **HTML** — a rich text editor with real formatting: bold, italic, underline, strikethrough, three heading levels, bullet and numbered lists, and links.

Switch modes at any time with **Ctrl+Shift+1/2/3**, the **View** menu, or the mode selector in the compose status row. Content converts between modes automatically; switching from a rich mode down to Plain Text asks for confirmation first, because formatting would be lost. Messages composed in Markdown or HTML are sent with both a formatted HTML part and a plain text part, so every recipient's mail app can display them.

**Accessibility was the design constraint, not an afterthought.** The HTML editor is a native Windows rich edit control, not an embedded browser — screen readers stay in their normal edit cursor with no virtual cursor or browse mode.

### Formatting Commands

Formatting works in both rich modes — in HTML mode the commands apply real formatting; in Markdown mode they insert the equivalent Markdown syntax at the cursor or around the selection. Each command confirms its result aloud ("Bold on", "Heading 2", "Bullet list off"):

| Command | Shortcut |
|---------|----------|
| Bold / Italic / Underline | `Ctrl+B` / `Ctrl+I` / `Ctrl+U` |
| Strikethrough | `Ctrl+Shift+X` |
| Heading 1 / 2 / 3 | `Ctrl+Alt+1/2/3` |
| Bullet list / Numbered list | `Ctrl+Shift+L` / `Ctrl+Shift+N` |
| Insert link | `Ctrl+L` |
| Clear formatting | `Ctrl+Space` |
| Announce formatting state | `Ctrl+T` |
| Show formatting list | `Ctrl+Shift+T` |
| Open Markdown preview | `F8` (Markdown mode) |

One exception: Markdown has no underline syntax, so underline is available in HTML mode only — invoking it in Markdown explains this aloud.

### Heading Commands Apply to the Selected Paragraph

Heading shortcuts (`Ctrl+Alt+1/2/3`) and menu items now apply to the paragraph the selection begins in, not the caret position. Previously, selecting a line and pressing a heading shortcut could apply the heading to the paragraph *after* the selection when the caret happened to land at the start of the next paragraph. Headings also now apply to every paragraph touched by the selection when multiple lines are selected.

### Announce Formatting While Navigating (HTML Mode)

In HTML compose mode, QuickMail now announces the block type whenever the caret moves to a paragraph with a different type — for example, moving onto a heading line announces "Heading 2" without needing to press `Ctrl+T`. This is on by default and can be turned off under **Settings → Screen Reader Announcements → Announce formatting while navigating in HTML compose**.

This announcement is HTML-mode-only. Markdown mode does not produce automatic formatting announcements, since the raw Markdown syntax is already present in the text.

### Markdown Preview Window (F8)

In Markdown mode, pressing **F8** opens a dedicated preview window that renders your message as formatted HTML. The preview window is fully focusable, so screen readers switch into browse mode and you can read the rendered output exactly as a recipient would see it. Links open in your default browser. Press **Escape** or **Ctrl+W** to close the preview and return focus to the editor. Pressing **F8** again while the preview is open closes it.

**Two ways to check formatting at the cursor:**

- **Announce Formatting (`Ctrl+T`)** speaks a one-line summary, for example "Heading 2. Bold on, Italic off, Underline off, Strikethrough off."
- **Show Formatting (`Ctrl+Shift+T`)** opens a small window listing the same facts one per row, so you can arrow through them at your own pace. **Escape** or **Enter** closes it and returns focus to the editor.

### Compose Menu Bar

The compose window now has a full menu bar — **File, Edit, View, Format, and Tools** — so every compose action is discoverable without memorizing shortcuts. Press **Alt** or **F10** to reach it; every item shows its keyboard shortcut. The Format menu's items gray out in Plain Text mode, and opening it there explains that formatting requires Markdown or HTML mode. Following Windows convention, top-level menus are never skipped during arrow navigation.

### Automatic Draft Saving

QuickMail now saves your message as a draft automatically while you compose — on by default, every 2 minutes. Auto-save is deliberately quiet:

- A successful save updates a small status ("Auto-saved 3:42 PM") in the compose status row **without any announcement**, so it never interrupts your writing.
- A failed save is announced **once**, then stays quiet until a save succeeds again.
- The command palette includes **Announce Last Auto-Save** to hear the last save time on demand.
- Auto-save skips untouched composes, empty composes, and template editing, and repeated saves replace the previous server draft rather than piling up copies.

Control it under **Settings → General → Composing**: turn auto-save off, choose an interval from 30 seconds to 10 minutes, and pick the **default compose mode** new messages start in.

### Compose Window Titles

The compose window title now leads with your subject and editing mode — for example, "Lunch Friday - HTML - QuickMail" — so the taskbar and Alt+Tab identify each message at a glance. The title updates live as you type the subject or switch modes.

### Microsoft Graph Backend (Preview)

QuickMail now includes a read-only Microsoft Graph backend alongside IMAP, enabling support for Microsoft 365 and Outlook.com accounts. This is a foundation for future Microsoft account support; full sync and compose features for Graph accounts are coming in future releases.

**Account setup for Graph accounts:**
- When adding an account, you can choose **OAuth** for Microsoft 365 / Outlook.com accounts
- QuickMail launches your browser to sign in via Microsoft's authentication page
- No password is stored — tokens are managed securely by the system
- After authentication, QuickMail connects and loads your account information

**Graph-backed account dialogs:**
- Dialogs for Graph accounts now show the account type and username clearly
- UI elements specific to IMAP (password, port, security settings) are hidden for Graph accounts
- OAuth token information is displayed where applicable

---

## UX Improvements

### Message Windows Now Have Full Mail Actions

When a message is opened in a standalone window (via **Ctrl+Enter** or Reading mode set to **Window**), the window previously offered only navigation — Previous, Next, and Move to Main Window. All mail actions are now available directly from the message window without needing to return to the main window first.

A full menu bar (**File**, **Message**, **Navigate**) provides access to every command, alongside a toolbar with the most common actions and keyboard shortcuts matching the main window:

| Shortcut | Action |
|----------|--------|
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+Shift+G` | Grab Addresses from Message |

The command palette (`Ctrl+Shift+P`) now includes all of these commands in addition to the navigation commands. A status bar at the bottom of the window shows the message's position in the folder — for example, "Message 3 of 47".

Deleting a message from its window closes the window immediately; focus returns to the originating position in the message list.

### Sync Progress Announcements

Screen reader users will experience significantly quieter sync progress announcements:

**Before:** Screen readers announced the sync progress number after every single folder completion, creating excessive chatter ("1", "2", "3", "4"... "45").

**After:** QuickMail announces sync progress only every 10 folders or at the end of sync:
- "Synced 10 of 45 folders."
- "Synced 20 of 45 folders."
- "Synced 30 of 45 folders."
- "Synced 40 of 45 folders."
- "Sync complete."

The status bar continues to show "Syncing mail…" without duplicating announcements through the screen reader.

---

## Bug Fixes

- **HTML compose mode was silent to screen readers.** Entering HTML mode replaced the rich editor's document, which permanently disconnected the text from the UI Automation layer — screen readers read nothing of what was actually in the editor. The editor now keeps one document for its lifetime and loads content into it in place, with an automated regression test asserting what assistive technology actually receives across mode switches.
- **F7 (next misspelling) always reported "No misspellings found" in compose.** The spell navigation search started at the current caret position and searched forward — with the caret at the end of the text after typing, the forward pass found nothing and gave up. Spell navigation now wraps: it searches from the caret to the end, then wraps from the beginning if nothing was found in the first pass. The same wrap applies to Shift+F7 searching backward.
- **Heading shortcut applied to the wrong paragraph when text was selected.** When you selected text and pressed a heading shortcut, the heading was applied to the paragraph at the caret (the active end of the selection) rather than the paragraph where the selection started. If the selection ended at a paragraph boundary, this meant the heading landed on the *next* paragraph — the one after your selected text. The heading now applies to the paragraph the selection begins in, matching what users expect.
- **Escape closed the whole compose window from open menus and dropdowns.** Escape now closes the open menu, combo dropdown, or address autocomplete first; only when nothing transient is open does it close the window.
- **Formatting announcements were silenced by focus changes.** Invoking a formatting command from the menu restored focus to the editor, and the focus speech overrode the result announcement — all you heard was "message body." Formatting feedback is now timed to land after focus settles.
- **Duplicate sync progress announcements.** The status bar text and explicit screen reader announcements were both being read aloud during sync, creating redundant chatter. Now only the explicit announcements (which respect user settings) are spoken, eliminating duplicates.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Rich Compose

- `ComposeMode` (PlainText / Markdown / Html) on `ComposeModel`; rich messages are sent as `multipart/alternative` by `MimeMessageBuilder` whenever `HtmlBody` is present
- `MarkdownService` (Markdig) renders Markdown to HTML; `RichTextDocumentConverter` converts FlowDocument ↔ HTML/Markdown with block kinds tracked via `Paragraph.Tag`
- `MarkdownEditing` — pure, unit-tested text operations behind the Markdown formatting commands, applied through `TextBox.SelectedText` so each toggle is one undo unit
- **Never replace `RichTextBox.Document`** — WPF's automation peer binds to the original document's text container and never rebinds; all loads go through `RichTextDocumentConverter.LoadInto`. Guarded by `ComposeUiaTextPatternTests`, which asserts UIA TextPattern content through real mode switches
- `FormattingListWindow` — the Show Formatting list; shares its state-reading code with the spoken announcement
- `MarkdownPreviewWindow` — focusable WebView2 preview window opened by F8 in Markdown mode; JS key relay returns F6/Escape/Ctrl+W to WPF; links open in the default browser; rich CSS with dark-mode support via `prefers-color-scheme`
- `AnnounceFormattingWhileNavigating` config key (default on): fires an announcement when the caret moves to a paragraph with a different block type in HTML mode; suppressed during mode switches and programmatic loads
- Auto-save: `ComposeViewModel.AutoSaveAsync` driven by a `DispatcherTimer`; config keys `AutoSaveDrafts` and `AutoSaveIntervalSeconds` (clamped 30–600); failure announcements arm once per failure streak
- New Settings → General → Composing group (auto-save toggle, interval, `DefaultComposeMode`)
- New Settings → Screen Reader Announcements → Announce formatting while navigating in HTML compose

### Microsoft Graph Support

- `IMailService` now abstracts backend operations to support IMAP, Graph, and future backends
- `MailServiceRouter` routes method calls to the appropriate backend based on account type
- `GraphMailService` — new Graph backend implementation (read-only for v0.7.2)
- Account dialog UX now detects account type and shows/hides protocol-specific settings accordingly

### Sync Progress Reporting

- `ISyncService.SyncProgressChanged` event fires after each folder completes with `(completedFolders, totalFolders)`
- `MainViewModel` now throttles status text updates to every 10 folders to prevent excessive screen reader announcements
- Announcements respect `ConfigModel.AnnounceStatus` user preference

---

## Known Limitations

### Rich Compose

- **Drafts and templates always reopen in Plain Text.** The editing mode is not yet round-tripped through the mail server, so a draft written in HTML reopens as plain text (the formatted content is preserved in the sent form, but further editing starts from the text representation).
- **No underline in Markdown mode.** Markdown has no underline syntax; use HTML mode when underline matters.
- **Images, tables, fonts, and colors** are not yet available in the HTML editor.

### Microsoft Graph Backend

- **Read-only in v0.7.2.** Fetching and viewing messages is supported; sending, drafts, and mutations are not yet implemented.
- **No direct Microsoft 365 integration for shared mailboxes.** Only personal accounts are supported at this time.

---
