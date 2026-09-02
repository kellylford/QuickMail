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
| Escape | `view.closeContactMail` | Close Contact Mail Results — dispatched only when no earlier Escape case claims the key (reading pane, calendar, tab mode) and the search box does not have focus |
| Ctrl+Shift+P | *(hardcoded)* | Command Palette |
| Ctrl+N | `mail.new` | New Message |
| Ctrl+R | `mail.reply` | Reply |
| Ctrl+Shift+R | `mail.replyAll` | Reply All |
| Ctrl+F | `mail.forward` | Forward |
| Delete | `mail.delete` | Delete |
| Ctrl+Shift+M | `mail.archive` | Move to Archive (the account's Archive folder) |
| Ctrl+Q | `mail.markRead` | Mark as Read |
| F5 | `mail.refresh` | Refresh |
| Ctrl+Shift+E | `mail.emptyTrash` | Empty Trash |
| *(unassigned)* | `mail.sendOutboxNow` | Send Outbox Now — tries every queued message and draft right away, including ones marked Failed (#637) |
| Ctrl+Shift+W | `mail.toggleWatch` | Watch Conversation — watches the selected message's conversation, or unwatches it if already watched. On a Conversations group header it acts on that group; on a From/To group header it is unavailable (a sender group spans many conversations) |
| *(unassigned)* | `mail.watchManager` | Watched Conversations… (review, rename, stop watching) |
| *(unassigned)* | `view.filterWatched` | Show Watched Conversations Only |
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
| Alt+Down | `mail.nextUnread` | Next Unread Message — the nearest unread message below the current one, in the flat list or a group tree (message-area focus only) |
| Alt+Up | `mail.previousUnread` | Previous Unread Message — the nearest unread message above the current one (message-area focus only) |
| Shift+, | `mail.jumpToFirstInGroup` | First Message in Group |
| Shift+. | `mail.jumpToLastInGroup` | Last Message in Group |
| *(unassigned)* | `mail.acceptInvite` | Accept Invitation |
| *(unassigned)* | `mail.declineInvite` | Decline Invitation |
| *(unassigned)* | `mail.tentativeInvite` | Tentatively Accept Invitation |
| *(unassigned)* | `help.keyboardTutorial` | Keyboard Tutorial |
| Ctrl+Shift+T | `view.focusTabs` | Focus Tab Strip |
| Alt+A | `view.focusAttachments` | Focus Attachment List (of the open message) |
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
| *(unassigned)* | `theme.manager.open` | Manage Themes |
| *(unassigned)* | `theme.next` | Next Theme |
| *(unassigned)* | `theme.previous` | Previous Theme |
| *(unassigned)* | `theme.apply.{id}` | Theme: [name] — one per available theme |
| *(unassigned)* | `view.density.comfortable` | Density: Comfortable |
| *(unassigned)* | `view.density.compact` | Density: Compact |
| *(unassigned)* | `view.rowFields` | Message List Fields… |
| *(unassigned)* | `folder.expand` | Expand Folder — the selected folder and every subfolder in it |
| *(unassigned)* | `folder.collapse` | Collapse Folder — the selected folder and every subfolder in it |
| *(unassigned)* | `folder.expandAll` | Expand All Folders |
| *(unassigned)* | `folder.collapseAll` | Collapse All Folders — account headers included |

The folder tree's own Right and Left arrow keys expand and collapse one level. They are
in-control navigation — the same kind as the arrow keys inside a list box — and are not registered
commands. The four `folder.expand*` / `folder.collapse*` commands above are the bulk equivalents
(#590): they act on the whole branch, or on the whole tree.

## Message List Fields Window

Opened from **View → Message List Fields…** or the command palette (`view.rowFields`). Left
unassigned deliberately: like the Flag and Theme managers it is a settings surface, and the
remaining `Ctrl+Shift` chords are worth more to per-message actions.

The window is **modeless**, so you can leave it open and arrow the message list behind it to hear
each change immediately. It has no OK/Cancel — every change saves as you make it.

| Key | Action |
|-----|--------|
| `F6` / `Shift+F6` | Cycle panes: Row type → Fields → Field options → Spoken preview → Buttons |
| `Up` / `Down` | Move between fields |
| `Home` / `End` | First / last field |
| *letter* | Jump to the next field starting with that letter; repeat to cycle matches |
| `Space` | Turn the focused field on or off |
| `Alt+Up` / `Alt+Down` | Move the focused field earlier or later in the spoken order |
| `Ctrl+Shift+P` | Window-local command palette (move, toggle, reset, labels, close) |
| `Escape` | Close |

Each row **is** a check box — the real control, so its role and checked state are reported by the
platform and `Space` toggles natively. That rules out a `ListBox`, whose `ListBoxItem` wrapper
carries a second copy of the row's name (see `Views/FieldCheckList.cs`); the cost is that
`Home`/`End` and first-letter navigation, which a `ListBox` provides for free, are implemented on
that control. First-letter uses QuickMail's own accumulator (`TypeAheadPrefixTracker`), not WPF
`TextSearch`, which only works on a `Selector` — so this list is **not** a `TypeAheadWiringTests`
site.

## Watched Conversations Window

Opened from **Message → Watched Conversations…** or the command palette (`mail.watchManager`).
Unassigned by default, for the same reason as the Message List Fields window.

**Modeless**, so it can stay open while you work behind it, and because it holds an editable field
over the reading pane's live WebView2 — the combination the modal-dialog rules in `CLAUDE.md`
forbid.

| Key | Action |
|-----|--------|
| `Enter` | Go to the selected conversation (opens Watched Conversations and selects its newest message) |
| `Delete` | Stop watching the selected conversation |
| *letter* | Jump to the next watch whose label starts with that letter |
| `Alt+G` / `Alt+R` / `Alt+S` / `Alt+C` | Go to / Rename / Stop watching / Close |
| `F6` / `Shift+F6` | Cycle panes: list → rename panel (while renaming) → buttons |
| `Ctrl+Shift+P` | Window-local command palette |
| `Escape` | Abandon a rename in progress, otherwise close the window |

**No button carries an access-key underscore.** The list has first-letter type-ahead, and WPF fires
a bare mnemonic without `Alt` when focus is not in a text field — that is how `c` closed the folder
picker (issue #418). The `Alt+` combinations above are wired explicitly in code-behind instead, and
guarded on `Keyboard.Modifiers == ModifierKeys.Alt` exactly, so AltGr (which reports as Ctrl+Alt)
still reaches type-ahead.

Renaming changes a watch's **label only**. Matching is on the normalized subject and is not
editable, because changing it would silently change which messages the watch collects.

## Compose Window

**Shortcuts** (Alt+S, Ctrl+Enter, Ctrl+S, Ctrl+Shift+A, Alt+A, F7, Ctrl+F7, Ctrl+Shift+F7, Alt+Y, Alt+U, Alt+M, Escape) are registered or hardcoded in `ComposeWindow.xaml.cs`. Registry-based ones appear in the compose window's command palette (`Ctrl+Shift+P`) but are **not** user-customisable via the Settings dialog. The main window's `CommandRegistry` and `hotkeys.json` do not include compose commands. `Ctrl+Enter` is hardcoded (like `Ctrl+Shift+P`) as a second send gesture so it does not create a duplicate "Send Message" entry in the palette.

**`Alt+A` focuses the attachment list** (`compose.focusAttachments`), the same gesture as `view.focusAttachments` in the main window and `window.focusAttachments` in a `MessageWindow` — one key reaches attachments wherever a message is on screen, whether you are reading it or writing it (issue #439). Adding files stays on `Ctrl+Shift+A`. The compose attachment list is collapsed when the draft has none, so the command announces "No attachments." rather than moving focus to a hidden control. Selecting the first item is done in the list's `GotKeyboardFocus` handler, not in the command, so `Tab` and `Alt+A` land in the same place. The Check Spelling dialog also binds `Alt+A` (Add to Dictionary); it is a separate modeless window, so the two never compete.

**Plain Enter never sends.** The Send button is deliberately **not** `IsDefault`. A default button makes WPF route any unhandled Enter to it, so every control that does not consume Enter itself — the From combo, the compose-mode combo, the Subject box, the attachment list — silently becomes a send gesture. Choosing a sending account with arrow-then-Enter used to send the half-written message and disclose its contents (issue #201). Send is reachable by `Alt+S`, `Ctrl+Enter`, or `Enter`/`Space` with the Send button focused. Do not re-add `IsDefault` to the compose Send button; `ComposeWindowEnterSendGuardTests` asserts it stays off.

**Enter on the From combo confirms the account instead.** `<account> used as From address` is announced as `AnnouncementCategory.Result` (so it respects **Announce action results**) when the choice *settles* — `Enter` on the closed combo, the dropdown closing on a different account, or focus leaving the combo after a change. It is deliberately **not** announced per `SelectionChanged`: arrowing through a closed combo changes the selection on every keystroke and the screen reader already reads each account name, so per-change announcing speaks twice per arrow press. `Enter` announces even when the account did not change, because the user asked explicitly; the other two paths stay silent unless something changed. Guarded by `ComposeWindowFromAccountAnnouncementTests`.

**Spell checking**: `F7` (`compose.checkSpelling`) opens the **Check Spelling dialog** — a modeless "Spelling" window that reviews the body and then the subject. Each error is announced as "Not in dictionary: \<word\>" with focus on the suggestions list (first suggestion selected). In-dialog keys: `Alt+C` Change (also the Enter default), `Alt+L` Change All, `Alt+I` Ignore, `Alt+G` Ignore All, `Alt+A` Add to Dictionary (persists to `custom.lex` in the profile directory), `Alt+R` Read in Context, `Alt+T` / `Alt+S` / `Alt+N` focus Change-to / Suggestions / context, `F6` cycles panes, `Ctrl+Shift+P` opens the dialog's palette, `Escape` cancels. Inline navigation is `Ctrl+F7` / `Ctrl+Shift+F7` (`compose.nextMisspelling` / `compose.prevMisspelling`; these were `F7` / `Shift+F7` before the dialog took the classic F7 binding — user overrides in `hotkeys.json` are unaffected). `Alt+F7` repeats the current spelling announcement; `Alt+1/2/3` accept an inline suggestion.

**Menu bar**: `ComposeWindow` has a standard menu bar (File / Edit / View / Format / Tools). It is not a tab stop (reached with Alt or F10, per platform convention). Every item dispatches to the same handler or command as its keyboard shortcut, and `InputGestureText` must match the registered default gesture. **Top-level menus are never disabled** (Windows standard — a disabled top-level menu is skipped by arrow navigation, stranding its items); availability is expressed per item. Format items gray out in Plain Text mode only, and because WPF skips disabled items during arrow navigation, opening the Format menu in Plain Text announces a Hint explaining why. The View menu's mode items get radio-style check marks synced in `SyncModeSelector`. The window's `PreviewKeyDown` steps aside on Escape when a menu, combo dropdown, or the autocomplete popup is open so transient UI can close itself.

**Formatting** works in both rich modes. HTML mode applies real formatting to the RichTextBox; Markdown mode inserts the equivalent syntax through `MarkdownEditing` (`Helpers/MarkdownEditing.cs` — pure, unit-tested text operations applied via `TextBox.SelectedText` so each toggle is one undo unit). Exception: underline has no Markdown form and the Markdig pipeline uses `DisableHtml()`, so underline in Markdown announces that it requires HTML mode. Formatting result announcements go through `ComposeWindow.AnnounceFormatting`, which defers to `DispatcherPriority.ApplicationIdle` and interrupts — menu invocations restore focus to the editor on close, and an immediate announcement would be silenced by the screen reader's focus speech.

**Window title** is `"{subject or kind} - {mode} - QuickMail"` (e.g. "Lunch Friday - HTML - QuickMail") so the taskbar and Alt+Tab identify the message and editing format. `WindowTitle` is notified on both Subject and CurrentMode changes.

**Draft autosave**: compose windows auto-save dirty composes as drafts on a `DispatcherTimer` (config keys `AutoSaveDrafts`, default on, and `AutoSaveIntervalSeconds`, default 120, clamped 30–600; both editable in Settings → General → Composing). `ComposeViewModel.AutoSaveAsync` is quiet by design: success only updates the visual `AutoSaveText` status ("Auto-saved 3:42 PM") with **no announcement**; a failure raises `AutoSaveFailed` once (announced with `AnnouncementCategory.Status`) and re-arms after the next success. Autosave skips template edits, untouched composes, and composes with no recipient/subject/body/attachment. The palette command `compose.announceAutoSave` ("Announce Last Auto-Save") speaks the last autosave time on demand.

**Offline (#637)**: no new compose gestures. `Ctrl+S` and auto-save fall back to the local Outbox when the server cannot be reached ("Draft saved on this computer. It will upload when you're online.", `Result`; auto-save says "Auto-save is keeping your draft on this computer until you're online." once, `Status`). `Alt+S` queues the message and closes the window when the server never answered ("Message queued. It will be sent when you're online.", `Result`); a server that answered and refused still fails in the window. `Escape` with unsaved changes → Save closes the window once the draft is kept locally; it stays open only when `ComposeViewModel.LastSaveOutcome` is `Failed` (nowhere took it — `--online` mode, or a broken store). The main window's `mail.sendOutboxNow` drains the queue on demand.

**Compose modes** (`ComposeMode`: PlainText / Markdown / Html) are switched with `Ctrl+Shift+1/2/3`, the View menu, or the mode ComboBox in the status row. Plain Text and Markdown edit in `BodyBox` (TextBox); HTML mode edits in `RichBodyBox` — a native WPF `RichTextBox`, deliberately **not** WebView2 `contenteditable`, so screen readers stay in their normal edit cursor with no virtual cursor.

**Never replace `RichTextBox.Document` — enforced.** WPF's `RichTextBoxAutomationPeer` binds its UIA TextPattern to the text container of the document present at peer creation and never rebinds, even for freshly created peers. After a `Document` assignment, screen readers permanently read the stale (empty) original document instead of what is on screen — the editor goes completely silent. All content loads must mutate the existing document via `RichTextDocumentConverter.LoadInto(doc, html)`. Regression-tested in `ComposeUiaTextPatternTests`, which asserts the UIA TextPattern text through real mode switches. Formatting commands (Ctrl+B/I/U, Ctrl+Shift+X strikethrough, Ctrl+Alt+1/2/3 headings, Ctrl+Shift+L/N lists, Ctrl+L insert link, Ctrl+Space clear formatting, Ctrl+T announce formatting state, Ctrl+Shift+T show formatting in a browsable list — `FormattingListWindow`) are HTML-mode-only via `IsAvailable`; `F8` opens the preview window (`MarkdownPreviewWindow`) in both Markdown and HTML modes — a fully focusable WebView2 in a separate window so screen readers can browse the rendered output as a web page. Conversions run through `IMarkdownService` (Markdig with an explicit bounded pipeline: pipe tables, strikethrough, auto-links, raw HTML disabled, task lists excluded for WCAG) and `RichTextDocumentConverter` (FlowDocument ↔ HTML/Markdown; headings 1–6, pre with fence language, hr, and blockquote tracked via `Paragraph.Tag`; table header cells and alignment via `TableCell.Tag`; image src via `Run.Tag` with alt text as run text; verbatim hrefs via `Hyperlink.Tag`). The Markdown → HTML → FlowDocument → Markdown round trip must stay lossless — `MarkdownRoundTripTests` holds an exact-equality corpus plus well-formedness/WCAG-structure checks on the wrapped document (`WrapDocument` emits a full HTML5 document: doctype, `lang`, charset, subject as title). Rich-mode messages are sent as `multipart/alternative` by `MimeMessageBuilder` whenever `ComposeModel.HtmlBody` is non-empty. Every formatting action announces its result ("Bold on", "Heading 2") via `AccessibilityHelper.Announce` with `AnnouncementCategory.Result`. The default mode for new composes is `DefaultComposeMode` in `config.ini` (plain/markdown/html); drafts reopen in the mode they were saved in (stored as `X-QuickMail-Compose-Mode` MIME header); templates always reopen in plain text. `RichTextDocumentConverter.LoadInto` accepts both HTML fragments and full HTML documents (the `<html>` and `<body>` wrappers are treated as transparent block containers), so the full wrapped document from `detail.HtmlBody` can be loaded directly into the rich editor when restoring an HTML draft.

## Appointment editor date and time fields

`EventEditorWindow` and `GoToDateWindow` enter dates, times and the repeat interval through `Controls/DateTimeField.cs` — a `TextBox` subclass that steps with the arrow keys. Gestures per field kind:

| Gesture | Date field | Time field | Number field |
| --- | --- | --- | --- |
| Up / Down | 1 day | 15 minutes, snapped to the quarter hour | 1 |
| Ctrl+Up / Ctrl+Down | 1 day | 1 minute | 1 |
| Shift+Up / Shift+Down | 1 week | 1 hour | 5 |
| PageUp / PageDown | 1 month | 1 hour | 10 |
| Ctrl+PageUp / Ctrl+PageDown | 1 year | 1 day | 10 |

These are **deliberately not registered in `CommandRegistry`**. They are in-field editing keys, like the arrow keys inside any text box, not application commands — registering them would put ten entries in the command palette that do nothing anywhere else in the app. This is a considered exception to the "register first, hardcode never" rule and was signed off with the feature.

Stepping and free-text parsing live in `Helpers/DateTimeFieldParser.cs` (pure, unit-tested in `DateTimeFieldParserTests`). A time field holds a full instant, not a `TimeSpan`, so stepping past midnight carries into the date instead of wrapping. `TryParseTime` uses an explicit format list and **rejects date-shaped text**: `DateTime.TryParse("8/3")` succeeds with a `TimeOfDay` of zero, which used to turn a date typed into the time box into a silent midnight.

**Automation surface — do not change without listening first.** The control is a plain `TextBox` with no automation peer of its own. Three shapes were built and evaluated with three screen readers before this one was chosen: an edit field, an edit field claiming `AutomationControlType.Spinner`, and a purpose-built spinner control implementing `IValueProvider` and `IRangeValueProvider`. All three announced correctly, so the one that invents nothing won — replacing `TextBox.Text` raises the UIA value-change event screen readers already act on. There is **no** `AccessibilityHelper.Announce` in the stepping path, and there must not be: a programmatic announcement is filtered by the user's announcement settings, while a native value change is not.

## Tab strips

Left, Right, Home and End on a tab header move through the tabs of any `TabControl` in the app, in
declaration order, wrapping at both ends; selection follows focus, so arrowing to a tab shows it.
`Helpers/TabStripNavigation.cs` installs this once, from `App.OnStartup`, as a class handler on
`TabControl` — a per-control attached property can be forgotten when a window with tabs is added,
a class handler cannot.

WPF's own arrow handling here is **geometric**: Right finds the nearest focusable element to the
right, on roughly the same line. `TabPanel` wraps headers onto as many rows as they need and then
moves the row holding the selected tab to the bottom, so on a wrapped strip the arrows can only
ever reach the tabs sharing a row. Settings has six tabs, wraps to two rows of three, and stranded
Startup, Windowing and Appearance completely (issue #528). How many rows a strip takes depends on
the window width, the font and the text-scaling setting, so a wider dialog or shorter headers is
not a fix. Covered by `TabStripNavigationTests`, which includes a walk over the real
`SettingsDialog`.

Like the date-field stepping keys above, these are **deliberately not registered in
`CommandRegistry`**: they are in-control navigation keys, the same kind as the arrow keys inside a
list box, and they do nothing outside a tab header. `Ctrl+Tab` / `Ctrl+Shift+Tab` remain
`TabControl`'s own — the handler ignores any key pressed with a modifier.

## Folder picker: Tab across the tree and the buttons

In its tree presentation (`FolderPickerWindow` with `useTreeView: true` — move or copy messages,
move or copy a folder, a rule's target folder, the startup folder) the picker handles `Tab` and
`Shift+Tab` itself where they cross between the folder tree and the buttons. The ring is: the tree
→ **New Folder** (where it is shown) → **Open** (while it is enabled) → **Cancel** → back to the
tree. Coming back into the tree lands on the folder that was selected, not on the first row, and
does not change the selection.

WPF cannot do this half of it on its own. `TreeViewItem` leaves `IsTabStop` false, so reverse
traversal finds no tab stop inside the tree, skips the whole control and wraps round to the last
button: `Shift+Tab` out of the first button could not get back to the folders at all. Forward entry
works by a different route — into a `TabNavigation="Once"` container it goes to the container's
focused descendant rather than to a tab stop — which is why only one direction was broken. Making
the items tab stops was measured first and is worse: reverse entry then lands on the first folder
rather than the selected one, and `Shift+Tab` from there does not leave the tree at all.

The other trap is that `Open` is disabled whenever the selection carries no folder (an account
header, or an IMAP path segment that is not itself a mailbox), and focusing a disabled button does
nothing while reporting nothing — so the handler steps to the first button that can actually take
focus, and `Cancel`, always shown and always enabled, is the backstop.

The main window solved the same problem the same way, earlier: `MessageList_PreviewKeyDown`,
`ConversationTree_PreviewKeyDown` and the sender/recipient group trees all intercept `Shift+Tab`
and call `SyncFolderTreeSelection`, and `AccountList_PreviewKeyDown` mirrors it. Its folder tree is
a `TreeView` with the same `TabNavigation="Once"`, so without those handlers `Shift+Tab` out of the
message list would skip it and land on the account list. A hand-wired `Shift+Tab` is what reaching
a `TreeView` in reverse costs in this framework; expect to write one for any new tree.

Like the date-field and tab-strip keys above, these are **deliberately not registered in
`CommandRegistry`**: `Tab` and `Shift+Tab` are framework focus navigation, not application
commands, and they carry no command title to show in the palette. Any other modifier is left alone
— `Ctrl+Tab` and friends fall straight through. The flat list presentation ("Go to Folder") is not
touched by any of this: `ListBoxItem` is a tab stop, so it traverses correctly both ways on its
own. Covered by `FolderPickerTabOrderTests`.
