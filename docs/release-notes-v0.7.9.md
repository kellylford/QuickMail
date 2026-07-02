# QuickMail v0.7.9 Release Notes

## Download

Two options are available for v0.7.9:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.9-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: the Check Spelling dialog

Compose windows now have a full dialog-based spelling review, modeled on the classic word-processor spelling dialog. Press **F7** (or choose **Tools → Check Spelling…**) to review the entire message: the check walks the body first, then the subject line, stopping on each word that is not in the dictionary. For each word you can:

- **Change** (Alt+C or Enter) — replace this occurrence with the "Change to" text
- **Change All** (Alt+L) — also replace later occurrences automatically for the rest of the check
- **Ignore** (Alt+I) — skip this occurrence
- **Ignore All** (Alt+G) — skip this word for the rest of the check
- **Add to Dictionary** (Alt+A) — never flag this word again, anywhere in QuickMail
- **Read in Context** (Alt+R) — hear the full line containing the word

When the dialog opens on a word, the word is announced and the first suggestion is selected in the Suggestions list, so screen readers voice both automatically. Arrow through the suggestions to pick a different one; the check ends with a confirmation of how many words were changed.

### Custom dictionary

**Add to Dictionary** stores words in `custom.lex` in your profile folder — one word per line. Added words stop being flagged everywhere (dialog, inline navigation, and squiggles) immediately and persist across restarts. You can edit the file by hand in a text editor to remove words.

### Subject line spell checking

The subject line is now spell-checked, both in the F7 review (checked after the body) and with inline squiggles while you type.

## Changed keyboard shortcuts — please note

**F7 now opens the Check Spelling dialog.** The previous inline navigation keys have moved:

| Action | Old key | New key |
|--------|---------|---------|
| Check Spelling (dialog) | — | **F7** |
| Next misspelling (inline) | F7 | **Ctrl+F7** |
| Previous misspelling (inline) | Shift+F7 | **Ctrl+Shift+F7** |

Alt+F7 (repeat spelling announcement) and Alt+1/2/3 (apply a suggestion inline) are unchanged. If you had customized these shortcuts in the keyboard customizations dialog, your bindings are untouched — these are new defaults only, and all three commands can be rebound there.

The compose **Tools** menu now groups all spelling commands at the top: Check Spelling…, Next/Previous Misspelling (moved from the Edit menu), and Toggle Spelling Announcements.

---

## Improved security and resource management (Fable5CR)

### Security hardening

- **URI allow-list (`ExternalUriPolicy`)** — Every URI that leaves the app via `ShellExecute` (reading pane links, message windows, markdown preview, update page) now goes through a configurable http/https/mailto allow-list. Everything else is logged and dropped. This closes the attack surface for social engineering attacks via crafted message content.
- **Attachment safety** — New `AttachmentSafety` service consolidates filename sanitization (directory stripping, invalid characters, trailing dots and spaces) and enforces an expanded dangerous-file-extension list, closing the path-traversal gap.
- **HTML sanitizer fail-closed** — If the HTML sanitizer's regex timeout during stripping, the app falls back to reader mode rather than rendering partially stripped HTML. The actual security boundary is CSP, which remains in place for all navigation and scripting.

### Resource management

- `GraphMailService` is now properly disposed in `App.OnExit` (it owns an `HttpClient` via the `GraphClient`).
- `MainViewModel` now implements `IDisposable` and is disposed when the main window closes, releasing its seven `CancellationTokenSource` instances and calendar timer.
- Compose window autocomplete `CancellationTokenSource` is properly cancelled and disposed on tab switch and window close.
- `ConfigService` now writes `config.ini` and `hotkeys.json` atomically via temp file + rename, matching the pattern used for account and view persistence.

### MVVM and testability improvements

- New `IUiDispatcher` service centralizes all `Dispatcher` calls out of `MainViewModel`, improving testability and MVVM compliance.
- Win32 file dialogs are no longer called directly from `ViewModels`; they are now wired through View-layer callbacks.
- 82 new unit tests added (URI policy, attachment safety, sanitizer behavior, config persistence, main window lifecycle, compose attachment behavior, background task fault logging). Full test suite: 879/879 passing.

### Reliability

- Background `Task.Run` calls now use `Task.LogFaults(context)` so failures are logged promptly instead of surfacing at finalization. Applied to local store writes, mark-read batches, and preview fetch operations.
- `SyncService` preview batch now has a per-folder catch block so a single folder failure cannot stop the rest.
- MSAL token-cache registration is now lazy and asynchronous instead of blocking the `OAuthService` constructor.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Check Spelling implementation (PR #167)

- `CustomDictionaryService` owns `{ProfileDir}\custom.lex` (UTF-16 LE with BOM, the encoding the WPF spell engine reads most reliably) and re-registers the lexicon on all three compose editors when a word is added, so new words stop being flagged without reopening the window.
- The find-next-misspelling cores were extracted into `SpellScan` / `TextBoxSpellSource` / `RichTextBoxSpellSource`; both the dialog session and the inline Ctrl+F7 navigation run on the same implementation. Scans use `GetNextSpellingErrorCharacterIndex` / `GetNextSpellingErrorPosition` — one spell-engine call per error rather than one per character.
- The Spelling dialog is modeless (`Show()`, owner = compose window) per the project's Modal Dialog Rules; the session re-queries the live editor for each next error, so editing the message mid-check is safe.
- Session state (ignore set, change-all map, counters, announcements) lives in `SpellCheckDialogViewModel`, unit-tested against scripted sources.
