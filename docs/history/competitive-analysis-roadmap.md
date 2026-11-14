# QuickMail Competitive Analysis & Product Roadmap

**Date:** May 24, 2026
**Author:** Product VP (AI-assisted)
**Branch:** roadmap
**Status:** Active — this is a living document that will be updated as features ship.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Competitive Landscape](#2-competitive-landscape)
3. [QuickMail Current State Assessment](#3-quickmail-current-state-assessment)
4. [Accessibility Audit](#4-accessibility-audit)
5. [Prioritized Feature Roadmap](#5-prioritized-feature-roadmap)
6. [Feature Crew Assignments](#6-feature-crew-assignments)
7. [Success Metrics](#7-success-metrics)
8. [The Story: Building Accessible Software with AI](#8-the-story)

---

## 1. Executive Summary

QuickMail is a WPF desktop email client for Windows built by Kelly Ford with AI-assisted development. It targets a specific, underserved market: **screen reader users and keyboard-first users who need a genuinely accessible email client.** This is not a general-purpose email client trying to compete with Outlook on features. It competes on one dimension: **being the most accessible Windows email client available.**

The competitive analysis below confirms that no major Windows email client delivers a fully accessible experience. Every competitor has significant gaps — some catastrophic for screen reader users. QuickMail's opportunity is to be the first email client where accessibility is not an afterthought but the foundation.

### Key Findings

- **Outlook (classic)** has the most features but the worst accessibility consistency. Screen reader experience varies dramatically by version, update, and which pane has focus.
- **Thunderbird** is open source and customizable but has deep accessibility debt. The message list, folder pane, and composition window all have known screen reader gaps that have persisted for years.
- **Windows Mail / new Outlook** is a web wrapper with all the accessibility problems of web-based apps in a desktop shell — focus trapping, ARIA misbehavior, and inconsistent keyboard navigation.
- **Gmail (web)** is the best of the web options but still a web app — latency, focus management issues, and Google's tendency to ship breaking accessibility changes without notice.
- **QuickMail (current)** has the strongest accessibility architecture of any client analyzed — UIA notifications, proper focus management, CommandRegistry for keyboard customization, and screen-reader-specific announcements with category filtering. But it's missing features that users need daily.

### Strategic Position

QuickMail should not try to match Outlook feature-for-feature. It should be **the email client that screen reader users recommend to each other.** The goal is: when someone asks "what email client works best with a screen reader on Windows?", the answer is QuickMail.

---

## 2. Competitive Landscape

### 2.1 Microsoft Outlook (Classic, Desktop)

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Feature set | A+ | Everything: rules, categories, search folders, calendar, tasks, add-ins |
| Keyboard navigation | B | Extensive shortcuts but inconsistent across panes; ribbon is keyboard-hostile |
| Screen reader UX | C- | UIA implementation is inconsistent. Reading pane frequently loses focus. Message list announces wrong item counts. Calendar is a disaster with screen readers. |
| Performance | C | Slow startup, frequent UI freezes during sync, search is sluggish on large mailboxes |
| Accessibility commitment | D | Microsoft talks accessibility but ships regressions constantly. See Kelly Ford's posts on Copilot, Notepad tables, and Windows Copilot's half-answers. |
| Price | Free (bundled) or Microsoft 365 subscription |

**Key accessibility failures:**
- Focus randomly jumps to the ribbon when reading messages
- Message list announces "1 of 50" when 200 messages are visible
- Calendar grid is nearly unusable with a screen reader
- Rules wizard has 40+ condition types in an inaccessible tree
- Custom ribbon customization breaks keyboard navigation

### 2.2 Mozilla Thunderbird

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Feature set | A- | Full IMAP/POP, filters, add-ons, calendar, chat |
| Keyboard navigation | B- | Many shortcuts but inconsistent; some dialogs are mouse-only |
| Screen reader UX | C+ | Better than Outlook in some areas, worse in others. Message list works. Folder pane has focus issues. Composition window accessibility varies by platform. |
| Performance | B | Acceptable on modern hardware; large folders can be slow |
| Accessibility commitment | C | Open source means accessibility depends on volunteer effort. Bugs sit for years. No dedicated accessibility team. |
| Price | Free |

**Key accessibility failures:**
- Folder pane tree view doesn't announce expand/collapse state reliably
- Filter/rule creation dialog is a maze of unlabeled controls
- Add-on manager is partially inaccessible
- Calendar tab has severe screen reader gaps
- No custom screen reader announcements for state changes

### 2.3 Windows Mail / New Outlook (Web Wrapper)

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Feature set | C | Basic email only. No rules, limited filtering, no conversation view |
| Keyboard navigation | C | Web-style keyboard traps; focus gets stuck in compose area |
| Screen reader UX | D | Web wrapper means ARIA behavior is unpredictable. Focus mode vs browse mode confusion. Reading pane announces raw HTML. |
| Performance | D | Web tech in a desktop shell — slow, memory-heavy, feels like a website pretending to be an app |
| Accessibility commitment | D | Microsoft's "new" Outlook is a regression from the classic version in almost every accessibility dimension |
| Price | Free |

**Key accessibility failures:**
- Browse mode / focus mode switching is constant and disorienting
- Message body sometimes announces as raw HTML with tags
- Keyboard shortcuts conflict with screen reader commands
- Settings panel is partially inaccessible
- Account setup wizard has unlabeled graphics

### 2.4 Gmail (Web)

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Feature set | B+ | Labels, filters, smart categories, excellent search |
| Keyboard navigation | B | Good shortcut system but requires enabling; conflicts with screen reader |
| Screen reader UX | C+ | Best of the web options but still a web app. Google ships accessibility regressions. ARIA labels change without notice. |
| Performance | B | Fast for a web app; still slower than native |
| Accessibility commitment | C | Google has accessibility teams but Gmail specifically gets less attention than Search or Docs |
| Price | Free |

**Key accessibility failures:**
- Keyboard shortcuts must be enabled and conflict with screen reader commands
- Conversation view is confusing with a screen reader — collapsed messages aren't announced
- Label management is partially accessible
- Settings page is a maze
- Google frequently A/B tests changes that break accessibility

### 2.5 eM Client

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Feature set | B+ | Good IMAP/Exchange support, calendar, contacts, tasks |
| Keyboard navigation | C+ | Some shortcuts; many features require mouse |
| Screen reader UX | D | Proprietary UI framework with poor UIA support. Many controls are custom-drawn and invisible to screen readers. |
| Performance | B | Reasonable |
| Accessibility commitment | F | No evidence of accessibility investment |
| Price | Free (limited) / $49.95 Pro |

### 2.6 Mailbird

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Feature set | B | Clean UI, app integrations, unified inbox |
| Keyboard navigation | C- | Minimal keyboard support; designed for mouse |
| Screen reader UX | F | Nearly unusable with a screen reader. Custom UI framework. |
| Accessibility commitment | F | No accessibility documentation or commitment |
| Price | Free (limited) / subscription |

---

## 3. QuickMail Current State Assessment

### 3.1 What Works Well

| Area | Assessment |
|------|------------|
| **UIA Notifications** | `AccessibilityHelper.Announce()` with category filtering (Hint, Status, Result) and user-configurable toggles. This is genuinely best-in-class. No competitor does this. |
| **Keyboard Navigation** | F6 pane cycling, Ctrl+0-3/9 for direct pane focus, Left/Right in status bar, Escape to close reading pane. Comprehensive and consistent. |
| **Command Registry** | All shortcuts registered in `CommandRegistry` with user-customizable bindings via `hotkeys.json`. Powers the Command Palette (Ctrl+Shift+P). |
| **IMAP Architecture** | Pooled connections per account, foreground/background lease separation, IMAP IDLE push. Production-grade. |
| **HTML Rendering** | WebView2 with strict CSP, simplified reader mode for heavy HTML, remote image blocking. Secure and responsive. |
| **MVVM Discipline** | Clean separation. ViewModels have no UI references. Code-behind is UI-only. |
| **Security** | Passwords in Windows Credential Manager, OAuth2 for Microsoft accounts, local-only rules. |
| **Status Bar** | Four named regions with arrow-key navigation, proper UIA StatusBar pattern, screen reader announcements per region. |

### 3.2 What's Missing (Feature Gaps)

| Feature | Priority | Competitors Have It? |
|---------|----------|---------------------|
| **Calendar** | High | All major competitors |
| **Contacts management UI** | High | All major competitors |
| **PGP/GPG encryption** | Medium | Thunderbird (via add-on), Outlook (via add-in) |
| **Spam filtering** | Medium | All major competitors |
| **Server-side search** | Medium | Gmail, Outlook |
| **Message templates** | Medium | Outlook, Thunderbird |
| **Offline mode improvements** | Medium | All major competitors |
| **Import/export** | Low | All major competitors |
| **Calendar invites (ICS)** | High | All major competitors |
| **Attachment preview inline** | Medium | Outlook, Gmail |
| **Spell check** | High | All major competitors |
| **Signatures (plain text + HTML)** | High | All major competitors |
| **Multiple identities per account** | Low | Outlook, Thunderbird |
| **Send later / scheduled send** | Medium | Gmail, Outlook |

### 3.3 Technical Debt (from May 21 Review)

| Issue | Severity | Status |
|-------|----------|--------|
| §1.1 User-customized hotkeys silently inert | **P0** | Must fix immediately |
| §1.2 No global unhandled-exception handler | **P0** | Must fix immediately |
| §1.3 LogService not thread-safe | **P0** | Must fix immediately |
| §1.5 BatchObservableCollection fragile | **P0** | Must fix |
| §1.6 ContactService race condition | **P0** | Must fix |
| §1.7 Drafts folder detection uses substring | **P1** | Should fix |
| §1.10 HasAttachments not populated from cache | **P1** | Should fix |
| §2.1 MainViewModel repeated patterns | **P1** | Refactor |
| §2.2 MainWindow.xaml.cs tree-view triplets | **P1** | Refactor |
| §2.3 Missing CTS disposal | **P1** | Should fix |
| §2.7 ComposeViewModel MessageBox violation | **P1** | Must fix (MVVM rule) |

---

## 4. Accessibility Audit

This section is written for Kelly Ford, a screen reader user who knows when accessibility claims are bullshit. Every claim below is verifiable against the current codebase.

### 4.1 What QuickMail Gets Right (Genuinely)

1. **UIA RaiseNotificationEvent.** Not ARIA live regions (which don't work in WPF). Not `AutomationProperties.LiveSetting` (which only works in browsers). Actual UIA Notification events — the correct API for desktop screen reader announcements on Windows 10+. Every announcement goes through `AccessibilityHelper.Announce()` with proper category filtering.

2. **Category-filtered announcements.** Users can independently toggle Hint, Status, and Result announcements. This matters because screen reader users get flooded with verbose output from most apps. QuickMail lets you silence "Press Escape to return to the list" after you've learned it, while keeping "3 messages found" results.

3. **Status bar as proper UIA StatusBar.** Four named regions navigable with Left/Right arrows. Each region has proper `AutomationProperties.Name`. The Rules region is a real Button with InvokePattern — not a styled TextBox that announces as "edit." This is the kind of detail that separates real accessibility from checkbox accessibility.

4. **Focus management that works.** F6 cycles through all panes. Ctrl+0-3/9 jumps directly. Escape closes the reading pane and returns to the message list. Focus is restored after folder loads. The WebView2 reading pane has retry logic for focus handoff.

5. **No raw class names in the UI.** Unlike Microsoft's Copilot app which announces `CopilotNative.Chat.Controls.ViewModels.MessageThinkingAndActivityPart`, QuickMail's AutomationProperties are human-readable strings.

6. **Keyboard customization that's transparent.** The CommandRegistry + hotkeys.json system means users can remap any command. The Settings dialog shows conflicts. This is better than Thunderbird's hidden key config and Outlook's ribbon-only customization.

### 4.2 What QuickMail Gets Wrong (Honest Assessment)

1. **The hotkey customization bug (§1.1).** User-customized hotkeys save correctly to `hotkeys.json` but are silently ignored on next launch. The `FindByGesture` method reads the legacy integer fields (`binding.Key`/`binding.Modifiers`) which are always 0 for new bindings, instead of parsing the `Gesture` string. This means the entire keyboard customization feature — a key differentiator — is broken. **This is a P0 fix.**

2. **No spell check.** Composing an email without spell check is unacceptable for a screen reader user. Visual users can see the red squiggly; screen reader users rely on the app to tell them about misspellings. This is table stakes.

3. **No signatures.** Every email client has signatures. QuickMail doesn't. This means manually typing your name and contact info on every message.

4. **No calendar invite handling.** When someone sends an ICS file, QuickMail shows it as an attachment. You can't accept/decline/tentative. You have to open the file externally.

5. **WebView2 reading pane accessibility is inherently limited.** WebView2 is a Chromium control. Screen reader interaction with it depends on UIA-to-IAccessible2 bridging, which is fragile. The CSP blocks scripts, which is correct for security, but also means we can't inject ARIA improvements. This is a fundamental tension between security and accessibility that no email client has fully solved.

6. **Message list doesn't announce selection state clearly enough.** When you arrow through messages, the screen reader should announce: subject, sender, date, read/unread status, and whether the message has attachments. Currently it announces some but not all of these consistently.

7. **No first-run accessibility tutorial.** A new screen reader user opening QuickMail for the first time gets no orientation. They need to discover F6, Ctrl+0-3, and the Command Palette on their own or read the user guide.

### 4.3 Accessibility Comparison Matrix

| Feature | QuickMail | Outlook | Thunderbird | Gmail Web | Win Mail |
|---------|-----------|---------|-------------|-----------|----------|
| UIA notifications | ✅ Best-in-class | ❌ None | ❌ None | ❌ N/A (web) | ❌ None |
| Category-filtered announcements | ✅ | ❌ | ❌ | ❌ | ❌ |
| F6 pane cycling | ✅ | ❌ (F6 does ribbon) | ❌ (F6 inconsistent) | ❌ (browser F6) | ❌ |
| Direct pane focus (Ctrl+0-3) | ✅ | ❌ | ❌ | ❌ | ❌ |
| Status bar arrow navigation | ✅ | ❌ | ❌ | ❌ | ❌ |
| Keyboard shortcut customization | ✅ (bug: §1.1) | ✅ (ribbon only) | ❌ (hidden config) | ✅ (limited) | ❌ |
| Command palette | ✅ | ❌ (Tell Me is different) | ❌ | ❌ | ❌ |
| Spell check | ❌ **Missing** | ✅ | ✅ | ✅ (browser) | ✅ |
| Signatures | ❌ **Missing** | ✅ | ✅ | ✅ | ✅ |
| Calendar invites | ❌ **Missing** | ✅ | ✅ (via Lightning) | ✅ | ❌ |
| Message list selection announcement | ⚠️ Partial | ⚠️ Partial | ⚠️ Partial | ⚠️ Partial | ❌ |
| First-run tutorial | ❌ **Missing** | ❌ | ❌ | ❌ | ❌ |
| Reading pane accessibility | ⚠️ WebView2 limits | ⚠️ Word renderer | ⚠️ Gecko engine | ⚠️ Web | ❌ |

---

## 5. Prioritized Feature Roadmap

Features are ordered by: (1) accessibility impact, (2) user pain, (3) competitive necessity, (4) implementation feasibility.

### Phase 1: Foundation Fixes (Sprint 1 — Now)

These are bugs and missing basics that undermine the entire product. Ship these first.

| # | Feature | Priority | Description |
|---|---------|----------|-------------|
| F1 | **Fix hotkey customization bug (§1.1)** | P0 | Parse `Gesture` string in `ApplyUserOverrides` instead of reading legacy integer fields. ~30 lines. Unblocks the keyboard customization differentiator. |
| F2 | **Add global exception handlers (§1.2)** | P0 | Wire `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` in `App.xaml.cs`. Log all, show message box for dispatcher exceptions. |
| F3 | **Fix LogService thread safety (§1.3)** | P0 | Add lock around `File.AppendAllText`. 5 lines. |
| F4 | **Fix BatchObservableCollection (§1.5)** | P0 | Add `finally` reset, depth counter, `BeginBatchScope()` helper. |
| F5 | **Fix ContactService race condition (§1.6)** | P0 | Hold lock for entire upsert/delete body. |
| F6 | **Fix ComposeViewModel MessageBox (§2.7)** | P1 | Replace `MessageBox.Show` with `ConfirmationRequested` callback. MVVM rule violation. |
| F7 | **Fix HasAttachments from cache (§1.10)** | P1 | Add `has_attachments` to SELECT statements in `ReadSummariesAsync`. |
| F8 | **Fix Drafts folder detection (§1.7)** | P1 | Use `SpecialFolderKind.Drafts` instead of substring match. |

### Phase 2: Table Stakes (Sprint 2)

Features that every email client must have. Their absence makes QuickMail feel incomplete.

| # | Feature | Priority | Description |
|---|---------|----------|-------------|
| F9 | **Spell check** | P0 | Integrate Windows built-in spell check (`System.Windows.Controls.SpellCheck`). Must announce misspellings to screen readers. Red squiggly + UIA notification on navigation into misspelled word. |
| F10 | **Email signatures** | P0 | Plain-text and HTML signatures per account. Stored in account config. Auto-inserted in compose window. Must be navigable/editable with keyboard. |
| F11 | **Calendar invite handling (ICS)** | P0 | Parse ICS attachments. Show event details (title, time, location, description). Accept/Decline/Tentative buttons. Generate ICS reply. Must work entirely from keyboard. |
| F12 | **Message list accessibility improvements** | P0 | Consistent screen reader announcement for each message: "Subject, From Name, Date, Unread/Read, Has Attachment." Use `AutomationProperties.Name` with computed string. |

### Phase 3: Differentiators (Sprint 3)

Features that make QuickMail better than competitors for screen reader users specifically.

| # | Feature | Priority | Description |
|---|---------|----------|-------------|
| F13 | **First-run accessibility tutorial** | P1 | On first launch, offer an interactive tutorial that teaches F6, Ctrl+0-3, Command Palette, and Escape. Each step waits for the user to perform the action. Fully self-voicing via UIA notifications. Can be replayed from Help menu. |
| F14 | **Audio feedback for send/receive** | P1 | Distinct sounds for: mail sent, new mail arrived, sync error, connection lost. Configurable per category. Visual users get status bar updates; audio users should get equivalent information. |
| F15 | **Message read aloud** | P2 | "Read this message" command that uses Windows TTS to speak the plain-text body. Pause/Resume/Stop. Not a full screen reader replacement — a convenience for quick triage. |
| F16 | **Contact manager UI** | P1 | Proper contacts dialog with add/edit/delete, search, import/export vCard. Currently contacts exist only as auto-collected addresses. |

### Phase 4: Power User (Sprint 4)

Features that power users expect and that round out the product.

| # | Feature | Priority | Description |
|---|---------|----------|-------------|
| F17 | **PGP/GPG encryption** | P2 | Integrate with GnuPG. Encrypt/sign outgoing, decrypt/verify incoming. Must announce signature verification results to screen reader. |
| F18 | **Message templates** | P2 | Save and insert canned responses. Keyboard-accessible template picker. |
| F19 | **Server-side search (IMAP SEARCH)** | P2 | Use IMAP SEARCH command for server-side full-text search. Fall back to local search when server doesn't support it. |
| F20 | **Send later / scheduled send** | P2 | Compose now, send at specified time. Local queue, no server dependency. |

### Phase 5: Refactoring (Ongoing)

Technical improvements that don't add user-facing features but make the codebase sustainable.

| # | Feature | Priority | Description |
|---|---------|----------|-------------|
| R1 | **Extract MainViewModel patterns (§2.1)** | P1 | `RebuildActiveGroupView()`, `FetchAndMergeAsync()`, group-action consolidation. |
| R2 | **Extract tree-view triplets (§2.2)** | P1 | `GroupedMessageTreeController<TGroup>` class. |
| R3 | **Fix CTS disposal (§2.3)** | P1 | `ReplaceCts` helper with dispose. |
| R4 | **Centralize ViewMode/Sort serialization (§2.4)** | P1 | Enum-based config instead of magic strings. |
| R5 | **Add user_version migration system (§2.5)** | P2 | Proper SQLite schema versioning. |

---

## 6. Feature Crew Assignments

Each feature crew consists of three AI agents working together:

- **PM Agent** — Writes the product spec: user stories, acceptance criteria, accessibility requirements, competitive context
- **Dev Agent** — Implements the feature: code, XAML, service integration, command registration
- **Test Agent** — Writes tests: unit tests, XAML parse tests, VM construction tests, accessibility verification

All three agents produce documentation. Crews can choose unified spec or separate PM/Dev/Test docs.

### Crew Alpha — Foundation Fixes (F1-F8)
- **PM:** AI PM Agent
- **Dev:** AI Dev Agent (Dev Lead)
- **Test:** AI Test Agent (Test Enforcer)
- **Deliverables:** Fixed code + tests for all P0/P1 bugs from §3.3

### Crew Bravo — Table Stakes (F9-F12)
- **PM:** AI PM Agent
- **Dev:** AI Dev Agent (Dev Lead)
- **Test:** AI Test Agent (Test Enforcer)
- **Deliverables:** Spell check, signatures, ICS handling, message list accessibility

### Crew Charlie — Differentiators (F13-F16)
- **PM:** AI PM Agent
- **Dev:** AI Dev Agent (Dev Lead)
- **Test:** AI Test Agent (Test Enforcer)
- **Deliverables:** First-run tutorial, audio feedback, message read-aloud, contact manager

### Crew Delta — Power User (F17-F20)
- **PM:** AI PM Agent
- **Dev:** AI Dev Agent (Dev Lead)
- **Test:** AI Test Agent (Test Enforcer)
- **Deliverables:** PGP, templates, server search, scheduled send

### Crew Echo — Refactoring (R1-R5)
- **PM:** AI PM Agent
- **Dev:** AI Dev Agent (Dev Lead)
- **Test:** AI Test Agent (Test Enforcer)
- **Deliverables:** Extracted patterns, CTS fixes, migration system

---

## 7. Success Metrics

### Accessibility Metrics (Primary)

| Metric | Target | Measurement |
|--------|--------|-------------|
| All UIA notifications use correct category | 100% | Code review: every `Announce()` call has explicit category |
| No raw class names in AutomationProperties | 100% | Code review + screen reader testing |
| Every dialog reachable via keyboard only | 100% | Manual testing: Tab through every dialog |
| F6 cycle reaches every pane | 100% | Manual testing |
| Command palette lists all registered commands | 100% | Automated test |
| Spell check announces errors to screen reader | Yes | Manual testing with screen reader |
| ICS invite fully operable by keyboard | Yes | Manual testing |

### Feature Metrics (Secondary)

| Metric | Target |
|--------|--------|
| Features from Phase 1-2 shipped | 12/12 |
| P0 bugs resolved | 6/6 |
| Test coverage on new code | >80% |
| Build stays green | Every commit |

### Story Metrics

| Metric | Target |
|--------|--------|
| Blog posts about AI-assisted accessible development | 3+ |
| Features shipped entirely by AI crews | 12+ |
| Lines of production code written by AI | >90% of new code |

---

## 8. The Story: Building Accessible Software with AI

### The Narrative

QuickMail is an experiment in a question: **Can AI-assisted development produce genuinely accessible software?**

The tech industry talks endlessly about AI writing code. GitHub Copilot, Claude, ChatGPT — they all promise to make developers more productive. But the conversation almost never includes accessibility. Can AI write *accessible* code? Can it understand UIA patterns, screen reader announcements, focus management? Can it produce software that a blind user can actually use?

Kelly Ford has been asking these questions through his blog and his projects. He's built the Image Description Toolkit, RSS Quick, Sports Scores, and WeatherFast — all with AI assistance, all with accessibility as a first-class requirement. QuickMail is the most ambitious test yet: a full desktop email client.

### What We're Proving

1. **AI can implement UIA correctly.** `RaiseNotificationEvent`, `AutomationProperties.Name`, `ControlType`, `InvokePattern` — these are well-documented APIs. AI models have been trained on MSDN. They should get this right.

2. **AI can maintain accessibility discipline.** No `MessageBox` in ViewModels. No raw class names in `AutomationProperties.Name`. Every `Announce()` call has a category. These are rules that can be encoded in instructions files and verified automatically.

3. **AI can write tests that verify accessibility.** XAML parse tests, VM construction tests, `AutomationProperties` verification — these are mechanical but essential.

4. **The bottleneck is human judgment, not AI capability.** AI doesn't know what "feels accessible." It doesn't know that a screen reader user needs to hear "3 unread messages from Kelly Ford" not just "3 items." The PM specs, the acceptance criteria, the accessibility requirements — these require human experience. But once specified, AI can implement them.

### The Process

1. **PM Agent writes the spec** — user stories, accessibility requirements, acceptance criteria
2. **Dev Agent implements** — following MVVM rules, accessibility patterns, CommandRegistry
3. **Test Agent verifies** — unit tests, XAML parse tests, accessibility assertions
4. **Human (Kelly) reviews** — the irreplaceable step: does it actually work with a screen reader?

### The Documentation

Every feature crew produces:
- **PM spec** — what we're building and why, with accessibility requirements
- **Dev spec** — how we're building it, files to create/modify, implementation order
- **Test plan** — what tests exist, what they verify, how to run them

These documents serve two purposes: they guide the AI agents, and they tell the story of how accessible software gets built with AI assistance.

### The Blog Posts (Planned)

1. **"Can AI Write Accessible Code?"** — The QuickMail experiment, what worked, what didn't
2. **"Accessibility by Specification"** — How detailed PM specs with accessibility requirements produce better AI-generated code
3. **"The Feature Crew Model"** — PM/Dev/Test AI agents collaborating on accessibility-first features

---

## Appendix A: Competitive Analysis Methodology

This analysis was conducted on May 24, 2026. Sources:

- Direct testing of QuickMail v0.6.3 codebase
- Public documentation for Outlook, Thunderbird, Gmail, Windows Mail, eM Client, Mailbird
- Kelly Ford's blog posts documenting accessibility failures in Microsoft products
- Prior QuickMail engineering reviews (May 16 and May 21, 2026)
- Screen reader community forums and issue trackers

Accessibility ratings are based on screen reader usability, not WCAG conformance checklists. A product can pass automated WCAG checks and still be unusable with a screen reader. Ratings reflect actual user experience.

---

*This document was produced by AI (GitHub Copilot, deepseek-v4-pro:cloud) under human direction as part of the QuickMail roadmap initiative. It will be updated as features ship and new information emerges.*
