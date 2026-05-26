# QuickMail v0.6.6 Release Notes

## New Features

### About dialog

**Help → About QuickMail** now opens an About dialog showing the application version and a link to the MIT License on GitHub. The same dialog is accessible from the Command Palette (**Ctrl+Shift+P**) — search for **About QuickMail**.

---

## Bug Fixes

- **Reading pane closed by incoming mail** — When new mail arrived while you were reading a message, the reading pane appeared to close and focus jumped back to the message list. This happened because QuickMail was incorrectly moving focus to the message list as part of updating the display for the new messages. QuickMail now leaves focus in the reading pane when new mail arrives.
