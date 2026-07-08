# Theming & Visual Design System — PM + Dev Spec

**Status:** Draft for review
**Author:** AI (Session 1), from direction by Kelly Ford
**Date:** 2026-07-03

---

## Section 1: Executive Summary

QuickMail is functionally strong but visually plain: colors are hardcoded in ~90 places across 30 XAML files and C#, there is no dark mode, no Windows high-contrast detection, and no way for a user to adjust the app's colors or text size. This spec adds a theme system built on semantic design tokens: the app ships with a curated set of understated, elegant themes; adapts correctly to all OS visual settings (high contrast, light/dark, DPI); gives users with vision needs direct controls (text scale, font, link underlining, focus thickness); and lets anyone export a theme to a file and share it between machines. The guiding aesthetic is **understated elegance** — color carries meaning (unread, selection, focus, status), never decoration. Being great at accessibility must not make the app look like a bare-bones utility, and looking good must not regress accessibility.

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified against the code)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| App-wide styling | One shared resource dictionary (`Styles/AccessibleStyles.xaml`, focus visuals only); ~77 inline styles across 30 XAML files | "Plain and boring"; no coherent visual identity; every change is a scattered edit | Everyone |
| Colors | ~13 hardcoded hexes in XAML (`#FFF3E3`, `#F0F4FF`, `#666`, `#D32F2F`…), named colors (`DarkBlue`, `Gray`, `DimGray`, `DarkRed`), plus colors set in C# (`TokenizedAddressBox`, `KeyCaptureDialog`, `RichTextDocumentConverter`, event-card HTML in `MainViewModel`) | No dark mode possible; hardcoded light-mode colors would be illegible on a dark background | Users who prefer/need dark UI |
| High contrast | No `SystemParameters.HighContrast` reference anywhere in the project | Hardcoded colors override the user's HC palette in exactly the places HC users need them respected | Low-vision users running Windows HC |
| OS light/dark | 40+ `{DynamicResource {x:Static SystemColors.*BrushKey}}` usages adapt, but hardcoded colors and WPF's light-only default control templates do not; only `MarkdownPreviewWindow` has real dark CSS | App is effectively light-only regardless of OS setting | Dark-mode users |
| Text size | Hardcoded `FontSize` values 9–18 scattered across XAML; no app-level scale | Only recourse is OS-wide DPI scaling, which affects every app | Low-vision users |
| Reading pane | `MessageBodyHtmlBuilder` CSS uses `background:Window; color:WindowText` (good) but fixed link blue `#0645ad`; no way to force readable colors onto sender HTML | Poorly-authored HTML mail (light gray on white, etc.) cannot be corrected | Low-vision users |
| Sharing | No import/export of any user artifact (views, rules, templates all lack it) | A carefully-tuned setup can't move to a second machine | Multi-machine users |

### 2.2 Target personas

- **The default user** — wants the app to simply look good and feel finished out of the box. Never opens the Appearance tab. Benefits from the new default theme and correct OS adaptation.
- **The low-vision user** — needs larger text, possibly a specific font, high-contrast or personally-tuned colors, always-underlined links, a thicker focus indicator. Today has only OS-level DPI. Gets direct, discrete controls.
- **The Windows HC user** — has a system palette chosen for their vision. Today QuickMail's hardcoded colors fight it. Gets an app with *no visual opinion* when HC is on.
- **The screen reader user** — doesn't see the theme, but must never be harmed by it: theme switching must be announced, settings must be operable, and nothing about theming may disturb focus, keyboard behavior, or UIA structure.
- **The tinkerer / helper** — tunes a theme for themselves or for someone they support, exports it as a file, and installs it on another machine.

### 2.3 Why now

The app's functional core is stabilizing (0.7.x). Architectural change of this breadth — touching every XAML file — only gets more expensive as more UI is added. The saved-views system, `CommandRegistry`, config infrastructure, and the settings dialog all exist as proven patterns to reuse. And the CSS-variable token system in `MarkdownPreviewWindow.xaml.cs` is a working in-repo prototype of exactly the token approach this spec generalizes.

## Section 3: Design Principles

1. **Color carries meaning, never decoration.** Unread, selection, focus, and status get color; nothing else does. One accent hue per theme. Email is not the place for design innovation — the innovation budget goes to vision-need adaptability, where QuickMail can lead.
2. **In high contrast, QuickMail has no visual opinion.** When Windows HC is active, every token resolves to live `SystemColors` and custom control templates are withdrawn. The user's palette wins, structurally and automatically.
3. **Accessibility and elegance are the same deliverable.** Every built-in theme passes WCAG AA contrast as a unit test, not a review comment. No state is conveyed by color alone.
4. **Zero change for users who don't opt in — except looking better.** The default "System" theme follows the OS exactly as today, then upgrades to the new default look; all vision-assist settings are opt-in.
5. **Themes are plain, documented files.** A theme is human-readable JSON. For this audience, hand-editing a well-documented text file is *more* accessible than a visual color picker.

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1, across phases)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Semantic token system | — | — | 26 color tokens + typography tokens, published as `Theme.*` DynamicResources |
| Theme selection | `AppearanceThemeId` (config.ini `[global]`) | `system` | System follows OS light/dark; yields entirely to Windows HC |
| Built-in themes | — | — | System, Light ("Quill", new default look), Dark, Ember (warm), Fjord (cool), Heather (muted) |
| Appearance settings tab | 5th tab in Settings dialog | — | Theme, font family, text size, vision-assist toggles |
| Text size scale | `AppearanceTextScale` | `1.0` | Discrete steps 100/110/125/150/175/200% |
| Font family override | `AppearanceFontFamily` | (theme default) | Applies app-wide and to reading-pane default CSS |
| Always underline links | `AppearanceUnderlineLinks` | off | App chrome hyperlinks + reading-pane CSS |
| Thicker focus indicators | `AppearanceThickFocus` | off | Focus ring 2px → 4px via `Theme.FocusThickness` |
| Force theme colors on message content | `AppearanceForceMessageTheme` | off | Overrides sender HTML colors/fonts in the reading pane |
| Theme Manager window | Command `theme.manager.open` (palette; no default key) | — | List, Apply, Duplicate, Rename, Delete, Export, Import, Open themes folder |
| Theme export/import | `.quickmailtheme` files | — | Plain JSON; import validates with friendly errors |
| Theme cycle commands | `theme.next` / `theme.previous` (palette; no default key) | — | Announce the new theme name |
| Per-theme apply commands | `theme.apply.{id}` | — | Hotkey-assignable, like `view.saved.{id}` |
| Live OS adaptation | — | always on | HC on/off and OS light/dark switches apply without restart, announced (Status) |

