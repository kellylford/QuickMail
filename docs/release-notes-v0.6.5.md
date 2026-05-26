# QuickMail v0.6.5 Release Notes

## Improvements

### Spell-check announcements while composing

Three refinements to how spelling errors are announced in the compose window:

**No more mid-word interruptions while typing.** Previously, QuickMail would announce a spelling error as soon as the partial word you were typing matched a known misspelling — even before you finished the word. Now, spelling announcements are suppressed while you are actively typing. Errors are announced when you navigate (arrow keys, Home/End, mouse) or jump between errors with F7/Shift+F7, but never mid-keystroke.

**New "Announce spelling errors while navigating" toggle.** A new setting controls whether spelling errors are announced as you arrow through the message body. When turned off, moving the caret into a misspelled word is silent — but **F7 and Shift+F7 always announce**, so you can still jump from error to error and hear each one. Alt+1/2/3 replacement works either way. The existing "Announce spelling suggestions" checkbox is now nested under this toggle and grays out when the parent setting is off.

The toggle is available in two places:
- **File → Settings → Screen Reader Announcements** — "Announce spelling errors while navigating" checkbox.
- **Compose Command Palette (Ctrl+Shift+P)** — search for "Toggle Spelling Announcements" to flip it on or off without leaving the compose window.

---

## Bug Fixes

- **Focus lost after Alt+Tab** — When switching away from QuickMail and returning (for example with Alt+Tab), keyboard focus was silently dropped instead of returning to the message you were on. You had to press Ctrl+3 or another navigation key to get focus back. QuickMail now restores focus to the correct message list row (or conversation/sender group, depending on view mode) automatically when the window comes back into the foreground.
