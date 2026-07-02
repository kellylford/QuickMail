# Check Spelling Dialog — Combined PM & Dev Specification

| | |
|---|---|
| **Status** | Draft for review |
| **Version** | 1.0 |
| **Date** | 2026-07-02 |
| **Author** | Kelly Ford (direction) + AI (spec) |
| **Target release** | Next minor release after approval |
| **Dependencies** | None new. Builds on existing WPF spell checking, `CommandRegistry`, `AccessibilityHelper`, `ProfileContext`. |

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Design Principles](#3-design-principles)
4. [Feature Scope & Acceptance Criteria](#4-feature-scope--acceptance-criteria)
5. [Architecture & Technical Decisions](#5-architecture--technical-decisions)
6. [Keyboard Walkthrough](#6-keyboard-walkthrough)
7. [Accessibility Checklist](#7-accessibility-checklist)
8. [Acceptance Walkthrough](#8-acceptance-walkthrough)
9. [Success Metrics](#9-success-metrics)
10. [Implementation Phases](#10-implementation-phases)
11. [Files to Create / Modify](#11-files-to-create--modify)
12. [Tests to Add](#12-tests-to-add)
13. [Known Risks & Open Questions](#13-known-risks--open-questions)
14. [Appendix A — Keyboard Reference](#14-appendix-a--keyboard-reference)
15. [Implementation Guidance for AI](#15-implementation-guidance-for-ai)

---

## 1. Executive Summary

QuickMail's spell checking today is inline-only: F7/Shift+F7 walk misspellings one at a time, and Alt+1/2/3 apply one of up to three suggestions. There is no way to systematically review an entire message, no Ignore/Ignore All, and no custom dictionary — "Add to Dictionary" does not exist anywhere in the app. This spec adds **Check Spelling**, a full dialog-based review modeled on the classic word-processor spelling dialog that was, for decades, the single best-understood interaction pattern between screen readers and productivity software: open the dialog, hear "Not in dictionary: \<word\>" with the first correction voiced automatically, and drive the whole review with Alt+C (Change), Alt+I (Ignore), Alt+G (Ignore All), and Alt+A (Add to Dictionary). F7 — the key that meant "check spelling" in every major word processor — opens the dialog; inline navigation moves to Ctrl+F7/Ctrl+Shift+F7.

The check covers the message body first, then the subject line, before declaring completion — catching the subject-line typo that every mainstream email client user has sent at least once.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified)

All claims verified against the code on 2026-07-02.

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Compose body (`BodyBox`, `RichBodyBox`) | WPF `SpellCheck.IsEnabled="True"` (`ComposeWindow.xaml` ~lines 399, 412). F7/Shift+F7 walk errors via `NavigateSpellingError` / `NavigateSpellingErrorInRichBox` (`ComposeWindow.xaml.cs`). Alt+1/2/3 replace with one of three cached suggestions. Alt+F7 repeats the announcement. | One word at a time, three suggestions max, no memory of decisions. Reviewing a long message means holding the whole state in your head. No "skip this word everywhere" — a proper noun used ten times interrupts ten times. | Everyone who proofreads before sending; screen reader users most acutely. |
| Ignore / Ignore All | Does not exist. | Repeated names, jargon, and product terms are flagged on every occurrence, every session. | All users; heavy correspondents worst. |
| Add to Dictionary | Does not exist. No `SpellCheck.CustomDictionaries` usage, no `.lex` file, no dictionary service anywhere in the codebase. | Personal vocabulary (names, technical terms, "QuickMail" itself) is permanently misspelled as far as the app is concerned. | All users. |
| Subject line (`SubjectBox`) | No spell checking at all — `SpellCheck.IsEnabled` is not set. | Subject typos ship silently; the subject is the most-read line of any message. | All users. |
| Menus | Next/Previous Misspelling live in the **Edit** menu; the Tools menu has Address Book, Check Addresses, Toggle Spelling Announcements, Command Palette. | Spelling features are split across two menus. | Anyone browsing menus to discover spelling features. |
| Announcements | `BuildSpellingAnnouncement` respects `AnnounceSpellingSuggestions` and `SpellingSuggestionsVerbosity` from `ConfigModel`; announcements go through `AccessibilityHelper.Announce`. | Works well inline — this infrastructure is reused, not replaced. | — |

### 2.2 Target personas

- **The screen reader power user.** Wants the classic review loop: hear the error, hear the top suggestion automatically, decide with a single Alt-chord, move on. This flow once made spell checking one of the *best* screen reader experiences in all of computing; nothing modern reproduces it.
- **The keyboard-only sighted user.** Wants a systematic pass over a long message without mousing through squiggles. The dialog shows the word in context, the suggestion list, and one-key actions.
- **The careful correspondent.** Occasionally writes messages that matter (job applications, announcements) and wants a "did I miss anything?" pass over body *and subject* before sending.
- **The jargon-heavy writer.** Emails full of product names, acronyms, and colleagues' surnames. Needs Ignore All for this message and Add to Dictionary forever.

### 2.3 Why now

- `CommandRegistry`, the compose command palette, `AccessibilityHelper.Announce` categories, and the inline scan code all exist — the dialog composes proven pieces rather than inventing new infrastructure.
- The inline experience (F7 walk + Alt+1/2/3) shipped and validated the announcement design; the dialog is its natural completion.
- The modeless-dialog pattern is now an enforced project rule with a documented rationale, which is exactly the launch pattern this dialog needs.

---

## 3. Design Principles

1. **Reproduce the classic, don't reinvent it.** The Alt+C/I/G/A verb set, "Not in dictionary" framing, and first-suggestion-voiced-automatically behavior are the spec. Where classic word processors and this design differ, the burden of proof is on the difference.
2. **The screen reader does the natural work; QuickMail supplies only the framing.** Focus placement makes the screen reader voice the selected suggestion natively. Our `Announce` calls carry only what focus cannot: "Not in dictionary: \<word\>." No double speech.
3. **Zero new configuration.** The dialog respects the existing announcement settings (`AnnounceResults`, `AnnounceStatus`, `AnnounceSpellingSuggestions`, `SpellingSuggestionsVerbosity`). No new config keys.
4. **Decisions are session-scoped, dictionary additions are permanent.** Ignore All lasts for this check session (classic behavior); Add to Dictionary persists in the profile forever.
5. **The document stays live.** The dialog is modeless; the message remains visible and editable behind it, and the current word is always selected and visibly highlighted in the editor.

---

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Check Spelling dialog | `compose.checkSpelling` / **F7** | Enabled | Modeless "Spelling" window over the compose window. Body first, then subject, then completion. |
| Change / Change All | Alt+C / Alt+L in dialog | — | Change replaces the current occurrence with the "Change to" text; Change All also auto-replaces later occurrences of the same word this session. |
| Ignore / Ignore All | Alt+I / Alt+G in dialog | — | Ignore skips this occurrence; Ignore All skips the word for the rest of this session. |
| Add to Dictionary | Alt+A in dialog | — | Appends to a new profile-level custom dictionary (`custom.lex`); word is never flagged again, anywhere spell checking runs. |
| Read in Context | Alt+R in dialog | — | Announces the full line containing the current word. |
| Custom dictionary infrastructure | — | — | New `CustomDictionaryService`; registered on body, rich body, and subject editors. |
| Subject spell checking | — | Enabled | `SpellCheck.IsEnabled="True"` on `SubjectBox` (side benefit: inline squiggles in the subject). |
| Inline nav rebinding | `compose.nextMisspelling` → **Ctrl+F7**, `compose.prevMisspelling` → **Ctrl+Shift+F7** | — | Defaults only; user overrides in `hotkeys.json` are untouched. Alt+F7 (repeat announcement) unchanged. |
| Tools menu spelling group | — | — | Check Spelling…, Next/Previous Misspelling (moved from Edit), Toggle Spelling Announcements grouped at the top of Tools. |
| Completion confirmation | — | — | "The spelling check is complete." with change count; OK to dismiss; focus returns to the body editor. |

### 4.2 Explicitly out of scope (v1)

- **Grammar, thesaurus, autocorrect.** Spelling only. (The name "Check Spelling" leaves room for a future "Proofread" umbrella if grammar ever arrives.)
- **Persisted Ignore All.** Ignore All is session-only, matching the classic model. A persisted ignore list is a v2 candidate.
- **Undo button inside the dialog.** The editor's own Ctrl+Z undoes replacements after the dialog closes (and during, since the dialog is modeless). A dedicated in-dialog Undo Last is deferred.
- **Dictionary management UI.** v1 has Add only. Removing a word means editing `custom.lex` in a text editor (documented in the user guide). A management dialog is a v2 candidate.
- **Multi-language dictionaries.** The check uses the editor's input language, as WPF spell checking does today.
- **Spell check outside compose.** No reading-pane or main-window entry points; this is a compose-window feature.
- **Checking address fields.** To/Cc/Bcc are not spell-checked (they are addresses; Check Addresses already validates them).

---

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision 1: The Spelling dialog is modeless (`Show()`, `Owner = ComposeWindow`).**

**Alternatives:**
1. Modal `ShowDialog()` — classic dialogs were modal, and modality prevents mid-check document edits. Con: the project's Modal Dialog Rules exist because modal nesting over a live editor with an editable TextBox in the dialog is exactly the pattern that produced the GrabAddresses dispatcher deadlock; and every action in this dialog mutates the parent window's editor while the dialog is open, which under a nested message loop is the re-entrancy pattern the rules forbid.
2. Modeless `Show()` — Pro: no nested message loop; parent editor updates (selection, replacement) are ordinary same-thread operations; matches the compose window itself, the proven modeless-editable-window pattern. Con: no `DialogResult`; Escape/Cancel must be wired explicitly; the user can edit the document mid-check (handled — see Decision 2).

**Rationale:** The Modal Dialog Rules in CLAUDE.md make this nearly automatic: the dialog contains an editable "Change to" TextBox and continuously touches the parent window's content. Modeless is the only pattern that satisfies both. Per the documented modeless trade-off, Escape closes via an explicit `PreviewKeyDown` handler (guarded so it does not steal Escape from an open ComboBox dropdown — the dialog has no ComboBox in v1, but the guard is cheap insurance) and Close/Cancel handlers call `Close()` directly.

**Decision 2: Incremental, position-based scanning — no upfront error list.**

**Alternatives:**
1. Snapshot all errors when the dialog opens, iterate the list. Pro: simple counter ("error 3 of 12"). Con: replacements shift offsets; a modeless dialog allows the user to edit the document mid-check, invalidating the snapshot; two sources of truth.
2. Re-query the live editor for the next error each time (chosen). Pro: always correct, tolerant of mid-check edits and of offset shifts from replacements; this is exactly how the existing inline navigation already works. Con: no up-front total count (the classic dialogs didn't have one either).

**Rationale:** The editor *is* the error model. The scan walks from the position after the last handled word: `TextBox.GetSpellingError(i)` character walk for Plain/Markdown, `RichTextBox.GetNextSpellingErrorPosition` + `GetSpellingErrorRange` for HTML — the same APIs `NavigateSpellingError` / `NavigateSpellingErrorInRichBox` use today. Scan order: caret position → end of body, wrap to start of body → original position, then subject start → subject end, then complete. Words in the session ignore set are skipped silently; words in the change-all map are replaced silently (and counted) without stopping.

**Decision 3: New `CustomDictionaryService` owning `{ProfileDir}\custom.lex`.**

**Alternatives:**
1. No service — inline file handling in the dialog code-behind. Con: the dictionary must be registered on three editors at compose-window load, independent of the dialog; and it needs unit tests.
2. A service following the manual DI pattern (chosen): interface `ICustomDictionaryService` in `Services/`, constructed in `App.OnStartup` after `ProfileContext`, passed to compose windows.

**Rationale:** WPF supports custom dictionaries natively via `SpellCheck.CustomDictionaries.Add(new Uri(path))` with `.lex` lexicon files (one word per line; written UTF-16 LE with BOM, the encoding WPF handles most reliably). The service exposes `EnsureDictionaryFile()`, `DictionaryUri`, `AddWordAsync(string word)`, and `RegisterOn(TextBoxBase editor)`. After `AddWordAsync`, the registration must be refreshed (WPF re-reads the lexicon on remove + re-add of the Uri); the service raises `DictionaryChanged` and the compose window re-registers on its three editors. The service holds no unmanaged state and no CTS — it does not implement `IDisposable` (nothing to dispose; do not add the interface just to satisfy a habit).

**Decision 4: MVVM split — testable session state in the VM, editor access behind a View-layer adapter.**

The spelling *session logic* (ignore set, change-all map, counters, current word/suggestions state, announcement text building, what-happens-on-each-verb) lives in `SpellCheckDialogViewModel` — no `System.Windows` UI types, fully unit-testable per the MVVM rules. The *editor access* (finding the next error, selecting it, replacing text, extracting the context line) is View-layer by nature — it manipulates `TextBox`/`RichTextBox` — so it sits behind a small adapter interface:

```csharp
// View layer (Views/SpellCheckSources.cs) — not referenced by the VM's logic tests directly;
// tests use a stub ISpellCheckSource.
internal interface ISpellCheckSource
{
    /// Advance to the next spelling error at or after the current position.
    /// Returns null when the source is exhausted (wrapped scan complete).
    SpellingErrorInfo? MoveToNextError();
    void ReplaceCurrent(string replacement);
    void SelectCurrent();          // select + bring into view in the editor
    string GetContextLine();       // full line containing the current error
}

internal sealed record SpellingErrorInfo(string Word, IReadOnlyList<string> Suggestions);
```

Implementations: `TextBoxSpellSource` (Plain/Markdown, and the subject) and `RichTextBoxSpellSource` (HTML). The dialog is constructed with an ordered list of sources — `[bodySource, subjectSource]` — and a display name per source so the VM can announce the "Checking subject." transition. The dialog code-behind mediates: VM raises intent events / exposes commands; code-behind calls the source and feeds results back into VM properties.

**Decision 5: F7 rebinding.**

F7 becomes `compose.checkSpelling` (the dialog); `compose.nextMisspelling` moves to Ctrl+F7 and `compose.prevMisspelling` to Ctrl+Shift+F7. These are *default* changes in `RegisterComposeCommands` — the `CommandRegistry` hotkey-override mechanism means any user with custom bindings in `hotkeys.json` is unaffected. `MenuItem.InputGestureText` values are updated to match, per the Keyboard Shortcuts rules. Release notes must call out the change (see §13.1).

**Decision 6: Completion is a small owned confirmation window, shown after the Spelling dialog closes.**

When the scan exhausts both sources (or when F7 finds no errors at all), the Spelling dialog closes first, then a minimal confirmation window ("The spelling check is complete." + change summary + OK) is shown. Because the compose window hosts no live WebView2 and the confirmation contains no editable field, `ShowDialog()` is acceptable here — and nothing subscribed to it can touch the parent while it runs. Enter/Escape/OK all dismiss it; focus then returns to the body editor.

### 5.2 Runtime mode compatibility

Spell checking is entirely local (WPF spell engine + a profile-directory file). No `LocalStoreService`, no IMAP, no network.

| Mode | LocalStoreService available? | Calls `LocalStore...Async`? | Fallback? |
|---|---|---|---|
| Normal | ✓ | No | n/a |
| `--online` | ✗ | No | n/a — feature is unaffected |
| `--profileDir <path>` | ✓ | No | `custom.lex` follows the profile directory via `ProfileContext` |

### 5.3 Code reuse and duplication risks

- **The inline scan code is the extraction source, not a sibling.** `NavigateSpellingError` (plain TextBox walk) and `NavigateSpellingErrorInRichBox` (RichTextBox pointer walk) in `ComposeWindow.xaml.cs` contain the exact error-finding logic the dialog needs. Plan: extract the find-next-error cores into `TextBoxSpellSource` / `RichTextBoxSpellSource` and re-implement the inline navigation on top of the same sources. One scan implementation, two consumers. Do **not** copy the loops into the dialog.
- **Announcement text.** `BuildSpellingAnnouncement` formats the inline announcement using `SpellingSuggestionsVerbosity`. The dialog's framing is different ("Not in dictionary: X" with the suggestion voiced by focus), so it does not reuse that method for its main announce — but the no-suggestions phrasing and any suggestion-list verbalization (Read in Context, fallback mode in §13.1 risk 2) must use the same config-driven vocabulary rather than inventing a second format.
- **Session ignore/change-all bookkeeping** exists nowhere yet — it goes in the VM once, and the inline Alt+1/2/3 path is *not* changed to consult it (inline quick-replace stays independent; a session concept only exists while the dialog is open).

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `ComposeWindow.xaml` menus | `Views/ComposeWindow.xaml` | All compose entry points (new mail, reply, forward, drafts, templates) | Move Next/Previous Misspelling from Edit → Tools; add Check Spelling…; update `InputGestureText` values; enable `SpellCheck` + `IsInactiveSelectionHighlightEnabled` on `SubjectBox`; `IsInactiveSelectionHighlightEnabled` on `BodyBox`/`RichBodyBox` | Menu access keys within Edit and Tools must stay unique after the move |
| Inline spell navigation | `Views/ComposeWindow.xaml.cs` (`NavigateSpellingError`, `NavigateSpellingErrorInRichBox`, Alt+1/2/3 handlers, Alt+F7) | Ctrl+F7/Ctrl+Shift+F7 users, `AnnounceSpellingWhileNavigating`/`WhileTyping` flows | Refactored onto the extracted spell sources; behavior must be identical | Highest-risk refactor in the feature — covered by dedicated regression scenarios in §8 |
| `RegisterComposeCommands` | `Views/ComposeWindow.xaml.cs` | Compose command palette, keyboard customizations dialog, `hotkeys.json` overrides | New `compose.checkSpelling`; new defaults for `compose.nextMisspelling` / `compose.prevMisspelling` | Users' saved overrides must survive; verify the customizations dialog shows the new defaults |
| `AccessibilityHelper` | `Views/AccessibilityHelper.cs` | Every announcement in the app | **No changes** — new call sites only (categories: Result, Status) | None |
| `ConfigModel` / `ConfigService` | `Models/ConfigModel.cs`, `Services/ConfigService.cs` | Settings dialog, all announcement gating | **No changes** — existing spelling settings are read, none added | None |
| `App.xaml.cs` DI chain | `App.xaml.cs` | All services | Construct `CustomDictionaryService` after `ProfileContext`; pass to compose window creation path | Follow the existing manual-DI ordering; no disposal needed (see Decision 3) |
| Compose command palette | `Views/CommandPaletteWindow.xaml` via `_registry` | Main window palette (separate registry scope) | No changes — new commands appear automatically via registration | None |
| `SubjectBox` | `Views/ComposeWindow.xaml` | Subject binding to `ComposeViewModel.Subject` | `SpellCheck.IsEnabled="True"` | Inline squiggles appear in the subject — intended side effect; verify no interference with subject autocomplete/validation if any exists |

New components (`SpellCheckDialog`, `SpellCheckDialogViewModel`, `SpellCheckSources`, `CustomDictionaryService`) have no other consumers — confirmed by construction.

---

## 6. Keyboard Walkthrough

All announcement texts below are the exact strings passed to `AccessibilityHelper.Announce`. Suggestion voicing marked *(native)* comes from screen reader focus/selection behavior, not from an `Announce` call. All `Announce` calls are category **Result** unless noted, and are therefore gated by the user's `AnnounceResults` setting — the visual UI carries the same information regardless.

### Path 1: Happy path — body with several errors, then subject, then done

Setup: compose window open in any editor mode; body contains "I will recieve the pacakge tomorow."; subject contains "Shiping update"; caret at start of body.

1. User presses **F7** (or Tools → Check Spelling…). **Expected:** The editor selects "recieve" (visibly highlighted). The modeless Spelling window opens. Announce: **"Not in dictionary: recieve."** Focus lands on the Suggestions list with "receive" (item 1) selected; the screen reader voices "receive" *(native)*. The context box shows "I will recieve the pacakge tomorow." The Change to box contains "receive".
2. User presses **Alt+C** (Change). **Expected:** "recieve" → "receive" in the body. The scan advances; the editor selects "pacakge". Announce: **"Not in dictionary: pacakge."** Suggestions list repopulates, "package" selected and voiced *(native)*. Change to = "package". Focus is on the Suggestions list.
3. User presses **Down Arrow**. **Expected:** Selection moves to suggestion 2 (e.g., "packages"), voiced *(native)*. Change to box updates to "packages".
4. User presses **Up Arrow**, then **Enter** (Change is the default button). **Expected:** "pacakge" → "package". Scan advances to "tomorow". Announce: **"Not in dictionary: tomorow."** "tomorrow" selected and voiced *(native)*.
5. User presses **Alt+C**. **Expected:** Body is now clean. The scan reaches the end of the body, wraps to the start, finds nothing, and moves to the subject. Announce (category **Status**): **"Checking subject."** Then the editor focus target becomes the subject: "Shiping" is selected in `SubjectBox`. Announce: **"Not in dictionary: Shiping."** "Shipping" selected in the list and voiced *(native)*.
6. User presses **Alt+C**. **Expected:** Subject is clean; the scan is exhausted. The Spelling window closes. The completion window opens with the text "The spelling check is complete. 4 words changed." Focus is on the OK button. Announce: **"Spelling check complete. 4 words changed."**
7. User presses **Enter**. **Expected:** The completion window closes. Focus returns to the body editor with the caret at the last replacement.

### Path 2: No misspellings anywhere

1. User presses **F7** in a clean message. **Expected:** The Spelling window never opens. The completion window opens directly: "The spelling check is complete. No changes made." Announce: **"Spelling check complete. No misspellings found."** Focus on OK.
2. User presses **Enter**. **Expected:** Window closes; focus returns to the editor exactly where it was.

### Path 3: Word with no suggestions

1. Scan lands on "xqzt". **Expected:** Editor selects "xqzt". Announce: **"Not in dictionary: xqzt. No suggestions."** The Suggestions list is empty. Focus lands on the **Change to** box, which contains "xqzt" (the original word, selected so typing replaces it).
2. User types a correction and presses **Enter**. **Expected:** The word is replaced with the typed text; scan advances normally.
3. *(Alternative)* User presses **Alt+I** instead. **Expected:** Word is skipped; scan advances.

### Path 4: Ignore All

1. Scan lands on "Kestrelworks" (a product name used five times). Announce: **"Not in dictionary: Kestrelworks."**
2. User presses **Alt+G**. **Expected:** "Kestrelworks" enters the session ignore set. The scan advances past all five occurrences without stopping, landing on the next different error (or completing). No announcement per skipped occurrence.

### Path 5: Add to Dictionary

1. Scan lands on "QuickMail". Announce: **"Not in dictionary: QuickMail."**
2. User presses **Alt+A**. **Expected:** "QuickMail" is appended to `custom.lex`; the dictionary registration refreshes on all three editors (squiggles for the word vanish); scan advances. Announce for the next error follows immediately. From now on — this session and every future one — "QuickMail" is never flagged.

### Path 6: Read in Context

1. Focus anywhere in the Spelling window, current word "tomorow". User presses **Alt+R**. **Expected:** Announce: **"I will receive the package tomorow."** (the full line containing the word). Focus does not move. Nothing else changes.

### Path 7: Change All

1. Scan lands on "recieve" (used three times). User edits nothing and presses **Alt+L** (Change All). **Expected:** Current occurrence replaced with "receive"; "recieve → receive" enters the change-all map. As the scan encounters the later two occurrences it replaces them silently (each counted). The dialog only stops on *different* errors.

### Path 8: Escape / cancel mid-check

1. Mid-session, focus in the Spelling window, user presses **Escape** (or activates Close). **Expected:** The Spelling window closes immediately. No completion window. Changes already made remain (they are ordinary editor edits, undoable with Ctrl+Z). Focus returns to the editor that owned the current word, caret at that word, which remains selected. Announce: **"Spelling check canceled."**

### Path 9: Mid-check document edit (modeless allowance)

1. Mid-session, user presses **Alt+Tab**-equivalent within the app: activates the compose window directly (or F6 per §7), edits the body text, then returns focus to the Spelling window and presses **Alt+I**. **Expected:** No crash, no stale replacement. The scan re-queries from the live document; the next stop is the next real error in the edited text. (Position-based rescan, Decision 2, makes this inherently safe.)

### Path 10: F6 inside the Spelling window

1. Focus on the Suggestions list. User presses **F6**. **Expected:** Focus moves to the "Not in dictionary" context box (read-only; screen reader reads the line).
2. **F6** again. **Expected:** Focus moves to the button row (Change).
3. **F6** again. **Expected:** Focus returns to the Suggestions list (or Change to box when the list is empty). **Shift+F6** cycles in reverse.

### Path 11: Editor-mode coverage

Paths 1–10 behave identically in **Plain Text**, **Markdown** (both `BodyBox`/`TextBoxSpellSource`), and **HTML** (`RichBodyBox`/`RichTextBoxSpellSource`) modes. In HTML mode, replacement preserves surrounding formatting (the replacement takes on the formatting of the replaced range, WPF's `TextRange.Text` assignment behavior). The subject is always a plain `TextBox` regardless of mode.

### Path 12: Inline navigation after rebinding (regression path)

1. User presses **Ctrl+F7** in the body. **Expected:** identical to the old F7 behavior — caret/selection moves to the next misspelling, announce per `BuildSpellingAnnouncement` ("Misspelling: \<word\>. 1: …, 2: …, 3: …" per verbosity setting).
2. User presses **Alt+2**. **Expected:** second suggestion replaces the word, announce "Replaced with \<word\>." — unchanged.
3. User presses **Ctrl+Shift+F7**. **Expected:** previous misspelling — unchanged behavior, new key.

---

## 7. Accessibility Checklist

- **AutomationProperties.Name** (short labels only, no roles, no hints):
  - Spelling window title: "Spelling"
  - Context box: "Not in dictionary"
  - Change-to box: "Change to"
  - Suggestions list: "Suggestions"
  - Buttons: "Change", "Change All", "Ignore", "Ignore All", "Add to Dictionary", "Read in Context", "Close"
  - Completion window title: "Spelling"; its text is static content read on focus; button "OK".
- **Announcements** (all via `AccessibilityHelper.Announce`, never `RaiseNotificationEvent` directly):

  | Text | Category | Why |
  |---|---|---|
  | "Not in dictionary: \<word\>." (+ " No suggestions." when empty) | Result | Outcome of opening/advancing the check |
  | "Checking subject." | Status | Background progress between scan sources |
  | "Spelling check complete. N words changed." / "…No misspellings found." | Result | Action outcome (also shown visually in the completion window) |
  | "Spelling check canceled." | Result | Action outcome |
  | Read in Context line text | Result | Explicitly requested read-back |

  No `force: true` anywhere — every announcement respects user preferences, and the visual dialog carries all the same information for users who disable announcement categories.
- **First-suggestion voicing** comes from focus landing on the Suggestions list with item 1 selected — native screen reader behavior, not an announcement. On advance (focus already in the list), the list is repopulated and item 1 re-selected; the UIA selection event voices it. If real-world testing shows screen readers do not consistently voice the re-selection, the fallback is defined in §13.1 (risk 2) — this is the one place where the user's actual experience, not this spec, is the authority.
- **Focus restoration:** Escape/Close → the editor owning the current word, word still selected. Completion → body editor at last replacement. No-errors case → wherever the caret was when F7 was pressed.
- **F6 ring:** The Spelling window gets its own three-stop cycle (context ↔ suggestions/change-to ↔ buttons), per the New Window Checklist. No changes to the compose window's or main window's F6 rings. No WebView2 in the dialog, so no F6 relay script is needed (stated explicitly per the checklist).
- **Radio buttons / checkboxes:** none in this UI.
- **Color-only information:** none. The current word is indicated by selection (which screen readers and high-contrast themes both honor), and `IsInactiveSelectionHighlightEnabled` keeps it visible while the dialog has focus.
- **Command palette:** the Spelling window wires Ctrl+Shift+P to a local palette listing all dialog actions (Change, Change All, Ignore, Ignore All, Add to Dictionary, Read in Context, Close), following the `ComposeWindow.xaml.cs` pattern.
- **Cancellation token:** the dialog performs no async loads; per the New Window Checklist this is stated explicitly rather than left ambiguous. (`AddWordAsync` is a fast local file append awaited from the code-behind; if implementation makes it synchronous, that is acceptable.)

---

## 8. Acceptance Walkthrough

Run each scenario in the app at Session 3. Mark pass/fail per step.

### Scenario 1: Full happy path with a screen reader

**Setup:** Screen reader running. New compose, Plain Text mode. Body: "I will recieve the pacakge tomorow." Subject: "Shiping update". Caret at body start.

1. Press F7. **Verify:** Spelling window opens; you hear "Not in dictionary: recieve" followed by "receive" (the selected suggestion); the compose window behind shows "recieve" highlighted.
2. Press Alt+C three times (pausing to hear each new word). **Verify:** each error is announced with its word, the top suggestion is voiced each time, the body text updates each time.
3. **Verify:** after the third Change you hear "Checking subject." then "Not in dictionary: Shiping" and "Shipping".
4. Press Alt+C. **Verify:** completion window appears, "The spelling check is complete. 4 words changed." is heard, focus is on OK.
5. Press Enter. **Verify:** focus is back in the body editor; Tab is not required to resume writing.

### Scenario 2: Every button does its job

**Setup:** Body: "Blorf blorf zzyx recieve." (two occurrences of one unknown word, one no-suggestion word, one classic typo).

1. F7 → first "Blorf". Press Alt+G (Ignore All). **Verify:** the check does *not* stop on the second "blorf"; next stop is "zzyx".
2. On "zzyx" (no suggestions). **Verify:** focus is in the Change to box containing "zzyx", announcement included "No suggestions." Press Alt+I. **Verify:** skipped, next stop "recieve".
3. Press Alt+R. **Verify:** the full line is read back; focus did not move.
4. Press Alt+A on a word you own (rerun with your name in the body). **Verify:** the word is skipped, and after closing and reopening the compose window it is no longer squiggled inline.
5. Rerun with "recieve" twice and use Alt+L on the first. **Verify:** both are corrected; the dialog stopped only once; the completion count includes both.

### Scenario 3: Escape and undo

1. Start a check, make one Change, press Escape. **Verify:** Spelling window closes, "Spelling check canceled." is heard, focus is in the editor on the word that was current, and the earlier change is still in the text.
2. Press Ctrl+Z. **Verify:** the change is undone by the editor's normal undo.

### Scenario 4: All three editor modes + subject handoff

Repeat Scenario 1 abbreviated (one body error + one subject error) in **Plain Text**, **Markdown**, and **HTML** modes. **Verify** in each: errors found, replacement applied, formatting intact in HTML (bold a misspelled word first and confirm the correction stays bold), subject transition announced, completion reached.

### Scenario 5: Custom dictionary round-trip across restart

1. Add a word via Alt+A. **Verify:** `custom.lex` exists in the profile directory and contains the word.
2. Close QuickMail entirely; relaunch; open compose; type the word. **Verify:** no inline squiggle; F7 does not stop on it.
3. **Edge case:** delete `custom.lex` while the app is closed; relaunch. **Verify:** no crash; the file is recreated on next Add.

### Scenario 6: Inline navigation regression (shared scan code)

1. Press Ctrl+F7 / Ctrl+Shift+F7 through a message with several errors in both Plain Text and HTML modes. **Verify:** identical behavior to pre-change F7/Shift+F7, including announcements, wrap-around, and `AnnounceSpellingWhileNavigating` caret behavior.
2. Navigate to an error with Ctrl+F7, press Alt+1. **Verify:** quick replace still works and announces "Replaced with …".
3. Press Alt+F7. **Verify:** repeat announcement unchanged.
4. Open the keyboard customizations dialog. **Verify:** Check Spelling shows F7; Next/Previous Misspelling show Ctrl+F7/Ctrl+Shift+F7; all three are rebindable.
5. With a pre-existing `hotkeys.json` override for `compose.nextMisspelling`, launch and test. **Verify:** the override wins over the new default.

### Scenario 7: Announcement settings respected

1. Turn off `AnnounceResults` in Settings. Run a check. **Verify:** no "Not in dictionary" announcements fire, but the dialog is fully usable — the context box, list focus, and completion window carry everything visually and via normal focus reading.
2. Turn off `AnnounceStatus`. **Verify:** "Checking subject." is not announced; the check still proceeds into the subject.
3. Turn both back on without restarting. **Verify:** announcements resume.

### Scenario 8: `--online` mode

Run with `--online`. Execute Scenario 1 abbreviated. **Verify:** identical behavior (feature touches no local store).

### Scenario 9: Mid-check edit (modeless)

1. Start a check; with the Spelling window open, activate the compose window, delete the sentence containing the current error, return to the Spelling window, press Alt+I. **Verify:** no crash; the check continues from the live text and completes normally.

---

## 9. Success Metrics

- **Behavioral:** A message with ten errors across body and subject can be fully corrected without leaving the keyboard, hearing every word and its top suggestion, in a single F7 pass.
- **Classic parity:** Alt+C / Alt+I / Alt+G / Alt+A perform exactly their historical verbs; a user who last used the classic dialogs decades ago needs no instruction.
- **Accessibility:** Every announcement respects the user's configured categories; the dialog is fully operable with announcements disabled; nothing requires screen reader scripting.
- **No regressions:** Inline navigation (now Ctrl+F7/Ctrl+Shift+F7), Alt+1/2/3 quick replace, Alt+F7 repeat, and while-typing/while-navigating announcements behave exactly as before. All existing tests pass unchanged.
- **Persistence:** Added dictionary words survive app restart and apply to inline checking everywhere.

---

## 10. Implementation Phases

### Phase 1: Custom dictionary infrastructure

**Goal:** `CustomDictionaryService` exists, is wired in `App.OnStartup`, and registered on all three compose editors; Add-word round-trips.

**Deliverables:** `Services/ICustomDictionaryService.cs`, `Services/CustomDictionaryService.cs`; `App.xaml.cs` construction; `ComposeWindow` registration on `BodyBox`, `RichBodyBox`, `SubjectBox` (and `SpellCheck.IsEnabled` on `SubjectBox`); `QuickMail.Tests/StubServices.cs` stub.

**Tests:** `CustomDictionaryServiceTests` — file creation, append, duplicate-add is idempotent, Uri refresh event, missing-file recovery, profile-dir override via `ProfileContext`.

**Risk:** WPF lexicon encoding quirks — mitigated by the UTF-16 LE + BOM decision and a round-trip test with non-ASCII words.

**Duration:** 2–3 hours.

### Phase 2: Scan extraction

**Goal:** `ISpellCheckSource` + `TextBoxSpellSource` + `RichTextBoxSpellSource` exist; inline navigation is re-implemented on top of them with zero behavior change.

**Deliverables:** `Views/SpellCheckSources.cs`; refactored `NavigateSpellingError` / `NavigateSpellingErrorInRichBox` in `ComposeWindow.xaml.cs`.

**Tests:** Existing inline behavior covered by manual regression (Scenario 6); `[StaFact]` tests exercising `TextBoxSpellSource` against a real `TextBox` where feasible.

**Risk:** Subtle behavior drift in wrap-around or word-boundary extraction — mitigated by extracting mechanically first, refactoring second, and running Scenario 6 before Phase 3 begins.

**Duration:** 3–4 hours.

### Phase 3: The Spelling dialog

**Goal:** Full dialog works end-to-end from a temporary entry point (palette-only), body + subject, all verbs, all announcements.

**Deliverables:** `Views/SpellCheckDialog.xaml(.cs)`, `ViewModels/SpellCheckDialogViewModel.cs`; completion window (a second minimal XAML or a parameterized confirmation window); launch code in `ComposeWindow.xaml.cs`.

**Tests:** `SpellCheckDialogViewModelTests` — verb state machine against a stub `ISpellCheckSource` (ignore-all skipping, change-all auto-replacement and counting, no-suggestions focus mode, source transition, completion counts, announcement text building); `XamlParseTests` entry for the new dialog.

**Risk:** Focus choreography (list re-selection voicing, focus return on close). Mitigation: the §6 walkthrough is normative; Kelly validates the voicing behavior early in Session 3 (see §15.2).

**Duration:** 4–6 hours.

### Phase 4: Keys, menus, docs

**Goal:** F7 = Check Spelling; Ctrl+F7/Ctrl+Shift+F7 = inline nav; Tools menu spelling group; documentation updated.

**Deliverables:** `RegisterComposeCommands` changes; `ComposeWindow.xaml` menu restructure with matching `InputGestureText`; `docs/KEYBOARD-SHORTCUTS.md`; `docs/USER-GUIDE.md` (Check Spelling section, custom dictionary location and hand-editing note); release-notes entry for the F7 change.

**Tests:** `CommandRegistryTests`-style coverage if compose registration is testable; manual Scenario 6 step 4–5.

**Risk:** Menu access-key collisions after the move — checked by hand (Edit loses N and V items; Tools gains them — verify uniqueness within each menu).

**Duration:** 2 hours.

---

## 11. Files to Create / Modify

### Files to Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `QuickMail/Services/ICustomDictionaryService.cs` | Dictionary service contract | 20–30 |
| `QuickMail/Services/CustomDictionaryService.cs` | `custom.lex` management + editor registration | 80–120 |
| `QuickMail/Views/SpellCheckSources.cs` | `ISpellCheckSource`, `SpellingErrorInfo`, `TextBoxSpellSource`, `RichTextBoxSpellSource` | 200–300 |
| `QuickMail/Views/SpellCheckDialog.xaml` | The Spelling window | 100–150 |
| `QuickMail/Views/SpellCheckDialog.xaml.cs` | Focus choreography, F6 cycle, Escape wiring, local palette, VM↔source mediation | 250–350 |
| `QuickMail/ViewModels/SpellCheckDialogViewModel.cs` | Session state machine + announcement text | 200–300 |
| `QuickMail.Tests/SpellCheckDialogViewModelTests.cs` | VM logic tests | 250–400 |
| `QuickMail.Tests/CustomDictionaryServiceTests.cs` | Dictionary round-trip tests | 100–150 |

### Files to Modify

| File | Changes | Lines changed (est.) |
|---|---|---|
| `QuickMail/Views/ComposeWindow.xaml` | Tools menu group; Edit menu removal; `SpellCheck.IsEnabled` on SubjectBox; `IsInactiveSelectionHighlightEnabled` on three editors; `InputGestureText` updates | ±40 |
| `QuickMail/Views/ComposeWindow.xaml.cs` | Scan refactor onto sources; `compose.checkSpelling` registration; nextMisspelling/prevMisspelling default keys; dialog launch + focus restoration | ±150 |
| `QuickMail/App.xaml.cs` | Construct `CustomDictionaryService` | +5 |
| `QuickMail.Tests/StubServices.cs` | `StubCustomDictionaryService` | +20 |
| `QuickMail.Tests/XamlParseTests.cs` | SpellCheckDialog parse test | +10 |
| `docs/KEYBOARD-SHORTCUTS.md` | F7/Ctrl+F7/Ctrl+Shift+F7 table updates + dialog key table | +25 |
| `docs/USER-GUIDE.md` | Check Spelling section; custom dictionary notes | +40 |

---

## 12. Tests to Add

| Test Class | Test Methods (representative) | Coverage |
|---|---|---|
| `SpellCheckDialogViewModelTests` | ChangeAdvancesToNextError; ChangeAllAutoReplacesLaterOccurrences; ChangeAllCountsSilentReplacements; IgnoreAllSkipsAllOccurrences; NoSuggestionsSetsChangeToFocusMode; SourceTransitionAnnouncesCheckingSubject; CompletionCountsAndText; CancelPreservesCounts; AnnouncementTextNoSuggestions | Verb state machine, counters, announcement building — all against a stub `ISpellCheckSource` |
| `CustomDictionaryServiceTests` | CreatesFileOnFirstAdd; AddIsIdempotent; RaisesDictionaryChanged; SurvivesMissingFile; NonAsciiWordRoundTrip; UsesProfileContextDirectory | File format, persistence, events |
| `XamlParseTests` (existing class) | SpellCheckDialogParses | XAML loads under `[StaFact]` |
| `TextBoxSpellSource` STA tests (in a new or existing STA test class) | FindsErrorsInOrder; WrapsToStart; ReplaceCurrentUpdatesText; GetContextLineReturnsFullLine | Real `TextBox` behavior where the WPF spell engine cooperates in tests; if the engine is unavailable headless, document and rely on Scenario 6 |

Every new public method gets at least one test; the VM's every verb branch gets a case.

---

## 13. Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| **F7 rebinding breaks existing QuickMail muscle memory** | High (it will surprise someone) | Minor | Deliberate decision (classic Office parity outweighs it). `hotkeys.json` overrides survive untouched; keyboard customizations dialog allows restoring old bindings in seconds; release notes call it out prominently. |
| **Screen readers do not voice programmatic re-selection when the Suggestions list repopulates while already focused** | Medium | Major (breaks the "first suggestion voiced automatically" promise) | Fallback defined now: append the first suggestion to the announce — "Not in dictionary: \<word\>. Suggestion: \<first\>." — and accept the possible double-speech on the *initial* open only. Kelly's testing decides; her report is authoritative per project rules. |
| **WPF custom dictionary does not refresh flagged words until Uri re-registration** | High (documented WPF behavior) | Minor | Designed in: `DictionaryChanged` event → remove/re-add Uri on all three editors. Acceptance Scenario 5 verifies. |
| **User edits the document mid-check and invalidates scan position** | Medium | Major if unhandled | Designed out: position-based live re-query (Decision 2); Scenario 9 verifies. |
| **Scan-extraction refactor changes inline navigation behavior** | Medium | Major | Phase 2 is isolated and regression-tested (Scenario 6) before the dialog builds on it. |
| **RichTextBox replacement loses formatting or breaks runs** | Medium | Major | Replacement via `TextRange` over the spelling-error range only; HTML-mode step in Scenario 4 explicitly tests a bold misspelled word. |
| **Access-key collision inside the dialog or in the restructured menus** | Low | Minor | Dialog keys chosen collision-free (N/T/S/C/L/I/G/A/R + Esc); menu uniqueness checked in Phase 4. |

### 13.2 Open questions

None. All decisions resolved with Kelly on 2026-07-02: F7 = dialog (inline → Ctrl+F7/Ctrl+Shift+F7); name = "Check Spelling…" / window title "Spelling"; scope = body then subject. Persisted ignore list and dictionary-management UI are explicitly deferred to v2, not open.

---

## 14. Appendix A — Keyboard Reference

### Compose window

| Key | Action | Notes |
|---|---|---|
| `F7` | Check Spelling (dialog) | **Changed** — was Next Misspelling. Registered `compose.checkSpelling`. |
| `Ctrl+F7` | Next misspelling (inline) | **Changed** — was F7. |
| `Ctrl+Shift+F7` | Previous misspelling (inline) | **Changed** — was Shift+F7. |
| `Alt+F7` | Repeat spelling announcement | Unchanged. |
| `Alt+1/2/3` | Inline quick replace | Unchanged. |

### Spelling dialog

| Key | Action |
|---|---|
| `Alt+C` or `Enter` | Change (default button) |
| `Alt+L` | Change All |
| `Alt+I` | Ignore |
| `Alt+G` | Ignore All |
| `Alt+A` | Add to Dictionary |
| `Alt+R` | Read in Context |
| `Alt+T` | Focus Change to box |
| `Alt+S` | Focus Suggestions list |
| `Alt+N` | Focus Not-in-dictionary context box |
| `Up/Down` in Suggestions | Select suggestion (updates Change to) |
| `F6` / `Shift+F6` | Cycle context ↔ suggestions ↔ buttons |
| `Ctrl+Shift+P` | Local command palette |
| `Escape` | Close (cancel check) |

---

## 15. Implementation Guidance for AI

### 15.1 Adjustments you're expected to make

- **Exact focus mechanics on advance.** The spec says focus lands on the Suggestions list with item 1 selected. Whether that is `listBox.Focus()` + `SelectedIndex = 0` or focusing the ListBoxItem directly is yours to determine — the requirement is that the screen reader voices the suggestion and Up/Down work immediately without an extra Tab.
- **Completion window construction.** A dedicated tiny XAML window or a parameterized shared confirmation window — your call. Requirements: title "Spelling", static message text, OK button, Enter/Escape both dismiss, no editable fields.
- **`TextBoxSpellSource` internals.** Reuse as much of the existing `NavigateSpellingError` word-boundary logic as possible; the character-walk performance characteristics are already accepted in shipped code.
- **Where the change-all silent replacement executes.** VM decides *that* a re-encountered word auto-replaces; whether the loop lives in the VM (repeated MoveToNextError calls) or the mediator is an implementation detail — keep the decision logic in the VM for testability.

### 15.2 When to ask for clarification

- **The suggestion-voicing behavior (§13.1 risk 2) is empirical.** Build the focus-based design first. Kelly will test it early in Session 3; if re-selection is not voiced reliably, apply the defined fallback — do not invent a third approach without asking.
- **The keyboard walkthrough (§6) is normative.** If any key conflicts with something discovered during implementation (e.g., an Alt-chord swallowed by the menu-activation suppression hook in `ComposeWindow.xaml.cs` — check the Win32 `SuppressMenuActivationHook` interaction with the dialog's Alt access keys), stop and ask before working around it.
- If the WPF spell engine behaves differently than assumed for any API named in §5.1 Decision 2, document the discrepancy and adjust the source implementations — but keep `ISpellCheckSource` stable so the VM and tests are unaffected.

### 15.3 After implementation: acceptance walkthrough preview

Kelly runs §8 in full. The steps most likely to catch bugs:

- **Scenario 1 steps 1–3** — the announcement/voicing choreography, the heart of the feature.
- **Scenario 6** — the scan-extraction refactor is the likeliest regression source.
- **Scenario 4 (HTML mode)** — RichTextBox replacement formatting.
- **Scenario 5** — dictionary refresh without restart.

Any failure gets documented and addressed in Session 4 (code review).
