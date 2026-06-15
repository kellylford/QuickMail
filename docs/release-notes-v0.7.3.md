# QuickMail v0.7.3 Release Notes

## Download

Two options are available for v0.7.3:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.3-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New Features

### Flag Messages

You can now flag messages for follow-up — and QuickMail stays in sync with flags you set in Outlook, on your phone, or in any other mail client.

**One keypress to flag or unflag:** Press `K` on any message in the list to toggle its flag. Press `K` again to clear it. No menu required. In Conversations, From, or To view, pressing `K` on a group row flags every message in the group at once.

**Named, color-coded flags:** QuickMail ships with one built-in "Flagged" flag. You can create up to 20 named flags with your choice of color — for example, "Urgent" in red, "Waiting" in yellow, "Done" in green. Open the Flag Manager from the command palette (`Ctrl+Shift+P` → "Manage Flags…") to create, rename, recolor, reorder, and delete flags.

**Choose which flag `K` applies:** In the Flag Manager, select any flag and choose "Set as K default." From that point, `K` applies that flag instead of the built-in one. The setting survives app restarts.

**Pick a specific flag:** Press `Ctrl+Shift+K` to open the flag picker and choose any of your named flags by keyboard. Arrow to the flag you want and press Enter. A "Clear flag" option at the bottom removes whatever flag is currently set.

**Filter to flagged messages:** The message filter now includes a "Flagged" option. Select it from the View menu or filter combobox to see only flagged messages in the current folder. You can save a flagged view with a hotkey via the View Manager.

**All Flagged Mail:** A new "All Flagged Mail" virtual folder appears in the folder tree, aggregating flagged messages across all your accounts in one place, sorted newest first.

**Cross-client sync:** Flags set in Outlook, on your phone, or in any other IMAP client appear in QuickMail on the next sync cycle. Flags set in QuickMail are written to the server immediately and visible in other clients. Microsoft 365 accounts use the Graph `followUpFlag` API.

### Flag Accessibility

**Flag name is announced first:** When you arrow to a flagged message, the flag name is spoken before read status — for example, "Urgent. Unread. Kelly Ford. Budget deadline. 6/13/2026." This makes it immediately clear a message needs attention without having to navigate past the sender and subject first.

**Flag toggle is confirmed aloud:** Pressing `K` announces the outcome: "Flagged: Urgent." or "Unflagged." or "Flagged 4 messages: Urgent." for a group. These announcements respect the "Announce results" preference.

**The announcement is optional:** If you prefer not to hear flag names while navigating the message list, turn off "Announce flag status" in Settings → General → Accessibility. The flag indicator remains visible on screen; only the spoken name is suppressed.

**Color is never the only indicator:** Every flagged message row shows the flag name in the status column text in addition to the colored indicator. The flag name is always in the accessible name of the row regardless of color settings.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Flagging

- `FlagDefinition` model (`Models/FlagDefinition.cs`): `Id` (Guid), `Name`, `ColorHex`, `SortOrder`, `IsBuiltIn`. One built-in "Flagged" flag with a well-known constant `Id`. Maximum 20 user-defined flags.
- `flags.json` in the profile directory, managed by `FlagService`. Atomic temp-then-rename writes. Corrupt file renamed to `flags.json.bak-{timestamp}`; built-in flag restored.
- `IFlagService` / `FlagService`: `ToggleDefaultFlagAsync`, `SetMessageFlagAsync`, `SetMessageFlagBatchAsync`, `GetKDefaultFlag`, `SetKDefaultFlagAsync`. Optimistic in-memory update; rolls back on failure. `FlagDefinitionsChanged` event for consumers to refresh denormalized names/colors.
- `MailMessageSummary` additions: `FlagId` (Guid string, null = unflagged), `FlagName`, `FlagColorHex` (denormalized), `FlagLabel`, `IsFlagged`. `StatusDisplay` shows flag name when flagged.
- SQLite schema migration 3 → 4: `ALTER TABLE MessageSummary ADD COLUMN flag_id TEXT NULL DEFAULT NULL`.
- `UpdateFlagIdAsync` / `UpdateFlagIdBatchAsync` on `ILocalStoreService`. Both wrapped in their own try/catch in `FlagService` so `SqliteException` (e.g., `--online` mode) does not prevent the IMAP/Graph flag from being set.
- IMAP write: `SetMessageFlaggedAsync` via `AddFlagsAsync` / `RemoveFlagsAsync` with `MessageFlags.Flagged`. Routed through `MailServiceRouter`.
- Graph write: PATCH `/messages/{id}` with `{ "flag": { "flagStatus": "flagged" } }` or `"notFlagged"`. `GraphFollowUpFlag` DTO added to `GraphDtos`.
- IMAP ↔ local reconciliation in `SyncService.OnFolderSynced`: `IsServerFlagged=true` + local `flag_id IS NULL` → set `flag_id` to default flag. `IsServerFlagged=false` + local `flag_id IS NOT NULL` → clear. Local named flag is preserved when the server flag is also set.
- `MessageFilter.Flagged` value added; `BuildFilteredMessages` in `MainViewModel` filters to `msg.IsFlagged`.
- `SavedView.FlagFilter` (nullable string): secondary filter to a specific named flag applied after `MessageFilter`.
- `\x00AllFlagged` virtual folder: aggregates flagged messages across all accounts, same merge pattern as `\x00AllInboxes`.
- `AnnounceFlagStatus` config key (bool, default `true`): controls whether `FlagLabel` is prepended to the message row accessible name. Wired to Settings → General → Accessibility.
- `ConfigModel.DefaultFlagId`: the Guid of the flag `K` applies. Falls back to the built-in flag if the stored Guid is missing or was deleted; config updated silently.
- `FlagManagerViewModel` / `FlagManagerWindow`: CRUD for named flags, color picker (12 preset colors + hex input, radio group with `DirectionalNavigation="Cycle"`), move up/down, Set as K default. VM does not raise `FlagDefinitionsChanged` directly; parent reloads after `ShowDialog()` returns to avoid COM apartment reentrancy.
- `FlagPickerViewModel` / `FlagPickerWindow`: inline popup for `Ctrl+Shift+K`; lists flags + "Clear flag"; focus restored to originating message row on close.
- `mail.toggleFlag` (K), `mail.pickFlag` (Ctrl+Shift+K), `mail.openFlagManager` (unassigned) registered in `CommandRegistry`.
- `MessagePropertiesBuilder`: "Flag" row added to Storage section.
- `BuildMessageAccessibleName` helper in `MainWindow.xaml.cs` centralizes the accessible name string so it is not duplicated across view modes.

---
