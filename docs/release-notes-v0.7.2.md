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
- **Markdown** — write Markdown source in the familiar text editor; QuickMail renders it to formatted HTML when the message is sent. Press **F8** to open a rendered preview in a separate window where screen readers can browse the output as a web page.
- **HTML** — a rich text editor with real formatting: bold, italic, underline, strikethrough, three heading levels, bullet and numbered lists, and links.

Switch modes at any time with **Ctrl+Shift+1/2/3**, the **View** menu, or the mode selector in the compose status row. Content converts between modes automatically; switching from a rich mode down to Plain Text asks for confirmation first, because formatting would be lost. Messages composed in Markdown or HTML are sent with both a formatted HTML part and a plain text part, so every recipient's mail app can display them.

The HTML editor is a native Windows rich edit control, not an embedded browser — screen readers stay in their normal edit cursor with no virtual cursor or browse mode. Settings allow you to control announcement of format information when cursoring. Also use CTRL+Shift+t to review fomratting information in a list at any point or ctrl+t to have it announced.

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
| Open preview | `F8` (Markdown and HTML modes) |

One exception: Markdown has no underline syntax, so underline is available in HTML mode only — invoking it in Markdown explains this aloud.

### Heading Commands Apply to the Selected Paragraph

### Announce Formatting While Navigating (HTML Mode)

In HTML compose mode, QuickMail now announces the block type whenever the caret moves to a paragraph with a different type — for example, moving onto a heading line announces "Heading 2" without needing to press `Ctrl+T`. This is on by default and can be turned off under **Settings → Screen Reader Announcements → Announce formatting while navigating in HTML compose**.

This announcement is HTML-mode-only. Markdown mode does not produce automatic formatting announcements, since the raw Markdown syntax is already present in the text.

### Preview Window (F8)

In Markdown or HTML mode, pressing **F8** opens a dedicated preview window that renders your message as formatted HTML. The preview window is fully focusable, so screen readers switch into browse mode and you can read the rendered output exactly as a recipient would see it. Links open in your default browser. Press **Escape** or **Ctrl+W** to close the preview and return focus to the editor. Pressing **F8** again while the preview is open closes it.

In HTML mode the editor is a rich text control, so screen readers read it in edit mode. The preview window is the way to hear the composed message as a recipient would — as a fully-rendered web page — without leaving the compose window.

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

### Valid, Accessible HTML Email

Messages composed in Markdown or HTML mode are now sent as a complete, valid HTML5 document built to WCAG 2.2 AA structure:

- The document declares the **message language** and carries the **subject as its title**, so recipients' mail apps and screen readers identify the message correctly.
- **Image descriptions (alt text) are preserved** end to end — what you write in `![description](address)` is exactly what a screen reader user receives.
- **Table header cells are sent as real headers** with column scope, so screen readers announce the column header while moving through table cells.
- Task-list syntax (`- [x] done`) is kept as literal, readable text instead of being rendered as checkbox form controls, which most mail apps strip and which are not accessible in email.

### Lossless Markdown ↔ HTML Mode Switching

Switching a Markdown message into HTML mode and back now returns exactly what you wrote. Tables (with header rows and column alignment), images (with their descriptions), headings at all six levels, fenced code blocks with their language, multi-paragraph quotes, nested lists, and exact link addresses all survive the trip in both directions. Images appear in the HTML editor as their description text, so the description stays readable and editable.

### Nested Lists (Tab and Shift+Tab)

In both HTML and Markdown modes, you can now create and navigate multi-level lists using the keyboard:

- **Tab** while the cursor is on a list item increases its indent level, creating a sub-list.
- **Shift+Tab** decreases the indent level. In HTML mode, pressing **Shift+Tab** on a top-level list item removes it from the list entirely, returning it to normal text.

Tab and Shift+Tab on a non-list line move focus between fields as usual — the indentation behavior only activates when the cursor is inside a list item.

