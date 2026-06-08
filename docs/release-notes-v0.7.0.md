# QuickMail v0.7.0 Release Notes

## Download

Two options are available for v0.7.0:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.0-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New Features

### Tabs and windows

Messages can now be opened in three ways, controlled by a new **Reading mode** setting in **Settings → Windowing**:

**Reading Pane** (default) — the original behavior. One message at a time in the reading pane on the right. Nothing changes for users who keep this setting.

**Tab** — each message you open gets its own tab in a strip below the toolbar. Multiple messages can be open at the same time.

- `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle through open tabs.
- `Ctrl+1` through `Ctrl+8` jump to tab N; `Ctrl+9` jumps to the last tab.
- `Ctrl+W` closes the active tab.
- `Ctrl+Shift+`` ` (backtick) opens a **tab list overlay** — a navigable list of all open tabs, similar to the Alt+Tab switcher.
- `Ctrl+Alt+4` focuses the tab strip itself for arrow-key navigation.
- The **Move Tab to New Window** command (command palette) detaches the active tab into a standalone window.

**Window** — each message opens in its own standalone window. Useful when you want messages on a second monitor or open side-by-side.

Regardless of the setting, **Ctrl+Enter on any message always opens a new window**.

### Message windows

Standalone message windows have a full set of navigation controls:

- A **Previous / Next** toolbar moves through the messages in the originating folder without going back to the main window.
- **F6 / Shift+F6** cycles focus through the toolbar, header fields, and message body.
- **Ctrl+Shift+P** opens a command palette scoped to the window: Previous Message, Next Message, Move to Main Window, Close Window.
- **Move to Main Window** opens the message as a tab in the main window's tab strip and closes the standalone window.
- When a message window closes, focus returns to the originating row in the main window's message list.
- A **loading indicator** is shown while the message body is fetching from the server.

### Pane navigation refinement

With `Ctrl+1`–`8` now doing double duty (pane focus when no tabs are open; tab N when tabs are open), three new always-reliable alternatives are registered:

| Shortcut | Action |
|----------|--------|
| `Ctrl+Alt+1` | Focus account list (works whether or not tabs are open) |
| `Ctrl+Alt+2` | Focus folder tree (always) |
| `Ctrl+Alt+3` | Focus message list (always) |
| `Ctrl+Alt+4` | Focus tab strip (when visible) |

`Ctrl+1`–`3` continue to focus the panes when no tabs are open, so existing muscle memory is preserved.

---

## Accessibility

- **Tab strip `AutomationProperties.Name` is a short label only.** The previous value included the keyboard shortcut instructions, violating the CLAUDE.md rule against embedding instructions in names. The name is now `"Open message tabs"`. Shortcut instructions are delivered as a `Hint`-category announcement the first time the tab strip receives keyboard focus.
- **Message-open-mode radio buttons** in the Settings dialog now form a single tab stop with arrow-key navigation between options, matching standard radio group behavior. Previously each radio button was independently reachable with Tab.
- **Role names removed from Settings tab labels.** The tab items in the Settings dialog previously appended "tab" to their names (e.g., "General settings tab"), which caused screen readers to announce "General settings tab tab." The names are now "General settings", "Advanced settings", "Keyboard shortcuts", and "Windowing settings".
- **Message window loading indicator** uses `AutomationProperties.LiveSetting="Polite"` so the "Loading…" text is announced when the indicator appears.
- **Message window F6 cycle** is fully implemented: toolbar → header fields → message body, with F6 relayed from inside the WebView2 message body via the same postMessage mechanism used in the main window.
- **Message window command palette** (`Ctrl+Shift+P`) surfaces all window-scoped actions for users who prefer not to remember individual shortcuts.
- **Tab strip close buttons are now keyboard-accessible.** When the tab strip has focus, Left/Right arrows navigate between tab headers and their individual close buttons (✕). Pressing Right from a tab header moves to that tab's close button; pressing Right again moves to the next tab header. Previously the close buttons were only reachable with a pointing device.
- **`Ctrl+W` works from inside the message body.** The shortcut is now relayed from the WebView2 message body back to WPF in both Tab mode (closes the active tab) and Window mode (closes the window), so it is not necessary to move focus out of the message to close.
- **`Ctrl+W` in Reading Pane mode closes the reading pane.** A new **Close Message** command (`Ctrl+W` default) is registered for Reading Pane mode, giving it consistent behavior across all three reading modes.
- **Radio button selections in Settings are announced.** When the selected option changes in the Message Open Mode or Log Format radio groups, the newly selected option is announced to screen readers. WPF's built-in `SelectionItemPatternOnElementSelected` event fires correctly but screen readers require a parent `ISelectionProvider` container — which a plain StackPanel does not provide — so the announcement is supplemented via the `AccessibilityHelper.Announce` pathway.
- **Log Format radio group on the Advanced tab** now uses single-tab-stop / arrow-key navigation, consistent with the Message Open Mode group and standard radio group behavior.
- **Tutorial overlay is hidden from the accessibility tree while inactive.** The overlay is placed over the main content area; when not in use it was still reachable by screen reader virtual cursor navigation. Setting `Visibility=Collapsed` when inactive removes it from the tree entirely.

---

## Bug Fixes

- **Message window Previous/Next navigation was always disabled.** The message list was not being populated when a window opened, so the navigation buttons had nothing to navigate. The window now receives the full message list from the originating folder.
- **Message window re-fetched the message body even when it was already loaded.** Opening a window from the main reading pane now reuses the already-loaded message detail instead of making a redundant IMAP request.
- **Message window did not cancel in-flight IMAP loads when closed or navigated.** A `CancellationTokenSource` now cancels any active fetch when the window closes or when the user navigates to a different message.
- **Arrow keys in the tab strip activated a message load.** Moving between tabs with the keyboard triggered `OnActiveTabChangedAsync`, which loaded the message body and moved focus away from the tab strip. Arrow-key navigation now only highlights tabs; focus on the message loads when Enter or Space is pressed.
- **Focus was not restored to the message list when a message window closed.** The main window now scrolls back to the row that was selected when the window opened.
- **Sync guards and address-grab availability did not account for messages open in windows.** Commands that should remain available while reading a message (`Grab Addresses`, sync-related guards) now check whether a message is open in a window, not only whether the reading pane is visible.
- **MessageListTabViewModel sentinel tab** was missing. In Tab mode, the tab strip was hidden when no message tabs were open, stranding keyboard focus. A non-closeable "Messages" sentinel tab is now always present in Tab mode so the strip stays visible and the focus cycle remains intact.
- **Reading pane stayed visible when a message opened in Window mode.** When Reading mode was set to Window, the reading pane was not always cleared before the new window opened, leaving the previous message visible in the background.
- **Deferred selection update could reopen the reading pane in Window mode.** A background `SelectMessageAsync` completion could arrive after a Window-mode open was already in progress and re-show the reading pane. Async version stamps now discard these stale completions.
- **Closing a tab moved focus to the reading pane instead of the tab strip.** After the active tab closed, focus jumped to the message list or reading pane. Focus now stays on the tab strip, landing on the next logical tab.

---

## Internal

- `MessageBodyHtmlBuilder` — new shared static class extracts all HTML rendering helpers (reader-mode detection, sanitization, plain-text auto-linking, heavy-HTML stripping) from both `MainWindow.xaml.cs` and `MessageWindow.xaml.cs`. Previously the ~150-line render pipeline was duplicated verbatim in both files.
- `MessageListTabViewModel` — new sentinel VM for the non-closeable "Messages" tab in Tab mode; extends `TabSessionViewModel` with `CanClose = false`.
- `TabSessionModel.TabKind` gains a `MessageList` variant for the sentinel tab.
- `MainViewModel.IsMessageOpenInWindow` — new observable property set when one or more standalone message windows are open; guards that previously checked only `IsMessageOpen` now check both.
- 449 tests, all green (one pre-existing clipboard-API failure in the test runner environment is unrelated to this change).
