# QuickMail v0.7.9.1 Release Notes

## Download

Two options are available for v0.7.9.1:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.9.1-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This is a small follow-up release on top of v0.7.9; there are no functional changes to mail handling.

---

## Bug Fixes

- **Stray underscores across the interface are gone (issue #168).** In several places the interface showed literal underscore characters — field labels in the Manage Accounts and Add Account dialogs (for example "_Account Name", "SMTP H_ost"), the From and Subject labels in Compose, the Insert Link labels, and every button on the main toolbar ("_New", "Reply _All", "For_ward", "Refres_h"). Those underscores are the markers that turn a letter into a keyboard access key, and in these spots they were being drawn on screen instead of doing their job — which also meant a screen reader read the underscore aloud as part of the field name. Field labels now register their access key correctly (the underscore becomes an underline shown when you press Alt, and screen readers announce a clean name). Toolbar buttons no longer carry access keys at all, matching how standard Windows toolbars work — they read as plain text (New, Reply, Reply All, Forward, Delete, Refresh, Empty Trash, Accounts, User Guide) and are still reachable by their existing Ctrl shortcuts, tooltips, and Tab/arrow navigation.

- **The Help menu now shows the running version (issue #169).** When you check for updates and you are already up to date, the entry reads "No updates available — running version 0.7.9.1", so you can always confirm which build you are on. The version reported to your screen reader and to File Explorer is now clean digits as well, rather than a long string with an appended build identifier.

---

## Thank You to Contributors

Special thanks to **Brian Vogel (@britechguy)** for reporting issue #168, which flagged the stray underscores scattered through the interface. Those characters had lingered longer than they should have — a good reminder that even when you navigate QuickMail by screen reader every day, it is easy to stop noticing a rough edge you have walked past a hundred times. Thank you, Brian, for the careful eye and the clear report; this is exactly the kind of feedback that keeps the app honest and makes it better for everyone.

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback.

---

## Internal

### Access-key cleanup (issue #168)

- **Field labels** in `AccountManagerDialog`, `AddAccountDialog`, `ComposeWindow` (From/Subject), and `InsertLinkDialog` were `TextBlock`s with underscore mnemonics. `TextBlock` does not process access keys, so the underscore rendered literally and, because each label is the field's `AutomationProperties.LabeledBy` source, a screen reader announced "underscore Account Name" as the field name. They are now `Label` controls with a `Target` binding (the pattern `AddressBookWindow` already used), so the mnemonic renders as an underline and the accessible name is clean.

- **Mnemonic letters were reworked** to remove access-key collisions (both within the dialogs and against the top-level menu letters), rather than the earlier self-colliding scheme that forced mid-word placements.

- **Toolbar buttons** rendered their underscores literally because a WPF `ToolBar` re-templates its child buttons with a flat template whose `ContentPresenter` leaves `RecognizesAccessKey = false`. Verified with a `RenderTargetBitmap` probe: plain buttons, menu items, and labels recognize the key; only `ToolBar` children do not. Rather than force the mnemonic through `AccessText`, the toolbar access keys were removed — every toolbar button already has a Ctrl accelerator, a tooltip, and an `AutomationProperties.Name`. Menu (`MenuItem.Header`) mnemonics were left unchanged, as they render correctly and are standard.

### Running version and version reporting (issue #169)

- The Help "check for updates" entry now appends the running version at rest, sourced from a shared `Helpers.AppVersion.Display` helper.
- `IncludeSourceRevisionInInformationalVersion` is set to `false`, so the informational/product version is clean digits (`0.7.9.1`) instead of `0.7.9.1+<git sha>` — that value is what File Explorer's Product version and screen readers report programmatically.
- `AppVersion.Display` shows the 4th (revision) component only when it is non-zero, so normal releases read "0.7.9" and a hotfix reads "0.7.9.1". It is used by the About dialog, the Help entry, the SMTP User-Agent, and the update check. Because the update check parses this string, the hotfix build compares at full precision and does not mistake its own release for a newer one.

### Version

- Bumped to `0.7.9.1` (`Version`, `AssemblyVersion`, `FileVersion`).
