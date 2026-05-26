# QuickMail v0.6.5 Release Notes

## Improvements

### Spell-check announcements while composing

QuickMail's spell-check announcement behavior has been refined and is now fully configurable.

**The default experience.** By default you will not hear about spelling errors as you are typing. You will hear about them when navigating through text with cursor keys, or using F7 and Shift+F7 to jump from spelling issue to spelling issue. When a spelling issue is encountered, you will hear the incorrect spelling and three possible replacements. With focus on the error, press Alt+1, Alt+2, or Alt+3 to select one of the three replacements. You can also press Shift+F10 to bring up a context menu with additional suggestions and an option to ignore the word.

**Three independent settings.** Open **File → Settings**, select the **General** tab, and look in the **Screen Reader Announcements** section:

- **Announce spelling errors when typing** — turn on to hear errors while typing. Announcements are held until you pause so you hear the complete word, not a partial one. Off by default.
- **Announce spelling errors while navigating** — turn off to silence errors during cursor-key navigation. F7 and Shift+F7 always announce regardless of this setting. On by default.
- **Alt+F7** — repeats the spelling error and suggestions for the word at the current caret position. Useful after your screen reader's Say Line command to re-hear the error details.
- **Announce spelling suggestions** — controls whether up to three replacement suggestions are spoken alongside the misspelled word. On by default.

**Quick toggle from the compose window.** Open the compose Command Palette with **Ctrl+Shift+P** and search for **Toggle Spelling Announcements** to flip the "announce while navigating" setting on or off without leaving the compose window. QuickMail confirms the change aloud. F7 and Shift+F7 always announce regardless of this toggle.

---

## Bug Fixes

- **Focus lost after Alt+Tab** — When switching away from QuickMail and returning (for example with Alt+Tab), keyboard focus was silently dropped instead of returning to the message you were on. You had to press Ctrl+3 or another navigation key to get focus back. QuickMail now restores focus to the correct message list row (or conversation/sender group, depending on view mode) automatically when the window comes back into the foreground.
