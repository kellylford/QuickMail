# Localization & Internationalization — PM & Dev Specification

> Status: **Draft / exploratory.** Not committed to the roadmap. This spec designs the infrastructure so localization can be built deliberately if/when we decide to ship a non-English UI. No target languages are chosen here — that is a later product decision.
>
> **Tracking:** [Issue #176](https://github.com/kellylford/QuickMail/issues/176)

---

## Section 1: Executive Summary

Every user-visible string in QuickMail today is a hardcoded English literal — roughly 752 attribute strings across 30 XAML views, 253 `AccessibilityHelper.Announce()` call sites, 34+ command titles in `CommandRegistry`, and ~48 dialog/message texts. There is no resource infrastructure, no language setting, and no right-to-left support. This spec defines a standard .NET localization architecture for QuickMail: strings move into `.resx` resources with a strongly-typed accessor, a `UILanguage` setting selects the culture at startup, counts flow through a pluralization helper instead of inline English ternaries, and translations are produced AI-assisted and human-reviewed with the English `.resx` as the source of truth. The design is language-agnostic: it makes QuickMail *localizable* without committing to any specific locale. Critically for this project, the spoken UI is a first-class localization surface — announcement strings and `AutomationProperties.Name` values are extracted and translated with the same rigor as visible text, and their `AnnouncementCategory` gating is unchanged.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| XAML views | ~752 hardcoded `Header`/`Content`/`Text`/`ToolTip`/`AutomationProperties.Name` literals across 30 files (MainWindow.xaml alone ≈ 208) | UI cannot be shipped in any language but English | Non-English-speaking users |
| Announcements | 253 `AccessibilityHelper.Announce()` call sites across 50 files, English sentences composed inline | The *spoken* UI is English-only; a translated visual UI without translated speech would be worse than useless for screen reader users | Screen reader users |
| Pluralization | Inline ternaries like `tab{(msgTabCount == 1 ? "" : "s")}` (`MainViewModel.cs:579`) | English plural rules baked into string composition; breaks in most other languages | Translators, all non-English users |
| Command Palette / keyboard customization | 34+ `CommandDefinition` titles and categories hardcoded in `MainViewModel.RegisterCommands()` (`MainViewModel.cs:~1187–1340`) | Command discovery surfaces are English-only | All non-English users |
| Dates | `MailMessageSummary.DateDisplay` hardcodes "Today"/"Yesterday"; 27 date-formatting sites rely implicitly on system culture | Mixed-language message list if OS culture differs from (future) UI language | All non-English users |
| Language choice | No setting; no `NeutralLanguage` in `QuickMail.csproj`; no `.resx` files anywhere | Nothing to configure even if translations existed | Everyone |
| RTL | Zero `FlowDirection` usage | Right-to-left languages structurally unsupported | RTL-language users |

**Verification anchors:** string survey performed 2026-07-03 — `.resx` count 0; `AutomationProperties.Name` 582 occurrences (240+ in XAML); `Announce(` 253 call sites; `MessageBox`/show-message patterns 48 sites; config pattern in `Services/ConfigService.cs` + `Models/ConfigModel.cs`; command registrations in `ViewModels/MainViewModel.cs` `RegisterCommands()`.

### 2.2 Target personas

- **A screen reader user working in a language other than English.** Needs the *entire* audible experience — labels, announcements, status text, command palette — in their language, gated by the same announcement preferences as today.
- **A sighted non-English user.** Needs menus, dialogs, settings, and dates in their language and locale conventions.
- **The translator/reviewer.** Receives AI-generated draft translations and needs a reviewable, diff-friendly format plus a checklist (access-key uniqueness, length limits, announcement tone).
- **Kelly (developer).** Needs extraction to be regression-free: the English build must look and *speak* identically before and after strings move to resources.

### 2.3 Why now

Not urgent — no target language is chosen and none is committed. But the string count grows with every feature; extraction cost only increases. Capturing the architecture now means (a) new code can adopt localization-friendly patterns immediately (no new inline plural ternaries), and (b) if a real demand appears (e.g., a community request), the build can start from an approved design instead of a research project.

---

## Section 3: Design Principles

1. **The spoken UI is a localization surface, not an afterthought.** Announcements, `AutomationProperties.Name` values, and status text are extracted and translated with the same priority as visible labels. A language ships when its *speech* is right, not just its pixels.
2. **English behavior is the regression gate.** Every extraction phase must leave the English build pixel-identical and speech-identical. Localization infrastructure earns its way in by changing nothing observable.
3. **Standard .NET machinery, no exotic dependencies.** `.resx` + satellite assemblies + `CultureInfo` — the boring, supported path. No third-party localization frameworks, no custom string loaders.
4. **Language-agnostic infrastructure.** Nothing in the design assumes which languages ship. RTL is explicitly deferred but with a documented path, so choosing an RTL language later is a phase, not a redesign.
5. **Translations are reviewed, never blindly shipped.** AI generates drafts; a human reviewer validates against a defined checklist before a language is enabled. The English `.resx` is the single source of truth.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1 plan)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| String resource infrastructure | — | — | `Strings.resx` (+ per-language `Strings.<culture>.resx`), strongly-typed accessor, `NeutralLanguage=en` in csproj |
| XAML string extraction | — | — | All ~752 literals → `{x:Static strings:Strings.Key}` references, view by view |
| C# string extraction | — | — | Announce() texts, command titles/categories, MessageBox/dialog texts, status bar text |
| Pluralization helper | — | — | `StringsHelper.Count(key, n)` style; per-language `_One`/`_Other` keys; bans inline `?"":"s"` |
| UI language setting | ComboBox in Settings: "Application language" | **Follow Windows language** | Persisted as `UILanguage` in config.ini via `ConfigModel`; restart required to apply |
| Culture applied at startup | — | — | `App.OnStartup` sets `CurrentUICulture`/`CurrentCulture` (and WPF `FrameworkElement.LanguageProperty` metadata) before any window exists |
| Locale-aware dates | — | — | "Today"/"Yesterday" become resources; formats flow through `CurrentCulture` |
| Translation workflow | — | — | AI-assisted draft → human review checklist → language enabled in the picker only after review |

