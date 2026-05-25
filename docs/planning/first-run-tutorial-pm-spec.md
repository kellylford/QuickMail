# First-Run Accessibility Tutorial — PM Specification

**Status:** Ready for Dev
**Date:** May 24, 2026
**Target:** Phase 3 (Differentiators)
**Crew:** Charlie (PM → Dev Lead → Test Enforcer)

---

## User Problem

A new screen reader user opening QuickMail for the first time gets no orientation. They need to discover F6 pane cycling, Ctrl+0-3 direct pane focus, the Command Palette, and Escape to close the reading pane — all on their own or by reading the user guide. No competitor offers a first-run tutorial. This is a genuine differentiator.

## User Stories

1. **As a screen reader user**, when I launch QuickMail for the first time, I want an interactive tutorial that teaches me the essential keyboard shortcuts by having me perform each one.
2. **As a keyboard user**, I want to be able to replay the tutorial from the Help menu at any time.
3. **As a user who already knows the shortcuts**, I want to skip the tutorial and never see it again.

## Acceptance Criteria

- [ ] On first launch (detected via config flag), offer to start the tutorial
- [ ] Tutorial has 6 steps, each teaching one essential shortcut:
  1. F6 — cycle through panes
  2. Ctrl+1 — focus account list
  3. Ctrl+2 — focus folder tree
  4. Ctrl+3 — focus message list
  5. Ctrl+Shift+P — open Command Palette
  6. Escape — close reading pane / dismiss dialogs
- [ ] Each step: announces instruction via UIA, waits for user to press the correct key, confirms success, moves to next
- [ ] User can press Escape at any time to exit the tutorial
- [ ] Tutorial can be replayed from Help → "Keyboard Tutorial"
- [ ] Tutorial completion is saved to config so it doesn't auto-show again
- [ ] All announcements use `AccessibilityHelper.Announce()` with `AnnouncementCategory.Hint`
- [ ] Tutorial is a modal overlay that captures keyboard input

## Accessibility Requirements

- Every instruction is announced via UIA Notification, not just displayed visually
- Visual overlay is high-contrast with large text
- Tutorial waits for correct keypress — no timeout that would rush a screen reader user
- "Press Escape to skip" is announced at the start of every step
- Success confirmation includes the command name: "Correct! F6 cycles focus through all panes."

## Technical Notes

- Add `bool TutorialCompleted` to `ConfigModel` (defaults to `false`)
- Create `TutorialViewModel` — owns tutorial state, current step, key detection
- Create `TutorialOverlay` — a full-window transparent overlay with a centered instruction card
- The overlay captures `PreviewKeyDown` on the MainWindow level
- Tutorial is launched from `MainViewModel` after accounts load on first run
- Register `help.keyboardTutorial` command in `CommandRegistry`

## Files to Create

| File | Purpose |
|------|---------|
| `QuickMail/ViewModels/TutorialViewModel.cs` | Tutorial state machine |
| `QuickMail/Views/TutorialOverlay.xaml` | Visual overlay |
| `QuickMail/Views/TutorialOverlay.xaml.cs` | Code-behind (key capture, focus) |

## Files to Modify

| File | Change |
|------|---------|
| `QuickMail/Models/ConfigModel.cs` | Add `TutorialCompleted` bool |
| `QuickMail/Services/ConfigService.cs` | Persist `TutorialCompleted` |
| `QuickMail/ViewModels/MainViewModel.cs` | Add `ShowTutorialCommand`, launch on first run |
| `QuickMail/Views/MainWindow.xaml` | Add tutorial overlay to visual tree |
| `QuickMail/Views/MainWindow.xaml.cs` | Register `help.keyboardTutorial`, wire overlay |

## Tests to Add

| File | Test |
|------|------|
| `QuickMail.Tests/TutorialViewModelTests.cs` | Step progression, escape exits, correct key advances |
| `QuickMail.Tests/ViewModelConstructionTests.cs` | TutorialViewModel constructs |
| `QuickMail.Tests/XamlParseTests.cs` | TutorialOverlay XAML parses |

---

*This spec is ready for Dev Lead implementation. Hand off with full context.*
