# Using the Spec Template — Quick Start

This guide shows how to use SPEC-TEMPLATE.md, with a worked example.

---

## The Workflow in 3 Steps

### Step 1: You bring the idea

You say: "I want to add a checkbox in Settings to toggle custom announcements on/off."

This is vague. That's okay. The template will make it precise.

### Step 2: You fill the template with AI

You open SPEC-TEMPLATE.md and guide an AI assistant (e.g., Claude) to write a combined PM + Dev spec:

```
Please write a PM & Dev spec for this feature:
- Users want to toggle custom screen reader announcements on/off via a Settings checkbox.
- Today, announcements are always on and can't be disabled.
- All custom announcements in the code use AccessibilityHelper.Announce() and respect config settings (AnnounceHints, AnnounceStatus, AnnounceResults).
- This should be a simple feature.

Use SPEC-TEMPLATE.md. Fill out sections 1–4, 5 (minimal), 6–12. Reference the existing keyboard walkthrough pattern from the codebase.
```

The AI reads the template and the CLAUDE.md file, then outputs a spec with all sections. You review it. If there are gaps, you ask clarifying questions and the AI revises.

### Step 3: You approve the spec

You look at the checklist in Section 15 (Checklist for Approving a Spec) and verify:
- [ ] Scope is bounded. (Yes, it's one checkbox.)
- [ ] Architecture is decided. (Yes, store in config.ini, bind in SettingsViewModel.)
- [ ] Keyboard walkthrough is complete. (Yes, includes Tab into checkbox, Space to toggle, Focus/announce state.)
- [ ] Accessibility is explicit. (Yes, announces "Custom announcements on/off".)
- [ ] Etc.

Once you check all boxes, the spec is approved. You commit it to `docs/planning/` with a name like `toggle-announcements-pm-dev-spec.md`.

---

## Worked Example: Toggle Custom Announcements Feature

This is a **minimal feature** spec to show what "done" looks like.

---

# Toggle Custom Announcements — PM & Dev Specification

**Status:** Approved  
**Date:** 2026-06-10  
**Target:** v0.7.2  
**Scope:** Small (single checkbox + config binding)

---

## 1. Executive Summary

QuickMail today always announces status messages ("Syncing...", "3 messages moved") to screen reader users. Some users find these intrusive and want to disable them entirely. This spec adds a Settings checkbox "Enable custom announcements" that toggles all programmatic announcements off/on. Existing announcement infrastructure (`AccessibilityHelper.Announce()` and config gates) are preserved; this is a global master switch.

---

## 2. User Problem & Opportunity

### 2.1 Current state

| Surface | Today | Pain |
|---|---|---|
| Screen reader announcements | Always on; gated by per-category config (AnnounceStatus, AnnounceResults, etc.). User must edit `config.ini` to disable all of them. | No discoverable UI to toggle all announcements at once. |

### 2.2 Target personas

| Persona | Need |
|---|---|
| **Power screen reader user (Chris)** | "I'm confident using QuickMail and don't need announcements. I want to turn them all off from Settings." |
| **New screen reader user** | "I keep hearing 'Syncing...' every few seconds and it's distracting. I want an on/off switch in the UI." |

### 2.3 Why now

Users have requested this in discussions. The config infrastructure already supports it (`config.ini` has per-category gates). The UI component (a checkbox) is trivial. This is a quick win that improves screen reader UX.

---

## 3. Design Principles

1. **Master switch, not replacement.** A "disable all announcements" checkbox. Users who want fine-grained control use the per-category checkboxes below it.
2. **Respects per-category settings.** If "Enable custom announcements" is on, each announcement still respects its category (AnnounceHints, AnnounceStatus, AnnounceResults). If the master switch is off, no announcements fire, regardless of category.
3. **Keyboard-navigable.** The checkbox is in the Settings dialog and follows standard WPF checkbox behavior (Tab, Space to toggle).

---

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v0.7.2)