### 4.2 Explicitly out of scope (v1)

- **In-app color editor / color picker.** Editing is via the documented JSON file; Duplicate + "Open themes folder" keeps the loop short. Revisit after v1 feedback.
- **Per-view themes (`SavedView.ThemeId`).** Deferred to a later phase; `ApplyTheme` is designed now with an internal `persist` flag so a view can later apply a theme non-persistently.
- **Density/spacing controls.** Spacing rework is low value/high cost now; density can become a theme property later.
- **Theming the flag color palette.** The 12 preset flag colors in `FlagManagerViewModel` are user data, not chrome.
- **Restyling sender HTML by default.** Message content renders as authored unless the user opts into force-theme.
- **Windows accent-color following.** Themes define their own accent; reading the user's Windows accent color is a possible v2 refinement.

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision: Semantic tokens as app-level `DynamicResource` brushes, built in code by a `ThemeService`.**
*Alternatives:* (1) Multiple static ResourceDictionary XAML files swapped at runtime — harder to compose sparse user themes and HC passthrough; (2) a third-party theming library (MahApps/ModernWpf) — heavy dependency, foreign control templates with unknown UIA behavior, loss of control over the exact accessibility characteristics this app exists for. *Rationale:* code-built dictionaries let one loader serve built-ins, user JSON, and the HC SystemColors passthrough identically; no new dependencies; every brush is `Freeze()`d.

**Decision: 26 color tokens + typography, no more.**
Enough to cover every color found in the survey; small enough that hand-authoring a theme is feasible. **Brushes only** — the repo has zero gradients/ColorAnimations (verified), so parallel `Color` resources are speculative. Token names and the camelCase↔resource-key map live once in `Theming/ThemeKeys.cs`.

Token set:
- **Surfaces (7):** `WindowBackground`, `SurfaceBackground`, `ChromeBackground`, `InputBackground`, `Border`, `BorderSubtle`, `InputBorder`
- **Text (4):** `TextPrimary`, `TextSecondary`, `TextDisabled`, `TextOnAccent`
- **Accent/interaction (7):** `Accent`, `AccentSubtle`, `Hyperlink`, `SelectionBackground`, `SelectionText`, `SelectionInactive`, `FocusIndicator`
- **Status (8):** `Error`/`ErrorBackground`, `Warning`/`WarningBackground`, `Success`/`SuccessBackground`, `Info`/`InfoBackground`
- **Typography:** `FontFamily`, `FontFamilyMono`, `FontSizeBase` + derived `FontSizeSmall`/`FontSizeLarge`/`FontSizeHeader` (ThemeService computes: theme base × user scale, then fixed offsets)
- **Vision-assist:** `FocusThickness` (double)

**Decision: Theme = sparse JSON over a light/dark base.**
```json
{
  "formatVersion": 1,
  "id": "ember",
  "name": "Ember",
  "base": "light",
  "colors": { "accent": "#8F4531", "windowBackground": "#FBF7F2" },
  "typography": { "fontFamily": "Segoe UI", "monoFontFamily": "Cascadia Code", "baseFontSize": 13 }
}
```
Missing color keys fall back to the built-in Light or Dark theme per `base` — accent variants are ~10 lines, and old theme files keep working when new tokens are added. Hex values validated on load (`#RGB`/`#RRGGBB`/`#AARRGGBB`); unknown keys tolerated; `formatVersion` gates imports from newer app versions. Built-ins ship as embedded resources (`Themes/BuiltIn/*.json`) parsed by the same loader as user themes; `system` is a virtual id resolved by ThemeService, not a file.

**Decision: `IThemeService` exposes hex strings, never `System.Windows.Media` types.**
This is what lets `MainViewModel` (event-card HTML colors) consume the theme without violating the no-UI-types-in-ViewModels rule.

```csharp
public interface IThemeService : IDisposable
{
    string ConfiguredThemeId { get; }               // e.g. "system"
    ThemeDefinition ResolvedTheme { get; }          // post System/HC resolution; hex strings only
    bool IsHighContrastActive { get; }
    IReadOnlyList<ThemeDefinition> GetAvailableThemes();
    void ApplyTheme(string themeId);                // caller persists config
    ThemeDefinition ImportTheme(string filePath);   // throws ThemeFormatException (friendly message)
    void ExportTheme(string themeId, string filePath);
    void SaveUserTheme(ThemeDefinition theme);
    void DeleteUserTheme(string themeId);
    string BuildMessageCss(bool forceOnContent);    // token → CSS-variable bridge for WebView2
    event EventHandler? ThemeChanged;               // raised on the Dispatcher thread
}
```

