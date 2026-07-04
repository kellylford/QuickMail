# Theming — Usage and Testing Guide

**Status:** Working guide for the `theming` branch. Not yet part of the user guide.
**Spec:** `docs/planning/theming-visual-design-pm-dev-spec.md` (issue #177). Section references below (§7, §9) point there.

This guide has two halves: a short orientation on how theming works, then a structured test pass. The test pass mirrors the spec's acceptance walkthroughs — the keyboard walkthrough in §7 is normative, so anything that doesn't match what you hear or see is a bug, not a follow-up.

---

## Part 1: How to use it

### The short version

- **Settings → Appearance tab** (Ctrl+, then Ctrl+Tab to Appearance, access key Alt+P) — pick a theme, font, text size, and the three vision toggles.
- **Command Palette (Ctrl+Shift+P)** — type "theme" for: **Manage Themes** (the Theme Manager window), **Next Theme** / **Previous Theme** (cycle), and one **Theme: [name]** command per theme (each can be given a shortcut in Settings → Keyboard Shortcuts).
- Everything applies immediately. Nothing requires a restart.

### The themes

| Theme | What it is |
|---|---|
| System (default) | Follows the Windows light/dark app setting. Light → Quill, dark → Quill Dark. |
| Quill | The standard light look: warm off-whites, muted steel-blue accent. |
| Quill Dark | The dark counterpart. |
| Ember / Fjord / Heather | Warm, cool, and muted variations on the light look. |
| (your themes) | Anything duplicated or imported in the Theme Manager. |

### Vision settings (all off by default, all in the Appearance tab)

- **Text size** — 100% to 200%, app-wide, independent of Windows display scaling.
- **Font** — app-wide font override; "(Theme default)" restores the theme's font.
- **Always underline links** — forces underlines on links in message content even when the sender removed them.
- **Thicker keyboard focus indicators** — doubles the focus ring width (2px → 4px).
- **Apply theme colors to message content** — overrides sender-chosen colors/fonts in the reading pane with your theme's. For messages that arrive with unreadable color choices.

### Windows High Contrast

When HC is on, QuickMail has no visual opinion: every color comes from your HC palette, and QuickMail's custom control styling is withdrawn so standard Windows rendering returns. Your theme choice is remembered and comes back when HC turns off. Font and text-size settings continue to apply during HC.

### Theme files

A theme is a plain JSON file in `{profile}\themes\` (one file per theme). Export writes the same JSON as a `.quickmailtheme` file you can move to another machine. To make your own: Theme Manager → select a built-in → **Duplicate** → **Open themes folder** → edit the copy in any text editor. Colors are hex (`#3D5A80`); any color you omit falls back to the built-in Light or Dark theme (whichever the file's `base` names). The full token list is documented in the user guide's Appearance section and in `QuickMail/Theming/ThemeKeys.cs`.

The configured theme also lives in `config.ini` as `AppearanceThemeId` (with the other five `Appearance*` keys), so it can be hand-edited while the app is closed.

---

## Part 2: Test pass

### Setup

Use an isolated profile so nothing touches your real one, and turn on debug logging in case something needs diagnosing:

```bat
QuickMail.exe --profileDir C:\temp\qm-theme-test /debug
```

For the "default experience" scenario below, use a **fresh** (empty) profile directory. For the rest, any profile with an account and some mail is more useful. Run the whole pass keyboard-only; if focus is ever lost or stranded, that is a bug.

### A. Default experience (fresh profile)

1. Launch with a fresh profile, Windows in light mode, HC off.
2. **Verify:** the app opens in the Quill look (warm off-white, not stark white). No new announcements at startup beyond the usual ones.
3. Open `config.ini` in the profile directory. **Verify:** `AppearanceThemeId = system` is present with its comment block, plus the five other `Appearance*` keys.

### B. Choosing a theme in Settings (§7 first walkthrough)

1. Open a message so the reading pane is populated, then open Settings and move to the **Appearance** tab. **Expected:** the tab announces as "Appearance".
2. Tab to the **Theme** combo box. **Expected:** announces the label and current value ("Theme", "System").
3. Choose **Quill Dark**, then activate **Save**. **Expected:** the dialog closes; the whole app goes dark immediately; you hear "Theme changed to Quill Dark." (Status category); the open message re-renders with dark background; focus returns to where Settings was invoked from.
4. Reopen Settings → Appearance. **Expected:** the Theme combo reads "Quill Dark".
5. Restart the app. **Expected:** Quill Dark persists.
6. Walk each remaining built-in theme in turn (Settings or the cycle commands). **Verify:** no unreadable text anywhere you check — message list, folder tree, menus, status bar, Settings itself, a compose window.

### C. Dark theme deep pass (the riskiest area)

With Quill Dark active, exercise each retemplated control with the keyboard and confirm both behavior and speech are unchanged from the light theme:

- **Menus** — open each main menu, arrow through items, check submenus (View → Filter, flag submenus), check a checkable item, activate one with an access key. Verify InputGestureText still reads, disabled items still announce as dimmed/unavailable, and Escape closes levels one at a time.
- **Message list** — arrow through rows; verify selection is visible (tinted, not white-on-white), unread rows are SemiBold with a small accent bar at the left edge, flag color bars still show, and column headers render.
- **Trees** — folder tree, conversations, by-sender: expand/collapse with Left/Right, verify the expander glyphs and selection visuals, and that announcements (level, expanded state, unread counts) are unchanged.
- **Combo boxes** — in Settings: open with F4/Alt+Down, arrow, typeahead, Escape to close without committing.
- **Check boxes / radio buttons** — Settings General tab: Space toggles, radio groups still one tab stop with arrow cycling.
- **Scroll bars, tabs, tooltips, status bar** — spot check visually.
- **Compose** — type in every field; verify the caret is visible in the dark input backgrounds and text selection is visible.

If any control here has broken keyboard behavior or wrong/missing speech, per §15.2 that control should ship untemplated rather than inaccessible — flag it and it comes out of `ThemedControls.xaml`.

### D. Text size, font, and vision toggles (§9 vision-assist scenario)

1. Set **Text size** to 150%. **Expected:** applies on Save with no restart; do a keyboard walkthrough of MainWindow, Settings, and Compose looking for clipped text or broken layouts. Try 200% too.
2. Set a **Font** override (e.g. Verdana). **Expected:** applies app-wide and to the reading pane. "(Theme default)" restores.
3. **Always underline links:** open an HTML message whose links aren't underlined. Toggle on → links underline; toggle off → back as authored.
4. **Thicker keyboard focus indicators:** toggle on, Tab around MainWindow — the dashed focus ring is visibly thicker. Toggle off restores.
5. **Apply theme colors to message content** (§7 last walkthrough): open a message with hard-to-read sender colors. Toggle on → the message re-renders in theme colors; your place in the app is preserved (reading-pane scroll may reset — accepted). Toggle off → sender's design returns.

### E. OS adaptation, live (§9 — highest-risk acceptance)

1. With Theme = **System**, toggle Windows dark mode (Settings → Personalization → Colors). **Expected:** the app follows within about a second, announces the change, no focus movement, no crash.
2. With any theme active, turn on **High Contrast** (Left Alt+Left Shift+PrintScreen) while the app is running with a message open. **Expected:** within about a second the whole app takes your HC palette — including the message list, status bar, and reading pane; you hear "High contrast is on; colors are supplied by Windows." (Status); focus does not move; no dialog appears.
3. Open Settings → Appearance during HC. **Expected:** a notice explains HC is supplying colors; the Theme combo is still present and operable (choosing records the preference for later).
4. Turn HC off. **Expected:** your configured theme returns, announced as "Theme changed to [name]."
5. Repeat the HC toggle with a compose window open as well.

### F. Theme Manager (§7 manager walkthroughs)

Note one deliberate deviation from the spec draft: the manager is a **modeless** window (this is the same pattern as Grab Addresses, for the same reason), so it stays open while themes apply behind it.

1. Ctrl+Shift+P → type "theme" → **Manage Themes**. **Expected:** window opens with focus in the theme list; the list announces as "Themes" and the current item includes its kind and current-theme marker (e.g. "Quill, built-in, current theme"); a one-time hint offers Tab for actions.
2. **Apply:** arrow to Ember, Tab to Apply, press Enter. **Expected:** the app restyles behind the window; "Theme changed to Ember."; focus stays on Apply.
3. **F6 / Shift+F6:** cycles list ↔ buttons.
4. **Duplicate:** select Quill, activate Duplicate. **Expected:** a name field appears prefilled "Quill copy" with focus in it; Enter (or OK) creates it; "Theme Quill copy created."; focus returns to the new item in the list.
5. **Rename:** works on your copy; disabled (announced unavailable) on built-ins.
6. **Export:** with the copy selected, activate Export. **Expected:** a standard Save dialog filtered to `*.quickmailtheme`, filename prefilled; on save, "Theme exported."
7. **Delete:** delete the copy. **Expected:** confirmation ("This cannot be undone", focus on No); on Yes, the item disappears, focus lands on a neighboring list item, "Theme deleted." Delete the *active* theme and verify the app falls back to System.
8. **Import:** import the exported file. **Expected:** it appears in the list (re-named "(imported)" if the name collides), focus moves to it, "Theme [name] imported."
9. **Import error case:** rename any `.txt` file to `.quickmailtheme` and import it. **Expected:** a dialog states the specific problem in plain language; on dismiss the list is unchanged and focus returns to the manager. Also try a valid JSON with a bad color value (e.g. `"accent": "blue"`) — the error names the key and value.
10. **Open themes folder** opens the profile's `themes` folder in Explorer.
11. **Ctrl+Shift+P inside the manager** opens a local palette with the manager's own actions.
12. **Escape** closes the window (name panel first, if open); focus returns to where you were before opening it.
13. **Hand-edit check (§9):** edit a user theme's JSON to an invalid hex, reopen the manager. **Expected:** the broken theme is simply not listed, a line in `quickmail.log` says why, and the app runs normally.

### G. Cycle and hotkey commands

1. Ctrl+Shift+P → **Next Theme** repeatedly. **Expected:** each press announces the new theme name and cycles System → built-ins → your themes → around.
2. Settings → Keyboard Shortcuts: find a **Theme: [name]** row, assign a shortcut, save, press it. **Expected:** that theme applies and announces.

### H. Shared-component regressions (§9)

1. **Compose invalid address:** type a bad address in To and commit it. **Verify:** the chip shows a distinct error state in both Quill and Quill Dark, and announces "Unrecognized:".
2. **KeyCaptureDialog:** Settings → Keyboard Shortcuts → Set Shortcut; verify the captured-key text is readable in both themes.
3. **Event card:** open a message with a calendar invite. **Verify:** the card and its Accept/Tentative/Decline buttons are readable in both themes (they now use the status tint style — pale background, dark status text).
4. **Markdown preview:** in a Markdown compose, press F8. **Verify:** the preview follows the theme.
5. **`--online` mode:** relaunch with `--online` and repeat scenario B. **Expected:** identical — theming never touches the local store.

### I. What "pass" means

- Every expected announcement above is heard, in the stated category (so it respects your Hints/Status/Results settings).
- Theme switching never moves focus, collapses a tree, or clears a selection. The only re-render is the reading pane document.
- No text anywhere is unreadable in any built-in theme.
- HC on/off round-trips cleanly with no restart.
- Keyboard behavior of every control is identical across themes.

Anything that misses — note the theme, the control, what you heard versus what §7 says — and it gets fixed before this merges.
