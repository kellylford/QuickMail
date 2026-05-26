# QuickMail v0.6.5 Release Notes

## Bug Fixes

- **Focus lost after Alt+Tab** — When switching away from QuickMail and returning (for example with Alt+Tab), keyboard focus was silently dropped instead of returning to the message you were on. You had to press Ctrl+3 or another navigation key to get focus back. QuickMail now restores focus to the correct message list row (or conversation/sender group, depending on view mode) automatically when the window comes back into the foreground.
