# QuickMail v0.8.33 Release Notes

> **Draft** — started from what has merged to `main` since v0.8.32. Add to the sections below as more lands before the release is tagged.

## Download

Two options are available for v0.8.33:

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.33-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This release fixes **opening a message in a tab**, which previously showed a copy of the message list with the message crammed into a small strip at the bottom instead of showing just the message. If you installed QuickMail from the MSI, this update is delivered automatically. Everything from v0.8.32 — contact sync, easy folder creation, personal Microsoft sign-in, and the reply-quoting fix — is included.

---

## Fixed in 0.8.33

### Opening a message in a tab now shows just the message

When you set messages to open in a **tab** (Settings → Windowing → Reading mode → Tab), activating a message opened a tab that showed a *copy of the whole message list* with the message itself squeezed into a small, fixed strip at the bottom — not the message on its own. Opening in a **window** worked correctly.

Tabs now behave as expected: opening a message in a tab fills the pane with that message, and the message list is set aside until you return to it. The message list is still there whenever you need it —

- **Escape** from an open message returns you to the message list (the message's tab stays open).
- **Ctrl+W** closes the message's tab and returns you to the list.
- The **tab strip** (Ctrl+Shift+T, or F6 to reach it) lets you switch between the list and any open messages.

The list also stays visible while a message is still loading, and if a message fails to load the list remains on screen rather than leaving the pane blank.

---

## Accessibility

- With a message open in a tab, the message list is removed from the view and from **F6** pane cycling, so F6 moves cleanly between the folder tree, the tab strip, and the open message without stopping on a list that isn't shown. Pressing **Escape** brings the list back and returns focus to it. While a message is loading — or if its load fails — the list stays in place and in the F6 cycle, so focus is never stranded on an empty pane.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the report that identified the tab-open behavior during the theming review.

---

## Internal

### Tab open-mode showed the message list instead of the message (issue #177, PR #264)

- In `MessageOpenMode.Tab`, the right-pane content region left the message-list container visible (it was the `DockPanel` fill child) while the reading pane showed as a `MinHeight=200` `Dock=Bottom` sliver, so a message tab rendered the list plus a body strip rather than the message alone. Window mode was unaffected (separate `MessageWindow`); Reading-Pane mode was unaffected.
- The content region is now a two-row `Grid` whose row sizes swap on a new `IsMessageListAreaVisible` VM flag via `BoolToGridLengthConverter`. The flag is `!(MessageOpenMode == Tab && ActiveTab is MessageTabViewModel && IsMessageOpen)` — the `IsMessageOpen` term keeps the list visible during the async body load and if the load fails/returns null, so a slow or failed open never blanks the pane (Feature Checklist rule 4). `CycleFocusAsync` gates the message-list F6 stop on the same flag; the window-level **Escape** handler routes to `ActivateMessageListTab()` (revealing the list, leaving the tab open) in Tab mode instead of `CloseReadingPane`.
- Reading-Pane and Window layouts are byte-for-byte equivalent (the flag is always true outside Tab mode). Independent review caught and fixed the transient/failed-load blank-pane case before merge. Adds `TabModeMessageListVisibilityTests` (8 cases).
- Brian Vogel's #177 review also surfaced two still-parked, pre-existing items — the Reading-Pane reading pane not being resizable, and account deletion triggering repeated re-auth prompts — neither of which is addressed here.
