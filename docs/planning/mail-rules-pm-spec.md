# Mail Rules — Product Management Specification

**Status:** Approved  
**Version:** 1.1  
**Date:** 2026-05-22  
**Author:** Design & PM  
**Target release:** v0.6  
**Implementation:** Complete (see [Appendix D: Post-Implementation Retrospective](#appendix-d-post-implementation-retrospective))

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Competitive Landscape](#3-competitive-landscape)
4. [Design Principles](#4-design-principles)
5. [Feature Scope](#5-feature-scope)
6. [Rule Model](#6-rule-model)
7. [User Experience Design](#7-user-experience-design)
8. [Accessibility (WCAG 2.2)](#8-accessibility-wcag-22)
9. [Technical Architecture](#9-technical-architecture)
10. [Implementation Phases](#10-implementation-phases)
11. [Success Metrics](#11-success-metrics)
12. [Open Questions & Risks](#12-open-questions--risks)

---

## 1. Executive Summary

Mail Rules gives users the ability to define **automatic actions** that run on incoming messages as they arrive during background sync. A user might say: "When a message arrives from my manager, move it to the Priority folder and mark it as unread so I see it." Or: "When a newsletter from ExampleCorp arrives, mark it as read and move it to Newsletters."

Rules run **client-side** in `SyncService` immediately after new messages are fetched from the IMAP server — before the UI is updated. This means rules fire within seconds of mail arrival during the normal sync cycle, and the user sees the post-rule state (moved, flagged, etc.) the moment the message list refreshes.

This feature is the #1 most-requested power-user feature missing from QuickMail. It directly competes with Gmail Filters, Outlook Rules, and Thunderbird Message Filters — but we can make it **simpler, more accessible, and keyboard-driven** in a way none of them are.

---

## 2. User Problem & Opportunity

### Current state

QuickMail users must manually triage every incoming message: read it, decide what to do, move it, flag it, or leave it. For users receiving 50+ messages per day across multiple accounts, this is unsustainable. Power users have explicitly requested automation.

### Target personas

| Persona | Need |
|---|---|
| **Knowledge worker with high volume** | Auto-sort newsletters, notifications, and CCs into folders so the inbox stays focused |
| **Accessibility-first user** | Reduce manual triage steps; every manual move/delete is a keyboard or screen-reader interaction they'd rather avoid |
| **Multi-account user** | Apply consistent rules across all accounts (e.g., "anything from my boss goes to Priority, regardless of which account it arrived on") |
| **Privacy-conscious user** | Rules run locally — no server-side filter configuration, no data leaves the machine |

### Opportunity

Mail rules are table stakes for desktop email clients. QuickMail's differentiator is **accessibility and simplicity**. Every competing rules UI is either overwhelming (Outlook's wizard with 40 condition types) or inaccessible (Gmail's filter dialog has known screen-reader gaps). We can ship something that is:

- **Easier to learn**: A single-screen rules manager, not a multi-step wizard
- **Faster to use**: Keyboard-driven creation and editing
- **Fully accessible**: WCAG 2.2 AA from day one, with custom screen-reader announcements
- **Transparent**: Rules fire locally; users can see exactly what ran and when

---

## 3. Competitive Landscape

| Product | Strengths | Weaknesses |
|---|---|---|
| **Gmail Filters** | Server-side, fast, simple condition set | Inaccessible filter dialog; no "run now" on existing mail; hard to reorder; no dry-run |
| **Outlook Rules** | Very powerful (40+ conditions, 30+ actions); client+server | Overwhelming wizard UI; accessibility gaps in rule list; slow to create |
| **Thunderbird Filters** | Client-side, decent condition set, run-on-demand | Dated UI; no screen-reader announcements; condition builder is clunky |
| **Apple Mail Rules** | Clean UI, iCloud sync | Mac-only; limited conditions; no accessibility announcements |

### QuickMail's positioning

| Dimension | QuickMail target |
|---|---|
| Condition count | 8–10 (the ones that matter) |
| Action count | 6 (move, copy, mark read, mark unread, delete, forward) |
| Creation UX | Single dialog, no wizard |
| Keyboard | Full CommandRegistry integration |
| Screen reader | Custom announcements for every state change |
| Transparency | Rule-run log visible in status bar |
| Dry-run | "Test against selected messages" before saving |

---

## 4. Design Principles

1. **Flat, not nested.** No folders of rules, no sub-conditions, no AND/OR trees. Each rule is one set of conditions ANDed together. Users with complex needs can create multiple rules.

2. **Visible, not hidden.** Rules are not buried in Settings → Advanced → Filters. They have their own top-level entry point. The status bar shows when rules last ran and how many messages were affected.

3. **Testable, not mysterious.** Every rule can be tested against the currently selected messages before saving. The user sees exactly which messages would match.

4. **Keyboard-first, not mouse-first.** Every action in the Rules Manager has a registered keyboard shortcut. The rule list supports type-ahead. Creating a rule from a selected message pre-fills conditions.

5. **Accessible by default.** All custom screen-reader announcements go through `AccessibilityHelper.Announce()` with appropriate categories. No ARIA live-region hacks. WCAG 2.2 AA compliant.

6. **Local and private.** Rules run on the user's machine. Rule definitions are stored in a local JSON file. No rule data is sent to any server.

---

## 5. Feature Scope

### In scope (v0.6)

- Rule creation, editing, deletion, and enable/disable toggle
- Conditions: From, To, Subject, Body contains, Has attachments, Account
- Actions: Move to folder, Mark as read, Mark as unread, Delete
- Rules run automatically during background sync on new messages
- Rules Manager dialog with full keyboard navigation
- "Create rule from message" — pre-fills From and/or Subject from selected message
- Rule test/dry-run against selected messages
- Rule execution log in status bar
- Rules stored in `%APPDATA%\QuickMail\rules.json`
- Per-account scoping (rules can apply to "All accounts" or a specific account)

### Out of scope (future)

- Server-side rule execution (Sieve/ManageSieve)
- Forward action (requires compose/SMTP integration — v0.7)
- Reply-with-template action (v0.7)
- Run rules on existing mail (v0.7 — "Apply rules to folder")
- Rule ordering / priority (rules run in definition order; reordering is v0.7)
- Regex conditions (v0.8)
- Import/export rules (v0.8)
- Rule groups / folders (v0.8)

---

## 6. Rule Model

### Data model (`MailRule.cs`)

```csharp
public class MailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    // Conditions — all ANDed together. Empty = match all.
    public string? FromContains { get; set; }
    public string? ToContains { get; set; }
    public string? SubjectContains { get; set; }
    public string? BodyContains { get; set; }
    public bool MustHaveAttachments { get; set; }

    // Account scope — null = all accounts
    public Guid? AccountId { get; set; }

    // Actions
    public RuleAction Action { get; set; } = RuleAction.MarkAsRead;
    public string? TargetFolder { get; set; } // required for MoveToFolder
}

public enum RuleAction
{
    MarkAsRead,
    MarkAsUnread,
    MoveToFolder,
    Delete,
}
```

### Condition matching rules

- All populated conditions are ANDed. An empty/null condition is ignored (matches everything).
- `FromContains`, `ToContains`, `SubjectContains`, `BodyContains` are case-insensitive substring matches.
- `BodyContains` searches the plain-text body preview stored in `MailMessageSummary.Preview`. Full-body search is out of scope for v0.6.
- `MustHaveAttachments` matches when `MailMessageSummary.HasAttachments` is true.
- `AccountId` scopes the rule to one account; null means all accounts.

### Storage

Rules are stored in `%APPDATA%\QuickMail\rules.json` as a JSON array:

```json
[
  {
    "id": "a1b2c3d4-...",
    "name": "Manager emails to Priority",
    "isEnabled": true,
    "fromContains": "manager@company.com",
    "toContains": null,
    "subjectContains": null,
    "bodyContains": null,
    "mustHaveAttachments": false,
    "accountId": null,
    "action": "moveToFolder",
    "targetFolder": "INBOX/Priority"
  }
]
```

This follows the same pattern as `contacts.json` and `hotkeys.json`.

---

## 7. User Experience Design

### 7.1 Entry points

| Entry point | How |
|---|---|
| **Menu bar** | Tools → Rules… (`Ctrl+Shift+R`) |
| **Command Palette** | "Manage Rules" (registered in `CommandRegistry`) |
| **Message context** | Right-click / Shift+F10 on a message → "Create Rule from Message…" |
| **Status bar** | "Rules: 3 active" indicator; selecting it opens Rules Manager |

### 7.2 Rules Manager dialog

A single resizable `Window` (not a wizard) with three regions:

```
┌──────────────────────────────────────────────────────────┐
│ Rules                                          [✕ Close] │
├──────────────────────────────────────────────────────────┤
│ ┌─────────────────────────┐ ┌──────────────────────────┐ │
│ │ [New Rule]  [Delete]    │ │ Rule Name: [___________] │ │
│ │                         │ │ [✓] Enabled               │ │
│ │ ● Manager → Priority    │ │                          │ │
│ │ ○ Newsletters → Read    │ │ Conditions               │ │
│ │ ○ GitHub → Read         │ │ ─────────                │ │
│ │                         │ │ Account: [All accounts ▾] │ │
│ │                         │ │ From contains: [________] │ │
│ │                         │ │ To contains:   [________] │ │
│ │                         │ │ Subject:       [________] │ │
│ │                         │ │ Body:          [________] │ │
│ │                         │ │ [✓] Has attachments       │ │
│ │                         │ │                          │ │
│ │                         │ │ Action                   │ │
│ │                         │ │ ──────                   │ │
│ │                         │ │ [Mark as read      ▾]    │ │
│ │                         │ │ Folder: [INBOX/Pri… ▾]   │ │
│ │                         │ │                          │ │
│ │                         │ │ [Test Rule]  [Save]      │ │
│ └─────────────────────────┘ └──────────────────────────┘ │
├──────────────────────────────────────────────────────────┤
│ Status: Rule saved. Last run: 3 messages matched.        │
└──────────────────────────────────────────────────────────┘
```

**Layout rationale:**
- **Left pane**: Rule list with radio-button-style selection (only one selected at a time). Shows rule name and a summary of what it does.
- **Right pane**: Detail editor for the selected rule. All conditions and actions visible at once — no scrolling through wizard pages.
- **Bottom bar**: Status messages, last-run info.

### 7.3 Rule list (left pane)

- Each item shows: `[enabled icon] Rule Name — Action summary`
- Enabled rules show a filled circle (●); disabled rules show an open circle (○).
- Single-select. Selecting a rule populates the right pane.
- `Delete` key removes the selected rule (with confirmation).
- `Ctrl+N` creates a new rule.
- Type-ahead: typing jumps to the first rule starting with that letter.

### 7.4 Rule editor (right pane)

**Rule Name** — free-text `TextBox`. Required. Validated on save.

**Enabled** — `CheckBox`. Defaults to checked.

**Conditions section:**
- **Account** — `ComboBox`. Options: "All accounts" + each account by name. Default: "All accounts".
- **From contains** — `TextBox` with placeholder "e.g., manager@company.com or just company.com"
- **To contains** — `TextBox` with placeholder "e.g., team@company.com"
- **Subject contains** — `TextBox` with placeholder "e.g., Weekly Report"
- **Body contains** — `TextBox` with placeholder "Text to search in message body"
- **Has attachments** — `CheckBox`

**Action section:**
- **Action** — `ComboBox`. Options: Mark as read, Mark as unread, Move to folder, Delete.
- **Target folder** — `ComboBox` (only visible when Action = Move to folder). Populated from the folder tree. Supports type-ahead search (reuse `FolderPickerWindow` pattern).

**Buttons:**
- **Test Rule** — Runs the rule against currently selected messages in the main window and shows a result dialog: "This rule would match 3 of 12 selected messages."
- **Save** — Validates and saves. Announces "Rule saved" via `AccessibilityHelper.Announce()`.

### 7.5 "Create Rule from Message" flow

1. User selects a message in the message list.
2. User activates context menu → "Create Rule from Message…" or presses `Ctrl+Shift+T` (T for "template").
3. Rules Manager opens with a new rule pre-filled:
   - **From contains** = sender's email address
   - **Subject contains** = message subject (if not empty)
   - **Account** = the account the message belongs to
4. User fills in the action and saves.

### 7.6 Status bar integration

The status bar shows a rules summary:

> Rules: 3 active, 0 disabled — Last run: 2 matched (10:42 AM)

This updates after each sync cycle. Selecting this area opens the Rules Manager.

### 7.7 Keyboard shortcuts

All registered in `CommandRegistry`:

| Shortcut | Command ID | Action |
|---|---|---|
| `Ctrl+Shift+R` | `mail.rules` | Open Rules Manager |
| `Ctrl+Shift+T` | `mail.createRuleFromMessage` | Create rule from selected message |
| `Ctrl+N` | (dialog-local) | New rule (when Rules Manager is focused) |
| `Delete` | (dialog-local) | Delete selected rule |
| `Escape` | (dialog-local) | Close Rules Manager |

---

## 8. Accessibility (WCAG 2.2)

### 8.1 Compliance targets

This feature targets **WCAG 2.2 Level AA** compliance. Specific success criteria:

| SC | Description | How we meet it |
|---|---|---|
| 1.1.1 Non-text Content | Icons have text equivalents | Rule enabled/disabled icons use `AutomationProperties.Name` |
| 1.3.1 Info and Relationships | Structure is programmatic | Rule list uses `ListBox`; form uses labeled `TextBox`/`ComboBox` |
| 1.3.2 Meaningful Sequence | Tab order is logical | Tab goes: rule list → name → enabled → account → conditions → action → folder → test → save |
| 1.4.1 Use of Color | Color is not the only indicator | Enabled/disabled state uses both icon shape (●/○) and text in AutomationProperties |
| 1.4.3 Contrast (Minimum) | 4.5:1 for text | Inherits from QuickMail's existing styles |
| 1.4.11 Non-text Contrast | 3:1 for UI components | TextBox borders, ComboBox arrows meet 3:1 |
| 2.1.1 Keyboard | All functionality via keyboard | Every control in the dialog is keyboard-accessible; no mouse-only interactions |
| 2.4.3 Focus Order | Focus moves meaningfully | Tab order follows visual layout |
| 2.4.7 Focus Visible | Focus indicator is visible | Inherits from WPF system focus visuals |
| 2.5.3 Label in Name | Visible label matches accessible name | `AutomationProperties.Name` matches visible `TextBlock` labels |
| 3.3.1 Error Identification | Errors are described in text | Validation errors announced via `AccessibilityHelper.Announce()` |
| 3.3.2 Labels or Instructions | Inputs have labels | Every `TextBox` and `ComboBox` has a visible label |
| 4.1.2 Name, Role, Value | UIA properties are correct | WPF controls expose correct UIA patterns |
| 4.1.3 Status Messages | Status announced without focus move | All status updates use `AccessibilityHelper.Announce()` with appropriate category |

### 8.2 Screen-reader announcements

All announcements go through `AccessibilityHelper.Announce()` with explicit categories:

| Event | Announcement | Category |
|---|---|---|
| Rules Manager opened | "Rules Manager. 3 active rules, 0 disabled." | `Status` |
| Rule selected in list | "Manager emails to Priority. Moves to INBOX/Priority. Enabled." | `Result` |
| Rule saved | "Rule 'Manager emails to Priority' saved." | `Result` |
| Rule deleted | "Rule 'Newsletters to Read' deleted." | `Result` |
| Test rule result | "Rule would match 3 of 12 selected messages." | `Result` |
| Validation error | "Rule name is required." | `Result` |
| Sync completed with rule matches | "Sync complete. 2 rules matched 5 messages." | `Status` |
| Rule created from message | "New rule created from message. Fill in the action and save." | `Hint` |

### 8.3 Keyboard navigation in Rules Manager

- `Tab` / `Shift+Tab`: Move between all controls in logical order
- `Up` / `Down`: Navigate rule list when focused
- `Enter`: Edit selected rule (moves focus to Name field)
- `Delete`: Delete selected rule (with confirmation dialog)
- `Ctrl+N`: New rule
- `Escape`: Close dialog
- `Alt+N`: Jump to Name field
- `Alt+C`: Jump to Conditions section (first condition field)
- `Alt+A`: Jump to Action combo box

### 8.4 Reduced motion

The Rules Manager dialog has no animations. Opening/closing is instant. The `Test Rule` result appears inline without animation. This respects the `@media (prefers-reduced-motion: reduce)` principle even though WPF doesn't directly support that media query — we simply don't animate.

### 8.5 Target size

All interactive controls (buttons, checkboxes, combo box dropdown arrows) have a minimum clickable area of 24×24 pixels, meeting WCAG 2.5.8 (Target Size, AA).

---

## 9. Technical Architecture

### 9.1 New files

| File | Purpose |
|---|---|
| `QuickMail/Models/MailRule.cs` | Rule data model + `RuleAction` enum |
| `QuickMail/Services/RuleService.cs` | Rule storage, matching engine, execution |
| `QuickMail/Services/IRuleService.cs` | Interface for DI |
| `QuickMail/ViewModels/RulesManagerViewModel.cs` | Rules Manager dialog VM |
| `QuickMail/Views/RulesManagerWindow.xaml` | Rules Manager dialog UI |
| `QuickMail/Views/RulesManagerWindow.xaml.cs` | Code-behind (focus, keyboard routing only) |

### 9.2 Modified files

| File | Change |
|---|---|
| `QuickMail/Services/SyncService.cs` | Call `IRuleService.ApplyRulesAsync()` after fetching new messages, before raising `FolderSynced` |
| `QuickMail/ViewModels/MainViewModel.cs` | Add `RulesManagerCommand`, `CreateRuleFromMessageCommand`; add rules status to status bar |
| `QuickMail/Views/MainWindow.xaml` | Add "Rules…" menu item; add context menu item "Create Rule from Message…" |
| `QuickMail/Views/MainWindow.xaml.cs` | Register new commands in `CommandRegistry` |
| `QuickMail/App.xaml.cs` | Wire `IRuleService` into DI |

### 9.3 Rule execution flow

```
SyncService.SyncFolderAsync()
  └─ imap.GetMessagesSinceAsync()          // fetch new messages from server
  └─ store.UpsertSummariesAsync(incoming)  // persist to SQLite
  └─ ruleService.ApplyRulesAsync(incoming) // ← NEW: apply rules
  │    └─ For each enabled rule:
  │         └─ Match conditions against each message
  │         └─ Execute action (move, mark read, delete, etc.)
  │         └─ Log matches
  └─ FolderSynced?.Invoke(incoming)        // UI updates with post-rule state
```

**Key design decisions:**

1. **Rules run after SQLite upsert but before UI notification.** This means the local store always has the pre-rule state, and the UI only sees the post-rule state. If a rule moves a message, the UI never shows it in the original folder.

2. **Move actions use `ImapService.MoveMessagesAsync()`.** This requires a foreground IMAP lease. Since rules run during background sync, we use a background lease. The move is fire-and-forget — if it fails, the message stays in the original folder and the error is logged.

3. **Delete actions use `ImapService.MoveToTrashBatchAsync()`.** We never hard-delete from rules. Messages go to Trash.

4. **Mark-as-read actions update `MailMessageSummary.IsRead` in memory and persist via `LocalStoreService`.**

5. **Rules are evaluated in definition order.** First match wins for conflicting rules (e.g., two rules both want to move the same message). Once a rule matches and executes, subsequent rules still evaluate against the message (non-exclusive). This is simpler than Outlook's "stop processing more rules" concept and avoids the most common user confusion.

### 9.4 RuleService design

```csharp
public interface IRuleService
{
    /// <summary>Load all rules from disk.</summary>
    List<MailRule> LoadRules();

    /// <summary>Save all rules to disk.</summary>
    void SaveRules(List<MailRule> rules);

    /// <summary>
    /// Apply enabled rules to a batch of incoming messages.
    /// Returns the count of messages that matched at least one rule.
    /// </summary>
    Task<int> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct);

    /// <summary>
    /// Test a rule against a set of messages without executing actions.
    /// Returns the list of messages that would match.
    /// </summary>
    List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages);
}
```

### 9.5 MVVM compliance

`RulesManagerViewModel`:
- Exposes `ObservableCollection<MailRule> Rules`, `MailRule? SelectedRule`
- Exposes `NewRuleCommand`, `DeleteRuleCommand`, `SaveRuleCommand`, `TestRuleCommand`
- Raises `ConfirmDeleteRequested` event (View shows confirmation dialog)
- No `MessageBox`, no `Window`, no `Dispatcher` references
- No direct control references

`RulesManagerWindow.xaml.cs`:
- Handles `ConfirmDeleteRequested` by showing a confirmation dialog
- Manages focus (sets focus to rule list on open, to Name field on New)
- Routes `Delete` key and `Ctrl+N` within the dialog
- No business logic

### 9.6 Error handling

- **Rule file corrupted**: `RuleService.LoadRules()` returns empty list, logs warning. User is not blocked.
- **Move action fails**: Message stays in original folder. Error logged. Rule continues processing.
- **Delete action fails**: Message stays. Error logged.
- **Rule has invalid target folder**: Rule is skipped for that message. Error logged once per sync cycle.
- **Circular moves** (rule moves to same folder): Detected and skipped.

---

## 10. Implementation Phases

### Phase 1 — Data model & storage (1–2 days)

- Create `MailRule.cs` and `RuleAction` enum
- Create `IRuleService` / `RuleService` with Load/Save
- Write `RuleServiceTests` (round-trip JSON, empty file, corrupted file)
- Register in `App.xaml.cs` DI

### Phase 2 — Rule matching engine (1–2 days)

- Implement `RuleService.ApplyRulesAsync()` with condition matching
- Implement `RuleService.TestRule()` for dry-run
- Write `RuleServiceTests` for each condition type and action type
- Integration test: apply rules to a synthetic batch of `MailMessageSummary`

### Phase 3 — SyncService integration (1 day)

- Call `IRuleService.ApplyRulesAsync()` in `SyncService.SyncFolderAsync()`
- Log rule execution counts
- Update status bar in `MainViewModel` with rule-run summary
- Test: rules fire during sync, messages appear in correct post-rule state

### Phase 4 — Rules Manager UI (2–3 days)

- Create `RulesManagerViewModel`
- Create `RulesManagerWindow.xaml` with the three-region layout
- Implement full keyboard navigation
- Implement "Create Rule from Message" flow
- Implement Test Rule against selected messages
- Wire up menu items and CommandRegistry entries

### Phase 5 — Accessibility & polish (1–2 days)

- Add `AutomationProperties.Name` to all controls
- Add `AccessibilityHelper.Announce()` calls for all state changes
- Tab-order audit
- Contrast audit
- Screen-reader testing pass
- Update `USERGUIDE.md` with Rules documentation

### Phase 6 — Testing & hardening (1–2 days)

- `RulesManagerViewModelTests` (VM construction, command behavior)
- `XamlParseTests` for `RulesManagerWindow`
- Manual test matrix: create/edit/delete/enable/disable rules across accounts
- Edge cases: empty rule name, invalid folder, 100+ rules, Unicode conditions

---

## 11. Success Metrics

| Metric | Target | Measurement |
|---|---|---|
| Rules created per active user (30 days) | ≥ 2 | Telemetry (opt-in) or survey |
| Rules Manager task completion rate | ≥ 90% of users who open it create at least one rule | Telemetry |
| Support tickets related to rules | < 5 in first 90 days | GitHub issues |
| Screen-reader usability | 0 critical a11y bugs filed | GitHub issues labeled `a11y` |
| Sync performance regression | < 5% increase in sync time with 10 active rules | Benchmark |

---

## 12. Open Questions & Risks

### Open questions

1. **Should rules be exclusive (stop processing) or non-exclusive?** Proposed: non-exclusive for v0.6. This is simpler. We can add a "Stop processing more rules" checkbox in v0.7 if users ask for it.

2. **Should "Create Rule from Message" also capture the folder?** Proposed: no. The rule should apply to incoming mail regardless of which folder it arrives in. Folder-scoping is a future feature.

3. **Should we support "run rules now" on existing mail in v0.6?** Proposed: no. This requires a different execution path (iterating over stored messages rather than incoming batch). v0.7.

4. **How do we handle rules that reference a deleted folder?** Proposed: the rule is skipped with a logged warning. The Rules Manager shows a warning icon next to rules with missing target folders.

### Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Move action fails silently, user doesn't know | Medium | Log every rule action; show last-run summary in status bar; surface failures in Rules Manager |
| User creates a rule that matches everything (empty conditions + Move) | Medium | Require at least one non-empty condition for Move and Delete actions |
| Performance degradation with many rules | Low | Rules are evaluated in a tight loop; 100 rules × 50 messages = 5,000 condition checks, which is negligible |
| Rule file corruption loses user's rules | Low | Atomic write (write to temp file, rename); backup on save |
| Confusion between UI filters and mail rules | Medium | Clearly label: "Filters" (View menu) = show/hide in current view; "Rules" (Tools menu) = automatic actions on incoming mail |

---

## Appendix A: Condition Matching Reference

| Condition | Field | Match type | Example |
|---|---|---|---|
| From contains | `MailMessageSummary.From` | Case-insensitive substring | "john@company.com" matches "John Smith <john@company.com>" |
| To contains | `MailMessageSummary.To` | Case-insensitive substring | "team@company.com" matches "team@company.com, alice@company.com" |
| Subject contains | `MailMessageSummary.Subject` | Case-insensitive substring | "weekly" matches "Weekly Report — May 2026" |
| Body contains | `MailMessageSummary.Preview` | Case-insensitive substring | "unsubscribe" matches preview text |
| Has attachments | `MailMessageSummary.HasAttachments` | Boolean | Checked = only messages with attachments |
| Account | `MailMessageSummary.AccountId` | Exact GUID match | "Work Account" matches only that account's messages |

## Appendix B: Action Reference

| Action | IMAP operation | Notes |
|---|---|---|
| Mark as read | `LocalStoreService.SetReadAsync()` + IMAP STORE \Seen | Updates local cache and server |
| Mark as unread | `LocalStoreService.SetUnreadAsync()` + IMAP STORE -\Seen | Updates local cache and server |
| Move to folder | `ImapService.MoveMessagesAsync()` | IMAP MOVE or COPY+DELETE fallback |
| Delete | `ImapService.MoveToTrashBatchAsync()` | Moves to Trash, never hard-deletes |

## Appendix C: Example Rules

### Rule 1: Manager emails to Priority folder
- **Name**: Manager → Priority
- **From contains**: `manager@company.com`
- **Action**: Move to folder `INBOX/Priority`
- **Account**: Work Account

### Rule 2: Newsletters marked as read
- **Name**: Newsletters → Read
- **From contains**: `newsletter@`
- **Action**: Mark as read
- **Account**: All accounts

### Rule 3: Build notifications deleted
- **Name**: CI notifications → Trash
- **Subject contains**: `Build failed`
- **Action**: Delete
- **Account**: Work Account

### Rule 4: Invoices kept unread
- **Name**: Invoices stay unread
- **Subject contains**: `Invoice`
- **Has attachments**: checked
- **Action**: Mark as unread
- **Account**: All accounts

## Appendix D: Post-Implementation Retrospective

**Date:** 2026-05-22  
**Author:** Design & PM, with engineering input

### What worked

The PM spec accurately predicted the feature shape, UX layout, accessibility requirements, and competitive positioning. The two-pane Rules Manager, condition checkboxes, folder picker, "Create Rule from Message" flow, and status bar integration all shipped as designed. The WCAG 2.2 checklist was followed and the feature is fully keyboard-accessible with screen-reader announcements.

### What didn't work

The implementation required **six rounds of bug fixes** after the initial build passed all 181 unit tests. The bugs were not in individual components — they were at the boundaries between components:

| Bug | Root cause | Where it lived |
|---|---|---|
| Moved/deleted messages still appeared in UI | `ApplyRulesAsync` removed messages from `incoming` but `FolderSynced` still passed the full list to the UI | Between `RuleService` and `SyncService` |
| Messages reappeared after cache reload | `UpsertSummariesAsync` was called *before* rules ran, so moved messages were persisted to SQLite; `InitialLoadAsync` reloaded them | Between `SyncService` and `LocalStoreService` |
| Rules had no visible effect in regular folders | `OnFolderSynced` only handled virtual folders (All Mail, per-account All Mail); regular folders like INBOX returned early | Between `SyncService` and `MainViewModel` |
| Rules never fired on already-cached mail | Rules only ran on NEW messages during sync (UID > maxUid); messages synced before the rule was created were never processed | Between `RuleService` and `LocalStoreService` |

Every bug was at a **component boundary** — the exact place where specs are weakest and unit tests are blind.

### Root cause analysis

1. **The specs described components, not flows.** The dev spec had detailed code for each file in isolation, but no document traced a single message from IMAP fetch → SQLite → rules → UI. That one data-flow diagram would have caught all four bugs before implementation.

2. **The tests tested units, not the pipeline.** `RuleServiceTests` verified matching and IMAP calls. But no integration test ran `SyncService.SyncFolderAsync` with a real `RuleService` and checked what ended up in the UI and the local store.

3. **Same agent wrote specs and code.** Blind spots carried through. A different implementer reading the spec would have hit the first bug during manual testing and flagged it.

### Process improvements for future features

| Change | Why |
|---|---|
| **Data-flow diagram required in dev spec** | One diagram tracing a message/event through every component. Catches boundary bugs before code is written. |
| **Integration test specified in dev spec** | At least one test that exercises the full pipeline (e.g., `SyncFolderAsync` → `RuleService` → `LocalStore` → UI events). |
| **Manual test checklist in dev spec** | Step-by-step verification: "1. Generate mail. 2. Create rule. 3. Press F5. 4. Verify messages disappear." |
| **Different agent for spec review vs implementation** | Even an AI agent running in review mode against the spec catches things the author missed. |
| **"Existing mail" path explicit in PM spec** | The PM spec said rules run "on incoming messages as they arrive." It should have explicitly called out whether rules also apply to already-cached mail, and if so, when. |

### What this means for AI-assisted development

This project validated that PM specs + dev specs produce better results than raw prompting. The feature shipped with the right architecture, UX, and accessibility. But the process has a blind spot: **specs describe structure; bugs live in flow.** The fix isn't more detail in the same format — it's a different kind of artifact (data-flow diagrams, integration tests, manual test checklists) that specifically targets the seams between components.