In HTML mode, screen readers hear the new level announced immediately — for example, "Bullet list item, level 2." The block type label also includes the level when navigating by caret, so moving between levels is audible without pressing **Ctrl+T**.

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
- **Heading formatting bled into the blank line below the selected text.** WPF places structural element-boundary positions between paragraphs. When a selection ended in that gap — for example after pressing Shift+Down to select a line — WPF attributed the position to the next paragraph, so the heading was applied to both the selected line and the blank line below it. The boundary check now correctly excludes any position at or before the start of the next paragraph's content.
- **Escape closed the whole compose window from open menus and dropdowns.** Escape now closes the open menu, combo dropdown, or address autocomplete first; only when nothing transient is open does it close the window.
- **Formatting announcements were silenced by focus changes.** Invoking a formatting command from the menu restored focus to the editor, and the focus speech overrode the result announcement — all you heard was "message body." Formatting feedback is now timed to land after focus settles.
- **Duplicate sync progress announcements.** The status bar text and explicit screen reader announcements were both being read aloud during sync, creating redundant chatter. Now only the explicit announcements (which respect user settings) are spoken, eliminating duplicates.
- **Spaces between adjacent formatted spans were silently deleted.** Switching a Markdown message like `*a* *b*` into HTML mode dropped the space between the styled words, merging them. Whitespace between inline elements is now preserved with correct HTML semantics (collapsed to a single space, never removed).
- **Markdown tables were destroyed by a mode switch.** Entering HTML mode flattened tables to tab-separated text and the table never came back. Tables are now real tables in the HTML editor and convert back to pipe-format Markdown, headers and alignment intact.
- **Images lost their address on a mode switch.** `![alt](address)` kept only the description text; the image address was discarded. Both now round-trip.
- **Headings 4–6 were demoted to heading 3** when switching from Markdown to HTML mode. All six levels are now preserved.
- **Code block language was dropped** (` ```csharp ` became a plain fence) and **link addresses were rewritten** by URL normalization. Both are now preserved character for character.
- **Multi-paragraph quotes split into separate quotes** on each round trip. Consecutive quoted paragraphs now stay one quote.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Rich Compose

- `ComposeMode` (PlainText / Markdown / Html) on `ComposeModel`; rich messages are sent as `multipart/alternative` by `MimeMessageBuilder` whenever `HtmlBody` is present
- `MarkdownService` (Markdig) renders Markdown to HTML with an explicit, bounded extension set (pipe tables, strikethrough, auto-links; raw HTML disabled, task lists excluded); `WrapDocument` emits a full HTML5 document with doctype, `lang`, charset, and the subject as `<title>`
- `RichTextDocumentConverter` converts FlowDocument ↔ HTML/Markdown with structure tracked on element tags: `Paragraph.Tag` for headings 1–6, code blocks (`PRE:lang`), hr, and blockquote; `TableCell.Tag` for header cells and column alignment; `Run.Tag` for image src (alt text is the run text); `Hyperlink.Tag` for the author's verbatim href (script/data schemes excluded)
- Round-trip fidelity is locked in by `MarkdownRoundTripTests`: an exact Markdown → HTML → FlowDocument → Markdown corpus, plus XML well-formedness and WCAG structure checks on the full sent document
- `MarkdownEditing` — pure, unit-tested text operations behind the Markdown formatting commands, applied through `TextBox.SelectedText` so each toggle is one undo unit
- **Never replace `RichTextBox.Document`** — WPF's automation peer binds to the original document's text container and never rebinds; all loads go through `RichTextDocumentConverter.LoadInto`. Guarded by `ComposeUiaTextPatternTests`, which asserts UIA TextPattern content through real mode switches
- `FormattingListWindow` — the Show Formatting list; shares its state-reading code with the spoken announcement
- `MarkdownPreviewWindow` — focusable WebView2 preview window opened by F8 in Markdown or HTML mode; JS key relay returns F6/Escape/Ctrl+W to WPF; links open in the default browser; rich CSS with dark-mode support via `prefers-color-scheme`
- `AnnounceFormattingWhileNavigating` config key (default on): fires an announcement when the caret moves to a paragraph with a different block type in HTML mode; suppressed during mode switches and programmatic loads
- Auto-save: `ComposeViewModel.AutoSaveAsync` driven by a `DispatcherTimer`; config keys `AutoSaveDrafts` and `AutoSaveIntervalSeconds` (clamped 30–600); failure announcements arm once per failure streak
- New Settings → General → Composing group (auto-save toggle, interval, `DefaultComposeMode`)
- New Settings → Screen Reader Announcements → Announce formatting while navigating in HTML compose
- `MarkdownEditing.IndentListItem` / `DedentListItem` — pure text operations for Tab/Shift+Tab in Markdown mode (2-space indentation per level)
- `RichBodyBox_PreviewKeyDown` intercepts Tab/Shift+Tab on list items and calls `EditingCommands.IncreaseIndentation` / `DecreaseIndentation`; suppresses the navigation announcement during the command and replaces it with a single Result announce
- `BlockTypeLabel` / `ListDepthOf` helpers: list item labels now include the nesting depth ("Bullet list item, level 2") so navigation between levels is audible everywhere

### Sync Progress Reporting

- `ISyncService.SyncProgressChanged` event fires after each folder completes with `(completedFolders, totalFolders)`
- `MainViewModel` now throttles status text updates to every 10 folders to prevent excessive screen reader announcements
- Announcements respect `ConfigModel.AnnounceStatus` user preference

---

## Known Limitations

### Rich Compose

- **Drafts and templates always reopen in Plain Text.** The editing mode is not yet round-tripped through the mail server, so a draft written in HTML reopens as plain text (the formatted content is preserved in the sent form, but further editing starts from the text representation).
- **No underline in Markdown mode.** Markdown has no underline syntax; use HTML mode when underline matters.
- **Images and tables cannot be created in the HTML editor** — author them in Markdown mode; they display correctly and survive switches into HTML mode and back. **Fonts and colors** are not yet available in either rich mode.

---
