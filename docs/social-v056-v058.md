# QuickMail v0.5.6–0.5.8 — Mastodon posts

Each chunk is under 450 characters. Post as a thread.
Replace @serrebidev with their Mastodon handle if known.

---

**[1/7]**

Several changes have been made to QuickMail over the past few days. The 0.5.8 release is now out. Highlights below. A special thanks to @serrebidev — the first outside contributor — for significant performance and accessibility work in v0.5.7.

---

**[2/7] v0.5.6 — Address book & compose**

• Built-in address book (Ctrl+Shift+B) — add, search, and manage contacts
• Autocomplete in To/Cc/Bcc as you type, sorted by recency
• Grab all addresses from a message in one action (Ctrl+Shift+G)
• Menu bar added with keyboard shortcuts shown for every item

---

**[3/7] v0.5.7 — Settings & performance**

• Settings dialog for view mode, sync range, preview lines, and more
• Assign custom keyboard shortcuts to any command — conflict detection included
• Folder opens paint cached messages immediately; IMAP refreshes in the background (@serrebidev)
• Messages prefetch so opening them feels instant (@serrebidev)

---

**[4/7] v0.5.7 — HTML rendering & connections**

• Heavy HTML built off the UI thread; complex messages use a simplified reader mode (@serrebidev)
• Bounded IMAP connection pool — background sync can't starve message opens (@serrebidev)
• Move/Copy to Folder now works on whole sender or recipient groups

---

**[5/7] v0.5.7 — Accessibility & security**

• Reading pane focus restored automatically after a message loads (@serrebidev)
• Screen readers get a clean single update at sync end, not a stream of events
• Attachment filenames sanitised to prevent path traversal
• Email addresses no longer written to the log at default log level

---

**[6/7] v0.5.8 — Search & filters**

• Inline search (Ctrl+Shift+S or /) filters the list by sender, subject, or preview as you type
• Press Down or Tab from the search box to jump into results; Escape to clear
• Filter bar: Unread, Read, With Attachments, Replied, Forwarded
• Filters and search can be combined

---

**[7/7] v0.5.8 — Screen reader announcement controls**

• New setting to toggle announcements by category: hints, sync status, action results
• Quick-toggle command in the command palette, assignable to a hotkey
• Instructional text removed from control names — delivered as optional hints instead
• Fixed: messages no longer read twice when navigating by arrow key
