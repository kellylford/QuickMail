# QuickMail v0.6.3 Release Notes

## Accessibility

### Status bar keyboard navigation

The status bar now supports full keyboard exploration. Press `Ctrl+9` or cycle through panes with `F6` to reach it, then use **Left** and **Right** arrow keys to move between its four regions. Press **Tab** to exit back to the main pane cycle.

| Region | What it shows |
|--------|---------------|
| **Status** | Current activity — message counts, sync progress, search results |
| **Connection** | Connection state — Offline, Connecting…, Syncing…, N accounts connected, or Connection error |
| **Rules** | Rules summary — how many rules are active, disabled, and when they last ran |
| **Sync progress** | A progress indicator shown while mail is syncing (hidden otherwise) |

Screen readers announce each region individually as focus moves, using the region's current value as the accessible name. The status bar also exposes the UIA StatusBar control pattern, which enables screen readers to read the entire bar with their built-in status-bar commands.

### Connection status region

A new **Connection** region in the status bar shows the current state of your account connections at a glance. The value updates in real time:

- **Offline** — no accounts are connected
- **Connecting…** — accounts are being connected
- **Syncing…** — a background sync is in progress
- **N accounts connected** — all accounts are online
- **Connection error** — a sync attempt failed

### Rules status region is now a button

The Rules summary in the status bar was previously a styled read-only text element with no accessible role. It is now a proper **Button** (ControlType.Button). Screen readers correctly announce it as interactive, and pressing **Enter** or **Space** opens the Rules Manager — the same as selecting **Rules…** from the menu or pressing `Ctrl+Shift+L`.

---

## Internal

- Removed `StatusBarAccessibleName` ViewModel property; status bar name is now derived from a XAML binding, keeping accessibility label logic in the View layer.
- `AutomationProperties.Name` consolidated onto child elements only, eliminating potential for double-announcement by screen readers when navigating the UIA tree.