### 4.2 Explicitly out of scope (v1)

- **Choosing target languages.** This spec builds the plumbing; which `Strings.<culture>.resx` files get created is a separate product decision.
- **RTL / mirroring.** No `FlowDirection` work in v1. Documented as the defined follow-on phase (Section 13.2) — window-level `FlowDirection` bound to culture, plus a layout audit.
- **Dynamic, no-restart language switching.** With ~752 `{x:Static}` bindings, live switching requires a rebindable indirection layer for marginal benefit. Restart-to-apply is the v1 contract, announced clearly.
- **User guide translation.** `docs/USER-GUIDE.md` (534 lines) and the published GitHub Pages guide remain English; translating docs is a separate effort with its own pipeline.
- **Localized keyboard gestures.** Registered shortcuts (Ctrl+R etc.) are identical in every language. Only command *titles* and *category display names* localize. Access keys (`_File`) do localize, inside the translated strings.
- **Email content translation.** Message bodies, subjects, sender names, and folder names from the server are displayed as received.
- **Spell-check language coupling.** Compose spell checking continues to follow the editor input language (existing behavior), independent of UI language.

### 4.3 Acceptance criteria

- After full extraction, the English build renders every window, menu, announcement, and status message character-for-character identically to pre-extraction (regression gate).
- With `UILanguage` unset, the app follows the Windows display language when a matching satellite assembly exists, else falls back to English.
- Selecting a language in Settings persists `UILanguage` to config.ini, announces that a restart is required, and takes effect on next launch — visible UI, announcements, command palette, and dates all switch together.
- No user-visible string composed with inline English plural logic remains; all count-bearing sentences go through the pluralization helper.
- A language appears in the Settings ComboBox only if its `.resx` exists and has passed human review.
- Screen reader output in a translated build carries the same information at the same moments, gated by the same `AnnouncementCategory` settings, as the English build.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision A — `.resx` resources with a strongly-typed accessor, referenced via `{x:Static}` in XAML.**

