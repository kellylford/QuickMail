# SPEC TEMPLATE — QuickMail AI-Collaborative Development

> This template guides the creation of specs that work effectively with AI assistants across the full development cycle: idea → spec → implementation → verification → code review → merge.
>
> **Key principle:** Specs are not just documentation; they are a contract between the human (you) and the AI about what will be built, with explicit decision points for when to start new sessions and what success looks like.

---

## How This Template Works

### Your Workflow

1. **Session 1 (this template):** You provide a broad goal. AI writes a combined PM + Dev spec, you review.
2. **Session 2 (implementation):** AI reads the approved spec, builds the feature, makes adjustments as needed.
3. **Session 3 (manual testing):** You test the built feature in the app, note any issues.
4. **Session 4 (code review):** AI does a comprehensive code review of the working feature.
5. **Session 5 (merge):** Polish and ship.

### Why This Matters

- **Session 1 catches design gaps early** — before any code is written. Keyboard walkthrough, architecture decisions, accessibility — all explicit.
- **Session 2 has latitude to adjust** — implementation always reveals unexpected challenges. The spec is detailed enough to guide, flexible enough to adapt.
- **Session 3 is manual human verification** — AI can't test the UI. You do. If bugs emerge, they get documented as spec gaps or implementation oversights.
- **Session 4 fixes quality systematically** — code review in a fresh session, with no prior context biases.

---

## Section 1: Executive Summary

**Length:** 3–4 sentences. **Purpose:** Why this feature exists and what it does.

This should answer:
- What problem does it solve?
- Who benefits?
- How is it different from today?

**Example:**
> QuickMail today has no way to compare two emails side-by-side or keep a draft visible while reading. This spec adds Tab and Window modes so power users can open multiple messages at once. The default stays "Reading Pane" (zero change for existing users), but users who opt into Tab or Window mode get a productive multi-message workflow.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

Create a **before/after table** of the specific friction:

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Feature X | Current behavior | What users can't do | User type |

**Key rule:** Every claim about "today" must be verified against the code. If you say "the reading pane has no way to zoom text," grep `MainWindow.xaml.cs` to confirm there's no zoom handler. If you say "Compose window titles are hardcoded," look at line 11 of `ComposeWindow.xaml`. AI will ask you to verify; have the evidence ready.

### 2.2 Target personas (3–5)

For each persona, state:
- **Who** (role, context)
- **What they want** (one core need)
- **Why it matters** (pain point or productivity gap)
- **How they'd use this feature** (one sentence)

### 2.3 Why now (optional but recommended)

Why is this the right time to build? E.g.:
- Dependencies are in place (e.g., `CommandRegistry` is already built).
- User feedback has accumulated.
- Roadmap alignment.
- Prerequisite features just shipped.

---

## Section 3: Design Principles

List 3–5 non-negotiable principles that guide all decisions in the spec. These are not requirements; they are *philosophies* that resolve tie-breakers.

**Examples:**
- "Zero-change for users who don't opt in."
- "Keyboard parity with Windows standards (Ctrl+Tab, Alt+Tab, etc.)."
- "Escape is local to the surface (in a tab, Escape returns to the tab's content; in a window, Escape closes the window)."
- "All accessibility features are user-configurable and off by default unless proven non-intrusive."

These will be cited in code review when implementation deviates.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

Explicit list of what ships. Include:
- New user-facing settings (with defaults).
- New keyboard shortcuts (with defaults).
- New UI elements.
- Changes to existing services or ViewModels.

Use a table:

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Feature A | `SettingName` / `Ctrl+X` | Value | What it does |

### 4.2 Explicitly out of scope (v1)

What does **not** ship. This prevents scope creep and surfaces assumptions that need design decisions.

**Examples:**
- "Tab sessions do not persist across app restart; they are in-memory only."
- "Detached tabs cannot be tiled inside a split pane; we use separate top-level windows only."
- "Drag-to-reorder tabs is implemented; drag-to-detach is not."

If something is deferred to v2, say so here — don't leave it implicit.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

For each major decision, state the **choice**, the **alternatives considered**, and the **trade-off**.

**Format:**

**Decision:** [Single-sentence statement of choice]

**Alternatives:**
1. Alt A: Pro X, Con Y.
2. Alt B: Pro X, Con Y.

**Rationale:** Why we chose this. Include constraints (e.g., "WebView2 pools are bounded to 6 per account, so multiple independent WebView2 instances per tab would exhaust the pool") and verified facts.

**Examples of decisions to document:**
- WebView2 strategy: One shared WebView2 per window, or one per tab?
- Modal dialog patterns: Does this feature open a dialog? If so, can it fire events that touch the parent window (violates COM rules)?
- Service layer changes: New service? Extension to existing service?
- Persistence: Config file, SQLite, JSON, or in-memory?
- Threading: Async/await patterns? Background tasks? PeriodicTimer?

