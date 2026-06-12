# Lessons Learned — Why the Spec Template is Designed This Way

This document explains the patterns and anti-patterns discovered in QuickMail's spec history, so you understand the reasoning behind the template.

---

## The Tab & Window Feature: What Went Wrong

The Tab & Window feature (v0.7.0, merged in commit b48d1e5) is the "bug farm" reference case. It was comprehensive and well-intentioned, but post-merge showed systemic issues:

### 1. Architectural Deviation Found Too Late

**What happened:** The spec proposed two architectural options for WebView2:
- **Per-tab WebView2:** Each tab gets its own WebView2 instance, independent rendering, clean separation.
- **Shared WebView2:** One WebView2 per main window, tabs re-render into it when activated.

The spec documented both but didn't decisively choose until review feedback. The deviation happened *during implementation* — the dev realized per-tab WebView2 would exhaust the IMAP connection pool (bounded to 6 per account), so they switched to shared WebView2.

**The lesson:** Major architectural choices should be **decided and documented before Phase 1 begins.** If there's any uncertainty, a spike/prototype in the approval phase is worth it. Discovering this mid-implementation meant:
- Rework of already-written code.
- Different design than the spec implied.
- Confusion during code review about what was intended.

**Template answer:** Section 5.1 (Architecture & Technical Decisions) now requires you to explicitly state the choice, alternatives, and trade-offs. No "we'll figure this out" allowed.

### 2. Keyboard Shortcut Design Conflicts

**What happened:** The spec proposed `Ctrl+1–8` for "jump to tab N" — standard browser/editor behavior. But QuickMail already uses `Ctrl+1–3` for pane navigation (focus toolbar, focus accounts, focus messages). 

The conflict was discovered mid-spec review and required a rev:
- Decision: `Ctrl+1–3` stay dual-mode (pane nav when no tabs, tab nav when tabs are open).
- New canonical pane nav: `Ctrl+Alt+1–3`.

**The lesson:** Keyboard shortcuts are **architecture**. They interact with every existing shortcut. The spec should have:
- Audited every existing shortcut before proposing new ones.
- Called out conflicts up front, not discovered in review.
- Documented the dual-mode behavior (not a hidden feature, an explicit design choice).

**Template answer:** The template now assumes you'll include a complete "existing shortcuts" audit in the initial PM section, and a full "shortcut table" (Section 13) before spec approval.

### 3. Focus & Visibility Bugs Post-Merge

**Post-merge fix commits:**
- `6b35e37`: Fix issues #41-54 (tab strip, message window, and accessibility)
- `02b07ec`: Relay Ctrl+W from WebView2 body to close tab or window
- `02b07ec`: Fix tab strip keyboard navigation and close-button focus behaviour
- `0b6642e`: Fix screen reader browse mode in Window and Tab modes
- `f2afff9`: Add IsMessageOpen change tracing for Window-mode reading-pane bug
- `0957995`: Fix: SelectMessageAsync can reopen reading pane in Window mode
- `7f3c8ef`: Fix: reading pane visible when message opens in Window mode

These are focus management, visibility, and state synchronization bugs that weren't caught until the feature was live.

**The lesson:** The **keyboard walkthrough** in the spec is supposed to catch these. But the walkthrough was incomplete — it didn't cover all paths:
- What happens when you switch accounts while a tab is open?
- What if the reading pane is open in Reading Pane mode and you also have a tab open?
- Does Escape in a tab's body close the tab, or just return focus to the message list?

**Template answer:** Section 6 (Keyboard Walkthrough) is now mandatory and emphasized as "proof the feature is fully designed." The example shows every path must be explicit. If you find a gap while reading the walkthrough, that's a design decision that needs making before code starts.

### 4. Online Mode Gaps

**Post-merge fix:** `d8be572` — "Share WebView2 environment from main window with MessageWindow" and `d0d332c` — "Fix MessageWindow body never loading in online mode."

The feature didn't fully work in `--online` mode (SQLite unavailable) until after launch.

**The lesson:** Runtime modes are easy to forget. The feature calls `LocalStoreService.LoadDetailAsync()` in the message window, but that throws in `--online` mode. The fallback to IMAP wasn't properly tested.

**Template answer:** Section 5.2 (Runtime Mode Compatibility) now forces you to think through `--online` mode explicitly before implementation. If a feature calls LocalStore, you state the fallback upfront.