| Feature | Setting | Default | Notes |
|---|---|---|---|
| Master enable/disable | `CustomAnnouncements` (bool) | `true` | Lives in `config.ini` under `[config]` section. |
| Settings UI | Checkbox in Settings → General tab | On | Label: "Enable custom announcements". |
| Behavior | `AccessibilityHelper.Announce()` checks this setting first. | All announcements respect it. | If false, `Announce()` is a no-op. |

### 4.2 Out of scope (v0.7.2)

- Per-window or per-action toggles. Master switch only.
- Announcement verbosity levels. Still only three categories.
- Persistent state for "last N announcements" (user can replay them).

---

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision:** Master switch is stored in `ConfigModel.CustomAnnouncements` (bool), persisted in `config.ini` under `[config]`.

**Alternatives:**
1. Store in a separate `.announcements.json` file. Con: fragmented settings; users don't expect this.
2. Store only in memory (no persistence). Con: user has to set it every time the app starts.

**Rationale:** `ConfigModel` already handles all settings. This keeps settings in one place and leverages existing persistence.

**Decision:** `AccessibilityHelper.Announce()` checks the setting first, before checking category gates.

**Alternatives:**
1. Have `Announce()` ignore the master switch and check only category gates. Con: user has to disable all three categories individually.

**Rationale:** Master switch means "disable all announcements globally." Implemented at the entry point (Announce method) so it's enforced everywhere.

### 5.2 Runtime mode compatibility

This feature doesn't call `LocalStoreService` or any mode-dependent code. Works in all modes: normal, `--online`, `--profileDir`.

### 5.3 Code reuse and duplication risks

No duplication risk. The only change is:
1. Add a property to `ConfigModel.cs`.
2. Add a checkbox to `SettingsDialog.xaml`.
3. Modify `AccessibilityHelper.Announce()` to check the setting.

---

## 6. Keyboard Walkthrough

### Path: Enable custom announcements in Settings

1. User presses Ctrl+Comma (open Settings). **Expected:** Settings dialog opens, focus on first tab (General). Screen reader announces "Settings, General tab."
2. User presses Tab until reaching "Enable custom announcements" checkbox. **Expected:** Focus lands on checkbox. Screen reader announces "Enable custom announcements, checkbox, checked."
3. User presses Space. **Expected:** Checkbox toggles to unchecked. Screen reader announces "Unchecked."
4. User presses Tab to OK button, then Space. **Expected:** Dialog closes, Settings applied. Any in-flight announcement is now suppressed.
5. User performs an action that would normally announce (e.g., move a message). **Expected:** No announcement (checkbox is off).

### Path: Re-enable announcements

1. User opens Settings again and tabs to the checkbox. **Expected:** Checkbox is unchecked (saved state). Screen reader announces "Unchecked."
2. User presses Space. **Expected:** Checkbox toggles to checked. Screen reader announces "Checked."
3. User OK's. **Expected:** Announcements resume.

---

## 7. Accessibility Checklist

