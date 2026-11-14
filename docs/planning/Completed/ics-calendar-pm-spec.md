# ICS Calendar Invite Handling — PM Specification

**Status:** Ready for Dev
**Date:** May 24, 2026
**Target:** Phase 2 (Table Stakes)
**Crew:** Bravo (PM → Dev Lead → Test Enforcer)

---

## User Problem

When someone sends a calendar invite (ICS file attachment), QuickMail currently shows it as a generic attachment. The user has to save the file and open it externally. This is unacceptable for daily use — calendar invites are a fundamental email workflow.

## User Stories

1. **As a screen reader user**, when I open a message with a calendar invite, I want to hear a clear summary of the event (title, time, location) without hunting through attachments.
2. **As a keyboard user**, I want to accept, decline, or tentatively accept an invite using only the keyboard.
3. **As any user**, I want the invite details displayed inline in the reading pane, not hidden in an attachment list.

## Acceptance Criteria

- [ ] When a message has a `text/calendar` MIME part, the reading pane shows an "Event Invitation" card above the message body
- [ ] The card displays: event title, organizer, start/end time, location, description
- [ ] Three buttons: Accept, Tentative, Decline — all keyboard accessible
- [ ] Accepting/declining generates an ICS reply and sends it via SMTP
- [ ] Screen reader announces the event summary when the card receives focus
- [ ] The card is navigable via Tab (part of the reading pane tab order)
- [ ] If ICS parsing fails, the attachment still appears in the attachment list (graceful degradation)
- [ ] All new commands registered in CommandRegistry

## Accessibility Requirements

- `AutomationProperties.Name` on every button: "Accept invitation", "Tentatively accept invitation", "Decline invitation"
- Event card has `AutomationProperties.Name` set to the `IcsModel.DisplaySummary`
- On focus, announce via `AccessibilityHelper.Announce()` with `AnnouncementCategory.Result`
- Tab order: message body → event card (if present) → attachments

## Technical Notes

- `IcsModel.cs` already exists with parsing and reply generation
- `MailMessageDetail` needs a new property: `IcsModel? CalendarInvite`
- `ImapService` needs to detect `text/calendar` parts when fetching message detail
- `MainViewModel` needs: `AcceptInviteCommand`, `DeclineInviteCommand`, `TentativeInviteCommand`
- Reply ICS is sent via `SmtpService` — needs a new method or overload for sending ICS replies
- The event card is HTML rendered in WebView2 — build the HTML in the ViewModel, not code-behind

## Files to Create

| File | Purpose |
|------|---------|
| *(none — all changes are modifications)* | |

## Files to Modify

| File | Change |
|------|--------|
| `QuickMail/Models/MailMessageDetail.cs` | Add `IcsModel? CalendarInvite` property |
| `QuickMail/Services/ImapService.cs` | Detect `text/calendar` parts, parse with `IcsModel.Parse()` |
| `QuickMail/Services/ISendMailService.cs` | Add `SendIcsReplyAsync` method |
| `QuickMail/Services/SmtpService.cs` | Implement ICS reply sending |
| `QuickMail/ViewModels/MainViewModel.cs` | Add invite commands, build event card HTML |
| `QuickMail/Views/MainWindow.xaml.cs` | Register new commands in CommandRegistry |

## Tests to Add

| File | Test |
|------|------|
| `QuickMail.Tests/IcsModelTests.cs` | Parse valid ICS, parse invalid ICS, generate reply |
| `QuickMail.Tests/ViewModelConstructionTests.cs` | Verify new commands exist on MainViewModel |
| `QuickMail.Tests/StubServices.cs` | Add `SendIcsReplyAsync` to stub |

---

*This spec is ready for Dev Lead implementation. Hand off with full context.*
