# Theme Review Response & Visual Quality Plan

**Date:** 2026-07-30
**Input:** External AI ("Fusion AI") visual review of QuickMail's window and six themes.
**Purpose:** Verify the review against the code, define the fixes, define the testing that makes visual quality self-sustaining, and define the process by which AI produces visually good work without a sighted human in the loop.
**Related:** #180 (visual verification harness — open, spec-only), #175 (debug screenshot capture — dependency of #180), #177/#179 (theming, shipped), `docs/planning/theming-visual-design-pm-dev-spec.md`.

---

## 1. Is the review accurate?

Split verdict. The review has two distinct parts with very different reliability, and telling them apart matters for how we use AI reviews going forward.

### Part 1 — the screenshot-grounded observations: largely accurate, one major find

| Claim | Verdict | Evidence |
|---|---|---|
| Toolbar text "very light and low-contrast, as though disabled" | **TRUE — real bug, root cause found** | `ThemedControls.xaml` styles ~30 control types but has **no ToolBar style**. The ToolBar container renders WPF's default light gradient chrome in every theme. Toolbar buttons *do* get the themed style — `Foreground = Theme.TextPrimary` on a transparent background. In dark themes that puts near-white text (#E8E6E3) on the default light toolbar: ≈1.1:1 contrast. Text is effectively invisible. Affects MainWindow, ComposeWindow (formatting toolbar), MessageWindow, and FlagManagerWindow. |
| "Abrupt transition from dark menu bar to very light toolbar" | **TRUE** | Menu is themed (dark), ToolBar is not (light). Same root cause. |
| Faint dividers/separators | **TRUE** | All three border tokens fail 3:1 in **every** theme: `border` 1.6–2.0:1, `borderSubtle` 1.3–1.6:1, `inputBorder` 2.1–2.4:1. The existing contrast tests don't cover border tokens. |
| Dense, crowded message list rows | **TRUE** | GridView row padding is 2px all around; flat-list rows 4,2. No breathing room by design; nothing broken, but the observation is fair. |
| Subjects truncated with no full-text access | **TRUE** | `TextTrimming="CharacterEllipsis"` with no ToolTip on the Subject cell. |

### Part 2 — the "comprehensive theme-by-theme review": largely confabulated

