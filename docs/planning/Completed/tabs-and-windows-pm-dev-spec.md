# Tab & Window Management — PM & Dev Specification

**Status:** Draft (for review)
**Date:** June 4, 2026
**Target:** Phase 6 (Workflow & Productivity)
**Crew:** Delta (PM → Dev Lead → Test Enforcer)

> Combined PM + Dev spec. **Sections 1–6 are the PM portion** (problem, users, scope, UX, accessibility). **Sections 7–12 are the Dev portion** (architecture, data model, service / VM layer, views, implementation phases). **Sections 13+** are shared (command registry, accessibility, success metrics, open questions, file/test tables, appendices).
>
> **Revisions:**
> - **2026-06-04** — Resolved the **Ctrl+1–3 vs Ctrl+1–8** conflict: pane jumps move to `Ctrl+Alt+1–3`; `Ctrl+1–8` becomes "jump to tab N". Pane jumps are added to the command registry (no longer hardcoded). See §6.6 *Resolved conflict with pane jumps* and §16 for the resolution note.
> - **2026-06-04** — Resolved the **tab list chord**: `Ctrl+Shift+BackQuote` (VS Code's Quick Open Tab) is the single chord. `Ctrl+Alt+Tab` is dropped because it is the Windows "all task switcher".

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Personas & Use Cases](#3-personas--use-cases)
4. [Competitive Landscape](#4-competitive-landscape)
5. [Design Principles](#5-design-principles)
6. [Feature Scope & Acceptance Criteria](#6-feature-scope--acceptance-criteria)
7. [Architecture Overview](#7-architecture-overview)
8. [Data Model — `TabSessionModel` & `WindowingPreferences`](#8-data-model--tabsessionmodel--windowingpreferences)
9. [ViewModels — `TabSessionViewModel` & `MessageTabViewModel`](#9-viewmodels--tabsessionviewmodel--messagetabviewmodel)
10. [Views — `MainWindow` Restructure & `MessageTabContent`](#10-views--mainwindow-restructure--messagetabcontent)
11. [Compose & Address Book Windows as Tab Hosts](#11-compose--address-book-windows-as-tab-hosts)
12. [Implementation Phases](#12-implementation-phases)
13. [Command Registry & Shortcuts](#13-command-registry--shortcuts)
14. [Accessibility (WCAG 2.2)](#14-accessibility-wcag-22)
15. [Success Metrics](#15-success-metrics)
16. [Open Questions & Risks](#16-open-questions--risks)
17. [Files to Create](#17-files-to-create)
18. [Files to Modify](#18-files-to-modify)
19. [Tests to Add](#19-tests-to-add)
20. [Appendix A — Keyboard Cheat Sheet](#appendix-a--keyboard-cheat-sheet)
21. [Appendix B — Tab Lifecycle State Diagram](#appendix-b--tab-lifecycle-state-diagram)
22. [Appendix C — Window-Open & Close Sequences](#appendix-c--window-open--close-sequences)

---

## 1. Executive Summary

QuickMail today opens a single email in an **in-place reading pane** (a WebView2 host inside the main window) and a **single compose window** that has a hard-coded title. There is no way to have two messages open at once, no way to put an open message side-by-side with a draft, and no way to tile address book or draft lists next to the message list. Power users must work in a strict one-email-at-a-time rhythm, which is the single most-cited gap between QuickMail and the keyboard-centric mail clients it competes with (mutt, aerc, neomutt, Thunderbird with `Tabbed Messages`).

**Tab & window management** turns QuickMail into a first-class tabbed & multi-window client. Three user-facing capabilities are introduced:

1. **Reading mode is now a user setting** — Reading Pane (default, current behaviour), Tab, or New Window — and applies to all messages including drafts and the address book. Existing keyboard muscle memory is preserved: Enter still opens; Escape still returns; the new behaviour is just *where*.
2. **A persistent tab strip** lives under the menu / toolbar of the main window. Every opened tab carries a label and close button, supports drag-to-reorder, and routes the standard tab keyboard model (Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W, Ctrl+1…8, Ctrl+9). A "tab list" overlay (Ctrl+Shift+` — VS Code style) mirrors the Alt+Tab / Windows-tab task switcher.
3. **Auxiliary surfaces — Address Book, Group Manager, Rules Manager, View Manager — can be opened as tabs in the main window or as standalone windows.** Compose windows (already standalone) get a proper dynamic title and a "Compose" / "Draft" / "Reply" prefix that is part of the new tab & window registry so the title also shows in the taskbar.

The feature is **strictly additive** for v1: users who keep the default "Reading Pane" setting see no change. Existing reading-pane keyboard shortcuts, focus restoration, and WebView2 plumbing continue to work identically when the new mode is "Reading Pane" or when no tab/window target is active.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified against the code)

| Surface | Today | Pain |
|---|---|---|
| Reading a message | Inline WebView2 in MainWindow, toggled by `_vm.IsMessageOpen`. Filled by `MessageList_MouseLeftButtonUp` and Enter on a row. | Only one at a time. To compare two emails you must open a draft in a new window and copy/paste — there is no way to keep two messages visible. |
| Compose | `OpenComposeWindow(ComposeModel)` builds a `ComposeWindow` and `ShowDialog()`. Window title is hard-coded `"Compose Message"` ([ComposeWindow.xaml:11](QuickMail/Views/ComposeWindow.xaml)). | The taskbar shows the same title for ten drafts in a row. Users cannot tell draft from draft. |
| Address Book | `AddressBookWindow` opened from `OpenAddressBook` (Ctrl+Shift+B). Single window, hides the main view. | To copy an address into a draft you must keep flipping between main window and Address Book. |
| Group Manager | `GroupManagerWindow` opened from `OpenGroupManager` (Ctrl+Shift+M). | Same — modal flip between two windows. |
| Rules / View / Account manager | All standalone `ShowDialog()` windows. | Same. |
| Multi-account folder tree | Lives in a single window; folder picker (`FolderPickerWindow`) is a dialog. | No persistent view of multiple folders side-by-side. |

### 2.2 What users want

- "Open this message in a new window" without losing the message list / reading pane context.
- "Pin two emails open side-by-side" (Outlook "New Window", Thunderbird "Open in New Tab" + a windowing option).
- "Address book in a tab I can drag to my second monitor" (the comment that triggered this spec).
- "Tell my ten draft windows apart from the taskbar."

### 2.3 Non-goals (v1, deliberately)

- **No `tmux`-style split panes inside a single window.** Tabs + free-floating windows are the v1 model. Splits would require a docking framework (Avalon Dock) and a separate usability study.
- **No detached tabs that survive a QuickMail crash.** Sessions are in-memory; on restart the user is back in the message list. (Persistence is a v2 candidate — see [§16](#16-open-questions--risks).)
- **No macOS / Linux support.** WPF / Windows only. The keyboard model is Windows-native (Ctrl+Tab, Alt+Tab-equivalent task switcher).
- **No MDI** (MDI child windows inside the main window). MDI is widely considered a UX regression; the modern model is `TabControl` + top-level `Window`.
- **No automatic "grouped conversation" tabs.** A conversation group is still a single thing in the message list; we do not spawn a tab per message inside a conversation.

### 2.4 Why now

This is the natural follow-on to **Alt+Enter View Properties** ([spec](docs/planning/alt-enter-properties-pm-dev-spec.md)) and **Status bar accessibility** ([plan](docs/planning/status-bar-accessibility-plan.md)) — both shipped in v0.6.9 — and the next step toward a "workflow / productivity" phase that the roadmap branch review flagged for Phase 6. The technical prerequisites already in place: `CommandRegistry`, the `Event` pattern on `MainViewModel`, the `IWindow` abstraction implicit in every existing secondary window, the consistent `Title="{Binding ...}"` pattern on `MainWindow`.

---

## 3. Personas & Use Cases

| Persona | Need | Use case | Touched feature |
|---|---|---|---|
| **Power keyboard user** (Alex) | "I have six email threads going. I want each in its own tab so I can Ctrl+Tab between them." | Sets *Reading mode = Tab*, presses Enter on six messages, Ctrl+Tabs between them, types replies in parallel. | Tab strip, Ctrl+Tab, per-tab dirty state. |
| **Two-monitor user** (Riley) | "I want my address book open on monitor 2, mail on monitor 1, no Alt+Tab." | Opens Address Book with *Open in window = Window*; pins it to the second monitor. | Window mode for non-message surfaces. |
| **Screen reader user** (Pat) | "I need to know which tab I'm in and how many are open." | Tab strip exposes `TabItem` UIA; Ctrl+Shift+` opens a list overlay that announces "Tab 2 of 5: subject, draft". | ARIA + announcement. |
| **Triage user** (Sam) | "I want to read 30 emails in a queue, then move on." | Sets *Reading mode = Reading Pane* (today's behaviour), nothing changes for them. | Default preserved. |
| **Family / hobby user** (Jordan) | "Drafts pile up; I forget which window is which." | Compose window title is now "Draft — Re: School pickup" so the taskbar disambiguates. | Compose dynamic title. |
| **Privacy-conscious user** (Morgan) | "Tab list shouldn't leak subject in the announcement at default verbosity." | Settings: `Announce tab subject in tab list = off`. | Accessibility fine-tuning (§14). |

### 3.1 Primary use cases

1. **Open a message in a tab** — `Enter` on a row, with the mode setting = Tab, opens a new tab and activates it.
2. **Open a message in a new window** — Ctrl+Enter on a row (or the *Open in new window* context-menu item) opens a standalone `MessageWindow` (or a tab in a new main window — see §11.4).
3. **Switch tabs** — Ctrl+Tab / Ctrl+Shift+Tab cycle, Ctrl+1…8 jump, Ctrl+9 jump to last.
4. **Reorder tabs** — Drag a tab header; release to drop at the new position. The data model reorders; focus follows.
5. **Close a tab** — Ctrl+W on the active tab, or `x` on the tab header, or middle-click. If the tab's message was open in the reading pane of the original window, focus returns to the message list at that row.
6. **Promote a tab to a window** — A tab menu item "Open in new window" detaches the tab into its own top-level `MessageWindow`.
7. **Demote a window to a tab** — A window menu item "Move to main window" closes the window and adds the tab to the main window's tab strip.
8. **Open Address Book in a tab** — Add a "Pin as tab" checkbox to the Address Book window. While checked, the next open uses a tab in the main window; the Address Book window is hidden behind the tab title.
9. **Compose window gets a real title** — Window title is `"{prefix} — {subject or 'Untitled'}"`, where `prefix ∈ {Compose, Draft, Reply, Reply All, Forward, Edit Template}`.
10. **Tab list overlay** — Ctrl+Shift+` opens a small modal list (the analogue of Alt+Tab on Windows), with arrow keys to navigate and Enter to activate.

---

## 4. Competitive Landscape

| Product | Tab model | Window model | Keyboard | Verdict |
|---|---|---|---|---|
| **Outlook (Win)** | One window with horizontal tab strip; tabs in Inspector windows (separate windows) | Detachable tabs become Inspectors | Ctrl+Tab cycles, Alt+Win for task switcher, Ctrl+F4 closes | Strong model; "tab/window" duality is the norm. |
| **Thunderbird** | "Tabbed Messages" add-on, default in 115+ | Each tab can be detached via context menu | Ctrl+Tab / Ctrl+Shift+Tab cycle, Ctrl+W closes, Ctrl+1..9 jump | Closest to what we want; tab list overlay is missing. |
| **The Bat!** | Each folder is a tab; message view is internal | Yes, "Workspace" model | Customizable but defaults to mouse | More aggressive tab model; not the right fit for IMAP. |
| **aerc / mutt** | No tabs (terminal) | N/A | All keyboard | Reference for keyboard density, not for tabs. |
| **Web Gmail** | Single message view; no native tab model | Compose opens a new browser window | Limited | Cloud / web model is different. |

**Decision points for QuickMail:**
- **Reading mode is a setting, not a mode toggle.** Tabs are the *expected* default for a keyboard-centric client (Thunderbird 115+ proves it), but we ship "Reading Pane" as the default to preserve zero-change for v0.6.x users.
- **Tabs are top-level, not nested.** A tab can hold a message OR an auxiliary surface (Address Book, etc.), but a tab does not itself contain tabs.
- **Detaching = creating a new top-level window** that owns its own tab strip (initially with one tab). This is the Outlook Inspector model and the safest mental model.

---

## 5. Design Principles

1. **Zero-change for users who don't opt in.** Setting "Reading mode = Reading Pane" reproduces today's behaviour exactly. All existing focus restoration, F6 pane cycling, and reading-pane announcements continue to work.
2. **Keyboard parity with the taskbar.** Ctrl+Tab cycles tabs in MRU order, mirroring Alt+Tab. A `TabListWindow` (Ctrl+Shift+`) makes that explicit for screen readers.
3. **Tabs are first-class objects, not labels on the window.** A `TabSessionModel` is persisted across the lifetime of a window, owns its content (a message, an address book, etc.), and is identified by a Guid so the user can rebind shortcuts per tab if needed (v2).
4. **Window mode is a property of the *open action*, not the *content*.** A message is content; whether it lives in a tab or a window is decided when the tab is opened. The same content can be promoted/demoted later.
5. **Escape is local to the surface.** In a tab, Escape returns focus to the tab's content (e.g. message body → message list in that tab's view). In a window, Escape closes the window. In a tab strip, Escape returns focus to the message list. This mirrors Outlook's behaviour and prevents the global Escape from being surprising.
6. **No regressions in existing tests.** The existing reading-pane tests must continue to pass without modification. New tests are added for tab behaviour.
7. **Existing MVVM rules are unchanged.** Code-behind is still limited to focus, keyboard, dialogs-from-VM-events, and WebView2 hosting. New business logic (e.g. promoting a tab to a window) lives in `MainViewModel` and is exposed via events / callbacks.

---

## 6. Feature Scope & Acceptance Criteria

### 6.1 Settings (Settings → Windowing page)

| Setting | Type | Default | Description |
|---|---|---|---|
| `MessageOpenMode` | enum: `ReadingPane`, `Tab`, `Window` | `ReadingPane` | Where Enter / Ctrl+Enter / click on a message opens it. |
| `OpenAddressBookInTab` | bool | `false` | If true, Address Book opens in a tab in the main window instead of as a standalone window. |
| `OpenRulesManagerInTab` | bool | `false` | Same, for Rules Manager (Ctrl+Shift+U). |
| `OpenGroupManagerInTab` | bool | `false` | Same, for Group Manager (Ctrl+Shift+M). |
| `OpenViewManagerInTab` | bool | `false` | Same, for View Manager (Ctrl+Shift+J). |
| `ConfirmCloseTabWithUnsaved` | bool | `true` | If true, closing a tab with a draft or in-progress send prompts a confirmation. (For v1, applies only to the Compose tab/window — see §11.2.) |
| `TabsRememberAcrossRestart` | bool | `false` (v1) | **Reserved for v2.** v1 always resets tabs at restart. Setting is stored and ignored with a "Coming soon" hint in the UI. |

All settings persist via the existing `ConfigService` (config.ini) — see [ConfigService.cs](QuickMail/Services/ConfigService.cs) for the canonical pattern.

### 6.2 Tab strip (new UI)

- A `TabControl` (WPF `TabControl`) lives in `MainWindow.xaml` below the toolbar, above the content area.
- It uses the standard WPF `TabItem` template so UIA and screen readers expose the correct role automatically.
- Tab header shows: **icon** (16×16, from `QuickMail/Resources/`), **title** (truncated, ellipsis at 30 chars, full title in tooltip), **close button** (`x`, hit target ≥ 16×16).
- Title text is bound to `TabSessionModel.Title`; the close button is a `Button` with `Command="{Binding CloseCommand}"` (or routed via code-behind using the existing `TabItem.CloseRequested` style pattern, see [§10.3](#103-tabitem-template--style)).
- A "tab list" button (`v` chevron) at the right end of the strip opens the `TabListWindow`.
- A drag handle is implicit on the tab header; reorder is a drag-to-move operation. Keyboard alternative: Ctrl+Shift+Left/Right.

### 6.3 Reading mode = `Tab`

When the setting is `Tab`:

| User action | Result |
|---|---|
| Click on a row in the message list | A new tab opens with the message, activated. |
| Enter on a row | Same. |
| Double-click on a row | A new tab opens **and** the row is left selected in the message list (so the user can keep arrow-keying). |
| Ctrl+Enter on a row | A new window opens (a "window" of one tab). |
| Shift+Enter on a row | The message opens in the **currently active** tab if it's a `MessageTabViewModel`, replacing its content (with a transient hint if the current tab is unsaved). |
| Enter on a row in a different folder while a tab is open | The current tab's content is replaced; the previous tab is closed (silent — not destructive). |

### 6.4 Reading mode = `Window`

Same as 6.3, but Ctrl+Enter is unnecessary — *Enter* itself opens in a new window. The user is encouraged to set `MessageOpenMode = Tab` and use Ctrl+Enter for the rare window case; setting `Window` as the default is supported because some users with very large screens want always-side-by-side.

### 6.5 Reading mode = `Reading Pane` (default, current behaviour)

**No behaviour change.** This is the explicit compatibility mode. All existing tests pass unchanged.

### 6.6 Tab keyboard model

| Key | Action | Notes |
|---|---|---|
| `Ctrl+Tab` | Next tab (MRU order) | Wraps around. |
| `Ctrl+Shift+Tab` | Previous tab (MRU order) | |
| `Ctrl+1` … `Ctrl+8` | Activate tab N | 1-indexed. Standard browser/editor "jump to tab N" chord. |
| `Ctrl+9` | Activate last tab | Standard "tabs" model. |
| `Ctrl+W` | Close active tab | Prompts if `ConfirmCloseTabWithUnsaved` and the tab is dirty. |
| `Ctrl+Shift+W` | Close all tabs except the active one | |
| `Ctrl+Shift+BackQuote` (`` ` ``) | **Open tab list overlay** | VS Code's "Quick Open Tab" chord. See §6.7. |
| `Ctrl+Shift+Left` / `Ctrl+Shift+Right` | Reorder active tab | For keyboard users who don't drag. |
| `Alt+Enter` on a tab | Tab Properties (count of messages, source folder, account, read state, opened at) | Reuses the existing `PropertiesWindow` (§10.5). |

> **Resolved conflict with pane jumps.** Today `Ctrl+1`, `Ctrl+2`, `Ctrl+3` are hardcoded pane jumps in `MainWindow.xaml.cs` (Account list, Folder tree, Message list) and listed as such in `CLAUDE.md`. The browser/editor "jump to tab N" chord is `Ctrl+1..8`, so the two models collide. **Decision:** pane jumps move to **`Ctrl+Alt+1`, `Ctrl+Alt+2`, `Ctrl+Alt+3`** and are added to the command registry (no longer hardcoded). Tab jumps take `Ctrl+1..8`. Rationale: pane jumps and tab jumps are conceptually distinct actions; using the *same digit* with different *modifiers* is a Windows convention (e.g. `Alt+1..9` for menu / ribbon access, `Ctrl+Alt+1..9` for in-app pane navigation). The `Alt+` prefix on the existing F6-family pane cycling (`Ctrl+0`, `Ctrl+9` in the legacy table) gives us room to absorb the shift without losing either capability. Migration is one new line in `OnWindowKeyDown` per digit and a one-line change to each CLAUDE.md table. The What's New dialog and the in-app tutorial both call this out.

### 6.7 Tab list overlay

A modal window (or `Popup`) triggered by `Ctrl+Shift+BackQuote` (VS Code's Quick Open Tab chord):

```
┌─ Open tabs ─────────────────────┐
│ ▶ Subject A — folder/Inbox      │  ← active
│   Subject B — folder/Important  │
│   Subject C — folder/Sent       │
│   ...                           │
└─────────────────────────────────┘
```

- Arrow keys navigate.
- Enter activates the selected tab; the overlay closes and focus returns to the tab strip.
- Escape closes the overlay without changing the active tab.
- Each row carries an `AutomationProperties.Name` like `"Tab 1 of 5, subject A, Inbox"`.
- A small "x" on the right of each row allows mouse-only users to close that tab.

### 6.8 Drag-to-reorder

Standard WPF `TabControl` does not support drag-to-reorder out of the box. We attach a `PreviewMouseLeftButtonDown` / `MouseMove` / `Drop` handler to the tab strip that:

1. On mouse-down on a tab header, records the source tab and a flag.
2. On mouse-move past a threshold, sets `DragDrop.DoDragDrop` with the source tab as data.
3. On drop on a tab header, calls `_vm.MoveTab(sourceIndex, targetIndex)`.

Keyboard alternative (Ctrl+Shift+Left/Right) is always available.

### 6.9 Window management

A "new window" is a top-level `Window` that contains the same shell as `MainWindow` but is a separate `MainViewModel` instance (sharing the same `ProfileContext` and services). It has its own tab strip, its own focus state, and its own settings. Closing the window disposes its VMs.

Why a separate `MainViewModel`? Because the message list, folder tree, and selected folder are per-window state; sharing one would corrupt focus restoration when two windows are open.

The new window is *not* a dialog — it has its own taskbar entry. The user can place it on a second monitor.

### 6.10 Compose window changes

- **Title becomes dynamic**: `"{prefix} — {subject or 'Untitled'}"`, where `prefix` is one of:
  - `Compose` (Ctrl+N blank)
  - `Draft` (auto-saved draft)
  - `Reply` (Ctrl+R)
  - `Reply All` (Ctrl+Shift+R)
  - `Forward` (Ctrl+F)
  - `Edit Template` (template manager → edit)
  - `Message` (re-edit of a sent message, if/when supported)
- The title updates on every `Subject` change (debounced 200ms) to avoid per-keystroke title churn.
- A new setting `OpenDraftsInTab` (default `false` — drafts continue to open in standalone windows) lets users open a draft as a tab in the main window instead. The window then gets a "Move to main window" button to promote it to a tab.
- Compose is the only "window" that always opens as a top-level window in v1, because it is a "task" that should not consume main-window tab space. (Rationale: see §11.2.)

### 6.11 Address Book / Rules / Group Manager / View Manager as tabs

When the corresponding `OpenXxxInTab` setting is on, the surface opens in a tab in the main window. When the setting is off, the existing standalone `ShowDialog()` window is used. The two modes are mutually exclusive per surface; we do not show both at once.

| Surface | Standalone shortcut | Tab-mode shortcut | Owner |
|---|---|---|---|
| Address Book | `Ctrl+Shift+B` | same | `AddressBookViewModel` |
| Group Manager | `Ctrl+Shift+M` | same | `GroupManagerViewModel` |
| Rules Manager | `Ctrl+Shift+U` | same | `RulesManagerViewModel` |
| View Manager | `Ctrl+Shift+J` | same | `ViewManagerViewModel` |
| Account Manager | (menu only) | not tabbed in v1 | — |
| Settings | (menu only) | not tabbed in v1 | — |

Account Manager and Settings are not tabbed in v1 because both are inherently modal: you complete them, you save, you leave. Tabbing them would invite "tab hoarding" with no workflow benefit.

### 6.12 Per-window preferences

When a window is detached, it inherits the parent's settings. Subsequent changes to settings in that window are local to that window. Settings are global in the sense that the `ConfigService` writes to disk; per-window overrides (e.g. "this window is always on top") are not in v1.

### 6.13 Acceptance criteria (must pass before merge)

1. Existing reading-pane tests pass unchanged.
2. `MessageOpenMode = ReadingPane` reproduces today's behaviour to the keystroke: Enter opens in the reading pane, Escape returns focus to the message list, F6 cycles the same way.
3. `MessageOpenMode = Tab` opens a new tab on Enter; Ctrl+Tab cycles tabs; Ctrl+W closes; Ctrl+1 jumps.
4. `MessageOpenMode = Window` opens a new top-level `MessageWindow` on Enter; closing that window returns focus to the row in the main window.
5. Ctrl+Enter on a row opens a new window regardless of the setting.
6. Drag-to-reorder tabs works with the mouse and updates the title strip order.
7. Tab list overlay opens with Ctrl+Shift+` (the standard "Quick Open Tab" chord, formerly listed as an open question), announces the active tab, and routes Enter/Escape correctly.
8. Compose windows show the dynamic title; the taskbar shows distinct titles for distinct drafts.
9. Each setting can be changed live and the new mode takes effect on the next open action.
10. No regression in screen-reader flow: opening a tab announces "Tab N of M, <subject>". Closing announces "Tab closed. N tabs remaining."
11. `Open Address Book in a tab` setting on → Address Book opens in a tab with the existing Address Book VM, no new VM is required.
12. New tests in §19 cover tab add, remove, reorder, mode resolution, and focus restoration.

---

## 7. Architecture Overview

### 7.1 Three-tier change

```
┌──────────────────────────────────────────────────────────────┐
│  Views (XAML + code-behind)                                  │
│  - MainWindow: now hosts TabControl above the content grid   │
│  - TabListWindow: new modal overlay                          │
│  - MessageWindow: new top-level Window for "Window" mode    │
│  - MessageTabContent: a UserControl hosting the reading pane │
│    (extracted from MainWindow)                               │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│  ViewModels (no business logic in code-behind)               │
│  - MainViewModel: gains OpenTabs, ActiveTab, child VM list  │
│  - TabSessionViewModel (abstract base)                       │
│  - MessageTabViewModel : TabSessionViewModel                 │
│  - AddressBookTabViewModel : TabSessionViewModel             │
│  - RulesManagerTabViewModel : TabSessionViewModel            │
│  - ...                                                       │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│  Services / models                                           │
│  - WindowingPreferences (in ConfigModel)                    │
│  - TabSessionModel (in-memory, not persisted in v1)          │
└──────────────────────────────────────────────────────────────┘
```

### 7.2 Where the reading pane lives now

Today the reading pane (WebView2 + header) is a `Grid` inside `MainWindow.xaml` whose `Visibility` is bound to `IsMessageOpen`. The grid contains a header stack panel (`FromField`, `SubjectField`, `DateField`, `ToField`, `CcField`, `AttachmentsField`) and `MessageBody` (the WebView2).

For tabs we **extract that grid into a `UserControl` named `MessageTabContent.xaml`** with its own code-behind, its own `MessageTabViewModel`, and its own WebView2 host. The current reading-pane code (the render pipeline, the Escape handler, the focus chain) moves into `MessageTabContent.xaml.cs` verbatim. `MainWindow` keeps a *single* inline reading pane that is used when `MessageOpenMode = ReadingPane`, and the same VM is bound to either the inline pane or the tab content. This is the **zero-regression path** required by §6.5.

### 7.3 The `TabControl` host

`MainWindow.xaml` adds a `TabControl` above the existing content grid:

```xml
<TabControl x:Name="TabStrip"
            ItemsSource="{Binding OpenTabs}"
            SelectedItem="{Binding ActiveTab}"
            Visibility="{Binding ShowTabStrip, Converter={StaticResource BoolToVisibilityConverter}}"
            ... />
```

`ShowTabStrip` is `true` when `OpenTabs.Count > 0` and the user is not in `ReadingPane` mode *with no tabs open*. The existing inline reading pane is hidden when `OpenTabs.Count > 0`.

When the user closes all tabs, `ShowTabStrip` becomes false and the inline reading pane re-appears (still in `ReadingPane` mode) — or, in `Tab` mode, the message list regains focus. The choice depends on the mode at the time of close.

### 7.4 New top-level window

A `MessageWindow` (a new `Window`) is a stripped-down shell:

```
┌─ MessageWindow ──────────────────────────────────┐
│ Menu (File / Message / View / Help — minimal)    │
│ Tab strip (initially one tab; can grow)          │
│ Content area: same as MainWindow's content grid  │
│ Status bar (read-only; no folders/accounts pane) │
└──────────────────────────────────────────────────┘
```

It reuses `MainWindow`'s content grid by composition (via a `MainContentView` UserControl) so the message list, group trees, and folder tree are all visible — but the left folder/account pane is collapsed by default. The user can drag a folder from the main window's folder tree to a `MessageWindow`'s "Show folders" button to populate that window's folder list.

This is non-trivial; v1 ships a **simpler** `MessageWindow` that contains only the message list + reading pane for a *single* folder. Windowed multi-folder is a v2 candidate (see §16).

### 7.5 Service layer

No new services. The existing `MessageDetail` fetch pipeline (IMAP foreground leases, `LocalStoreService` cache) is used as-is. `MainViewModel.SelectMessageCommand` and `MainViewModel.OpenDraftCommand` are reused by `MessageTabViewModel`.

`ConfigService.Load()` is called by `MainViewModel` to read `MessageOpenMode` and the auxiliary tab settings. The setting is applied per open action, not per window.

---

## 8. Data Model — `TabSessionModel` & `WindowingPreferences`

### 8.1 `ConfigModel` additions (in [Models/ConfigModel.cs](QuickMail/Models/ConfigModel.cs))

```csharp
// ── Windowing (Phase 6) ─────────────────────────────────────────────
public enum MessageOpenMode
{
    ReadingPane = 0,   // today's behaviour, default
    Tab         = 1,   // opens in a tab in the main window
    Window      = 2,   // opens in a new top-level window
}

public class WindowingPreferences
{
    /// <summary>Where Enter / click on a message opens it.</summary>
    public MessageOpenMode MessageOpenMode { get; set; } = MessageOpenMode.ReadingPane;

    /// <summary>If true, the named surface opens in a tab instead of a window.</summary>
    public bool OpenAddressBookInTab   { get; set; } = false;
    public bool OpenGroupManagerInTab  { get; set; } = false;
    public bool OpenRulesManagerInTab  { get; set; } = false;
    public bool OpenViewManagerInTab   { get; set; } = false;

    /// <summary>If true, drafts continue to open in standalone Compose windows.</summary>
    public bool OpenDraftsInWindow { get; set; } = true;

    /// <summary>Confirm before closing a tab whose content is a draft or unsent reply.</summary>
    public bool ConfirmCloseTabWithUnsaved { get; set; } = true;

    /// <summary>Reserved for v2. v1 always resets tabs at restart.</summary>
    public bool TabsRememberAcrossRestart { get; set; } = false;
}
```

`ConfigModel` gets a new property:

```csharp
public WindowingPreferences Windowing { get; set; } = new();
```

INI serialization uses the existing pattern (`[windowing] MessageOpenMode = tab`); see [ConfigService.cs:69-99](QuickMail/Services/ConfigService.cs) for the comment-out / parse / write helpers.

### 8.2 `TabSessionModel` (in-memory, not persisted in v1)

```csharp
// QuickMail/Models/TabSessionModel.cs
public enum TabKind
{
    Message,
    AddressBook,
    GroupManager,
    RulesManager,
    ViewManager,
    Draft,        // compose tab when OpenDraftsInWindow = false
    Unknown,
}

/// <summary>
/// In-memory representation of a single open tab. Not persisted in v1
/// (the TabsRememberAcrossRestart setting is read but not yet honoured).
/// Identity is by Guid; titles are derived from the content's state.
/// </summary>
public sealed class TabSessionModel
{
    public Guid   Id           { get; init; } = Guid.NewGuid();
    public TabKind Kind        { get; init; }
    public string  Title       { get; set; } = string.Empty;
    public string  Tooltip     { get; set; } = string.Empty;
    public bool   IsDirty      { get; set; }   // draft / unsent reply
    public bool   CanClose     { get; set; } = true;
    public object? ContentKey  { get; init; } // e.g. the message UID, the
                                             // address book VM instance, etc.
}
```

The `Id` is used by the tab list overlay and any future shortcut rebinding.

### 8.3 VM-side base class

```csharp
// QuickMail/ViewModels/TabSessionViewModel.cs
public abstract partial class TabSessionViewModel : ObservableObject
{
    public TabSessionModel Model { get; }

    protected TabSessionViewModel(TabSessionModel model) { Model = model; }

    /// <summary>Refreshes Model.Title from current state. Called whenever
    /// the underlying content changes (e.g. message subject loaded).</summary>
    protected void RefreshTitle() { /* base impl pushes Model.Title → Title */ }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool   _isDirty;
    [ObservableProperty] private bool   _canClose = true;

    /// <summary>Returns true if the tab can be closed right now (no unsaved
    /// work, or the user has confirmed discarding it).</summary>
    public virtual bool CanCloseNow() => !IsDirty || !CanClose;

    public event Action<TabSessionViewModel>? CloseRequested;
    protected void RequestClose() => CloseRequested?.Invoke(this);
}
```

### 8.4 `MessageTabViewModel`

```csharp
public sealed class MessageTabViewModel : TabSessionViewModel
{
    private readonly MainViewModel _main;

    public MailMessageDetail? Detail { get; private set; }
    public MailMessageSummary? Summary { get; }

    public MessageTabViewModel(MainViewModel main, MailMessageSummary summary)
        : base(new TabSessionModel { Kind = TabKind.Message, ContentKey = summary.UniqueId })
    {
        _main   = main;
        Summary = summary;
        RefreshTitle();
    }

    /// <summary>Loads the message detail via the same path the inline reading
    /// pane uses (foreground IMAP lease, fallback to local cache).</summary>
    public async Task LoadAsync(CancellationToken ct) { ... }

    public bool IsMessageOpen { get; private set; }
    public event Action? IsMessageOpenChanged;        // re-renders the body
    partial void OnIsMessageOpenChanged(bool value) => IsMessageOpenChanged?.Invoke();
}
```

The render pipeline (HTML build, CSP, WebView2 navigation, focus chain) is **moved unchanged** from `MainWindow.xaml.cs` into `MessageTabContent.xaml.cs` and is parameterised by the `MessageTabViewModel`. The existing `ShowMessageBodyAsync` and `FocusMessageBodyAsync` helpers become instance methods on `MessageTabContent`.

---

## 9. ViewModels — `MainViewModel` changes

`MainViewModel` gains the following members. All are observable so the tab strip binds cleanly.

```csharp
public partial class MainViewModel
{
    // ── Tabs ─────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<TabSessionViewModel> _openTabs = new();
    [ObservableProperty] private TabSessionViewModel? _activeTab;
    [ObservableProperty] private bool _showTabStrip;

    // ── Settings (read from config) ─────────────────────────────────
    public MessageOpenMode MessageOpenMode { get; private set; } = MessageOpenMode.ReadingPane;

    // ── Existing flags (unchanged) ──────────────────────────────────
    // IsMessageOpen, MessageDetail, SelectedMessage — continue to drive
    // the inline reading pane when MessageOpenMode = ReadingPane and no
    // tabs are open. When tabs are open, IsMessageOpen is false and
    // MessageDetail lives on the active MessageTabViewModel.

    // ── New events (subscribed by MainWindow code-behind) ───────────
    public event Action<TabSessionViewModel>? TabOpenRequested;
    public event Action<TabSessionViewModel>? TabPromoteToWindowRequested;
    public event Action? OpenNewMessageWindowRequested;
    public event Func<MessageOpenMode, Task<TabSessionViewModel?>>? OpenMessageTabAsync;

    // ── Commands ────────────────────────────────────────────────────
    [RelayCommand] private void ActivateNextTab() { ... }   // Ctrl+Tab
    [RelayCommand] private void ActivatePrevTab() { ... }   // Ctrl+Shift+Tab
    [RelayCommand] private void ActivateTabByIndex(int idx) { ... } // Ctrl+1..9
    [RelayCommand] private void CloseActiveTab() { ... }    // Ctrl+W
    [RelayCommand] private void CloseOtherTabs() { ... }    // Ctrl+Shift+W
    [RelayCommand] private void MoveTabLeft() { ... }       // Ctrl+Shift+Left
    [RelayCommand] private void MoveTabRight() { ... }      // Ctrl+Shift+Right
    [RelayCommand] private void PromoteActiveTabToWindow() { ... } // menu / context
    [RelayCommand] private void OpenTabList() { ... }       // Ctrl+Shift+`
    [RelayCommand] private void OpenMessageInNewWindow() { ... } // Ctrl+Enter
}
```

### 9.1 The "open a message" decision

The decision tree (called from the message-list mouse-up / Enter / Ctrl+Enter handlers in `MainWindow.xaml.cs`):

```csharp
private async Task HandleOpenMessageAsync(MailMessageSummary summary, OpenMessageGesture gesture)
{
    // 1. Ctrl+Enter always opens in a new window.
    if (gesture == OpenMessageGesture.CtrlEnter)
    {
        OpenNewMessageWindowRequested?.Invoke();
        return;
    }

    // 2. Resolve the effective mode. Per-action override: Shift+Enter
    //    reuses the current tab; bare Enter follows the setting.
    var mode = gesture switch
    {
        OpenMessageGesture.ShiftEnter => ActiveTab is MessageTabViewModel
            ? MessageOpenMode.Tab          // reuse
            : MessageOpenMode,
        _ => MessageOpenMode,
    };

    // 3. Dispatch.
    switch (mode)
    {
        case MessageOpenMode.ReadingPane:
            await SelectMessageCommand.ExecuteAsync(summary);
            if (IsMessageOpen && MessageDetail != null)
                await ShowMessageBodyAsync(MessageDetail);
            break;

        case MessageOpenMode.Tab:
            var tab = await OpenMessageTabAsync?.Invoke(MessageOpenMode.Tab)
                       ?? await OpenMessageTabLocalAsync(summary);
            ActiveTab = tab;
            break;

        case MessageOpenMode.Window:
            OpenNewMessageWindowRequested?.Invoke();
            break;
    }
}
```

`OpenMessageTabAsync` is a delegate set by the View: it gives the VM a chance to ask the View to construct a tab (which needs access to the WPF `TabItem` template and the `MessageTabContent` UserControl). If the View hasn't subscribed, `OpenMessageTabLocalAsync` builds the VM and raises `TabOpenRequested` (which the View handles by inserting the `TabItem`).

### 9.2 Tab lifecycle

`OpenTabs` is the source of truth. Operations:

| Action | Method | Notes |
|---|---|---|
| Add | `OpenMessageTabAsync(summary)` | Returns the new `MessageTabViewModel`. Inserts at end, or after the active tab if the active tab is a `MessageTabViewModel` and `gesture == ShiftEnter` (in which case we *replace* the active tab's content). |
| Close | `CloseTab(tab)` | Removes from `OpenTabs`. If the closed tab was active, activates the next / previous. Disposes any owned resources. |
| Reorder | `MoveTab(from, to)` | Moves the entry. Triggers the existing `SelectedItem` mechanism to keep `ActiveTab` valid. |
| Activate | `ActiveTab = tab` | The View scrolls the tab strip to make the tab visible. |
| Promote | `PromoteActiveTabToWindow()` | Closes the tab in this window, opens a new `MessageWindow` with the same VM. |
| Demote | (called by `MessageWindow` when user picks "Move to main window") | Closes the source window, adds the tab to the main window's `OpenTabs`. |

### 9.3 Per-action `OpenMessageGesture` enum

```csharp
public enum OpenMessageGesture
{
    Click,
    Enter,
    ShiftEnter,
    DoubleClick,
    CtrlEnter,
}
```

This is consumed by `MessageList_MouseLeftButtonUp` and the `Enter` handler. The `DoubleClick` branch leaves the source row selected (so arrow-keys still work in the list).

### 9.4 Reading-pane mode + tabs

When `MessageOpenMode = ReadingPane` and the user has *no tabs open*, the existing reading-pane code path is used verbatim. When the user opens even a single tab, the inline reading pane is hidden and the tab is shown — this prevents a confusing "two readings of the same message" state. The settings panel clarifies this with a "Reading mode" hint when the tab strip is visible.

When the user closes all tabs in `ReadingPane` mode, control returns to the message list and the inline reading pane is hidden. The next `Enter` opens the inline pane again, identically to today.

### 9.5 "Window" mode for a *single* message

A `MessageWindow` in v1 has its own `MessageWindowViewModel`, which is a thin wrapper around a single `MessageTabViewModel` and a *minimal* message list (for the user to navigate to the next / previous message without going back to the main window). The window can be promoted to a tab in the main window via the window's "Move to main window" command.

```csharp
public sealed class MessageWindowViewModel : ObservableObject
{
    public MessageTabViewModel ActiveTab { get; }
    public ObservableCollection<MailMessageSummary> MessageList { get; } = new();
    public MailMessageSummary? SelectedMessage;
    [ObservableProperty] private bool _isLoading;
    public event Action<MessageWindowViewModel>? RequestClose;
    [RelayCommand] private void Close() => RequestClose?.Invoke(this);
    [RelayCommand] private void MoveToMainWindow();
    [RelayCommand] private void NextMessage() { ... }
    [RelayCommand] private void PreviousMessage() { ... }
}
```

`MessageList` is populated from the same folder as the originating message. Loading the previous / next message replaces `ActiveTab`'s `MessageDetail` (re-fetched via the same path) and updates the tab title and window title.

---

## 10. Views — `MainWindow` restructure & `MessageTabContent`

### 10.1 `MainWindow.xaml` change

The content grid is wrapped in a `TabControl` whose `ItemsSource` is `OpenTabs` and whose `SelectedItem` is `ActiveTab`. The existing inline reading pane lives in a `ContentControl` that is visible only when `OpenTabs.Count == 0` AND `IsMessageOpen` AND `MessageOpenMode == ReadingPane`.

```xml
<!-- New layout: tab strip on top of content, inline pane hidden when tabs are open -->
<DockPanel Grid.Row="2">
    <TabControl x:Name="TabStrip"
                DockPanel.Dock="Top"
                ItemsSource="{Binding OpenTabs}"
                SelectedItem="{Binding ActiveTab}"
                Visibility="{Binding ShowTabStrip, Converter={StaticResource BoolToVisibilityConverter}}"
                ... />
    <ContentControl x:Name="InlineReadingPaneHost"
                    Content="{Binding}"
                    Visibility="{Binding InlineReadingPaneVisible, Converter={StaticResource BoolToVisibilityConverter}}">
        <!-- the existing Grid with MessageBody, header fields, etc. -->
    </ContentControl>
    <Grid x:Name="MessagePane">
        <!-- message list / group trees as today -->
    </Grid>
</DockPanel>
```

`InlineReadingPaneVisible` is a new computed property on `MainViewModel`:

```csharp
public bool InlineReadingPaneVisible =>
    MessageOpenMode == MessageOpenMode.ReadingPane
    && OpenTabs.Count == 0;
```

### 10.2 `MessageTabContent.xaml` (new UserControl)

This is a *near-verbatim* copy of the existing reading-pane XAML grid, parameterised by `MessageTabViewModel`. The `WebView2` host is renamed `MessageBody`. The Escape handler posts a `RequestClose` to the VM (the tab strip then closes the tab); if the tab is the last one and the mode is `Reading Pane`, control falls through to the existing `CloseReadingPane` path.

```xml
<UserControl x:Class="QuickMail.Views.MessageTabContent"
             ...>
    <Grid>
        <!-- Header fields bound to MessageTabViewModel.Detail.* -->
        <DockPanel>
            <StackPanel DockPanel.Dock="Top" ...>
                <TextBlock Text="{Binding Detail.FromDisplay}" ... />
                <TextBlock Text="{Binding Detail.Subject}" ... />
                <!-- ... -->
            </StackPanel>
            <wv2:WebView2 x:Name="MessageBody" ... />
        </DockPanel>
    </Grid>
</UserControl>
```

### 10.3 `TabItem` template & style

The tab header template is a small `DataTemplate` (in `MainWindow.xaml` resources or a dedicated resource dictionary) that contains the icon, title, and close button.

```xml
<DataTemplate x:Key="TabHeaderTemplate" DataType="{x:Type vm:TabSessionViewModel}">
    <StackPanel Orientation="Horizontal" Spacing="6">
        <Image Source="{Binding IconSource}" Width="16" Height="16" />
        <TextBlock Text="{Binding Title}" MaxWidth="240" TextTrimming="CharacterEllipsis" />
        <Button Content="✕"
                Width="18" Height="18"
                Padding="0" Margin="4,0,0,0"
                AutomationProperties.Name="Close tab"
                Click="TabCloseButton_Click"
                Tag="{Binding}" />
    </StackPanel>
</DataTemplate>
```

`TabCloseButton_Click` lives in `MainWindow.xaml.cs` (UI concern: routing the click to the right `CloseTab` call). It uses the `Button.Tag` to identify the VM, *not* a code-behind search of the visual tree (defensive against UI-tree races).

The title is bound to `Title` and updates reactively (debounced 200ms in the VM) so the tab strip doesn't churn on every keystroke in the subject field of a draft.

### 10.4 Drag-to-reorder

Attached to `TabStrip` in `MainWindow.xaml.cs`:

```csharp
private Point? _tabDragStart;
private TabSessionViewModel? _tabDragSource;

private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (e.OriginalSource is DependencyObject d
        && FindAncestor<TabItem>(d) is TabItem ti
        && ti.DataContext is TabSessionViewModel vm)
    {
        _tabDragStart  = e.GetPosition(TabStrip);
        _tabDragSource = vm;
    }
}

private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
{
    if (_tabDragSource is null || _tabDragStart is null) return;
    if (e.LeftButton != MouseButtonState.Pressed) { ResetDrag(); return; }
    if ((e.GetPosition(TabStrip) - _tabDragStart.Value).Length < SystemParameters.MinimumHorizontalDragDistance) return;
    DragDrop.DoDragDrop(_tabDragSource, _tabDragSource, DragDropEffects.Move);
    ResetDrag();
}

private void TabStrip_Drop(object sender, DragEventArgs e)
{
    if (_tabDragSource is null) return;
    if (e.OriginalSource is DependencyObject d
        && FindAncestor<TabItem>(d) is TabItem target
        && target.DataContext is TabSessionViewModel targetVm
        && !ReferenceEquals(targetVm, _tabDragSource))
    {
        var from = _vm.OpenTabs.IndexOf(_tabDragSource);
        var to   = _vm.OpenTabs.IndexOf(targetVm);
        _vm.MoveTab(from, to);
    }
    ResetDrag();
}
```

`FindAncestor<T>` is the same `IsDescendantOf` helper that already exists in `MainWindow.xaml.cs` (re-use, do not duplicate).

### 10.5 Tab Properties (Alt+Enter on a tab header)

Reuses the existing `PropertiesWindow` + `PropertiesViewModel` pipeline. A new `TabPropertiesBuilder.Build(tab)` static function returns `(title, sections)`, following the same pattern as `MessagePropertiesBuilder`, `FolderPropertiesBuilder`, etc. (see [CLAUDE.md §Architecture](CLAUDE.md) for the pattern).

```
Tab Properties
  Tab ID:           <guid>
  Kind:             Message
  Subject:          Re: Project X
  Opened from:      <account label> / Inbox
  Opened at:        <local datetime>
  Read state:       Read / Unread
  Has attachments:  Yes / No
```

`isAvailable` for the `view.showProperties` command is extended to include "a tab header has focus" so Alt+Enter works on tabs.

### 10.6 `MessageWindow.xaml` (new top-level Window)

A minimal shell. Reuses the menu, toolbar, status bar from `MainWindow` via shared resources / `MergedDictionaries`. Owns a single `MessageWindowViewModel` and a single tab strip. The left folder / account pane is hidden by default; a "Show folders" toggle reveals a slim folder tree (read-only for v1 — selecting a folder switches the window's message list source).

```xml
<Window x:Class="QuickMail.Views.MessageWindow"
        Title="{Binding WindowTitle}"
        Height="600" Width="720">
    <DockPanel>
        <Menu DockPanel.Dock="Top">... minimal ...</Menu>
        <TabControl ItemsSource="{Binding OpenTabs}" ... />
    </DockPanel>
</Window>
```

The window has a single tab initially (the message the user opened). A "+" button at the right end of the tab strip opens the current folder's next message in a new tab in this window.

### 10.7 `TabListWindow.xaml` (new modal overlay)

A small `Window` (not a `Popup` — `Popup` cannot be a parent for keyboard focus restoration reliably) with:

- A `ListBox` of `OpenTabs` with `DisplayMemberPath = "Title"`.
- A `TextBlock` showing the current count ("Tab N of M").
- Buttons: *Activate* (Enter), *Close* (Delete on selected row), *Cancel* (Escape).

Subscribed events from `MainViewModel.OpenTabs` keep the list in sync. The window restores focus to the previously-active element on close (the existing palette pattern — see `OpenCommandPalette` in `MainWindow.xaml.cs`).

---

## 11. Compose & Address Book windows as tab hosts

### 11.1 Compose window title

`ComposeWindow.xaml:11` changes from `Title="Compose Message"` to:

```xml
Title="{Binding WindowTitle}"
```

`ComposeViewModel` gets:

```csharp
public string WindowTitle => ComposeKind switch
{
    ComposeKind.NewMessage    => "Compose",
    ComposeKind.Reply         => "Reply",
    ComposeKind.ReplyAll      => "Reply All",
    ComposeKind.Forward       => "Forward",
    ComposeKind.EditDraft     => "Draft",
    ComposeKind.NewDraft      => "Draft",
    ComposeKind.EditTemplate  => "Edit Template",
    _                         => "Compose",
} + " — " + (string.IsNullOrWhiteSpace(Subject) ? "Untitled" : Subject);

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(WindowTitle))]
private string _subject = string.Empty;
```

The `Subject` change handler is debounced 200ms via the existing `DispatcherTimer` pattern (see `QueueStatusAnnounce` in `MainWindow.xaml.cs`) so the title doesn't flicker on every keystroke.

The window's taskbar entry uses the same title; Windows collapses the taskbar entries to the application icon + tooltip, so the dynamic title is what users actually see on hover.

### 11.2 Why Compose is *not* a tab in v1

Compose is a discrete "task" (write a message → send / save / discard) that has its own state machine and its own set of dirty-flag rules. Putting it in a tab would require a `ComposeTabViewModel` with all the `ComposeViewModel` logic; the lift is large and the workflow is already good. The user's request is "title the window with the subject" — that's the 80/20 win. Promoting a compose window *to* a tab (and demoting a tab *to* a compose window) is in v2.

### 11.3 Address Book tab mode

When `OpenAddressBookInTab` is on, the existing `AddressBookWindow` is replaced by a tab in `MainWindow` whose content is the *same* `AddressBookViewModel`. The tab's title is `"Address Book"` (no per-tab disambiguation needed; there's only ever one). The user can drag the tab to a new `MessageWindow` via the "Detach" context-menu item, which closes the tab and opens a standalone `AddressBookWindow` — reusing the existing `AddressBookWindow` code-behind verbatim.

The conversion is one method:

```csharp
private void ConvertAddressBookToTab()
{
    if (_addressBookWindow is null || !_addressBookWindow.IsVisible) return;
    _addressBookWindow.Hide();
    _vm.OpenAddressBookTabRequested?.Invoke();
}

private void ConvertAddressBookTabToWindow()
{
    if (_vm.OpenTabs.FirstOrDefault(t => t is AddressBookTabViewModel) is not AddressBookTabViewModel tab) return;
    _vm.CloseTab(tab);
    _addressBookWindow = new AddressBookWindow(_addressBookVm) { Owner = this };
    _addressBookWindow.Show();
}
```

### 11.4 Group Manager / Rules Manager / View Manager as tabs

Same pattern as Address Book. Each has an existing `XxxViewModel` and `XxxWindow`; we add an `XxxTabViewModel : TabSessionViewModel` that wraps the VM and a `MainWindow` event handler that hosts it as a tab.

`XxxTabViewModel` exposes the same `Commands` as the original VM (delegated), so no command wiring is duplicated.

### 11.5 Convert window ↔ tab

A single surface can flip between modes. The user picks `Window → Move to main window` (closes the standalone window, opens a tab) or `Tab → Open in new window` (closes the tab, opens a standalone window). Both paths share a single helper:

```csharp
private void RepositionSurface<TVm, TWindow>(TVm vm, bool toTab)
    where TVm : TabSessionViewModel
    where TWindow : Window, new()
{
    if (toTab) { /* close window, open tab */ }
    else        { /* close tab, open window */ }
}
```

The helper uses a small `IDisposable` token on each VM to track "is this currently hosted in a tab or a window" so the conversion is idempotent.

---

## 12. Implementation Phases

Each phase is shippable in isolation. Phases 1 and 2 are required for v1; the others are stretch.

### Phase 1 — Tab infrastructure (no behaviour change yet)

- Add `TabSessionModel`, `TabSessionViewModel`, `MessageTabViewModel`.
- Extract the reading pane into `MessageTabContent.xaml(.cs)`.
- Add `MainWindow.OpenTabs` / `ActiveTab` / `ShowTabStrip` (all hidden by default).
- Add tab commands (`ActivateNextTab`, `CloseActiveTab`, `MoveTabLeft/Right`) to the registry (no defaults — they only fire when a tab is focused).
- Verify: no behaviour change, all existing tests pass.

### Phase 2 — Settings + first mode (Tab)

- Add `WindowingPreferences` to `ConfigModel`.
- Add a "Windowing" page to the Settings dialog with the new settings.
- Add the `MessageOpenMode = Tab` code path. `ReadingPane` (default) and `Window` are no-ops for now.
- Add tab list overlay, drag-to-reorder, Alt+Enter on a tab.
- Add `Ctrl+Tab`, `Ctrl+Shift+Tab`, `Ctrl+1..9`, `Ctrl+W`, `Ctrl+Shift+W` to the registry.
- Add `Ctrl+Enter` to open a message in a new window regardless of the mode.
- Add Compose window dynamic title.
- Tests: tab add/remove/reorder, mode resolution, focus restoration, drag-to-reorder, tab list overlay keyboard, Ctrl+W with unsaved draft prompt.

### Phase 3 — Address Book / Group / Rules / View as tabs (stretch)

- `XxxTabViewModel` for each.
- `MainWindow` event handlers for tab vs window hosting.
- `ConvertWindowToTab` / `ConvertTabToWindow` helpers.
- Tests: each surface opens in the requested mode.

### Phase 4 — `MessageWindow` (stretch)

- New `MessageWindow.xaml` with a single tab strip and minimal shell.
- `MessageWindowViewModel`.
- `OpenNewMessageWindowRequested` handler in `App.xaml.cs` to spin up the window.
- `NextMessage` / `PreviousMessage` for in-window navigation.
- Tests: window add/remove, focus restoration, multi-window focus invariant.

### Phase 5 — Persistence (v2, **not in v1**)

- Persist open tabs across restarts when `TabsRememberAcrossRestart` is on.
- Save tabs in `tabs.json` (atomic write, like the other JSON files).
- Re-open tabs in a `MainWindow.OnLoaded` post-step.
- Resilient to missing messages: a tab whose message no longer exists in any account shows a "Message not found" placeholder and is closed on next open.

### Phase 6 — Drag-from-folder-tree to a `MessageWindow` (v2, **not in v1**)

- Drag a folder node from the main window's folder tree, drop on a `MessageWindow`'s "Show folders" pane, set the window's active folder.
- Useful for "I have a window for Inbox, I want a second window for Important".

### Phase 7 — Splits (v3+)

- A docking framework (Avalon Dock) splits the main content area into multiple tab strips side-by-side or top-bottom.
- Out of scope for v1, v2, and most of v3.

---

## 13. Command Registry & Shortcuts

Every new shortcut is registered in `MainWindow.OnLoaded` (and in `MessageWindow.OnLoaded` for window-only shortcuts). The shortcut table below adds to the existing list in [CLAUDE.md §Keyboard Shortcuts](CLAUDE.md).

| Key | Command ID | Category | Title | Default location | Notes |
|---|---|---|---|---|---|
| `Ctrl+Alt+1` | `view.focusAccounts` | View | Focus Account List | main | **Replaces the old `Ctrl+1` hardcoded pane jump.** See §6.6. |
| `Ctrl+Alt+2` | `view.focusFolders` | View | Focus Folder Tree | main | **Replaces the old `Ctrl+2` (and `Ctrl+Y` alias).** Migrated from hardcoded. |
| `Ctrl+Alt+3` | `view.focusMessages` | View | Focus Message List | main | **Replaces the old `Ctrl+3` hardcoded pane jump.** Routes to the right panel (Message list, Conversation tree, Sender group tree, or To group tree) based on `ViewMode`. |
| `Ctrl+Tab` | `tabs.next` | View | Next Tab | main, message windows | Disabled when `OpenTabs.Count <= 1`. |
| `Ctrl+Shift+Tab` | `tabs.previous` | View | Previous Tab | main, message windows | |
| `Ctrl+1`…`Ctrl+8` | `tabs.activate{N}` | View | Activate Tab N | main, message windows | N is 1-indexed; 8 is the cap. |
| `Ctrl+9` | `tabs.last` | View | Last Tab | main, message windows | |
| `Ctrl+W` | `tabs.close` | Mail | Close Tab | main, message windows | Prompts on dirty. |
| `Ctrl+Shift+W` | `tabs.closeOthers` | Mail | Close Other Tabs | main, message windows | |
| `Ctrl+Shift+BackQuote` | `tabs.list` | View | Open Tab List | main, message windows | The default chord for the tab list overlay. |
| `Ctrl+Shift+Left` | `tabs.moveLeft` | View | Move Tab Left | main, message windows | |
| `Ctrl+Shift+Right` | `tabs.moveRight` | View | Move Tab Right | main, message windows | |
| `Ctrl+Enter` | `mail.openInNewWindow` | Mail | Open Message in New Window | main | Always opens a new `MessageWindow`, regardless of mode. |
| `Ctrl+Shift+Enter` | `mail.openInNewTab` | Mail | Open Message in New Tab | main | Force a new tab, regardless of mode. |
| *(tab header context)* | `tabs.promoteToWindow` | View | Open in New Window | tab context menu | Detach the active tab into a new `MessageWindow`. |
| *(tab header context)* | `tabs.showProperties` | View | Tab Properties | tab context menu | Routes to `PropertiesWindow`. Already covered by the existing `view.showProperties` when the tab header has focus. |

The `IsAvailable` delegate on each tab command checks `OpenTabs.Count` and `ActiveTab` so the shortcuts are inert when no tab is active. The exception is `Ctrl+Tab` and `Ctrl+W` which are still bound but no-op when the tab list is empty.

Compose window additions: the existing `Ctrl+Enter` "Send" chord is preserved and is **not** clobbered by the new `mail.openInNewWindow` (compose-window's `CommandRegistry` is separate — see [ComposeWindow.xaml.cs:1848](QuickMail/Views/ComposeWindow.xaml.cs)).

The new shortcuts show up in the **Command Palette** (`Ctrl+Shift+P`) and the **Keyboard customisations** dialog automatically via the existing registry plumbing.

---

## 14. Accessibility (WCAG 2.2)

### 14.1 Tab strip

- `TabControl` exposes `TabItem` UIA elements with the right role (`Tab`), so screen readers announce "Tab 1 of 3: subject A. Tab control." automatically.
- Each tab's `AutomationProperties.Name` is set to `"{Title}, {Kind} ({position} of {count})"`. Example: `"Re: Project X, message, 2 of 3"`.
- The close button is a separate accessible element with `AutomationProperties.Name = "Close {title}"`. Hitting it announces "Closed tab Re: Project X. 2 tabs remaining." via `AccessibilityHelper.Announce(category: Result)`.
- `TabNavigation=Local` on the tab strip so Tab moves *into* the active tab, not across tabs.

### 14.2 Tab list overlay

- A11y name: "Open tabs list. {count} tabs."
- Each row: "Tab {N} of {count}, {title}, {kind}. Press Enter to activate, Delete to close."
- ARIA: the listbox is `SingleSelect`; arrow keys move; Enter activates; Delete closes (with confirmation if dirty); Escape closes the overlay.
- A small text indicator at the bottom shows the current row: "Row {N} of {count}".

### 14.3 Reading-pane focus

The existing `FocusMessageBodyAsync` helper in `MessageTabContent.xaml.cs` is reused for tabs. The focus chain (header → body → Escape → return) is identical.

### 14.4 Compose window title

- The dynamic title is read by screen readers via the standard window UIA `Name` property.
- On subject change, the title updates; screen readers are not re-announced (window titles are not announced events). This is intentional: re-announcing on every keystroke would be unbearable.

### 14.5 Screen reader announcements

New announcements, all routed through `AccessibilityHelper.Announce(category:)`:

| Event | Category | Default text |
|---|---|---|
| Tab opened | `Result` | `"Opened tab: {title}. {N} tabs open."` |
| Tab closed | `Result` | `"Closed tab: {title}. {N} tabs remaining."` |
| Tab activated (via Ctrl+Tab) | `Result` | `"Tab {N} of {count}: {title}."` |
| Tab reordered | `Result` | `"Tab moved to position {N}."` |
| Promote to window | `Result` | `"Tab opened in new window."` |
| Demote to tab | `Result` | `"Tab moved to main window."` |
| Tab list opened | `Hint` | `"Open tabs list. Use arrow keys to navigate."` |
| Tab list closed | `Status` | (none — silent) |
| Compose window subject typed | (none) | subject changes silently to avoid chatter |
| Draft prompt on close | `Result` | `"This draft has unsaved changes. Discard?"` (followed by standard MessageBox) |

Existing settings (`AnnounceHints`, `AnnounceStatus`, `AnnounceResults`) gate these consistently.

### 14.6 PII in tab titles

Tab titles can contain message subjects, which may include PII. Existing logging rules already forbid PII at default log levels. We add a *UI-level* rule: tab titles are never logged. `LogService.Debug($"Tab open {tab.Model.Title}")` is replaced with `LogService.Debug($"Tab open {tab.Model.Id} kind={tab.Model.Kind}")` — the Id is a Guid, the Kind is an enum.

### 14.7 Color and contrast

- Tab strip uses the system tab control theme (Windows 10/11 default), which meets contrast requirements out of the box.
- Close button: ≥ 16×16 hit target, with a 1px border at rest and a 2px border on focus, matching the rest of the app's focus indicator style.
- Drag-to-reorder shows a 2px accent-coloured insertion indicator; the indicator's contrast is checked against the tab strip background.

### 14.8 Keyboard-only parity

Every action achievable with a mouse is also achievable with a keyboard:

| Mouse | Keyboard |
|---|---|
| Click on a tab | `Ctrl+{N}` (1–8) or `Ctrl+9` for last |
| Click `x` on a tab | `Ctrl+W` (active) or Delete in tab list |
| Drag a tab | `Ctrl+Shift+Left` / `Right` |
| Click on a tab list row | arrow keys, then Enter |
| Click a tab's "Open in new window" context item | `Ctrl+Enter` on the message, or "Move to window" command (assigned no default chord; findable via Command Palette) |
| Click outside the tab list to dismiss | Escape |

---

## 15. Success Metrics

| Metric | Target | Measurement |
|---|---|---|
| Reading-pane-mode regression | Zero (all v0.6.x tests pass unchanged) | `dotnet test --filter "FullyQualifiedName~ReadingPane"` (after adding the filter — see §19) |
| Tab-mode first-open latency | ≤ 250ms (cached) / ≤ 1500ms (cold IMAP) | `Stopwatch` instrumentation in `MessageTabViewModel.LoadAsync` |
| Tab cycle latency (Ctrl+Tab) | ≤ 50ms | `Stopwatch` in `MainViewModel.ActivateNextTab` |
| Tabs open per session | Median 3, p95 8 | optional telemetry (gated on `/debug`) |
| Windowed-mode usage | < 5% of sessions (most users prefer Tab) | optional telemetry |
| Crash count from tab ↔ window conversion | Zero | UI test that performs 100 convert cycles |
| Screen reader tests pass | 100% | `XamlParseTests` (new) + manual NVDA/JAWS pass |

Telemetry is opt-in only (a `SendAnonymousUsageStats` setting, **not** added in v1) — the targets above are measured locally during dogfood.

---

## 16. Open Questions & Risks

| Question | Risk | Decision / Next step |
|---|---|---|
| ~~**Tab list chord.** Ctrl+Shift+Tab is "previous tab" in browsers, and Ctrl+Alt+Tab is "all task switcher" in Windows. We chose `Ctrl+Shift+BackQuote` (VS Code style) and `Ctrl+Alt+Tab` as alternate.~~ **RESOLVED:** Tab list chord is `Ctrl+Shift+BackQuote` (VS Code style). Document in `USERGUIDE.md`. | ~~Both chords are discoverable in the Command Palette; users who remap can pick.~~ | Closed — see also §6.6 *Tab keyboard model*. |
| **Ctrl+1–3 (pane jumps) vs Ctrl+1–8 (tab jumps).** The two chords overlap on the first three digit rows. Ctrl+1–3 are hardcoded pane jumps in `MainWindow.xaml.cs` (Account list, Folder tree, Message list) and listed as such in `CLAUDE.md`. **RESOLVED:** pane jumps move to **`Ctrl+Alt+1–3`**; `Ctrl+1–8` becomes "jump to tab N" (with `Ctrl+9` = last tab). Rationale and migration in §6.6. | Existing users lose the Ctrl+1–3 muscle memory. | Document the change in `USERGUIDE.md`, the What's New dialog, and the in-app tutorial. The Command Palette surfaces both commands under their new names. |
| **Splits.** Splits feel like a natural extension. | Adds a docking dependency (Avalon Dock is ~500KB and a substantial API surface). | Defer to v3. v1's "open in window" gives a similar workflow with a single monitor. |
| **Persistence.** v1 always resets tabs at restart. Some users will be annoyed. | A persisted tab can outlive its message (deleted, moved to another folder on a different machine). | v1 shows a "Message not found" placeholder; v2 saves/loads `tabs.json` with full schema migration. |
| **Two windows, two IMAP pools.** Each window uses its own `IMailService` lease. With `MaxImapConnectionsPerAccount = 6` (default) and two windows, the effective cap is 12 — within server limits for most providers. | Outlook/Office365 has a 5-connection cap; two windows of Office365 could trip the limit. | Document the constraint. v1 does not auto-pause background sync in secondary windows; v2 could add a "share IMAP leases between windows" service. |
| **Tabbing a draft.** `OpenDraftsInWindow = true` (default) keeps compose as a window. Users who want a draft tab must explicitly opt in. | Discoverability of the setting. | The setting's label is "Open drafts in main window tabs" with help text: "When off, drafts open in standalone windows. The window title is the subject so the taskbar disambiguates." |
| **Windowed mode vs `Ctrl+Enter` overlap.** `Ctrl+Enter` is "send" in compose. | They live in different windows' registries. | Already handled — the compose registry is separate. |
| **Tab-close confirmation.** A draft tab being closed without confirmation is a data-loss risk. | Users with many drafts may want a "do not ask again" option. | v1: confirm by default (`ConfirmCloseTabWithUnsaved = true`); user can opt out. Per-tab dirty state already exists. |
| **Reading pane when tabs are open.** When `OpenTabs.Count > 0`, the inline reading pane is hidden. Some users may want both. | The "two readings of the same message" state is confusing. | Document the choice. v2 could allow a "split: tab + reading pane". |
| **Tab strip in `MessageWindow`.** A `MessageWindow` with one tab looks like a single-message window. | Visually the same. | When `OpenTabs.Count > 1` in a `MessageWindow`, the tab strip is visible. When `== 1`, the strip is hidden (the title bar carries the title). |
| **Modifying `MessageList_MouseLeftButtonUp`.** The existing handler calls `SelectMessageCommand` directly. | The new mode-aware dispatch is a fork in the handler. | Wrap the existing call in a small `HandleOpenMessageAsync(summary, gesture)` that consults `MessageOpenMode`. The default path (ReadingPane) is identical to today's behaviour. |
| **Per-window focus restoration when the user closes a window.** Today's `_paneIndexBeforeDeactivation` is per-`MainWindow`. | Each `MessageWindow` needs its own. | Add a `WindowFocusState` per window; move the field from `MainWindow` to a `WindowFocusState` record. |

---

## 17. Files to Create

| File | Purpose |
|---|---|
| `QuickMail/Models/WindowingPreferences.cs` | Settings for tab/window behaviour. |
| `QuickMail/Models/TabSessionModel.cs` | In-memory tab identity. |
| `QuickMail/ViewModels/TabSessionViewModel.cs` | Abstract base. |
| `QuickMail/ViewModels/MessageTabViewModel.cs` | Owns `MailMessageDetail` for a tab. |
| `QuickMail/ViewModels/MessageWindowViewModel.cs` | Owns a `MessageWindow`'s state. |
| `QuickMail/ViewModels/AddressBookTabViewModel.cs` | (Phase 3) wraps `AddressBookViewModel` for tab hosting. |
| `QuickMail/ViewModels/GroupManagerTabViewModel.cs` | (Phase 3) wraps `GroupManagerViewModel`. |
| `QuickMail/ViewModels/RulesManagerTabViewModel.cs` | (Phase 3) wraps `RulesManagerViewModel`. |
| `QuickMail/ViewModels/ViewManagerTabViewModel.cs` | (Phase 3) wraps `ViewManagerViewModel`. |
| `QuickMail/Views/MessageTabContent.xaml(.cs)` | The reading pane extracted into a `UserControl`. |
| `QuickMail/Views/MessageWindow.xaml(.cs)` | New top-level window for `Window` mode. |
| `QuickMail/Views/TabListWindow.xaml(.cs)` | New modal list overlay. |
| `QuickMail/Helpers/TabPropertiesBuilder.cs` | Static `(title, sections[])` builder for `PropertiesWindow`. |
| `QuickMail.Tests/TabSessionModelTests.cs` | Tab model invariants. |
| `QuickMail.Tests/MessageTabViewModelTests.cs` | Load and title-update behaviour. |
| `QuickMail.Tests/MainViewModelTabTests.cs` | Open/close/reorder/activate logic. |
| `QuickMail.Tests/WindowingPreferencesTests.cs` | Default values, INI round-trip. |
| `QuickMail.Tests/MessageWindowViewModelTests.cs` | Window VM behaviour. |
| `QuickMail.Tests/TabListWindowTests.cs` | Overlay keyboard model. |
| `QuickMail.Tests/AddressBookTabHostTests.cs` | Phase 3 — address book in tab. |
| `QuickMail.Tests/ComposeWindowTitleTests.cs` | Title format, debouncing. |
| `QuickMail.Tests/XamlParseTests.MessageTabContent.cs` | XAML loads. |

---

## 18. Files to Modify

| File | Change |
|---|---|
| `QuickMail/Models/ConfigModel.cs` | Add `WindowingPreferences Windowing` property. |
| `QuickMail/Services/ConfigService.cs` | Add `[windowing]` section parser / writer. (Follow the existing `BuildFileText` pattern; see [ConfigService.cs:69-99](QuickMail/Services/ConfigService.cs) for the comment.) |
| `QuickMail/Views/SettingsDialog.xaml(.cs)` | Add a "Windowing" page with the new settings. |
| `QuickMail/ViewModels/MainViewModel.cs` | Add `OpenTabs`, `ActiveTab`, `ShowTabStrip`, `MessageOpenMode`, the tab commands, the `HandleOpenMessageAsync` decision tree, and the new events. |
| `QuickMail/Views/MainWindow.xaml` | Add the `TabControl` and re-layout the content grid (see §10.1). |
| `QuickMail/Views/MainWindow.xaml.cs` | Subscribe to the new VM events; route keyboard shortcuts to the new commands; extract the reading pane; wire drag-to-reorder. |
| `QuickMail/Views/ComposeWindow.xaml` | Change `Title` to `"{Binding WindowTitle}"`. |
| `QuickMail/ViewModels/ComposeViewModel.cs` | Add `WindowTitle`, `Subject` (if not already), the debounced title refresh, and the `ComposeKind` enum. |
| `QuickMail/Views/AddressBookWindow.xaml.cs` | Add a "Pin as tab" checkbox to the title bar (visible only when the global setting is configurable per-window). |
| `QuickMail/ViewModels/AddressBookViewModel.cs` | No change — same VM is reused when hosted as a tab. |
| `QuickMail/Views/MessageList_MouseLeftButtonUp` (in MainWindow.xaml.cs) | Route to `HandleOpenMessageAsync(summary, OpenMessageGesture.Click)`. |
| `QuickMail/Views/MessageList_PreviewKeyDown` (in MainWindow.xaml.cs) | Enter / Shift+Enter / Ctrl+Enter routes to the same dispatch. |
| `QuickMail.Tests/StubServices.cs` | Add `IWindowingPreferences` accessor (or just `IConfigService` mock returns the new section). |
| `docs/USERGUIDE.md` | New "Tabs & windows" section. |
| `CHANGELOG.md` | New entry under the next version. |

---

## 19. Tests to Add

| Test | Verifies |
|---|---|
| `WindowingPreferences_DefaultValues_AreSensible` | `MessageOpenMode = ReadingPane`, all `OpenXxxInTab = false`, `OpenDraftsInWindow = true`, `ConfirmCloseTabWithUnsaved = true`. |
| `WindowingPreferences_IniRoundTrip` | Write → read → write produces identical INI. |
| `MessageOpenMode_EnumParsing_CaseInsensitive` | `"tab"`, `"Tab"`, `"TAB"` all parse to `MessageOpenMode.Tab`. |
| `MessageOpenMode_InvalidString_FallsBackToDefault` | `"foo"` → `ReadingPane`. |
| `TabSessionModel_DefaultId_IsUnique` | 100 instances have 100 distinct Ids. |
| `MessageTabViewModel_LoadAsync_SetsIsMessageOpen` | Loading a detail sets `IsMessageOpen = true` and raises `IsMessageOpenChanged`. |
| `MessageTabViewModel_LoadAsync_Cancelled_DoesNotMutate` | Cancellation token cancelled → no state change. |
| `MessageTabViewModel_Title_TruncatesAtConfiguredLength` | Subject > 240 chars → `Title` is truncated with `…`. |
| `MessageTabViewModel_Title_EmptySubject_ShowsUntitled` | Subject is whitespace → `Title = "Untitled"`. |
| `MainViewModel_OpenTab_AddsAndActivates` | `OpenMessageTab` returns the new tab, it's in `OpenTabs`, and `ActiveTab` is the new tab. |
| `MainViewModel_OpenTab_Duplicate_DoesNotOpenTwice` | Opening the same `UniqueId` twice activates the existing tab; `OpenTabs.Count` is 1. |
| `MainViewModel_CloseTab_RemovesAndActivatesNext` | Closing the active tab activates the next tab (or `null` if last). |
| `MainViewModel_CloseTab_OtherTabSelected_DoesNotChangeActive` | Closing a non-active tab does not change `ActiveTab`. |
| `MainViewModel_ActivateNextTab_WrapsAround` | Last tab → Ctrl+Tab → first tab. |
| `MainViewModel_ActivatePrevTab_WrapsAround` | First tab → Ctrl+Shift+Tab → last tab. |
| `MainViewModel_ActivateTabByIndex_OutOfRange_NoOp` | `ActivateTabByIndex(99)` when 3 tabs open → no change. |
| `MainViewModel_MoveTab_ReordersCollection` | Move from index 0 to index 2 → `OpenTabs[2]` is the moved tab. |
| `MainViewModel_MoveTab_SameIndex_NoOp` | Move from index 1 to index 1 → no change. |
| `MainViewModel_PromoteActiveTabToWindow_RaisesEvent` | Promoting raises `TabPromoteToWindowRequested` with the active tab's payload. |
| `MainViewModel_ShowTabStrip_True_WhenTabsOpen` | `ShowTabStrip` is `OpenTabs.Count > 0`. |
| `MainViewModel_ShowTabStrip_False_WhenTabsClosed` | After closing the last tab, `ShowTabStrip = false`. |
| `MainViewModel_HandleOpenMessage_ReadingPane_OpensInlinePane` | Mode = `ReadingPane` → `IsMessageOpen` becomes `true` and `OpenTabs` is empty. |
| `MainViewModel_HandleOpenMessage_Tab_OpensTab` | Mode = `Tab` → `OpenTabs.Count` increments; the active tab's `MessageTabViewModel.Summary.UniqueId` matches. |
| `MainViewModel_HandleOpenMessage_Window_RaisesEvent` | Mode = `Window` → `OpenNewMessageWindowRequested` fires. |
| `MainViewModel_HandleOpenMessage_CtrlEnter_AlwaysOpensWindow` | Regardless of mode, Ctrl+Enter → `OpenNewMessageWindowRequested`. |
| `MainViewModel_HandleOpenMessage_DoubleClick_LeavesSourceRowSelected` | The source row's `SelectedIndex` is preserved after the open. |
| `MessageWindowViewModel_NextMessage_ReplacesActiveTab` | Next-message replaces `ActiveTab.Detail` and updates `WindowTitle`. |
| `MessageWindowViewModel_PreviousMessage_AtFirst_DoesNothing` | At index 0, `PreviousMessage` is a no-op. |
| `MessageWindowViewModel_Close_RaisesEvent` | `RequestClose` event fires. |
| `TabListWindow_ArrowKeys_MoveSelection` | Down / Up arrow moves the highlighted row. |
| `TabListWindow_Enter_ActivatesAndCloses` | Enter on a row sets `MainViewModel.ActiveTab` and the window returns `true` from `ShowDialog`. |
| `TabListWindow_Escape_ClosesWithoutChanging` | Escape returns `false`; `ActiveTab` is unchanged. |
| `TabListWindow_Delete_ClosesSelectedTab` | Delete on a row closes the tab; window stays open. |
| `ComposeWindowTitle_BlankSubject_ShowsUntitled` | `Subject = ""` → `WindowTitle = "Compose — Untitled"`. |
| `ComposeWindowTitle_NonBlankSubject_IncludesSubject` | `Subject = "Re: Test"` → `WindowTitle = "Reply — Re: Test"`. |
| `ComposeWindowTitle_DebounceDoesNotUpdateEveryKeystroke` | A burst of 10 subject changes within 100ms updates the title only once. |
| `ComposeWindowTitle_ReplyKind_ShowsReply` | `ComposeKind = Reply` → prefix is `"Reply"`. |
| `ReadingPaneMode_ExistingBehaviourPreserved` | All 12 existing reading-pane tests still pass without modification. |
| `PaneJump_ChordIsCtrlAlt1_NotCtrl1` | The `view.focusAccounts` command is registered with `Key.D1 + ModifierKeys.Control + ModifierKeys.Alt`. If a future change binds the old `Ctrl+1` chord the test fails — preventing silent regression of the pane-jump shift. |
| `PaneJump_ChordIsCtrlAlt2_NotCtrl2` | The `view.focusFolders` command is registered with `Key.D2 + ModifierKeys.Control + ModifierKeys.Alt`. (No `Ctrl+Y` alias is registered any more — verified by the same test.) |
| `PaneJump_ChordIsCtrlAlt3_NotCtrl3` | The `view.focusMessages` command is registered with `Key.D3 + ModifierKeys.Control + ModifierKeys.Alt`. |
| `TabActivateN_ChordIsCtrl1_NotCtrlAlt1` | The `tabs.activate1` command is registered with `Key.D1 + ModifierKeys.Control` (no Alt). Pins the inverse: future agents must not let the two conflict again. |
| `TabList_ChordIsCtrlShiftBackQuote` | The `tabs.list` command is registered with `Key.OemBackQuote + ModifierKeys.Control + ModifierKeys.Shift`. No alternate chord is registered. |
| `XamlParseTests.MessageTabContent_Loads` | XAML parses without exception. |
| `XamlParseTests.TabListWindow_Loads` | XAML parses without exception. |
| `XamlParseTests.MessageWindow_Loads` | XAML parses without exception. |

A `tests/tabs-and-windows/regression-list.md` file is added (in the test project) listing the existing tests that must pass unchanged, so a future agent can verify the zero-regression goal.

---

## 20. Appendix A — Keyboard Cheat Sheet

A new section in `USERGUIDE.md` (also surfaced in the tutorial overlay and the Keyboard Tutorial ViewModel):

```
PANE JUMPS
  Ctrl+Alt+1             Focus account list
  Ctrl+Alt+2             Focus folder tree
  Ctrl+Alt+3             Focus message list / conversation / sender / to tree
  F6 / Shift+F6          Cycle through the panes (unchanged)

  Note: the previous Ctrl+1 / Ctrl+2 / Ctrl+3 chords (without Alt)
  now mean "jump to tab N". If you haven't enabled the tab strip
  (Reading mode = Reading Pane), Ctrl+1–3 are inert and Ctrl+Alt+1–3
  remain the only way to jump to a pane by keyboard.

TABS
  Ctrl+Tab               Next tab
  Ctrl+Shift+Tab         Previous tab
  Ctrl+1 .. Ctrl+8       Jump to tab N
  Ctrl+9                 Jump to last tab
  Ctrl+W                 Close tab
  Ctrl+Shift+W           Close other tabs
  Ctrl+Shift+`           Open tab list
  Ctrl+Shift+Left/Right  Reorder active tab
  Alt+Enter on tab       Tab Properties
  Tab header context →   Open in new window / Move to main window

WINDOWS
  Ctrl+Enter on message  Open in new window (any mode)
  Ctrl+Shift+Enter       Open in new tab (any mode)
  Escape                 Close the active window (if no unsaved work)
  Ctrl+Shift+`           Tab list (across all windows of this process)

READING MODE (set in Settings → Windowing)
  Reading Pane (default) — Enter opens in the reading pane
  Tab                    — Enter opens in a new tab
  Window                 — Enter opens in a new window

COMPOSE
  Window title           "<prefix> — <subject or Untitled>"
  Prefixes: Compose, Reply, Reply All, Forward, Draft, Edit Template
```

---

## 21. Appendix B — Tab lifecycle state diagram

```
                        ┌────────────┐
        ┌──────────────►│  No tabs   │◄──────────┐
        │               └────┬───────┘           │
        │                    │                   │
   user picks           user opens          user closes
   "Window"             a tab in            the only
   in settings          Tab mode            remaining
        │                    │                   tab
        │                    ▼                   │
        │            ┌───────────────┐           │
        │   ┌───────►│ Tab 1 active  │           │
        │   │        └──────┬────────┘           │
        │   │               │                    │
        │   │ user opens    │ user closes        │
        │   │ another       │ the active         │
        │   │               │                    │
        │   │               ▼                    │
        │   │        ┌───────────────┐           │
        │   │        │ Tab 2 active  │           │
        │   │        │ (Tab 1 still  │           │
        │   │        │  open, inact.)│           │
        │   │        └───────────────┘           │
        │   │                                    │
        │   └────────────────────────────────────┘
        │
        ▼
   (alt path) ── user "Open in new window" ──► a `MessageWindow`
                                                   │
                                                   │ user "Move to main"
                                                   ▼
                                            back in the main tab strip
```

The `No tabs` state shows the inline reading pane *only* in `MessageOpenMode = ReadingPane`. In `Tab` mode, "No tabs" shows the message list. The setting controls which.

---

## 22. Appendix C — Window-open & close sequences

### C.1 Open a message in a new window (Ctrl+Enter, mode = ReadingPane)

```
User presses Ctrl+Enter on a row
   │
   ▼
MessageList_PreviewKeyDown detects Ctrl+Enter
   │  e.Handled = true
   ▼
HandleOpenMessageAsync(summary, OpenMessageGesture.CtrlEnter)
   │
   ▼
OpenNewMessageWindowRequested.Invoke()
   │
   ▼ (MainWindow code-behind)
OpenMessageWindow(summary)
   │
   ├── builds MessageWindowViewModel
   ├── builds MessageWindow
   ├── MessageWindow.Show()  (non-modal, Owner = this)
   └── vm.RequestClose += OnMessageWindowClosed
   │
   ▼ (user later closes)
OnMessageWindowClosed
   │
   ├── e.Handled logic
   ├── removes the window from _openMessageWindows
   └── (focus restoration) if the closing window was the active
       foreground and the main window is still open, focus goes
       to the message list at the originating row.
```

### C.2 Open a message in a tab (mode = Tab, Enter)

```
User presses Enter on a row
   │
   ▼
MessageList_PreviewKeyDown detects Enter
   │  e.Handled = true
   ▼
HandleOpenMessageAsync(summary, OpenMessageGesture.Enter)
   │
   ▼ (mode = Tab)
OpenMessageTabAsync(summary)
   │
   ├── duplicate check: if a tab with this UniqueId is open,
   │   activate it instead of opening a new one
   ├── build MessageTabViewModel
   ├── OpenTabs.Add(tab)
   ├── ActiveTab = tab
   ├── ShowTabStrip = true
   └── tab.LoadAsync(ct)  ── fires IsMessageOpenChanged
                            ── MessageTabContent renders body
   │
   ▼
OnMessageListFocused (registered in MainWindow.PropChanged)
   │
   └── focus moves to the new tab's content via
       FocusActiveMessageTab(tab) helper
```

### C.3 Close a tab (Ctrl+W, dirty)

```
User presses Ctrl+W on a tab
   │
   ▼
tabs.close command fires
   │  isAvailable: ActiveTab != null && ActiveTab.CanClose
   ▼
CloseActiveTab()
   │
   ├── if ActiveTab is dirty && ConfirmCloseTabWithUnsaved
   │   │
   │   ├── VM raises ConfirmationRequested (existing pattern)
   │   │   View shows MessageBox "Discard unsaved changes?"
   │   │   │
   │   │   ├── Yes → continue
   │   │   └── No  → abort
   │   │
   │   └── continue
   │
   ├── capture index of ActiveTab in OpenTabs
   ├── OpenTabs.Remove(tab)
   ├── activate the next tab (or previous if last)
   ├── ShowTabStrip = OpenTabs.Count > 0
   ├── tab.Dispose() (cleans WebView2 if message tab)
   └── raise TabsChanged event for status bar / window title
```

### C.4 Promote tab to window (tab context menu)

```
User picks "Open in new window" on a tab header
   │
   ▼
tabs.promoteToWindow command fires
   │
   ▼
MainViewModel.PromoteActiveTabToWindow()
   │
   ├── capture the tab to promote
   ├── TabPromoteToWindowRequested.Invoke(tab)
   │
   ▼ (MainWindow code-behind)
OnTabPromoteToWindowRequested(tab)
   │
   ├── OpenTabs.Remove(tab)  ── close the tab here
   ├── build MessageWindow with the same tab VM (no duplication)
   ├── MessageWindow.Show()
   └── wire the new window's "Move to main window" command
       to OpenMessageTabAsync(...)
```

### C.5 Tab list overlay open / close

```
User presses Ctrl+Alt+Tab
   │
   ▼
tabs.listAlt command fires
   │
   ▼
OpenTabList()
   │
   ├── build TabListWindow
   ├── remember previous focus
   ├── ShowDialog()
   └── (dialog returns)
       │
       ├── Enter  → MainViewModel.ActiveTab = selectedTab
       ├── Delete → CloseTab(selectedTab)
       └── Escape → no change
   │
   ▼
restore focus to previous element
   (e.g. the tab strip or the message list, whichever
    had focus before the dialog opened)
```

---

*End of spec.*
