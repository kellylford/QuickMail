# QuickMail v0.6.5 Release Notes

## Improvements

### Spell-check announcements while composing

Three refinements to how spelling errors are announced in the compose window:

**No more mid-word interruptions while typing.** Previously, QuickMail would announce a spelling error as soon as the partial word you were typing matched a known misspelling — even before you finished the word. Now, spelling announcements are suppressed while you are actively typing. Errors are announced when you navigate (arrow keys, Home/End, mouse) or jump between errors with F7/Shift+F7, but never mid-keystroke.

**Three independent spelling announcement settings.** The Screen Reader Announcements section in **File → Settings** now has three separate spelling controls:

- **Announce spelling errors when typing** — when on, errors are announced while you type. A short pause after typing triggers the announcement so you hear the complete word rather than a partial one. Off by default.
- **Announce spelling errors while navigating** — when on, errors are announced as you arrow through the message body and the caret moves into a misspelled word. On by default. F7 and Shift+F7 always announce regardless of this setting.
- **Announce spelling suggestions** — when on, up to three correction suggestions are spoken alongside the error. On by default.

The **Compose Command Palette (Ctrl+Shift+P)** includes a "Toggle Spelling Announcements" command that flips the "while navigating" setting on or off without leaving the compose window.

---

## Bug Fixes

- **Focus lost after Alt+Tab** — When switching away from QuickMail and returning (for example with Alt+Tab), keyboard focus was silently dropped instead of returning to the message you were on. You had to press Ctrl+3 or another navigation key to get focus back. QuickMail now restores focus to the correct message list row (or conversation/sender group, depending on view mode) automatically when the window comes back into the foreground.