**Decision: Apply = atomic replacement of one merged dictionary.**
`App.xaml` keeps `AccessibleStyles.xaml` as `MergedDictionaries[0]`. ThemeService builds a new `ResourceDictionary` (frozen `SolidColorBrush` / `FontFamily` / boxed `double` per token) and replaces its previous instance in `Application.Current.Resources.MergedDictionaries` — never mutate/`Clear()` in place, which creates transient missing-resource states while DynamicResource listeners re-resolve. Merged order: [0] AccessibleStyles (migrated to consume tokens), [1] theme token dictionary, [2] `Styles/ThemedControls.xaml` (Phase 3 implicit control styles; **removed** under HC).

**Decision: OS change detection uses two signals, debounced, dispatcher-marshaled.**
- `SystemParameters.StaticPropertyChanged` for `HighContrast` (canonical WPF signal, fires on UI thread).
- `Microsoft.Win32.SystemEvents.UserPreferenceChanged` (categories General/Color) for OS light/dark; on signal re-read `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` (no managed API in .NET 8; `Application.ThemeMode` is .NET 9+). This event fires on a **non-UI thread in bursts**: marshal via Dispatcher, debounce ~250 ms.
Both converge on one `Refresh()` that rebuilds only if the effective theme changed, then raises `ThemeChanged` and announces via `AccessibilityHelper.Announce` (Status). ThemeService is `IDisposable` and unsubscribes both static events in `Dispose()` (called from `App.OnExit`).