### 5. Code Duplication (HTML Rendering Helpers)

**Memory note:** "Tech debt: `MessageWindow.xaml.cs` duplicates HTML rendering helpers from `MainWindow.xaml.cs`."

Two code-behind files with nearly identical WebView2 setup, HTML rendering, and CSP configuration. This was noted as deferred tech debt in v2, but it's a sign the feature wasn't fully planned.

**The lesson:** When you introduce a second place where code already exists, duplication risk is high. This should have been caught in planning:
- "Where does WebView2 HTML rendering live today?"
- "If we need it in two places (main window + message window), extract a shared helper first."

**Template answer:** Section 5.3 (Code Reuse and Duplication Risks) now calls out anticipated duplication and plans extractions. This forces upfront refactoring decisions.

---

## Successful Features: What Went Right

### IMAP Connection Health (v0.7.1, spec + implementation in PR 2)

**What worked:**
- **Very focused scope.** Only three things: startup retry, dynamic IsConnected, and false-error verification.
- **Clear success metrics.** Specific, measurable: "Accounts show 'Disconnected' only when genuinely unreachable; reconnect automatically."
- **No keyboard walkthrough needed** (no UI changes). The accessibility and runtime-mode thinking was explicit in the spec anyway.
- **Small, testable phases.** 1a (retry logic), 1b (IsConnected event), 1c (exponential backoff), 1d (heartbeat).
- **Implementation was straightforward.** Few surprises, minimal post-merge fixes.

**Why it worked:** The spec was surgical. It identified three specific bugs and fixed them without introducing new features or architecture. Small scope = fewer unknowns.

### Address Book Contacts Tab Redesign (v0.7.1)

**What worked:**
- **Bounded problem.** "Fix three specific issues: JSON redundancy, broken edit flow, no action affordances."
- **Clear ViewModel refactor.** The spec detailed the mode enum, the properties, the commands. Implementation had a clear blueprint.
- **Accessibility designed in.** The spec explicitly called out what announcements would fire.
- **No keyboard conflicts.** The existing Grab Addresses workflow was preserved; only added Add/Edit/Delete buttons.

**Why it worked:** Surgical, bounded scope. No architectural surprises.

### Rich Compose (v0.7, large feature, comprehensive spec)

**What worked despite being large:**
- **Explicit phase breakdown.** 5 phases, each with clear deliverables and testability gates.
- **Accessibility thorough.** Specific announcement categories (Hint, Status, Result), WebView2 browse mode behavior, menu bar accessibility.
- **Keyboard design documented.** Full shortcut table, no conflicts discovered post-hoc.
- **Architecture decided upfront.** "Plain text uses TextBox, Markdown uses TextBox + WebView2 preview, HTML uses WebView2 contenteditable." No surprises during coding.
- **Out-of-scope explicit.** "No drag-to-insert images, no table editing, no template library (v2)."

**Why it worked:** Large features can succeed if they're **thoroughly designed before implementation**. The spec was detailed enough that implementation had no major surprises. There were still post-merge fixes (code quality, focus edge cases), but no architectural rework.

---

## Pattern Extraction

### Small Scope, High Success

| Feature | Scope | Result | Why |
|---|---|---|---|
| IMAP Connection Health | 3 specific fixes | Few post-merge bugs | Surgical, testable. |
| Address Book Contacts | Fix 3 issues | Smooth | Bounded problem. |
| Mail Rules (post-review) | New feature, focused | Manageable bugs | Clear architecture upfront. |

### Large Scope, Risk of Bugs

| Feature | Scope | Result | Why |
|---|---|---|---|
| Tab & Window | 10+ use cases, 3 view modes, multi-window | 14+ post-merge fixes | Architectural deviations, incomplete keyboard walkthrough, online mode untested. |
| Rich Compose | 3 editing modes, format toolbar, conversion matrix | Some post-merge fixes | Mitigated by thorough spec; still had surprises during implementation. |

**Conclusion:** Large features don't fail because they're large; they fail when the spec doesn't prove design completeness. Rich Compose succeeded because the spec was thorough. Tab & Window had issues because architectural and keyboard decisions were unresolved.

---

## Template Rules, Justified

### Rule: Keyboard Walkthrough is Mandatory

**Why:** The Tab & Window keyboard walkthrough didn't cover all paths. Missing paths = missing design decisions = bugs discovered at test time.