### 5.2 Runtime mode compatibility

If your feature calls any of these, state what happens in each mode:

| Mode | LocalStoreService available? | Calls `LocalStore...Async`? | Fallback? |
|---|---|---|---|
| Normal | ✓ | Yes | — |
| `--online` | ✗ | Falls back to IMAP | ✓ |
| `--profileDir <path>` | ✓ | Yes (alternate path) | — |

This forces you to think about crash edge cases early.

### 5.3 Code reuse and duplication risks

Call out code you might duplicate and plan extractions:

- "HTML rendering from message body (currently in `MainWindow.xaml.cs`) will be needed in `MessageWindow.xaml.cs`. Plan: extract to a shared `MessageBodyHelper` internal static class, or share a method."
- "Focus restoration logic exists in three places (`MainWindow`, `ComposeWindow`, `AddressBookWindow`). Plan: can we refactor to a single pattern before adding `MessageWindow`?"

This is where you catch the "bugs that hide in duplication" ahead of time.

---

## Section 6: Keyboard Walkthrough (Mandatory)

This section proves the feature is fully designed before code is written. It's a numbered script of what the user does and what they hear/see, for each major path.

**Format:**

### Path: [Name, e.g., "Open message in a tab"]

1. User presses [key] on [context]. **Expected:** Screen reader announces "…", focus moves to [element], [visual change].
2. User presses [key]. **Expected:** …
3. User presses [key]. **Expected:** …

**Complete every path without ambiguity.** If you find a gap (e.g., "what happens if the user presses Tab while focus is on the close button?"), that gap is a design decision that needs resolving — not something to leave for implementation.

**Paths to cover:**
- Happy path (main use case).
- Error cases (validation failed, operation failed).
- Edge cases (empty state, focus edge cases, boundary conditions).
- Every configured mode or setting.
- Keyboard-only navigation (no mouse).
- Screen reader user (all announcements).

**Example from Tab feature:**

### Path: Open a message in a tab

1. User is in the message list with focus on a row. User presses Enter. **Expected:** A new tab opens with the message subject as the title, the reading pane is populated with the message, focus moves to the message body, screen reader announces "Message body. [subject]."
2. User presses Ctrl+Tab. **Expected:** Focus moves to the previous tab in MRU order (if multiple tabs exist), screen reader announces "Tab [N] of [M]: [title]."
3. User presses Escape. **Expected:** Focus returns to the message list at the row that was open before the tab opened.
4. User presses Ctrl+W. **Expected:** The tab closes, focus returns to the message list.

---

## Section 7: Accessibility Checklist (Mandatory)

Before implementation, answer these explicitly:

- **AutomationProperties.Name** — What short labels will be introduced? (No role names, no hints, no shortcuts.)
- **AnnouncementCategory** — What announcements will you make? For each, state the category (Hint/Status/Result) and explain why.
- **Screen reader browse mode** — In WebView2 (if used), how will the reading pane behave? Will the user be able to Tab out of it, or is Tab trapped inside the body?
- **Focus restoration** — If a dialog opens, how does focus return when it closes?
- **F6 ring** — Does this feature add a new pane that should be in the F6 focus cycle? If yes, where?
- **Checkbox / radio button groups** — If new form controls are introduced, are radio buttons in a single tab stop with directional navigation?
- **Color-only information** — Is any UI state communicated by color alone (no text label)?

**Expected answer:** "Feature X introduces no new panes, no WebView2 browsing, one TextBox for [name] which is bound to [VM property]. Announcements: 'X completed' (Result) when [action] finishes. No F6 changes."

---

## Section 8: Success Metrics

How will you measure that this feature works, post-implementation and post-review?

- **Behavioral:** "User can open 5 messages in 5 tabs and cycle between them with Ctrl+Tab."
- **Keyboard-centric:** "All primary actions (open, close, cycle, reorder) work keyboard-only."
- **No regressions:** "Existing reading-pane tests pass unchanged. No Alt+Tab behavior change."
- **Accessibility:** "Screen reader user can navigate all tabs and learn which is active and which is current."
- **Online mode:** (if applicable) "Feature works correctly with `--online` flag."

---

## Section 9: Implementation Phases

Break the work into **3–5 testable phases**, each of which can be code-reviewed and committed independently. Each phase should have:

- **Name** (e.g., "Phase 1: Data model & persistence")
- **Goal** (what's complete after this phase)
- **Deliverables** (what files are created/modified)
- **Tests** (what's tested)
- **Risk** (what could go wrong, and when will you catch it)
- **Duration estimate** (for AI — helps with planning)

**Example from Tab feature:**

### Phase 1: Data Model & Config Persistence

**Goal:** `TabSessionModel` and `WindowingPreferences` classes exist, config can be read/written.

**Deliverables:**
- Create `QuickMail/Models/TabSessionModel.cs` (data class)
- Create `QuickMail/Models/WindowingPreferences.cs` (settings)
- Modify `QuickMail/Services/ConfigService.cs` to read/write `[windowing]` section
- Modify `QuickMail/Models/ConfigModel.cs` to include `Windowing` property

**Tests:**
- `ConfigServiceTests` — round-trip config read/write
- `TabSessionModelTests` — construction, validation

**Risk:**
- ConfigService can corrupt config.ini if INI parsing is fragile. Mitigation: test with edge cases (empty values, special chars).
- New properties in ConfigModel must be included in equality tests.

**Duration:** 2–3 hours

### Phase 2: UI Shell (Tab strip, no functionality)

**Goal:** Tab strip appears in MainWindow, styled correctly, tab control wired but not functional.

**Deliverables:**
- Modify `QuickMail/Views/MainWindow.xaml` to add TabControl
- Add `TabItemTemplate.xaml` (Style for tab header)
- Modify `QuickMail/ViewModels/MainViewModel.cs` to expose `OpenTabs: ObservableCollection<TabSessionModel>`

**Tests:**
- `XamlParseTests` — MainWindow XAML loads without errors
- `ViewManagerViewModelTests` — (existing, ensure no regression)

**Risk:**
- TabControl may interfere with existing keyboard navigation (Ctrl+Tab, etc.). Mitigation: test with full keyboard walkthrough in phase 1 completion.
- Tab strip may grab focus unexpectedly. Mitigation: F6 tests.

**Duration:** 2–3 hours

### Phase 3: Open/Close & Activation

**Goal:** Opening a message creates a tab, closing a tab removes it, clicking/Ctrl+Tab switches the active tab.

**Tests:**
- New tests: `MainViewModelTabTests` — OpenMessage, CloseTab, ActivateTab
- Regression: message-list keyboard nav unchanged

**Risk:**
- Race condition: if user opens message while the previous open is rendering, both tabs may exist. Mitigation: cancel any in-flight render before opening new message.
- Focus management: when a tab closes, where does focus go? Must be verified in keyboard walkthrough.

**Duration:** 3–4 hours

---

## Section 10: Files to Create / Modify

Explicit list. This is your implementation checklist and the code reviewer's map.

### Files to Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `Models/TabSessionModel.cs` | Tab data class | 30–50 |
| `Views/TabItemTemplate.xaml` | Tab header style | 40–60 |

### Files to Modify

| File | Changes | Lines changed (est.) |
|---|---|---|
| `Models/ConfigModel.cs` | Add `Windowing` property | +10 |
| `Services/ConfigService.cs` | Parse `[windowing]` section | +20 |
| `Views/MainWindow.xaml` | Add TabControl | +30 |
| `ViewModels/MainViewModel.cs` | Add tab commands, OpenTabs collection | +150 |

---

## Section 11: Tests to Add

For each new class, list the tests you'll need:

| Test Class | Test Methods | Coverage |
|---|---|---|
| `TabSessionModelTests` | Construction, validation, title truncation | Happy path + edge cases |
| `MainViewModelTabTests` | OpenMessage → creates tab, CloseTab → removes, ActivateTab → switches | Happy path, focus edge cases |
| `ConfigServiceTabTests` | Read/write `[windowing]` section, round-trip | Config persistence |

**Key rule:** Every new public method gets at least one test. Every branch in a control-flow statement (if/else, loop) gets a test case.

---

## Section 12: Known Risks & Open Questions

### 12.1 Risks identified in the spec

For each risk, state:
- **Risk:** What could go wrong?
- **Probability:** High / Medium / Low
- **Impact:** If it happened, how bad? (Blocker / Major / Minor)
- **Mitigation:** How will you reduce the probability or impact? (Code review, test, design constraint, etc.)

**Example:**

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| WebView2 pool exhaustion if tabs have independent instances | High | Blocker | Design choice: share one WebView2 per window. Document in §5.1. |
| Modal dialog fires ViewsChanged while tab dialog is open → crash | Medium | Blocker | Rule enforcement: tab dialog must not subscribe to ViewsChanged before ShowDialog(). Code review gate. |
| Screen reader user can't tell which tab is active | Medium | Major | Accessibility test in keyboard walkthrough; automated test in v2. |

### 12.2 Open questions

Issues that need resolution before implementation starts:

- "Should closing a tab with unsaved compose warn the user? Yes or no?"
- "If user switches accounts while a tab is open, does the tab persist?" (Deferred to v2 for now.)

Every open question gets a decision before the spec is approved. Document the decision and the rationale.

---

## Section 13: Appendix — Keyboard Reference

If the feature adds multiple shortcuts, a cheat sheet is helpful:

| Key | Action | Notes |
|---|---|---|
| `Ctrl+Tab` | Next tab (MRU) | Wraps to first if last. |
| `Ctrl+Shift+Tab` | Previous tab | Wraps to last if first. |
| `Ctrl+1–8` | Jump to tab N | If no tab N, ignored. |
| `Ctrl+9` | Jump to last tab | Same as Alt+End in browser. |
| `Ctrl+W` | Close active tab | Prompts if unsaved. |

---

## Section 14: Implementation Guidance for AI

This section is **written to the AI assistant** who will read the spec during Session 2. It's in the approved spec, not a separate memo.

### 14.1 Adjustments you're expected to make

State what you've intentionally left vague, expecting the AI to figure out:

- "The spec describes `TabSessionModel` but doesn't specify whether tabs are stored in a list or a dictionary. You'll decide based on the lookup pattern (by Guid, by index, by name?)."
- "The keyboard walkthrough assumes focus restoration works the same as the existing reading pane. If you find that's not true, adjust the implementation and document the deviation."
- "The accessibility checklist says tabs will announce their title and position. You'll decide whether 'Tab 2 of 5: subject' or 'Second tab: subject' is better, and make that announcement consistent throughout."

### 14.2 When to ask for clarification

Signal what's unclear or what needs a design decision before you start:

- "If [architectural risk] becomes a problem during implementation, the approved mitigation is [X]. If that doesn't work, ping the user before proceeding further."
- "The keyboard walkthrough is normative — if you find a shortcut conflicts with an existing one, stop and ask before working around it."

### 14.3 After implementation: what will be tested

"After you build this, the user will test in the app by:
1. Opening 5 messages in tabs.
2. Cycling with Ctrl+Tab and verifying the active tab switches.
3. Testing with `--online` flag to ensure SQLite unavailability doesn't break tabs.
4. Testing with a screen reader to verify announcements are present and correct.

If any of these fail, you'll address them in Session 4 (code review)."

---

## Section 15: Session Boundaries (Template Guidance)

Insert this section **after the spec is approved**, to guide the AI on when to start each session.

### Session 1 → Session 2: Transition

**When to start Session 2:** After spec is approved and you've verified no outstanding questions.

**What to give Session 2:** The approved spec, the message "Build this spec."

**What Session 2 delivers:** Working code that:
- Passes all new tests listed in §11.
- Implements all Phase 1, 2, 3... phases (or up to a natural stopping point if the feature is large).
- Has no commented-out code or TODOs from the spec.
- Is ready for your manual testing.

### Session 2 → Session 3: Transition

**When to start Session 3:** After you've tested the built feature manually and either (a) it works, or (b) you found bugs to report.

**What to give Session 3:** A note like "I tested in the app. Found bugs: [list]. Request code review on the PR."

**What Session 3 delivers:** Code review of the working feature, with fixes applied.

---

## Checklist for Approving a Spec

Before you approve a spec and move to Session 2 (implementation), verify:

- [ ] **Scope is bounded.** The feature doesn't try to do too much in one session.
- [ ] **Architecture is decided.** No major "we'll figure this out during coding" decisions.
- [ ] **Keyboard walkthrough is complete.** No "TBD" paths.
- [ ] **Accessibility is explicit.** Not "we'll make it accessible during review."
- [ ] **Implementation phases are testable.** Each phase is code-reviewable on its own.
- [ ] **Risk assessment is documented.** Known risks have mitigations.
- [ ] **No open questions remain.** Every design decision is made (or explicitly deferred to v2).
- [ ] **Files and tests are listed.** The implementation is a checklist.
- [ ] **Runtime modes are considered.** If applicable, `--online` behavior is defined.

---

## When This Template is Overkill

Some features don't need all sections:

- **Bug fix or small enhancement** — Use sections 1, 2, 3, 4, 8, 11 only. (1 page, not 20.)
- **Refactoring with no user-facing change** — Use section 5 (architecture), skip UI/keyboard/accessibility.
- **Configuration tweak** — Use sections 1, 4, and a simple table of settings.

Adjust the depth to the scope. The template is a maximum, not a minimum.

---

## Why This Template Works with AI

1. **Explicit decision points** — AI knows what's decided and what needs judgment. It won't second-guess architecture decisions during implementation.
2. **Keyboard walkthrough** — AI reads the walkthrough and builds UX that matches. No surprises at test time.
3. **Accessibility is upfront** — Not bolted on post-hoc. AI implements with access in mind.
4. **Phases are independent** — AI can tackle each phase in a session, commit it, and you can review.
5. **Risk mitigation is named** — When AI encounters the risk during coding, it knows the mitigation to apply.
6. **Session boundaries are clear** — AI knows when to say "this is working, time for human review" vs. "I found a bug, let me fix it."