**Decision: High contrast = SystemColors passthrough + template withdrawal.**
When `SystemParameters.HighContrast` is true: every token rebuilt from live `SystemColors` (WindowBackground/InputBackground→`Window`, TextPrimary→`WindowText`, TextSecondary/TextDisabled→`GrayText`, Chrome/Surface→`Control`, Border→`ActiveBorder`, Accent/Hyperlink→`HotTrack`, Selection→`Highlight`/`HighlightText`, FocusIndicator→`WindowText`, all status colors→`WindowText`/`Window` — HC palettes don't guarantee distinguishable red/green, and status is already conveyed in text). `ThemedControls.xaml` is removed so controls fall back to WPF's built-in HC-aware templates. Typography tokens stay active (font/scale matter more to low-vision HC users). The configured theme id is untouched; leaving HC restores it. `BuildMessageCss` emits no color overrides in HC — WebView2's `forced-colors` handling is correct natively.

**Decision: WebView2 bridge generalizes the MarkdownPreviewWindow CSS variables.**
`BuildMessageCss()` emits `:root { --qm-bg; --qm-text; --qm-text-muted; --qm-surface; --qm-border; --qm-accent; --qm-link; --qm-font; --qm-font-size }`. `MessageBodyHtmlBuilder` stays pure static and gains an optional `themeCss` parameter; `background:Window;color:WindowText` becomes `var(--qm-bg, Canvas)` / `var(--qm-text, CanvasText)`; the fixed link blue `#0645ad` becomes `var(--qm-link)`. Default CSS styles document defaults only — sender-styled HTML is untouched. The force-on-content toggle appends `* { background-color: var(--qm-bg) !important; color: var(--qm-text) !important; } a { color: var(--qm-link) !important; }`. On `ThemeChanged`, MainWindow re-renders the displayed message (`NavigateToString`) and sets `CoreWebView2.Profile.PreferredColorScheme` to the theme's base. Per the modal-dialog rules, this never fires while a modal is open: settings-driven applies happen after the dialog closes (existing `ApplySettings` flow), and OS-driven refreshes are debounced and dispatcher-posted.

**Decision: Text scale via inherited `FontSize` on window roots.**
Each `Window` gets `FontFamily="{DynamicResource Theme.FontFamily}" FontSize="{DynamicResource Theme.FontSizeBase}"` (~25 windows, mechanical). These are inheriting properties; every child without an explicit `FontSize` follows. Scattered hardcoded sizes migrate to the derived size tokens opportunistically; stragglers simply don't scale yet — degraded, not broken. A regression-guard test stops new hardcoded sizes.

**Decision: Theme persistence mirrors the saved-views infrastructure.**
`ThemeStore` follows `ViewService`'s shape (ProfileContext ctor, `AtomicFile` writes) but stores **one file per theme** in `{profile}\themes\` — a theme is an individually shareable artifact. Export writes the same JSON to a user-chosen `.quickmailtheme` path; import validates, re-ids on collision, appends "(imported)" on name collision, and saves into the themes folder.

### 5.2 Runtime mode compatibility

| Mode | LocalStoreService available? | Theming behavior | Fallback? |
|---|---|---|---|
| Normal | ✓ | Full | — |
| `--online` | ✗ | Identical — theming never touches LocalStore | n/a |
| `--profileDir <path>` | ✓ | Themes and config read from the alternate profile | — |

Theming reads only config.ini, embedded resources, and `{profile}\themes\*.json`. A missing/corrupt user theme file is skipped with a log line and never blocks startup; an unknown configured theme id falls back to `system` without throwing.

### 5.3 Code reuse and duplication risks

- **Reading-pane CSS** is already shared via `MessageBodyHtmlBuilder` (MainWindow reading pane + MessageWindow). The theme CSS parameter goes there once; both consumers get it. `MarkdownPreviewWindow`'s CSS should migrate to `BuildMessageCss` output rather than keeping a parallel variable set.
- **JSON persistence patterns** exist in `ViewService`/`TemplateService`; `ThemeStore` copies the shape rather than inventing a new one.
- **Manager-dialog pattern** exists in `ViewManagerWindow`/`ViewManagerViewModel`; `ThemeManagerWindow` copies its structure, including the post-`ShowDialog` event-firing rules.
- **Per-item command registration** (`view.saved.{id}` + hotkey persistence in `CustomHotkeys`) is the model for `theme.apply.{id}`.

### 5.4 Shared component audit

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `App.xaml` / `App.xaml.cs` | root | everything | Merged-dictionary slot; construct/dispose ThemeService | Startup order: must init before `new MainWindow` |
| `AccessibleStyles.xaml` | `Styles/` | every window (app-wide implicit styles) | SystemColors keys → `Theme.*` keys | Focus visuals must look identical in System theme; verify HC |
| `MainWindow.xaml(.cs)` | `Views/` | — (but hosts message list, trees, status bar, reading pane) | Color/font setters → tokens; re-render reading pane on ThemeChanged | Largest surface; virtualized lists (Recycling confirmed) |
| `MessageBodyHtmlBuilder` | `Helpers/` | MainWindow reading pane, MessageWindow | Optional `themeCss` param; system keywords → CSS vars with system fallbacks | Both consumers must pass the new arg; default keeps old behavior |
| `SettingsDialog.xaml` + `SettingsViewModel` | `Views/`, `ViewModels/` | MainWindow menu | New Appearance tab + fields | Tab order, access keys (`A_ppearance` must not collide) |
| `ConfigModel` / `ConfigService` | `Models/`, `Services/` | everything | 6 new `[global]` keys | INI round-trip tests |
| `MainViewModel` | `ViewModels/` | MainWindow | Inject `IThemeService`; event-card HTML colors from `ResolvedTheme` | Constructor signature change ripples to tests/stubs |
| `TokenizedAddressBox.xaml.cs` | `Controls/` | ComposeWindow, GrabAddresses | Hardcoded brushes → `SetResourceReference` | Invalid-address state must stay visually distinct in all themes |
| `KeyCaptureDialog.xaml.cs` | `Views/` | Settings hotkeys, ViewManager hotkeys | Black/DimGray → `SetResourceReference` | Low |
| `RichTextDocumentConverter` | root | ComposeWindow HTML mode | `TryFindResource` with fallbacks (Freezable/TextElement can't `SetResourceReference`) | Stale colors until doc rebuild — bounded per message |
| `CommandRegistry` | `Services/` | all commands | New `theme.*` commands (category Settings) | Follow registration rules; palette-visible |
| `StubServices.cs` | `QuickMail.Tests/` | all VM tests | Add `StubThemeService` | Keep constructor churn contained |

### 5.5 The C# migration mechanism table

| Site | Mechanism |
|---|---|
| Element in the visual tree (`TokenizedAddressBox`, `KeyCaptureDialog`) | `SetResourceReference(Property, ThemeKeys.X)` — live theme updates for free |
| Freezable / `TextElement` properties (`RichTextDocumentConverter`) | `Application.Current.TryFindResource(ThemeKeys.X) as Brush ?? fallback` — never `FindResource` (throws) |
| ViewModel-generated HTML (`MainViewModel` event cards) | Inject `IThemeService`, read hex strings from `ResolvedTheme` |

Pitfall rules (enforce in review): `DynamicResource` works on dependency properties and `Setter.Value`, **not** on `Style.BasedOn` or trigger conditions; C# lookups always `TryFindResource` with a hardcoded fallback.

## Section 6: The "Quill" Visual Design

Philosophy: warm off-whites instead of stark white; one muted steel-indigo accent; unread = SemiBold weight + a 3px accent bar at the row's left edge (weight is the primary cue, color the garnish); banners use the pale `*Background` tints with their dark text partners; the status bar sits on `ChromeBackground` with a 1px accent-tinted top border; hyperlinks always `Hyperlink`. Nothing else gets accent — except selection. That restraint *is* the elegance.

> **Revision (2026-07, post visual review):** the original "tinted, not saturated" selection
> (~1.15:1 against the list surface) proved indistinguishable from unselected rows in every
> theme during independent visual review; secondary text and row separation were flagged as
> too faint across the board. Selection is now accent-strength (`SelectionBackground` = the
> theme accent in light themes) with `SelectionText` white, ≥ 3:1 against `SurfaceBackground`
> — the one place each theme's accent does functional work. TextSecondary and the borders were
> darkened (light) / lightened (dark) a step, and message-list rows carry a 1px `BorderSubtle`
> bottom divider. The tables below reflect the revised values.

### Light — "Quill" (new default)

| Token | Hex | Contrast notes |
|---|---|---|
| WindowBackground | `#FBFAF8` | warm off-white |
| SurfaceBackground | `#F5F3EF` | lists/panels |
| ChromeBackground | `#EFEDE8` | toolbar/status bar |
| InputBackground | `#FFFFFF` | fields pop slightly |
| Border / BorderSubtle / InputBorder | `#C9C4BA` / `#DEDAD1` / `#B8B4AC` | dividers perceivable in dense lists |
| TextPrimary | `#1F2328` | 15.4:1 on window bg |
| TextSecondary | `#495057` | 7.4:1 on surface |
| TextDisabled | `#8A9099` | exempt from AA (disabled) |
| Accent | `#3D5A80` | 6.8:1 — usable as text |
| AccentSubtle | `#E7EDF4` | banner/hover tint |
| TextOnAccent | `#FFFFFF` | |
| Hyperlink | `#2A5D9E` | 5.9:1 |
| SelectionBackground / SelectionText | `#3D5A80` / `#FFFFFF` | accent-strength selection, 6.4:1 vs surface — the selected row is unmistakable |
| SelectionInactive | `#D4D8DE` | quiet but perceivable (1.3:1 vs surface) |
| FocusIndicator | `#1F2328` | high-visibility ring |
| Error / ErrorBackground | `#B3261E` / `#FBEAE9` | 6.3:1 |
| Warning / WarningBackground | `#8A5A00` / `#FBF3E2` | 5.4:1 |
| Success / SuccessBackground | `#2E6B3E` / `#E9F3EC` | |
| Info / InfoBackground | `#31597F` / `#EAF0F7` | |

### Dark — "Quill Dark"

WindowBackground `#1E2023`, Surface `#26292E`, Chrome `#2B2F34`, Input `#26292E`; Border `#4A4F56` / Subtle `#3B4046` / InputBorder `#5C6269`; TextPrimary `#E8E6E3` (13.5:1), TextSecondary `#B4B9C1` (7.4:1), Disabled `#767B83`; Accent `#8FB0D9` (7.1:1), AccentSubtle `#2C3A4C`, TextOnAccent `#16202E`, Hyperlink `#8FB8E8`; Selection `#4677B4` / `#FFFFFF` (3.2:1 vs surface, white text 4.6:1), SelectionInactive `#3C434C`; FocusIndicator `#E8E6E3`; Error `#F0857C`/`#3B2523`, Warning `#E0B45C`/`#3A3223`, Success `#87C795`/`#233A2A`, Info `#A3C4E8`/`#24303C`.

### Accent variants (sparse over Light, ~10 lines each)

- **Ember** (warm): Accent `#8F4531` terracotta, WindowBackground `#FBF7F2`, AccentSubtle `#F5E9E3`, Selection `#8F4531` (= accent)
- **Fjord** (cool): Accent `#2E6462` teal, WindowBackground `#F8FAFA`, AccentSubtle `#E4EFEE`, Selection `#2E6462` (= accent)
- **Heather** (muted): Accent `#5E566E` plum-gray, WindowBackground `#FAF9FB`, AccentSubtle `#ECEAF1`, Selection `#5E566E` (= accent)

Each variant's selection is its own accent, so the selected row is where the theme's personality shows — terracotta in Ember, teal in Fjord, plum in Heather (SelectionText `#FFFFFF` is inherited from the base).

### Contrast policy (enforced by unit test on every built-in)

- Every text-role token ≥ 4.5:1 against every background it is declared against (TextSecondary included; TextDisabled exempt per WCAG, asserted ≥ 3:1).
- Accent and FocusIndicator ≥ 3:1 as non-text indicators against Window/Surface backgrounds.
- SelectionText ≥ 4.5:1 on SelectionBackground.
- SelectionBackground ≥ 3:1 against SurfaceBackground (the selected row must be identifiable from its fill alone, not just the focus outline).
- SelectionInactive ≥ 1.2:1 against SurfaceBackground, and TextPrimary ≥ 4.5:1 on SelectionInactive.
- Each status color ≥ 4.5:1 on its own `*Background` and on WindowBackground.

## Section 7: Keyboard Walkthrough (Mandatory)

### Path: Choose a theme in Settings

1. User opens Settings (existing route) and presses Ctrl+Tab to the **Appearance** tab. **Expected:** screen reader announces "Appearance". Focus lands on the tab; Tab moves into the tab's content.
2. Focus lands on the **Theme** ComboBox. **Expected:** announces "Theme, combo box, System" (current value).
3. User presses Down to "Dark", then Tab onward, then activates **OK**. **Expected:** dialog closes; the theme applies immediately (no restart); announcement "Theme changed to Dark" (Status category); focus returns to where Settings was invoked from.
4. User reopens Settings → Appearance. **Expected:** Theme ComboBox reads "Dark".

### Path: Text size

1. In the Appearance tab, user tabs to **Text size** ComboBox. **Expected:** announces "Text size, combo box, 100%".
2. User selects "125%" and activates OK. **Expected:** dialog closes; all app text (windows, lists, menus using themed sizes) renders 25% larger; no restart; no focus loss.

### Path: Windows High Contrast toggles while the app is running

1. User enables Windows HC (Left Alt+Left Shift+PrintScreen or Settings). **Expected:** within ~1 second QuickMail rebuilds its colors from the system HC palette; custom control styling withdraws; announcement "High contrast is on; colors are supplied by Windows" (Status). Focus does not move. No modal appears.
2. User opens Settings → Appearance. **Expected:** a read-only notice "Windows High Contrast is active; theme colors are supplied by Windows." The Theme ComboBox remains present and operable (choosing a theme still records the preference for when HC ends).
3. User disables HC. **Expected:** the configured theme returns; announcement "Theme changed to [name]" (Status).

### Path: Theme Manager — apply and duplicate

1. User presses Ctrl+Shift+P, types "theme", chooses **Manage themes**. **Expected:** Theme Manager window opens; focus lands on the theme list; announces the list name and current item, e.g. "Themes, list, Quill, built-in, current theme, 1 of 6".
2. User presses Down to "Ember" and Tab to **Apply**, presses Enter. **Expected:** theme applies immediately behind the window; announcement "Theme changed to Ember" (Status); focus stays on Apply.
3. User Shift+Tabs back to the list, selects "Quill", tabs to **Duplicate**, presses Enter. **Expected:** a name prompt appears with "Quill copy" prefilled; on OK, a new user theme appears in the list, focus returns to it in the list; announcement "Theme Quill copy created" (Result).
4. User presses F6. **Expected:** focus cycles theme list → button row → (back to list).
5. User presses Escape. **Expected:** window closes; focus returns to the control that had focus before the window opened.

### Path: Theme Manager — export and import

1. With a theme selected, user activates **Export…**. **Expected:** standard Save dialog, filter "QuickMail theme (*.quickmailtheme)", filename prefilled with the theme name. On save: announcement "Theme exported" (Result); focus returns to Export.
2. User activates **Import…** and picks a valid file. **Expected:** theme appears in the list and focus moves to it; announcement "Theme [name] imported" (Result).
3. **Error case:** user imports a malformed file. **Expected:** a dialog states the specific problem in plain language (e.g. "The color "accent" has the value "blue", which is not a hex color."); on dismiss, focus returns to Import; the list is unchanged.

### Path: Delete a user theme

1. User selects a user theme, tabs to **Delete**, presses Enter. **Expected:** confirmation dialog "Delete theme [name]? This cannot be undone." Focus on No.
2. User arrows to Yes, Enter. **Expected:** theme disappears from the list; focus moves to the next item in the list (or previous if it was last); announcement "Theme deleted" (Result). If the deleted theme was active, the app falls back to System and announces it.
3. **Edge:** Delete is disabled when a built-in theme is selected. **Expected:** the button is announced as unavailable ("Delete, button, unavailable").

### Path: Force theme colors onto a message

1. User opens a message with hard-to-read sender colors, opens Settings → Appearance, checks **Apply theme colors to message content**, OK. **Expected:** the open message re-renders with theme background/text/links; focus and reading position in the app are preserved (reading pane scroll may reset).
2. User unchecks it. **Expected:** the message re-renders as the sender authored it.

## Section 8: Accessibility Checklist (Mandatory)

- **AutomationProperties.Name:** new short labels only — "Appearance" (tab), "Theme", "Font", "Text size", "Always underline links", "Thicker keyboard focus indicators", "Apply theme colors to message content", "Manage themes", "Themes" (list), and button labels Apply/Duplicate/Rename/Delete/Export/Import/Open themes folder. No roles, hints, or shortcuts in names.
- **Announcements:** theme changes and HC transitions → **Status** (background state change); create/duplicate/delete/export/import outcomes → **Result**; a first-focus hint in the Theme Manager list ("Press Tab for actions on the selected theme") → **Hint**. All via `AccessibilityHelper.Announce` with category; none use `force`.
- **Screen reader browse mode:** no new WebView2 surfaces. Reading-pane behavior unchanged except colors; force-theme mode must not alter document structure, only CSS.
- **Focus restoration:** Theme Manager captures the invoking element and restores focus on close (New Window Checklist). Settings dialog behavior unchanged.
- **F6 ring:** MainWindow ring unchanged. Theme Manager defines its own two-stop ring (list ↔ buttons).
- **Radio groups:** none introduced (ComboBoxes chosen deliberately — discrete, precise, screen-reader-friendly).
- **Color-only information:** none introduced. Unread keeps SemiBold as primary cue; the accent bar is additive. Status colors always accompany text.
- **Theme switching must never move focus, collapse trees, or clear selection.** DynamicResource swaps restyle in place; the only re-render is the reading pane document.

## Section 9: Acceptance Walkthrough (Mandatory)

### Scenario: Default experience unchanged (Phase 1)

**Setup:** Fresh profile (`--profileDir` temp), Windows light mode, HC off.

1. Launch the app. **Verify:** visually identical to the previous build (System theme resolves to current-look values). No new announcements at startup.
2. Open config.ini. **Verify:** `AppearanceThemeId = system` present with comment.

### Scenario: Theme switch end-to-end (Phase 2+)

**Setup:** App running with a message open in the reading pane; screen reader on.

1. Settings → Appearance → Theme = Dark → OK. **Verify:** all chrome goes dark immediately; announcement "Theme changed to Dark"; reading pane background/text follow; message list selection still visible and readable; focus back at the invoking control.
2. Restart the app. **Verify:** Dark persists from config.ini.
3. Switch to each built-in theme in turn. **Verify:** no unreadable text anywhere in MainWindow, Settings, Compose (spot check).

### Scenario: OS adaptation live

1. With Theme = System, toggle Windows dark mode. **Verify:** app follows within ~1 second, announced, no focus loss, no crash, no restart.
2. Enable Windows HC while the app runs. **Verify:** HC palette everywhere (including custom-templated status bar controls); reading pane readable; announcement heard. Settings → Appearance shows the HC notice.
3. Disable HC. **Verify:** prior theme returns, announced.

### Scenario: Theme Manager round trip (Phase 4)

1. Open Theme Manager from the command palette. **Verify:** focus on list, correct announcements per §7.
2. Duplicate Quill, export the copy, delete the copy, import the exported file. **Verify:** each step per §7, file exists on disk, imported theme functions when applied.
3. **Edge:** import a text file that isn't JSON. **Verify:** friendly error naming the problem; no crash; list unchanged.
4. Hand-edit the imported theme's JSON to an invalid hex; reopen Theme Manager. **Verify:** the broken theme is skipped (not listed), a log line records why, the app runs normally.

### Scenario: Shared component regressions

1. Compose a message; type an invalid address in To. **Verify:** the token still shows the error state distinctly (themed error background), in Light and Dark.
2. Settings → Keyboard Shortcuts → rebind a key (KeyCaptureDialog). **Verify:** captured-key text readable in both themes.
3. Open a message with an ICS event card. **Verify:** Accept/Tentative/Decline buttons use theme status colors, readable in both themes.
4. `--online` mode: repeat the theme-switch scenario. **Verify:** identical behavior.

### Scenario: Vision-assist settings (Phase 4)

1. Toggle each of: text size 150%, font family override, underline links, thick focus, force message theme. **Verify each:** immediate effect, no restart, toggle back restores, per §7.

## Section 10: Implementation Phases

### Phase 1: Token infrastructure (M)

**Goal:** All tokens exist and are consumed; HC formally correct; zero visible change.
**Deliverables:** `Theming/ThemeKeys.cs`, `Models/ThemeDefinition.cs`, `Services/ThemeStore.cs`, `Services/IThemeService.cs` + `ThemeService.cs` (HC passthrough + OS detection), embedded `light.json`/`dark.json` where **light = current look**, App.xaml.cs wiring + OnExit dispose, `AppearanceThemeId` config field (no UI), migration Steps A+B for MainWindow.xaml + AccessibleStyles.xaml + the C# sites (§5.5).
**Tests:** ThemeDefinitionTests, BuiltInThemeTests (parse + contrast), ThemeServiceTests, config round-trip; XamlParseTests stay green.
**Risk:** Static→Dynamic conversion mistakes → StaFact smoke tests; HC mapping gaps → manual HC pass.

### Phase 2: The new look + Appearance tab (M)

**Goal:** Quill default ships; users can pick theme/font/size.
**Deliverables:** light.json → Quill palette; typography tokens + window `FontFamily`/`FontSize` wiring (~25 windows); WebView2 bridge in `MessageBodyHtmlBuilder` + reading-pane re-render on ThemeChanged + `PreferredColorScheme`; Appearance tab + SettingsViewModel fields + `ApplySettings` hookup; finish Step B in remaining Views.
**Tests:** SettingsViewModelTests additions; BuildMessageCss tests; contrast test now guards Quill.
**Risk:** Font scaling reveals layout assumptions (fixed heights) → keyboard-only walkthrough at 150%+.

### Phase 3: Dark + variants + control restyle (L — riskiest)

**Goal:** Dark is genuinely usable; System follows OS dark; variants ship.
**Deliverables:** `Styles/ThemedControls.xaml` — implicit token-driven templates for ScrollBar, Menu/ContextMenu, ComboBox, TabControl, ListView headers, Button, TextBox, CheckBox/RadioButton (WPF Aero2 templates hardcode light chrome); withdrawn under HC; every retemplate preserves focus visuals and keyboard behavior per the accessibility checklist. Final Dark palette; ember/fjord/heather.json.
**Tests:** XamlParseTests for ThemedControls; manual keyboard walkthrough of every retemplated control with a screen reader.
**Risk:** Retemplating breaks UIA patterns or keyboard behavior → template-by-template review against the checklist; ship Dark only when clean.

### Phase 4: Theme Manager + vision-assist extras (S–M)

**Goal:** Manage/share themes; remaining vision-assist toggles.
**Deliverables:** ThemeManagerWindow/VM (+F6 ring, palette, focus restoration), export/import + `.quickmailtheme`, `theme.manager.open` / `theme.next` / `theme.previous` / `theme.apply.{id}` commands, underline-links/thick-focus/force-content toggles, USER-GUIDE.md + token documentation for hand-editing.
**Tests:** ThemeManagerViewModelTests; import/export round-trip; regression guards.
**Risk:** Modal-dialog rules around the manager (fire events after `ShowDialog` returns) — follow ViewManager precedent exactly.

### Phase 5 (later, designed-for now): `SavedView.ThemeId`

Optional nullable `ThemeId` on SavedView; ViewManager theme dropdown; applying a view applies its theme non-persistently (via `ApplyTheme`'s internal `persist` flag). Out of scope for v1.

## Section 11: Files to Create / Modify

### Create

| File | Purpose |
|---|---|
| `QuickMail/Theming/ThemeKeys.cs` | Token key constants + camelCase↔key map |
| `QuickMail/Models/ThemeDefinition.cs` | Theme model, JSON parse/validate, sparse resolve |
| `QuickMail/Services/ThemeStore.cs` | Built-in + user theme persistence |
| `QuickMail/Services/IThemeService.cs`, `ThemeService.cs` | Core service |
| `QuickMail/Themes/BuiltIn/light.json`, `dark.json`, `ember.json`, `fjord.json`, `heather.json` | Built-ins (embedded) |
| `QuickMail/Styles/ThemedControls.xaml` (P3) | Implicit token-driven control templates |
| `QuickMail/Views/ThemeManagerWindow.xaml(.cs)`, `ViewModels/ThemeManagerViewModel.cs` (P4) | Theme Manager |

### Modify

| File | Changes |
|---|---|
| `QuickMail/QuickMail.csproj` | EmbeddedResource for Themes/BuiltIn |
| `QuickMail/App.xaml`, `App.xaml.cs` | Dictionary slot; construct/init/dispose ThemeService |
| `QuickMail/Styles/AccessibleStyles.xaml` | SystemColors → Theme tokens; FocusThickness |
| `QuickMail/Views/MainWindow.xaml(.cs)` | Token migration; reading-pane re-render on ThemeChanged |
| `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` | `themeCss` param; CSS vars with system fallbacks |
| `QuickMail/Views/SettingsDialog.xaml`, `ViewModels/SettingsViewModel.cs` | Appearance tab |
| `QuickMail/Models/ConfigModel.cs`, `Services/ConfigService.cs` | 6 appearance settings |
| `QuickMail/ViewModels/MainViewModel.cs` | IThemeService injection; event-card colors |
| `QuickMail/Controls/TokenizedAddressBox.xaml.cs`, `Views/KeyCaptureDialog.xaml.cs`, `RichTextDocumentConverter.cs` | C# color migration (§5.5) |
| Remaining Views XAML | Step A/B token migration |
| `QuickMail.Tests/StubServices.cs` | StubThemeService |

## Section 12: Tests to Add

| Test Class | Coverage |
|---|---|
| `ThemeDefinitionTests` | JSON round-trip; sparse fallback to base; unknown-key tolerance; invalid hex rejection; formatVersion gate; font-size range |
| `BuiltInThemeTests` | Every embedded theme parses; **WCAG contrast policy (§6) for every built-in** via a relative-luminance helper |
| `ThemeServiceTests` (StaFact) | Dictionary present pre-render; ApplyTheme swaps atomically; HC forces SystemColors passthrough + withdraws ThemedControls; unknown id → system fallback, no throw; import collision re-ids; export/import round trip; BuildMessageCss emits all `--qm-*` vars, none in HC |
| `ConfigService` additions | Appearance keys round-trip in config.ini |
| `SettingsViewModelTests` additions | Appearance fields persist through Save |
| `ThemeManagerViewModelTests` (P4) | List/apply/duplicate/rename/delete/import/export with stub store |
| `XamlParseTests` additions | ThemedControls.xaml (P3), ThemeManagerWindow (P4) |
| Regression guards | No new hex/named-color literals or numeric `FontSize=` in `Views/*.xaml` outside an allowlist |

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| WPF default templates make Dark unusable before Phase 3 | Certain | Major | Dark is not offered in the UI until ThemedControls ships |
| Retemplated controls (P3) regress UIA/keyboard behavior | Medium | Blocker | Per-template review against accessibility checklist; screen reader walkthrough gate |
| DynamicResource density in virtualized message list | Low | Major | Recycling virtualization confirmed on all lists (bounded realized containers); fallback: hoist colors into implicit styles |
| Theme swap during a modal dialog → re-entrancy crash | Medium | Blocker | Applies happen after dialogs close; OS refreshes debounced + dispatcher-posted; never fire ThemeChanged handlers that rebuild parent UI mid-modal |
| `SystemEvents.UserPreferenceChanged` leak / wrong-thread access | Medium | Major | IDisposable ThemeService; unsubscribe in Dispose; Dispatcher marshal; 250 ms debounce |
| Static→Dynamic conversion mistakes (BasedOn, trigger conditions) | Medium | Major | Pitfall rules in §5.5; XamlParseTests + StaFact smoke tests |
| WebView2 re-render loses scroll position on theme change | Certain | Minor | Accepted; theme changes are rare events |
| Font scaling breaks fixed-size layouts | Medium | Major | Keyboard walkthrough at 150%/200% in Phase 2 acceptance |
| Stale `TryFindResource` colors in rich-text docs after switch | Low | Minor | Docs rebuilt per message; visible one re-renders on ThemeChanged |

### 13.2 Open questions — all resolved

- Theme/view relationship? **Decided:** standalone first; SavedView.ThemeId later (Phase 5). `ApplyTheme` gets the internal `persist` flag now.
- Restyle sender HTML? **Decided:** chrome + reading-pane defaults always; force-on-content as an off-by-default toggle.
- Built-in lineup? **Decided:** System, Quill, Dark, Ember, Fjord, Heather.
- Typography in scope? **Decided:** yes — family + discrete size scale.
- In-app color editor? **Decided:** no (v1); documented JSON + Duplicate + Open-folder.

## Section 14: Appendix — Command Reference

| Command id | Category | Default key | Action |
|---|---|---|---|
| `theme.manager.open` | Settings | none (palette) | Open Theme Manager |
| `theme.next` / `theme.previous` | Settings | none (palette) | Cycle themes; announce new name |
| `theme.apply.{id}` | Settings | none (user-assignable) | Apply a specific theme |

Existing: `Ctrl+Shift+P` opens the palette where all of these are discoverable.

## Section 15: Implementation Guidance for AI

### 15.1 Adjustments you're expected to make

- Exact hover/pressed token choices inside retemplated controls (P3) — use AccentSubtle/Border families; keep them subtle.
- The System→registry read and debounce plumbing details — the contract is: correct resolution, no UI-thread violations, no leaks.
- Where each hardcoded FontSize maps onto Small/Base/Large/Header — judgment call per site; don't invent new size tokens.
- Whether MarkdownPreviewWindow migrates to BuildMessageCss in Phase 2 or 3 — either is fine; don't leave a third variable set behind.

### 15.2 When to ask

- If a Phase 3 retemplate cannot preserve a control's UIA pattern or keyboard behavior, stop — that control ships untemplated (light-biased) rather than inaccessible, and the user decides the trade-off.
- If the Settings dialog's access keys collide with `A_ppearance`, ask before changing an existing access key.
- The keyboard walkthrough (§7) is normative; any deviation needs sign-off.

### 15.3 Highest-risk acceptance steps

- HC toggle while running (§9 "OS adaptation live") — the structural HC design lives or dies here.
- Theme switch with a message open — the WebView2 re-render path and modal rules.
- Invalid-address token in Compose after migration — the most visible C# color site.

---

## Approval checklist status

- Scope bounded per phase; architecture decided (no "figure out during coding" items); shared component audit complete (§5.4); keyboard walkthrough complete (§7); acceptance walkthrough written (§9); accessibility explicit (§8); phases independently testable (§10); risks mitigated (§13); files and tests listed (§11–12); `--online` considered (§5.2); no open questions (§13.2).
