# QuickMail v0.7.9.2 Release Notes

## Download

Two options are available for v0.7.9.2:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.9.2-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This is a small follow-up release with quality and accessibility fixes to the Calendar view; there are no changes to mail handling.

---

## Bug Fixes

- **Refreshing the Calendar now actually refreshes the Calendar.** Pressing F5, choosing the toolbar Refresh button, choosing View → Refresh, or running Refresh from the Command Palette while viewing the Calendar previously triggered the mail refresh instead — which silently did nothing, since the Calendar folder has no mail to fetch. All four now correctly refresh the calendar's event list.

- **Accepting, declining, or marking an invitation tentative now updates what you hear immediately.** Previously, the calendar list's spoken summary for an event did not reflect a changed response status until the list was reloaded from scratch.

- **Calendar keyboard shortcuts are now visible and customizable.** `T` (filter to today) and `Enter` (open the source invitation) in the Calendar list are now registered like every other shortcut in the app, so they show up in the Keyboard Customizations dialog and the Command Palette and can be rebound. As part of this fix, holding a modifier key no longer causes them to misfire — for example, `Ctrl+T` or `Alt+Enter` while browsing the calendar no longer incorrectly triggers the today filter or opens the invitation.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback.

---

## Internal

### Calendar fixes (PR #178)

These fixes closed out review findings from an earlier calendar feature branch that never made it back to main after the calendar feature shipped; auditing which of those findings still applied against today's code turned up real, still-present gaps.

- `CalendarEvent.ResponseStatus` now carries `[NotifyPropertyChangedFor(nameof(DisplayLine))]`, so the bound `DisplayLine` text (and any screen-reader announcement tied to it) updates the moment the response status changes.
- `MainViewModel.RefreshAsync` — the single command bound to the View menu, the toolbar button, and the Command Palette's "Refresh" entry — now checks `IsCalendarView` and delegates to `CalendarViewModel.RefreshCommand` before doing any mail-folder work. Making the shared command itself calendar-aware means every entry point agrees, rather than only the F5 keyboard gesture.
- `calendar.toggleTodayFilter` and `calendar.openSourceMessage` are now registered in `CommandRegistry` (scoped to `CalendarList.IsKeyboardFocusWithin`), replacing a hardcoded `PreviewKeyDown` switch that had no modifier guard.
- Moved a `MailMessageSummary` stub-construction and account-selection decision out of `MainWindow.xaml.cs` code-behind and into `MainViewModel.OpenCalendarSourceMessage`, matching the project's MVVM rules and removing logic that duplicated what `SelectMessageAsync` already does internally.
- Two new test files (`CalendarEventTests`, `MainViewModelCalendarTests`) cover the change notification and the refresh-delegation behavior. Full test suite: 929/929 passing.

### Version

- Bumped to `0.7.9.2` (`Version`, `AssemblyVersion`, `FileVersion`).
