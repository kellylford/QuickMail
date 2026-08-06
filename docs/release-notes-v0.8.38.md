# QuickMail v0.8.38 Release Notes

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail-win-arm64.msi`** — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |
| **`QuickMail-arm64.exe`** — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: choose which calendar new appointments start on

If most of your appointments belong on one calendar, you no longer have to steer the **Calendar** picker away from **Local Calendar** every time you create one.

In the folder tree, move to the calendar you want — **Local Calendar**, an account, or one of the calendars beneath an account — open its context menu with **Shift+F10** or the Applications key, and choose **Use as Default Calendar for New Appointments**. From then on the appointment editor opens on that calendar. **Clear Default Calendar** on the same menu goes back to the local calendar. Both are also in the Command Palette (**Ctrl+Shift+P**) and can be given keys in **Settings → Keyboard**.

The calendar you picked is marked **(default)** in the folder tree and its name is announced as "…, default calendar", so which one is set is something you can check rather than remember.

Two things it deliberately does not do. It does not change which events you are *looking at* — that is still whatever you have selected in the tree. And it is a starting point, not a rule: the **Calendar** picker in the editor still sends any individual appointment wherever you want. ([#497](https://github.com/kellylford/QuickMail/issues/497))

## Fixed: the calendar's context menu offered mail folder actions

The context menu on the **Calendar** node and everything under it was the mail folder menu — **New Folder**, **Move Folder**, **Set as Archive Folder**, **Delete Folder**. None of them mean anything on a calendar, and every one of them did nothing at all when activated. That menu is now the calendar's own. ([#497](https://github.com/kellylford/QuickMail/issues/497))

## Fixed: Run on Existing Mail has a button again with a Microsoft 365 account

With a Microsoft 365 account, **Tools → Rules** opens the Rules Manager that works one account at a time, and in v0.8.37 that window had no **Run on Existing Mail** control — the only way to reach it was **Ctrl+Shift+P**. It is now a button beside the others, and is also on the rule list's context menu, alongside the Command Palette entry that was already there.

It runs **the account shown in the picker**, not every account. That is the difference you would expect from a window that shows you one account at a time: it never acts on rules for a mailbox you are not looking at. The Rules Manager you get without a Microsoft 365 account lists every account together and still covers them all, unchanged.

The button is turned off when the account in the picker has no enabled QuickMail rules to run — a mailbox whose rules are all server-side has nothing for it to do, because server rules run in Exchange and cannot be applied to existing mail from here. The outcome is written to the window's status line as well as announced, so it is there to read whether or not you have announcements turned on. ([#493](https://github.com/kellylford/QuickMail/issues/493))

## Fixed: "Show field labels in the rules list" now works with a Microsoft 365 account

The checkbox is in **Settings → General** for everyone, but only the Rules Manager you get *without* a Microsoft 365 account was reading it. With one, turning it on changed nothing.

Both windows honor it now. With it on, a rule in the account-at-a-time window reads as "Rule Newsletters, runs on server, status enabled" rather than running the pieces together. The setting is read when the Rules Manager opens, so change it in Settings and then open Rules to hear the difference. ([#493](https://github.com/kellylford/QuickMail/issues/493))

## Fixed: three Settings checkboxes were read out with words that were not their labels

The three **Show field labels in the … list** checkboxes — contact list, calendar event list, rules list — were each read out with "accessible names" tacked on the end, and the contact one called the list "the address book contact list" rather than what the label says. Each is now read out as its own label. ([#493](https://github.com/kellylford/QuickMail/issues/493))

---

## Internal

Everything below is developer detail — implementation notes, test coverage, and build changes. Nothing here is needed to use QuickMail.

### Calendar

- **The default is stored as the tree node's own tail encoding, so there is one parser rather than two.** `ConfigModel.DefaultCalendarSource` holds exactly what follows `CalendarSourcePrefix` on the node the user chose — `local`, `{guid}`, or `{guid}|{escapedCalId}` — and `MainViewModel.DefaultCalendarFilter` reads it back through the existing `CalendarFilterFor`. Storing an account/calendar pair instead would have meant a second encoding to keep in step with the tree's, for no gain: the setting is only ever produced by, and only ever refers to, a node in that tree. Empty means no preference, which is distinct from an explicit `local` in the config file but identical in behavior.
- **Honoring the default is a preselection in `NewEvent`, not a change to `BuildSaveTargets`.** `EventEditorViewModel.SelectTarget(accountId, calendarId)` moves `SelectedTargetIndex`; the target list itself still puts Local at index 0 unconditionally. That keeps the no-default path byte-for-byte what it was, and keeps the fallbacks in one testable place. The fallback ladder matters because the tree and the picker do not offer the same set: the tree shows a node per *discovered* calendar for any account with more than one, while only iCloud contributes a target per calendar (Microsoft and Google contribute one, their default calendar). So an exact `(account, calendar)` match is tried first, then any target on the same account — landing on "that account" rather than on Local, which is a different mailbox entirely — and only a default whose account is gone at all leaves the editor on Local. `SelectTarget` returns whether it matched so the fallback is observable in tests rather than inferred.
- **Calendar nodes now carry `IsCalendarNode` and get their own context menu.** The `ItemContainerStyle` set `FolderContextMenu` on every tree item including the Calendar subtree, where all five entries fell through `IsMovableFolder`'s `\0` guard and silently did nothing. A `DataTrigger` on the new flag swaps in `CalendarContextMenu`. The menu is declared in `Window.Resources` and referenced from the trigger's setter — the XAML-compiler crash the neighbouring comment documents is about *declaring* a Click-handler menu inside a `Style.Setter.Value`, which this does not do.
- **The marker lives in `AutomationName`, not only in `ItemStatus`.** `FolderTreeNode.IsDefaultCalendar` appends ", default calendar" to the accessible name and drives a `(default)` badge modelled on the unread badge. This is the #227 finding applied to a second piece of state: a marker carried only in `ItemStatus` is not reliably spoken, and a default the user cannot hear is a default they cannot check. `PersistDefaultCalendar` moves the marker between existing node objects via `MarkDefaultCalendarNodes` rather than rebuilding the tree, which would replace every node and throw keyboard focus out of the item the user just acted on; `BuildFolderTree` re-applies it after a genuine rebuild.
- **Both commands are registered, so the context menu is not the only way in.** `calendar.setDefaultCalendar` and `calendar.clearDefaultCalendar` are Calendar-category `CommandDefinition`s with no default key — palette-reachable and rebindable. That is the #250 lesson (folder creation was context-menu-only and therefore undiscoverable without a mouse) applied up front. Refusals are sentences, not silence: the bare Calendar node and All Calendars are not one calendar, and `SetDefaultCalendar` returns why, which the View sends to both `StatusText` and a `Result` announcement.

### Rules

- **Run on Existing Mail was the same parity gap as Test Rule, one sweep later, and it carried a scope decision with it.** The unified window had the command wired and registered but no button and no context-menu item, so it was palette-only for every Microsoft 365 user — the one the v0.8.37 Test-Rule sweep missed (#493). The mechanical half was the easy half. `RunClientRulesOnExisting` had always built its Inbox map over **every** account in `CachedFolders`, which was unambiguous in the client-only window because that window lists every account together; under an account picker, a button directly below **Account: Work** would have read as "run this for Work" while processing every mailbox. `RunOnExistingRequested` now carries a `Guid?`: the unified window passes `SelectedAccount.Id` and the shared handler filters the map to it; the client-only window passes `null` and keeps its all-accounts run, byte-for-byte unchanged. The scope is enforced where it is load-bearing rather than cosmetically — `ApplyRulesToExistingAsync` only ever touches messages whose account is a key in that map, so an *unscoped* legacy rule reaching an out-of-scope mailbox is not possible. The scope decision then settled the `CanExecute` question filed alongside it: because the run is now exactly the account on screen, "this account has no enabled QuickMail rules" is an unambiguous statement, so `CanRunOnExisting` gates on it. A Graph mailbox whose rules are all server-side therefore gets a disabled control rather than one that reports "0 moved". The outcome also sets `StatusText`, not just `Announce`: the same `Result`-category reasoning as Test Rule, since a newly surfaced button whose only feedback is an announcement is imperceptible to a user running with announcements off, error included.
- **`RuleListShowFieldLabels` was read by one window and ignored by the other.** The **Show field labels in the rules list** checkbox is in Settings for every user regardless of account type, but only `RulesManagerViewModel` read it — so for a Microsoft 365 user the checkbox silently did nothing. `UnifiedRuleRow` now takes the flag through its `ForServer`/`ForClient` factories and prefixes each field in `RowText`: `Rule Newsletters, runs on server, status enabled` rather than `Newsletters, on server, enabled`. Read once when the manager opens, matching the client window, so a Settings change applies the next time it is opened. Note the one asymmetry between the two windows: in the client window the setting shapes `AccessibleName` only, while `UnifiedRuleRow.ToString()` returns `RowText`, so here it changes the visible row text too. The detail pane is untouched either way — it always carries the full definition.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
