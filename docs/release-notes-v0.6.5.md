# QuickMail v0.6.5 Release Notes

## Improvements

### Spell-check announcements while composing

Three refinements to how spelling errors are announced in the compose window:

**No more mid-word interruptions while typing.** Previously, QuickMail would announce a spelling error as soon as the partial word you were typing matched a known misspelling — even before you finished the word. Now, spelling announcements are suppressed while you are actively typing. Errors are announced when you navigate (arrow keys, Home/End, mouse) or jump between errors with F7/Shift+F7, but never mid-keystroke.

**New "Announce spelling errors" toggle.** A new setting controls whether any spelling error is announced at all, regardless of how the caret arrived at the misspelled word. When turned off, F7 still navigates to and selects errors so you can fix them with Alt+1/2/3 — it just does so silently. The existing "Announce spelling suggestions" checkbox is now nested under this toggle and grays out when the parent setting is off.

The toggle is available in two places:
- **Tools → Settings → Screen Reader Announcements** — "Announce spelling errors" checkbox.
- **Compose Command Palette (Ctrl+Shift+P)** — search for "Toggle Spelling Announcements" to flip it on or off without leaving the compose window.

---

## Bug Fixes

- **Focus lost after Alt+Tab** — When switching away from QuickMail and returning (for example with Alt+Tab), keyboard focus was silently dropped instead of returning to the message you were on. You had to press Ctrl+3 or another navigation key to get focus back. QuickMail now restores focus to the correct message list row (or conversation/sender group, depending on view mode) automatically when the window comes back into the foreground.