- **AutomationProperties.Name:** "Enable custom announcements" (simple label, no extra text).
- **AnnouncementCategory:** N/A (this controls announcements; doesn't make them). When the checkbox state changes, WPF's built-in announcement says "Checked" or "Unchecked."
- **Focus restoration:** Not applicable (Settings dialog is modal; focus returns to main window when closed).
- **F6 ring:** Not applicable (Settings dialog doesn't add panes to the main window's F6 cycle).
- **Radio buttons:** Not applicable.

---

## 8. Success Metrics

- User can toggle the setting from Settings → General.
- When the checkbox is unchecked, no announcements are heard (including status, results, hints).
- When checked, announcements resume (category gates apply as before).
- Setting persists across app restart.
- Keyboard-only navigation works (Tab, Space, Enter to OK).

---

## 9. Implementation Phases

### Phase 1: Data Model & Persistence

**Goal:** `ConfigModel.CustomAnnouncements` exists and is persisted in `config.ini`.

**Deliverables:**
- Modify `QuickMail/Models/ConfigModel.cs` — add `public bool CustomAnnouncements { get; set; } = true;`
- Modify `QuickMail/Services/ConfigService.cs` — parse/write `CustomAnnouncements` from `[config]` section

**Tests:**
- `ConfigServiceTests` — round-trip read/write of `CustomAnnouncements`

**Risk:** None. Straightforward.

**Duration:** 30 minutes

### Phase 2: UI Binding

**Goal:** Checkbox appears in Settings dialog and is bound to the setting.

**Deliverables:**
- Modify `QuickMail/Views/SettingsDialog.xaml` — add checkbox to General tab
- Modify `QuickMail/ViewModels/SettingsViewModel.cs` — add `[ObservableProperty] private bool _customAnnouncements;` and load/save binding

**Tests:**
- `XamlParseTests` — SettingsDialog XAML loads
- `SettingsViewModelTests` — load/save round-trip, binding works

**Risk:** None. Standard MVVM.

**Duration:** 30 minutes

### Phase 3: Enforcement

**Goal:** `AccessibilityHelper.Announce()` checks the setting before announcing.

**Deliverables:**
- Modify `QuickMail/Helpers/AccessibilityHelper.cs` — add early check: if `_config.CustomAnnouncements == false`, return early

**Tests:**
- Unit test or integration test: call `Announce()` with setting off, verify `RaiseNotificationEvent` is not called

**Risk:** Need access to `ConfigModel` or `ConfigService` from the static helper. Solution: inject `IConfigService` into the app's static state during startup, or use a callback pattern.

**Duration:** 1 hour

---

## 10. Files to Create / Modify

### Files to Modify

| File | Changes | Lines changed |
|---|---|---|
| `Models/ConfigModel.cs` | Add `CustomAnnouncements` property | +3 |
| `Services/ConfigService.cs` | Parse/write `CustomAnnouncements` | +8 |
| `Views/SettingsDialog.xaml` | Add checkbox to General tab | +15 |
| `ViewModels/SettingsViewModel.cs` | Add binding for `CustomAnnouncements` | +10 |
| `Helpers/AccessibilityHelper.cs` | Check setting in `Announce()` | +5 |

---

## 11. Tests to Add

| Test Class | Test Methods |
|---|---|
| `ConfigServiceTests` (existing) | Add: `CanRoundTripCustomAnnouncementsSettingAsync()` |
| `SettingsViewModelTests` (existing) | Add: `CustomAnnouncementsLoadAndSaveAsync()` |
| `AccessibilityHelperTests` (new if needed) | `AnnounceRespectsCustomAnnouncementsSetting()` — call Announce with setting off, verify no notification fired |

---

## 12. Known Risks & Open Questions

### 12.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `AccessibilityHelper` doesn't have access to `ConfigService` | Low | Medium (need to refactor helper to accept injected service) | Decide early how to pass config to the helper (static state, DI container, callback). Document in phase 3. |

### 12.2 Open questions

None. Scope is clear, design is decided.

---

## 13. Keyboard Reference

| Key | Action |
|---|---|
| `Ctrl+,` | Open Settings (existing; not changed) |
| `Tab` | Navigate to checkbox in General tab |
| `Space` | Toggle checkbox state |
| `Enter` / `Alt+O` | OK (existing pattern) |

---

## 14. Implementation Guidance for AI

When you implement this spec:

1. **Phase 1 is purely data.** Add the property, test config round-trip. Should be trivial.
2. **Phase 2 binds the UI.** Standard MVVM — no complexity. If the checkbox doesn't appear, verify `SettingsDialog.xaml` is being loaded (XamlParseTests help here).
3. **Phase 3 is the risky part.** `AccessibilityHelper` is a static utility. You need to decide: how does it get access to `ConfigModel`? Options:
   - Make `ConfigService` a static property on `App` during startup, then `AccessibilityHelper` reads `App.ConfigService.Config.CustomAnnouncements`.
   - Pass a `Func<bool>` callback into `Announce()` that returns the setting.
   - Inject `ConfigService` into `AccessibilityHelper` via a static initializer in `App.xaml.cs`.
   
   Pick one, document the choice, and ask if you're uncertain. Don't guess.

4. **Test in all three phases.** Each phase should be testable independently. After phase 1, the setting persists. After phase 2, the checkbox works. After phase 3, announcements respect the setting.

---

## 15. Session Boundaries

After spec approval:

**Session 2 (implementation):** Build phases 1, 2, 3. Commit the feature. Expected delivery: working, all tests passing, ready for manual test.

**Session 3 (manual test):** You open Settings, toggle the checkbox, and verify announcements go silent and resume. You test in both normal mode and with a screen reader. Any regressions are reported.

**Session 4 (code review):** AI reviews the code for: config parsing correctness, MVVM binding correctness, `AccessibilityHelper` modification correctness (no accidentally broken calls elsewhere).

---

## Approval Checklist

- [x] Scope is bounded. (One checkbox, tiny feature.)
- [x] Architecture is decided. (Config storage, MVVM binding, helper check.)
- [x] Keyboard walkthrough is complete. (Tab, Space, OK.)
- [x] Accessibility is explicit. (Checkbox announces state; no surprises.)
- [x] Implementation phases are testable. (Each phase produces testable code.)
- [x] Risk assessment is documented. (One risk: helper access.)
- [x] No open questions remain. (All decisions made.)
- [x] Files and tests are listed. (Clear implementation checklist.)
- [x] Runtime modes are considered. (No mode-specific code.)

**Status:** ✅ **APPROVED** — Ready for Session 2 (implementation).

---

## End of Worked Example

---

## How to Apply This to Your Own Feature

1. **Copy the sections.** Start with sections 1–4, 6–12. Skip sections you don't need (e.g., no keyboard walkthrough for a refactor).
2. **Be specific.** "Users want X" is vague. "Users want to toggle all announcements on/off via a Settings checkbox" is specific.
3. **Verify your claims.** "The config already persists to INI" — yes, check ConfigService.cs. "AccessibilityHelper.Announce() gates announcements" — yes, check the code.
4. **Ask AI for help.** Use the template to guide an AI assistant to write the full spec. You review and refine.
5. **Use the approval checklist.** When the spec feels complete, check all boxes. If any box is unchecked, the spec needs more work.
6. **Commit it.** Save to `docs/planning/your-feature-pm-dev-spec.md`. Reference it in Session 2.

---

## Common Pitfalls

### Pitfall 1: "I'll figure out the architecture during implementation."

**Template fix:** Section 5.1 forces architecture decisions upfront. If you can't decide, do a 2-hour spike and verify it works.

### Pitfall 2: "The keyboard walkthrough is straightforward, I'll skip it."

**Template fix:** Write it anyway. You'll find design gaps. (Tab & Window skipped this and hit focus bugs post-merge.)

### Pitfall 3: "I'll add accessibility later."

**Template fix:** Section 7 is required. If you can't answer the accessibility questions, the design isn't complete.

### Pitfall 4: "This is a small feature, I don't need all the sections."

**Template fix:** True. Use the minimal template for small features (see "When This Template is Overkill" in SPEC-TEMPLATE.md). But do write section 6 (keyboard walkthrough) and 8 (success metrics). These catch surprises.

---

## Questions?

If the template is unclear or you find sections that don't make sense for your feature:

1. **Read LESSONS-LEARNED.md** to understand why each section exists.
2. **Look at existing specs** in `docs/planning/` to see how others filled it out.
3. **Ask an AI assistant.** Prompt: "I'm using SPEC-TEMPLATE.md for [my feature]. I'm stuck on section [X]. Help me fill it out."

The template is a living document. Update it if you discover new patterns or find sections that consistently don't apply.
