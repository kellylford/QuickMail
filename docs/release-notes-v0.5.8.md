# QuickMail v0.5.8 Release Notes

## New Features

### Message search

A new inline search bar lets you filter the message list by sender, subject, or body preview without leaving the keyboard.

- Press `Ctrl+Shift+S` or `/` from the message list or folder tree to open the search bar
- Results update as you type; the count ("3 found") is shown next to the search box
- Press `Down Arrow` or `Tab` from the search box to move directly into the filtered results
- Press `Escape` to clear the search and return focus to the message list
- Search works across all view modes (Messages, Conversations, By Sender, By Recipient)
- Results stay current as background sync adds new messages

### Message filters

A filter bar above the message list lets you narrow down what is shown in any folder:

| Filter | What it shows |
|--------|--------------|
| All | Every message (default) |
| Unread | Messages not yet read and not replied to or forwarded |
| Read | Messages that have been opened |
| With Attachments | Messages that have at least one attachment |
| Replied | Messages you have replied to |
| Forwarded | Messages you have forwarded |

Filters and search can be used together. Selecting a different folder clears both.

### Screen reader announcement controls

Users can now tune or silence the custom announcements QuickMail adds on top of native control speech.

**Settings → General → Screen Reader Announcements** exposes four independent toggles:

| Setting | What it controls |
|---------|-----------------|
| Enable custom announcements | Master switch; when off, only native focus and selection events are spoken |
| Announce hints and instructions | Instructional text shown the first time you use a feature (e.g. how to exit the search box or the message body) |
| Announce loading and sync status | Background progress updates — folder sync counts, connection state |
| Announce action results | Outcomes of things you did — search result counts, move and copy confirmations |

A **Toggle Custom Announcements** command is also available in the command palette. You can assign it a hotkey in Settings → Keyboard Shortcuts so you can silence or restore announcements with a single key press at any time.

---

## Accessibility

### Quieter control names

The account list, folder tree, message body, search box, and clear search button previously had navigation instructions embedded in their accessible names (e.g. "Folders. Arrow keys to navigate and expand, Enter to load messages."). These instructions are now removed from the control names, which were read aloud on every focus event. Where useful, the instructions are now delivered as one-time Hint announcements that can be silenced.

### Double-reading fix

Each message in the list was being read twice when navigating by arrow key — once by the tree-view element and once by a manual announcement. The redundant announcement has been removed; the native focus event is now the sole source of speech for list navigation.

---

## Bug Fixes

- **Search missing messages** — Search would find only one result in folders with hundreds of messages because background sync was not updating the unfiltered message store. New messages added by sync are now reflected in search results immediately.
- **Search box losing focus** — Typing in the search box would unexpectedly return focus to the folder tree after each keystroke. Fixed by correctly identifying the search box as an active UI element that should retain focus.
- **Unread filter including replied/forwarded** — Messages you had replied to or forwarded were being shown by the Unread filter. These are now excluded.
- **Filter bypass on folder change** — Switching folders while a filter was active could bypass the filter and show all messages. The filter is now correctly applied after every folder load.