**Alternatives:**
1. **WPF `x:Uid` + LocBaml.** The "official" WPF story circa 2006. Requires post-build BAML round-tripping, has poor tooling, is brittle across refactors, and separates translators from a readable key/value file. Rejected.
2. **Custom JSON/INI string catalog + loader service.** Full control, but reinvents satellite assemblies, culture fallback, and designer support that `.resx` provides for free. Rejected.
3. **Third-party markup-extension libraries** (dynamic-binding localization frameworks). Enable live language switching but add a dependency and a second binding idiom across 30 views. Rejected — live switching is out of scope (Decision B).
4. **`.resx` + generated `Strings` class, `xmlns:strings` + `{x:Static strings:Strings.Key}` in XAML, `Strings.Key` in C#.** Chosen. Compile-time-checked keys (a deleted key is a build error, not a silent blank), standard MSBuild satellite assemblies, XML files that diff cleanly for AI-draft + human-review workflows.

**Rationale:** the review workflow is the constraint that matters. AI generates a `Strings.de.resx`; the reviewer diffs it against `Strings.resx` entry by entry. `.resx` keeps that loop simple, keeps keys compile-checked, and adds zero dependencies.

**Decision B — Culture chosen at startup; language change requires restart.**

`App.OnStartup` (before any window is constructed) resolves the culture: `ConfigModel.UILanguage` if set and available, else `CultureInfo.CurrentUICulture` (OS), else English. It then sets `Thread.CurrentThread.CurrentUICulture`/`CurrentCulture`, `CultureInfo.DefaultThreadCurrentUICulture`/`DefaultThreadCurrentCulture`, and overrides `FrameworkElement.LanguageProperty` metadata with an `XmlLanguage` for the culture so WPF bindings format dates/numbers per locale. Because `{x:Static}` resolves once at XAML load, changing language live would require rebinding every string; restart-to-apply avoids that entire complexity class. The Settings ComboBox announces the restart requirement as a `Result` when changed.

**Decision C — Pluralization and composition helper; no inline grammar.**