Rich Compose's keyboard walkthrough forced designers to think through every mode switch and every edge case upfront. When implementation started, surprises were minimal.

### Rule: Architecture Decisions are Explicit

**Why:** Tab & Window deferred the WebView2 decision. IMAP Connection Health and Address Book decided architecture upfront. Rich Compose did too.

When major decisions are deferred to implementation, you lose time and introduce rework.

### Rule: Runtime Modes are Documented

**Why:** Tab & Window wasn't fully tested in `--online` mode before merge. The spec section on runtime modes would have forced the question: "Does MessageWindow.Load work when LocalStore throws?"

### Rule: Code Duplication Risks are Named

**Why:** HTML rendering duplication in MessageWindow vs. MainWindow was discovered post-merge. Naming it in the spec forces a decision: extract now, or accept the tech debt?

### Rule: Phases are Testable Milestones

**Why:** Rich Compose's phases each produced code that could be reviewed independently. This prevented late discoveries and allowed early course-correction.

Tab & Window's phases were less clear, leading to integrated review and harder rework.

---

## How the Template Prevents Tab & Window Issues

### Issue 1: Architectural Deviation

**Template prevents this:** Section 5.1 requires architecture decisions before Phase 1. "Decision: [X]. Alternatives: [Y, Z]. Rationale: [why X]." No approving a spec with "we'll figure this out during implementation."

### Issue 2: Keyboard Conflicts

**Template prevents this:** Keyboard Walkthrough (Section 6) covers every path. If a conflict emerges, it's resolved before coding starts.

### Issue 3: Focus & Visibility Bugs

**Template prevents this:** The keyboard walkthrough is detailed enough that missing focus paths become obvious. "User presses Escape in the message body. Expected: [focus moves to X]." If you can't define the expected behavior, the design isn't complete.

### Issue 4: Online Mode Gaps

**Template prevents this:** Section 5.2 explicitly asks "Does LocalStoreService.LoadDetailAsync work in --online mode? If not, what's the fallback?" No hand-waving.

### Issue 5: Code Duplication

**Template prevents this:** Section 5.3 asks "Will this code live in two places? Where is it today?" This forces a choice: refactor upfront or accept the debt.

---

## When to Use This Template

### Use the full template for:
- New features with keyboard interaction
- Multi-window or multi-pane features
- Anything touching ViewModels or services
- Features with accessibility requirements

### Simplify for:
- Bug fixes (sections 1, 2, 4, 8, 11)
- Config tweaks (sections 1, 4)
- Refactoring (section 5 only)

---

## Working with AI: The Template as a Contract

The template makes specs **machine-readable**:

1. **AI reads Section 4:** Understands scope and what's in vs. out.
2. **AI reads Section 5:** Understands architectural decisions and constraints.
3. **AI reads Section 6:** Implements to the keyboard walkthrough; any deviation is a question for you.
4. **AI reads Section 9:** Knows the phases and what to test after each.
5. **AI reads Section 14:** Understands what's expected and what's out of scope for the spec.

The spec is not "here's a feature, build it however you want." It's "here's exactly what I want, including design details, keyboard behavior, and accessibility requirements. Implement to this spec, and ask if the design needs to change."

This prevents implementation surprises and reduces the number of iterations needed.

---

## Recommended Improvements Going Forward

1. **Require design review of keyboard walkthrough before implementation.** Walk through it with a screen reader user or accessibility consultant if possible. Catch gaps early.

2. **Prototype architectural risks in the approval phase.** If you're unsure about WebView2 pooling (like the Tab & Window case), write a 2-hour spike code and verify it works.

3. **Test in --online mode immediately after Phase 1.** Don't wait until the feature is "done." Catch LocalStore gaps early.

4. **Extract duplication upfront.** If the spec says "MessageWindow needs the same HTML rendering as MainWindow," extract it in Phase 0, before either window code is written.

5. **Have a phase 0 for architecture/refactoring if needed.** Rich Compose might have benefited from "Phase 0: Extract mode enums and conversion utilities" before touching any UI.

6. **Use code review checklist in the spec itself.** List the specific things a reviewer should check: "Does MessageWindow.Load handle --online mode? (Check the try/catch.)"

---

## Final Note

The template is opinionated because it's based on real experience. The rules aren't arbitrary; they're patterns that prevent the specific bugs you've seen. As you use it, you'll refine it further based on what you learn in each feature cycle.

Keep it in the repo. Update it as you discover new patterns.