- **It misidentifies three of six themes.** Ember is described as "dark, warm, red/orange-accented"; Fjord as "the calmest dark-theme direction." Both are **light** themes (`base: "light"`, window backgrounds #FBF7F2 / #F8FAFA). Ember, Fjord, and Heather are thin recolors of Parchment overriding only 4 tokens (accent, windowBackground, accentSubtle, selectionBackground).
- **The prose is hedged guesswork, not observation.** Nearly every "problem" is conditional: "can disappear," "may blur," "likely," "if it relies on…" — these are generic statements true of any theme of that color family, not findings about QuickMail.
- **"Cream backgrounds reduce contrast margin / pale text" — FALSE.** Measured text contrast is excellent everywhere: textPrimary 13.5–15.1:1, textSecondary 7.4–8.3:1, hyperlink 6.0–7.9:1, across all five palettes. These are unit-enforced (`BuiltInThemeTests`).
- **"Selection relies on subtle changes" — FALSE.** Selection is a full accent-strength fill with white text at 6.7–7.1:1 — deliberately strengthened in the 2026-07 selection-contrast revision after visual review found the original tint invisible.
- **"Read/unread relies mainly on color" — FALSE.** Unread = Bold text + 3px accent bar + status column text. Non-color cues are the primary signal, per spec.
- **"Provide a high-contrast option" — misses that QuickMail intentionally defers to Windows High Contrast** (ThemeService withdraws all theming under HC so the OS palette wins — the correct WPF behavior).

**One thing the review missed that measurement caught:** the focus indicator on a *selected* row fails in all four light themes — `focusIndicator` (#1F2328) on `selectionBackground` runs 2.2–2.3:1. Only Parchment Dark passes (3.7:1). Keyboard users can't see the focus ring exactly where it matters most.

**Lesson for §4:** the screenshot-anchored first pass produced one high-value verified bug. The "comprehensive review" produced errors and filler. AI visual review is worth exactly as much as the pixels it is grounded in.

---

## 2. Fixes (near-term, ordered)

### Fix 1 — Style the ToolBar family *(highest impact, small change)*
Add ToolBar, ToolBarTray, the overflow/grip parts, and ToolBar-hosted Separator styles to `ThemedControls.xaml`: `Theme.ChromeBackground` background, `Theme.Border` edges, themed overflow button. Verify all four windows that host toolbars. This single change eliminates the review's #1 complaint in every theme.

### Fix 2 — Border/divider contrast policy
Decide the policy, then retune tokens in both base palettes:
- `inputBorder` (identifies a control boundary — WCAG 1.4.11 applies): raise to ≥3:1 vs `inputBackground`.
- `border` (functional separators, grid lines, GroupBox): target ≥3:1 vs the surfaces it divides.
- `borderSubtle` (decorative hairlines): exempt from 3:1 but set a deliberate floor (e.g. ≥1.5:1) so it's a choice, not drift.
Encode the policy as tests (§3.1) in the same PR that retunes the values.

### Fix 3 — Focus ring visible on selection
Options, pick one during implementation: (a) two-tone focus visual (outer ring in `focusIndicator`, inner 1px in `windowBackground`), which survives any background; or (b) a `focusOnSelection` token set to `selectionText`. (a) is systemic and fixes user themes too — recommended. Add the missing contrast test either way.

### Fix 4 — Message-list ergonomics (design decisions, then small PRs)
- Row padding: try 4,3–4,4 on GridView rows (visual-harness before/after, §3.3).
- Add ToolTip with the full subject on truncated cells (sighted-user parity with what a screen reader already gets).
- Left pane: minor — spacing around account/folder groups. Low priority.
- A density setting ("comfortable/compact") is the theming spec's deferred item; only if the padding bump proves contentious.

### Fix 5 — Give Ember/Fjord/Heather real identities *(optional, later)*
They're 4-token recolors; hyperlink, status colors, and selection-adjacent tokens all stay Parchment blue, which is why an accent-heavy theme can feel incoherent (e.g. Ember's rust selection next to Parchment's blue links). Either tune the full token set per theme or document them as accent variants. Not urgent; behind Fixes 1–3.

---

## 3. Testing: make visual quality regression-proof

### 3.1 Extend the contrast unit tests (immediate, cheap)
`BuiltInThemeTests` already fails the build on text-contrast regressions. Add the missing pairs:
- `focusIndicator` vs `selectionBackground` (≥3:1) — currently failing; lands with Fix 3.
- `inputBorder` vs `inputBackground` (≥3:1) — lands with Fix 2.
- `border` / `borderSubtle` per the Fix 2 policy.
- `textPrimary` vs `accentSubtle` (hover fill) — passes today; pin it.

### 3.2 Themed-control coverage guard (new test, would have caught the toolbar)
`ThemedControlCoverageTests`: scan `Views/*.xaml` for control elements in use; assert each type has an implicit style in `ThemedControls.xaml` or appears in an explicit, commented exemption table (the `TypeAheadWiringTests` "Sites table" pattern — the suite fails when someone uses a new control type without deciding its theming). This converts "we forgot to style X" from a sighted-review catch into a build failure. ToolBar is exactly the bug class it exists for.

### 3.3 Build the visual harness — #175 then #180 (the strategic investment)
Both specs are complete; **zero code exists today**. This is the actual answer to "no way to catch visual breakage without a sighted spot-check":
1. **#175** Debug screenshot capture (PrintWindow-based, captures WebView2, debug-gated). ~7–11h per spec.
2. **#180 Phases 1–3**: fixture profile written through real persistence code; `--ui-probe <surface>` offline launch mode; `scripts/ui-probe.ps1` orchestrator looping surface × theme × text-scale.
3. **#180 Phase 4**: AI review of the PNGs against the fixed checklist, PASS/FAIL/UNSURE per surface, `report.md`.
Priority order stands: 3.1 and 3.2 are hours and stop regressions now; the harness is days and stops the *unknown* breakage.

### 3.4 Cadence once the harness exists
- **Per-PR (touched surfaces):** any PR touching XAML/Styles/Themes runs probes for affected surfaces in the current default theme; AI checklist review on the diff'd screenshots.
- **Pre-release (full sweep):** all surfaces × all six themes × 100%/150% text scale, plus Windows High Contrast as a seventh pass. Report attached to the release checklist next to the installer soak test.
- **Baseline diffing** (#180 Phase 5) only after the above is routine.

---

## 4. How AI produces visually good work without sighted review

The Fusion review demonstrates both the value and the failure mode; these rules operationalize the difference.

### 4.1 Grounding rules for any AI visual review
1. **No screenshot, no claim.** Every reported problem must cite what is visible in a named image ("in `inbox-dark.png`, the toolbar region…"). The theme-by-theme section of the Fusion review would have been rejected by this rule alone.
2. **Hedge words are a rejection signal.** "Can/may/likely/risks being" describe a color family, not this app. The reviewer is instructed to report only what it observes; anything else goes in a separate clearly-labeled "speculation" section that never becomes a work item without verification.
3. **Measure, don't estimate.** Contrast claims come from computed ratios against the token JSON, never from "looks low-contrast." (This session: the review's "pale text" claim died on measurement in minutes; the real failures — borders, focus-on-selection — were found the same way.)
4. **Fixed checklist over open prose.** The #180 spec's 8-item "obviously broken?" checklist, extended with: unstyled/default-chrome regions (the toolbar class), theme-mismatch regions, invisible focus ring at the captured focus position.
5. **Two independent passes for release sweeps** (different model or fresh context); disagreement → UNSURE → human-priority triage. Kelly's time is spent on UNSURE, not on PASS.

### 4.2 Design authority: the token system is the single source of truth
The theming spec (§6) already defines semantic tokens with normative contrast rules, and the code enforces them. Extend rather than bypass:
- New UI never hardcodes colors; a hardcoded-brush lint over Views XAML (allowing `Transparent` and documented exceptions) is cheap to add to 3.2.
- Design changes are made *in the token JSON / spec first*, then rendered and screenshot-reviewed — so "make it look better" becomes a reviewable diff of named values, which is a workflow a blind developer owns end-to-end.
- The deferred spacing scale: adopt one (4px base) when Fix 4 lands, so density becomes tokens too.

### 4.3 Sourcing "does this actually look good?" (beyond "not broken")
Automated checks catch broken; they don't produce taste. Three channels, all compatible with Kelly's workflow:
- **AI mockup iteration:** for any visual redesign, generate 2–3 HTML mockups of the surface (cheap to render and screenshot), run the same AI checklist + a comparative "which reads better and why, citing pixels" pass, then port the winner to XAML tokens. Comparative judgments are where current vision models are far more reliable than absolute "is this good" judgments.
- **Reference anchoring:** reviews compare against a named reference (e.g. "column layout and density vs Outlook classic / Thunderbird Supernova screenshots") instead of free-floating aesthetics.
- **Periodic human calibration:** a sighted pass (collaborator or ad-hoc tester) once per release cycle, whose findings are diffed against the AI report — every human find the AI missed becomes a new checklist item. The checklist compounds; the human dependency shrinks.

### 4.4 Standing principle (add to CLAUDE.md once fixes land)
> Visual quality claims must be grounded: screenshots for appearance, computed ratios for contrast, the token spec for intent. An AI review that cannot cite its pixels is speculation, and speculation is not a work item.

---

## 5. Actions & sequencing

| # | Action | Size | Depends on |
|---|---|---|---|
| 1 | File issue: unstyled ToolBar family (bug, all themes, 4 windows) | issue | — |
| 2 | File issue: border-token contrast policy + retune (with 3.1 tests) | issue | — |
| 3 | File issue: focus ring invisible on selected rows in light themes (with test) | issue | — |
| 4 | PR: ToolBar styles | S | 1 |
| 5 | PR: border policy + tokens + tests | S | 2 |
| 6 | PR: two-tone focus visual + test | S | 3 |
| 7 | PR: ThemedControlCoverageTests | S–M | — |
| 8 | PR: subject ToolTip + row padding experiment | S | harness helpful, not required |
| 9 | Implement #175 (capture service) | M | — |
| 10 | Implement #180 Phases 1–3 (fixtures, --ui-probe, orchestrator) | L | 9 |
| 11 | #180 Phase 4 (AI checklist review) + per-PR/pre-release cadence | M | 10 |
| 12 | CLAUDE.md grounding principle + review-prompt template checked into docs | S | after 4–6 |
| 13 | Optional: full palettes for Ember/Fjord/Heather | M | 5, harness |

Items 1–7 are independent of the harness and fix everything the review correctly identified. Items 9–11 are the standing answer to "how do we catch this without waiting for an outside review."
