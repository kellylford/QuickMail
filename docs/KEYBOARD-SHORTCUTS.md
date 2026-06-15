# Keyboard Shortcuts Reference

## Registered shortcut table (MainWindow)

| Key | Command ID | Title |
|---|---|---|
| Ctrl+0 | *(hardcoded)* | Focus toolbar |
| Ctrl+1 | *(hardcoded)* | Focus account list (or tab 1 when tabs are open) |
| Ctrl+2 / Ctrl+Y | `view.focusFolders` | Focus Folder Tree (or tab 2 when tabs are open) |
| Ctrl+3 | *(hardcoded)* | Focus message list (or tab 3 when tabs are open) |
| Ctrl+4–8 | *(hardcoded)* | Jump to tab 4–8 (when tabs are open) |
| Ctrl+9 | *(hardcoded/registry)* | Jump to last tab (tabs open) or `view.focusStatusBar` (no tabs) |
| Ctrl+Alt+1 | `view.focusAccounts` | Focus Account List (always) |
| Ctrl+Alt+2 | *(hardcoded)* | Focus Folder Tree (always) |
| Ctrl+Alt+3 | `view.focusMessages` | Focus Message List (always) |
| F6 / Shift+F6 | *(hardcoded)* | Cycle panes |
| Escape | *(hardcoded)* | Close reading pane |
| Ctrl+Shift+P | *(hardcoded)* | Command Palette |
| Ctrl+N | `mail.new` | New Message |
| Ctrl+R | `mail.reply` | Reply |
| Ctrl+Shift+R | `mail.replyAll` | Reply All |
| Ctrl+F | `mail.forward` | Forward |
| Delete | `mail.delete` | Delete |
| Ctrl+Q | `mail.markRead` | Mark as Read |
| F5 | `mail.refresh` | Refresh |
| Ctrl+Shift+E | `mail.emptyTrash` | Empty Trash |
| Ctrl+Shift+V | `view.openViewMenu` | Open View Menu |
| Ctrl+Shift+F | `view.searchFolders` | Search Folders… |
| Ctrl+Shift+S | `view.search` | Search Messages… |
| Ctrl+Shift+G | `contacts.grabAddresses` | Grab Addresses from Message |
| Ctrl+Shift+B | `contacts.openAddressBook` | Address Book |
| F1 | `help.userGuide` | Open User Guide |
| *(unassigned)* | `settings.toggleCustomAnnouncements` | Toggle Custom Announcements |
| Ctrl+A | `mail.selectAll` | Select All Messages (message list focus only) |
| K | `mail.toggleFlag` | Toggle Flag |
| Ctrl+Shift+K | `mail.pickFlag` | Pick Flag… |
| *(unassigned)* | `mail.openFlagManager` | Manage Flags… |
| Shift+, | `mail.jumpToFirstInGroup` | First Message in Group |
| Shift+. | `mail.jumpToLastInGroup` | Last Message in Group |
| *(unassigned)* | `mail.acceptInvite` | Accept Invitation |
| *(unassigned)* | `mail.declineInvite` | Decline Invitation |
| *(unassigned)* | `mail.tentativeInvite` | Tentatively Accept Invitation |
| *(unassigned)* | `help.keyboardTutorial` | Keyboard Tutorial |
| Ctrl+Shift+T | `view.focusTabs` | Focus Tab Strip |
| Alt+Enter | `view.showProperties` | View Properties |
| Ctrl+Tab | `tabs.next` | Next Tab |
| Ctrl+Shift+Tab | `tabs.previous` | Previous Tab |
| Ctrl+W | `tabs.close` | Close Tab |
| Ctrl+Shift+` | `tabs.list` | Tab List… |
| *(unassigned)* | `tabs.closeOthers` | Close Other Tabs |
| *(unassigned)* | `tabs.moveLeft` | Move Tab Left |
| *(unassigned)* | `tabs.moveRight` | Move Tab Right |
| *(unassigned)* | `tabs.promote` | Move Tab to New Window |
| *(unassigned)* | `mail.openInNewTab` | Open in New Tab |
| *(unassigned)* | `mail.openInWindow` | Open in New Window |

## Compose Window

**Shortcuts** (Alt+S, Ctrl+Enter, Ctrl+S, Ctrl+Shift+A, F7, Shift+F7, Alt+Y, Alt+U, Alt+M, Escape) are registered or hardcoded in `ComposeWindow.xaml.cs`. Registry-based ones appear in the compose window's command palette (`Ctrl+Shift+P`) but are **not** user-customisable via the Settings dialog. The main window's `CommandRegistry` and `hotkeys.json` do not include compose commands. `Ctrl+Enter` is hardcoded (like `Ctrl+Shift+P`) as a second send gesture so it does not create a duplicate "Send Message" entry in the palette.

**Menu bar**: `ComposeWindow` has a standard menu bar (File / Edit / View / Format / Tools). It is not a tab stop (reached with Alt or F10, per platform convention). Every item dispatches to the same handler or command as its keyboard shortcut, and `InputGestureText` must match the registered default gesture. **Top-level menus are never disabled** (Windows standard — a disabled top-level menu is skipped by arrow navigation, stranding its items); availability is expressed per item. Format items gray out in Plain Text mode only, and because WPF skips disabled items during arrow navigation, opening the Format menu in Plain Text announces a Hint explaining why. The View menu's mode items get radio-style check marks synced in `SyncModeSelector`. The window's `PreviewKeyDown` steps aside on Escape when a menu, combo dropdown, or the autocomplete popup is open so transient UI can close itself.

**Formatting** works in both rich modes. HTML mode applies real formatting to the RichTextBox; Markdown mode inserts the equivalent syntax through `MarkdownEditing` (`Helpers/MarkdownEditing.cs` — pure, unit-tested text operations applied via `TextBox.SelectedText` so each toggle is one undo unit). Exception: underline has no Markdown form and the Markdig pipeline uses `DisableHtml()`, so underline in Markdown announces that it requires HTML mode. Formatting result announcements go through `ComposeWindow.AnnounceFormatting`, which defers to `DispatcherPriority.ApplicationIdle` and interrupts — menu invocations restore focus to the editor on close, and an immediate announcement would be silenced by the screen reader's focus speech.

**Window title** is `"{subject or kind} - {mode} - QuickMail"` (e.g. "Lunch Friday - HTML - QuickMail") so the taskbar and Alt+Tab identify the message and editing format. `WindowTitle` is notified on both Subject and CurrentMode changes.

**Draft autosave**: compose windows auto-save dirty composes as drafts on a `DispatcherTimer` (config keys `AutoSaveDrafts`, default on, and `AutoSaveIntervalSeconds`, default 120, clamped 30–600; both editable in Settings → General → Composing). `ComposeViewModel.AutoSaveAsync` is quiet by design: success only updates the visual `AutoSaveText` status ("Auto-saved 3:42 PM") with **no announcement**; a failure raises `AutoSaveFailed` once (announced with `AnnouncementCategory.Status`) and re-arms after the next success. Autosave skips template edits, untouched composes, and composes with no recipient/subject/body/attachment. The palette command `compose.announceAutoSave` ("Announce Last Auto-Save") speaks the last autosave time on demand.

**Compose modes** (`ComposeMode`: PlainText / Markdown / Html) are switched with `Ctrl+Shift+1/2/3`, the View menu, or the mode ComboBox in the status row. Plain Text and Markdown edit in `BodyBox` (TextBox); HTML mode edits in `RichBodyBox` — a native WPF `RichTextBox`, deliberately **not** WebView2 `contenteditable`, so screen readers stay in their normal edit cursor with no virtual cursor.

**Never replace `RichTextBox.Document` — enforced.** WPF's `RichTextBoxAutomationPeer` binds its UIA TextPattern to the text container of the document present at peer creation and never rebinds, even for freshly created peers. After a `Document` assignment, screen readers permanently read the stale (empty) original document instead of what is on screen — the editor goes completely silent. All content loads must mutate the existing document via `RichTextDocumentConverter.LoadInto(doc, html)`. Regression-tested in `ComposeUiaTextPatternTests`, which asserts the UIA TextPattern text through real mode switches. Formatting commands (Ctrl+B/I/U, Ctrl+Shift+X strikethrough, Ctrl+Alt+1/2/3 headings, Ctrl+Shift+L/N lists, Ctrl+L insert link, Ctrl+Space clear formatting, Ctrl+T announce formatting state, Ctrl+Shift+T show formatting in a browsable list — `FormattingListWindow`) are HTML-mode-only via `IsAvailable`; `F8` opens the preview window (`MarkdownPreviewWindow`) in both Markdown and HTML modes — a fully focusable WebView2 in a separate window so screen readers can browse the rendered output as a web page. Conversions run through `IMarkdownService` (Markdig with an explicit bounded pipeline: pipe tables, strikethrough, auto-links, raw HTML disabled, task lists excluded for WCAG) and `RichTextDocumentConverter` (FlowDocument ↔ HTML/Markdown; headings 1–6, pre with fence language, hr, and blockquote tracked via `Paragraph.Tag`; table header cells and alignment via `TableCell.Tag`; image src via `Run.Tag` with alt text as run text; verbatim hrefs via `Hyperlink.Tag`). The Markdown → HTML → FlowDocument → Markdown round trip must stay lossless — `MarkdownRoundTripTests` holds an exact-equality corpus plus well-formedness/WCAG-structure checks on the wrapped document (`WrapDocument` emits a full HTML5 document: doctype, `lang`, charset, subject as title). Rich-mode messages are sent as `multipart/alternative` by `MimeMessageBuilder` whenever `ComposeModel.HtmlBody` is non-empty. Every formatting action announces its result ("Bold on", "Heading 2") via `AccessibilityHelper.Announce` with `AnnouncementCategory.Result`. The default mode for new composes is `DefaultComposeMode` in `config.ini` (plain/markdown/html); drafts reopen in the mode they were saved in (stored as `X-QuickMail-Compose-Mode` MIME header); templates always reopen in plain text. `RichTextDocumentConverter.LoadInto` accepts both HTML fragments and full HTML documents (the `<html>` and `<body>` wrappers are treated as transparent block containers), so the full wrapped document from `detail.HtmlBody` can be loaded directly into the rich editor when restoring an HTML draft.