A small static helper over the resource class, e.g. `StringsHelper.Count("TabsOpen", n)` resolving `TabsOpen_One` / `TabsOpen_Other` (key-suffix convention; English needs two forms, and the convention extends per-language if a shipped language needs more forms — resolved via the culture's plural rule, implemented in the helper, still zero dependencies). Composed sentences use indexed `string.Format` placeholders (`"Opened tab: {0}. {1}"`) so translators can reorder. Inline `(n == 1 ? "" : "s")` patterns are banned in new code from the day Phase 1 merges, even before extraction reaches them.

**Decision D — Access keys localize; key gestures do not.**

Access keys travel inside the translated string (`"_File"` → translator picks the mnemonic for their language), with per-menu uniqueness on the review checklist. Registered `CommandRegistry` gestures (`defaultKey`/`defaultModifiers`) are untouched — Ctrl+R is Ctrl+R in every language — and `InputGestureText` display strings remain the gesture names. Command `title` and the category *display* string localize; the internal category identifiers and command ids (`"mail.reply"`) do not, so saved hotkey overrides in config.ini survive a language change.

**Decision E — Dates and numbers via `CurrentCulture`; relative-date words via resources.**

`MailMessageSummary.DateDisplay` keeps its Today/Yesterday/date logic but sources the words from `Strings.DateToday`/`Strings.DateYesterday` and formats explicit dates with culture-standard patterns. All other `ToString` date/number sites flow through ambient `CurrentCulture` (already mostly true; the survey found 27 sites to audit). Log output (`LogService`) stays invariant-culture English — logs are a developer surface.

**Decision F — RTL deferred with a defined path.**

v1 does nothing RTL-specific. The documented follow-on: bind each window's `FlowDirection` to a value derived from the UI culture (`CultureInfo.TextInfo.IsRightToLeft`), then audit layouts that assume left-to-right (message list columns, reading-pane chrome, folder tree indentation). Recorded here so adopting an RTL language later is an estimated phase, not a surprise.

### 5.2 Runtime mode compatibility

| Mode | Effect |
|---|---|
| Normal | Culture resolved and applied at startup as described |
| `/debug` | No interaction; log strings remain invariant English |
| `--online` | No interaction — localization never touches `LocalStoreService` |
| `--profileDir <path>` | `UILanguage` lives in that profile's config.ini, so test profiles can run in different languages side by side |

### 5.3 Code reuse and duplication risks

- One `Strings.resx` for the application (split into a second file only if it becomes unwieldy — start with one; keys are namespaced by prefix, e.g. `Compose_Send`, `Settings_Language`).
- The pluralization helper is the *only* place plural resolution lives.
- `ConfigModel`/`ConfigService` gain one string property following the existing setting pattern — no new persistence mechanism.
- Duplicate English strings across views (e.g., "Cancel", repeated `AutomationProperties.Name` labels) collapse into shared keys during extraction — a deduplication side benefit.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk / mitigation |
|---|---|---|---|---|
| `App` startup | `App.xaml.cs` | Entire app | Resolve + apply culture before window construction | Low; applied earlier than any consumer. Wrong placement = mixed-culture first window — acceptance test covers it |
| `ConfigModel`/`ConfigService` | `Models/ConfigModel.cs`, `Services/ConfigService.cs` | All settings | Add `UILanguage` (nullable/empty = follow OS) | Low; follows existing pattern. `SettingsViewModelTests` extended |
| `SettingsDialog` + VM | `Views/SettingsDialog.xaml`, VM | All app settings | Language ComboBox + restart-required announce | Low; one new row. Restart announce is `Result` category |
| `AccessibilityHelper` | `Services/AccessibilityHelper.cs` | 253 call sites | No API change; *call sites* switch from literals to `Strings.*` | The category/force contract is untouched — only the text source changes |
| `CommandRegistry` registrations | `MainViewModel.cs` `RegisterCommands()`, `MainWindow.xaml.cs` | Command Palette, keyboard customization dialog, hotkey persistence | Titles/category display strings from resources; ids and gestures unchanged | Medium: hotkey overrides key off command *id* — verify config.ini round-trip with `CommandRegistryTests` |
| All 30 XAML views | `Views/*.xaml` | — | Literals → `{x:Static}` per view, mechanical | High volume, low individual risk. Mitigation: per-view extraction commits + `XamlParseTests` + English-identical gate |
| `MailMessageSummary.DateDisplay` | `Models/MailMessageSummary.cs` | Message list, tests | Today/Yesterday from resources; culture-formatted dates | Low; unit-testable with forced cultures |
| `QuickMail.csproj` | — | Build | `<NeutralLanguage>en</NeutralLanguage>`; satellite assemblies emitted automatically | Verify `build.bat publish` single-file publish still carries satellites (known single-file consideration — Phase 1 exit gate) |

**Summary:** one new resource file + accessor + helper; one new config setting; mechanical extraction across views and VMs. No service interfaces change; announcement gating, command ids, and hotkey persistence are explicitly preserved.

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path: Change the application language

1. User opens **Settings** (existing path) and Tabs to a ComboBox labeled "Application language". Screen reader announces: "Application language, combo box, Follow Windows language."
2. User presses Down Arrow through the list. Each available, reviewed language is announced in its own language plus English (e.g., "Deutsch (German)"). "Follow Windows language" is always the first entry.
3. User selects a language and the selection commits. Screen reader announces (Result category): "Language set to [language]. Restart QuickMail to apply."
4. User saves/closes Settings as today. Nothing visible changes in the running session; no strings swap mid-flight.
5. User quits and relaunches. The app starts in the selected language: window title, menus, folder pane, status bar, and the startup announcement are all in that language. Focus and startup flow are unchanged from today.

### Path: First launch, no language configured

1. User launches QuickMail on a Windows system set to a language with a reviewed translation. **Expected:** the app comes up in that language with no configuration step.
2. Same launch on a system language with *no* translation. **Expected:** the app comes up in English. No error, no partial mixing.

### Path: No change (regression path)

1. English user on English Windows, `UILanguage` unset, before and after every extraction phase. **Expected:** every screen, menu, announcement, and status string is identical to the current build. Keyboard behavior, access keys, and shortcuts are untouched.

### Path: Command palette in a translated build

1. User presses Ctrl+Shift+P. Palette opens as today; command titles and category names appear in the UI language.
2. User types a few letters of a translated title; filtering matches the translated text. Enter executes the command. **Expected:** identical behavior to English; any customized hotkey (stored by command id) still applies.

---

## Section 7: Accessibility Checklist (Mandatory)

- **AutomationProperties.Name** — all ~582 values become resource-backed but remain *short identifying labels* (the existing rule); translation must not smuggle in role names or instructions. One new name: "Application language" on the Settings ComboBox.
- **AnnouncementCategory** — unchanged mechanics. Every extracted `Announce()` call keeps its existing category and `force` flag; only the text source changes. The one new announcement — "Language set to X. Restart QuickMail to apply." — is `AnnouncementCategory.Result` (an action outcome), not forced.
- **Spoken-language coherence** — in a translated build, *all* speech surfaces (names, announcements, status text) are in the UI language; a mixed-language spoken experience is a shipping blocker for that language, verified in the per-language review.
- **Screen reader browse mode / WebView2** — no changes; message content remains as received.
- **Focus restoration / F6 ring** — no new panes; no F6 changes. Settings focus flow gains one ComboBox in the normal Tab order.
- **Radio groups / checkboxes** — one standalone ComboBox; no new groups.
- **Color-only information** — none introduced.
- **Validation note** — per-language review includes a keyboard-only, speech-on pass of the main flows in that language before the language is enabled in the picker.

**Answer:** no new panes, no F6 changes, one new labeled ComboBox, one new `Result` announcement. The substantive accessibility work is guaranteeing the translated spoken UI is complete and category-gated exactly like English.

---

## Section 8: Acceptance Walkthrough (Mandatory)

### Scenario: English regression gate (run after every extraction phase)

**Setup:** current main build vs. extraction-phase build, same profile, English OS.

1. Launch both builds; traverse File/Edit/View/… menus, Settings pages, Compose, Address Book. **Verify:** every label, header, tooltip, and access key identical.
2. With speech on, perform: launch, folder change, message open, delete, sync completion. **Verify:** announcements word-for-word identical, same categories (toggle `AnnounceStatus` off and confirm the same announcements disappear in both builds).
3. Open the keyboard customization dialog and Command Palette. **Verify:** identical titles and categories; a customized hotkey saved in the old build still applies in the new build.

### Scenario: Language selection end-to-end (requires at least one reviewed test language)

1. In Settings, select the test language. **Verify:** Result announcement heard; `config.ini` contains `UILanguage = <tag>`; running session unchanged.
2. Relaunch. **Verify:** menus, Settings, status bar, startup announcement, and message-list dates ("Today"/"Yesterday" equivalents, date formats) all in the test language.
3. Press Ctrl+Shift+P; filter by a translated command title; execute it. **Verify:** works; gesture text still shows the same key names.
4. Set the ComboBox back to "Follow Windows language"; relaunch. **Verify:** back to OS-language behavior (English on an English system).

### Scenario: Fallback

1. Set `UILanguage` in config.ini to a culture with no satellite assembly; launch. **Verify:** app runs fully in English; no crash, no blank strings; Settings ComboBox shows "Follow Windows language" (invalid value ignored, not persisted back silently as something else).
2. Delete a single key from the test-language `.resx` in a dev build. **Verify:** that string falls back to English (resource-manager fallback), never to an empty string or key name.

### Scenario: Pluralization

1. In the test language, open 1 tab, then 2 tabs. **Verify:** the tab-count announcements use the correct singular/plural forms for that language, not an English "s" pattern.

### Scenario: Publish/installer

1. Run `build.bat publish` with one satellite language present. **Verify:** published `QuickMail.exe` starts in the test language when configured (satellite resources included in single-file output); installer build unaffected.

---

## Section 9: Success Metrics

- **Zero-regression extraction:** English build passes the Scenario-1 gate after every phase; `XamlParseTests` and full test suite stay green throughout.
- **Complete spoken surface:** in any enabled language, a keyboard-only, speech-on walkthrough of the core mail flows encounters no English strings.
- **Translator-ready:** producing a new language draft is a one-step operation (copy `Strings.resx` → AI translate → review against checklist), with no code changes required until the language is enabled in the picker.
- **No new dependencies:** localization ships on `.resx` + `CultureInfo` alone.
- **Future-proofing:** new features after Phase 1 add strings as resources from day one; no inline plural ternaries pass review.

---

## Section 10: Implementation Phases

### Phase 1: Infrastructure (no visible change)
**Goal:** `Strings.resx` + generated accessor + `StringsHelper` (pluralization/format) exist; `NeutralLanguage` set; culture resolution in `App.OnStartup`; `UILanguage` in `ConfigModel`/`ConfigService`; Settings ComboBox (showing only "Follow Windows language" + English until translations exist); publish carries satellites.
**Tests:** culture-resolution unit tests (config set / unset / invalid); `SettingsViewModelTests` for the new setting; helper plural tests; `build.bat publish` satellite check.
**Risk:** single-file publish satellite handling — verify first. **Duration:** 4–6 h.

### Phase 2: XAML extraction (view by view)
**Goal:** all ~752 XAML literals → `{x:Static}` keys, one view (or view cluster) per commit; shared keys deduplicated ("Cancel", common automation names).
**Tests:** `XamlParseTests` green per commit; English-identical spot gate per view; full Scenario-1 gate at phase end.
**Risk:** volume/monotony → mechanical per-view checklist; access keys must be preserved exactly. **Duration:** 8–12 h across sessions.

### Phase 3: C# extraction
**Goal:** `Announce()` texts (253 sites), command titles/categories (34+), MessageBox/dialog texts (48), status bar and `WindowTitle` composition, `DateDisplay` words — all via `Strings`/`StringsHelper`; inline plural ternaries eliminated.
**Tests:** announcement-text unit tests where VMs expose them; `CommandRegistryTests` hotkey-persistence round-trip; `DateDisplay` culture tests.
**Risk:** composed sentences needing reordering-safe placeholders — enforce indexed `string.Format` in review. **Duration:** 6–10 h.

### Phase 4: Pilot language + review workflow
**Goal:** one language chosen (product decision), AI-drafted `Strings.<culture>.resx`, human review against the checklist (access-key uniqueness per menu, length/truncation pass, spoken-tone pass, plural forms), language enabled in the picker; Scenario 2–5 acceptance runs.
**Tests:** the acceptance walkthrough itself; resource-completeness check (script comparing keys between neutral and language files).
**Risk:** reviewer availability per language — a language simply stays unlisted until reviewed. **Duration:** depends on language.

---

## Section 11: Files to Create / Modify

### Create
| File | Purpose | Lines (est.) |
|---|---|---|
| `QuickMail/Resources/Strings.resx` | Neutral (English) string catalog | grows to ~900+ entries |
| `QuickMail/Resources/Strings.Designer.cs` (generated) | Strongly-typed accessor | generated |
| `QuickMail/Resources/StringsHelper.cs` | Plural resolution + format helpers | 60–100 |
| `scripts/check-resx-keys.ps1` (Phase 4) | Key-parity check between neutral and language files | 30–50 |

### Modify
| File | Changes |
|---|---|
| `QuickMail/QuickMail.csproj` | `<NeutralLanguage>en</NeutralLanguage>` |
| `App.xaml.cs` | Culture resolution/application at top of `OnStartup` |
| `Models/ConfigModel.cs`, `Services/ConfigService.cs` | `UILanguage` setting |
| `Views/SettingsDialog.xaml` + `ViewModels/SettingsViewModel.cs` | Language ComboBox + restart Result announce |
| All 30 `Views/*.xaml` | Literals → `{x:Static}` (Phase 2, mechanical) |
| ViewModels/Views/Services with user-visible strings | Literals → `Strings.*` / `StringsHelper` (Phase 3) |
| `Models/MailMessageSummary.cs` | Localized relative-date words, culture formats |

---

## Section 12: Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `CultureResolutionTests` | Config value honored; empty → OS; unknown culture → English fallback; applied before window creation (order test via seam) | Startup behavior |
| `StringsHelperTests` | `_One`/`_Other` selection for n = 0/1/2; indexed placeholder formatting; missing plural key falls back sanely | Pluralization |
| `SettingsViewModelLanguageTests` | ComboBox list = reviewed languages + follow-OS; selection persists to config; restart announce raised as Result | Setting behavior |
| `DateDisplayCultureTests` | Today/Yesterday words from resources; date format under forced non-English culture | Dates |
| `CommandRegistryTests` (extend) | Hotkey override persistence keyed by id survives title localization | Palette/customization integrity |
| Resource parity script (Phase 4) | Neutral vs. language key sets match | Translation completeness |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| Extraction introduces subtle English regressions (typo'd key, lost access key) | Medium | Major | Per-view commits; English-identical acceptance gate (§8 Scenario 1) run per phase |
| Single-file publish drops satellite assemblies | Medium | Major | Phase 1 exit gate verifies `build.bat publish` with a dummy satellite before any extraction begins |
| Translated strings overflow fixed layouts (German ~30% longer) | High | Minor–Major | Review checklist includes a truncation pass; prefer auto-sizing layouts when issues found |
| AI translations sound wrong in speech (tone, word order in announcements) | Medium | Major for SR users | Human review explicitly includes a speech-on pass; language not enabled until it passes |
| Mixed-culture UI if culture applied too late in startup | Low | Major | Applied first in `OnStartup`; acceptance checks the startup announcement language |
| Plural-rule complexity for future languages (Slavic, Arabic forms) | Low (v1) | Medium | Helper resolves via culture plural rules; key-suffix convention extends beyond `_One`/`_Other` when such a language is actually chosen |

### 13.2 Open questions (decide before build — proposed answers)

- **Pilot language:** undecided by design; pick when Phase 4 is scheduled, driven by actual user demand. *(Proposed: whichever language has a reachable reviewer.)*
- **Command Palette category names:** localize the display string, keep internal identifiers stable. *(Proposed: yes — decided in Decision D; listed here for visibility.)*
- **v1 language cap:** *(Proposed: exactly one pilot language through Phase 4; add more only after the workflow proves out.)*
- **RTL trigger:** when (if) an RTL language is requested, Phase "RTL" = `FlowDirection` per Decision F + layout audit. *(Proposed: defer; revisit on demand.)*
- **In-app translation credits:** *(Proposed: About dialog line per language, decided at Phase 4.)*

---

## Section 15: Implementation Guidance for AI

- **Latitude:** exact key naming convention (prefix scheme), whether `Strings.resx` splits into multiple files if it grows unwieldy, and the mechanical order of view extraction are implementer's choice.
- **Normative constraints:**
  - The English regression gate (§8 Scenario 1) is non-negotiable after every phase — if extraction changes any English string or announcement, stop and fix before continuing.
  - Announcement categories and `force` flags must not change during extraction; only the text source moves.
  - Command ids, categories-as-identifiers, and registered gestures must remain stable so persisted hotkey overrides survive.
  - No new dependencies. `.resx` + `CultureInfo` only.
  - Do not begin Phase 2 until the Phase 1 single-file-publish satellite check passes.
- **After build:** the highest-risk acceptance steps are §8 "Publish/installer" (satellites in single-file output) and §8 "Fallback" step 2 (missing key falls back to English, never blank). Exercise those first.
