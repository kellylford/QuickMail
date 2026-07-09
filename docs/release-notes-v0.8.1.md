# QuickMail v0.8.1 Release Notes

## Download

Two options are available for v0.8.1:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This is a **stability release** on top of v0.8.0. It fixes an intermittent freeze that could occur when opening a reply, and it is the first update delivered automatically to installed copies. **Everything introduced in v0.8.0 — themes and the visual design system, automatic updates, the Tools menu, and in-app bug reporting — is included, and the full v0.8.0 notes are reproduced below** because they bubble down as this release moves to the top of the releases page.

---

## Fixed in 0.8.1: intermittent freeze when opening a reply

On some systems, opening a **Reply**, **Reply All**, or **Forward** could intermittently freeze QuickMail — the window stopped responding to the keyboard and had to be closed from Task Manager. It was most likely to happen when starting a reply directly from an open message while a screen reader was running, and especially on Windows-on-ARM machines, where QuickMail runs under emulation that widens the timing window.

The cause was a timing collision at the exact moment the compose window opened. QuickMail was building the compose window in the middle of handling a keystroke that had arrived from the reading pane's web-based view, and the two cross-process calls involved — one to the reading pane, one to the accessibility layer — could deadlock the interface. QuickMail now opens the compose window a fraction of a second later, after that keystroke has finished being handled, which removes the collision entirely. In normal use the change is invisible: the compose window opens exactly as before.

This is the third and, we believe, the decisive fix in a family of related freezes tracked over the 0.8.0 cycle (issues addressed in #190, #198, and now #181/#209); unlike the earlier two, this one removes the underlying trigger the whole family shared rather than one symptom.

### Updating to 0.8.1

If you installed QuickMail **0.8.0 from the MSI**, you receive 0.8.1 automatically — this is the first release to exercise that path end to end. The update downloads quietly in the background and installs the next time you exit and reopen QuickMail; Help → About will then read 0.8.1. If you are still on a version **before 0.8.0** (the older Inno Setup installer), the one-time reinstall step described in the carried-over 0.8.0 notes below still applies to get you onto the automatic-update track.

---

# Included from v0.8.0

v0.8.0 introduced **theming and a visual design system**: light and dark themes, app-wide text scaling and font choice, and a Theme Manager for creating, sharing, and hand-editing themes. See the [Themes section of the User Guide](https://kellylford.github.io/QuickMail/themes.html) for the full walkthrough. It also introduced **automatic updates** — installed copies of QuickMail now keep themselves current, with you in control throughout. And it added an always-available **Tools** menu and in-app **bug reporting** that doesn't require a GitHub account.

## Automatic updates

Starting with the 0.8.0 release, QuickMail keeps itself up to date. When a newer version is
available, the installed app downloads it quietly in the background and installs it the next
time you exit and reopen QuickMail — no download page, no installer to run, no security
warnings. The Help menu continues to show whether you are current, offers a **Restart to
Update** option when a new version is waiting, and the first start after an update confirms
what was installed. You stay in control throughout: automatic updating and its notifications
can each be turned off in **Settings → Advanced**, with manual updating always available. See
the [Installing and Updating section of the User Guide](https://kellylford.github.io/QuickMail/installing-and-updating-quickmail.html)
for the full walkthrough.

### One-time step for existing installed users

The 0.8.0 release moved to a new installer, so getting onto the automatic-update track requires
one manual reinstall — this applies only if you are still on a version **before 0.8.0**:

1. Uninstall your current QuickMail from **Settings → Apps**. When the uninstaller offers to
   delete user data, **choose No**.
2. Download and run **QuickMail-win.msi** from this release page. A standard setup wizard
   walks through the license and installation.
3. Start QuickMail. All your accounts, settings, contacts, rules, templates, saved views, and
   cached mail are exactly as you left them.

Your data is safe throughout: settings and mail live in a separate location the installer
never touches, and passwords stay in Windows Credential Manager. After this one reinstall,
all future updates are automatic.

Notes on the install:

- QuickMail installs per-user (no administrator prompt). The previous option to install
  for all users is gone.
- A Start Menu entry is created. The first time QuickMail starts, it asks whether to add a
  desktop shortcut; the choice can be changed anytime in Settings → General.
- Uninstalling asks whether to also remove your data — and unlike before, choosing Yes
  also clears QuickMail's saved passwords from Windows Credential Manager.

### Portable exe users

Nothing changes. `QuickMail.exe` remains a single-file download that you update manually;
the Help menu still tells you when a new version is available.

## Themes

QuickMail has a color theme, chosen in **Settings → Appearance**. Six themes ship built in:

- **System** — follows the Windows light or dark setting.
- **Parchment** (the default light look) — warm off-whites with a muted steel-blue accent.
- **Parchment Dark** — the dark counterpart.
- **Ember** (warm), **Fjord** (cool), and **Heather** (muted) — light variants with different accent and selection colors.

Theme changes apply immediately — no restart. An open message re-renders in the new colors **without moving focus**.

### Understanding a theme without seeing it

Every theme carries a plain-language **Theme description** — a written account of its overall look, its fonts, and each individual color together with exactly where that color is used (message list, links, selection, focus outline, error and status text, and so on). Color values are translated into words like "dark muted blue" or "warm off-white," with the nearest well-known color name for reference. The Theme Manager shows this description for whichever theme is selected, in a read-only text box you can read or hear at your own pace, so themes can be understood and compared without seeing the colors.

## Text size, font, and vision settings

The **Appearance** tab also gives you:

- **Text size** — scale all app text using fixed stops at 100%, 110%, 125%, 150%, 175%, and 200%, independent of Windows display scaling. The chosen size flows through every window.
- **Font** — override the app font, or keep the theme's own font.
- **Always underline links** — underline links everywhere, not just by color.
- **Thicker keyboard focus indicators** — widen the focus outline for easier tracking.
- **Apply theme colors to message content** — override a sender's hard-to-read colors and fonts in the reading pane with your theme's colors. Off by default, so messages render as their sender intended unless you turn it on.

## The Theme Manager

Choose **Manage Themes…** from the **Tools** menu, or **Manage Themes** from the Command Palette (**Ctrl+Shift+P**), to open the Theme Manager. It is a separate, non-blocking window, so you can leave it open and try themes against real messages. From the theme list, press Tab to reach the actions:

- **Apply** — switch to the selected theme immediately.
- **Duplicate** — copy a theme as a starting point for your own.
- **Rename** / **Delete** — for your own themes.
- **Export…** — save a theme as a shareable `.quickmailtheme` file.
- **Import…** — load a `.quickmailtheme` file. If a file has a problem, QuickMail tells you exactly what is wrong (for example, which color value is not a valid hex color).
- **Open themes folder** — open the folder where your themes are stored, for hand-editing.

A theme is a plain, documented JSON text file: duplicate a built-in theme, open the themes folder, and edit the copy in any text editor. Any color you leave out is filled in from the built-in Light or Dark theme.

The Command Palette also offers **Next Theme** and **Previous Theme**, and a **Theme: [name]** command for each theme. None of these carry a default shortcut — assign one in **Settings → Keyboard** if you want direct access.

## The Tools menu

The main window has an always-available **Tools** menu, grouping the commands used less often than day-to-day mail actions: **Manage Themes…**, **Next Theme**, **Previous Theme**, **Address Book…** (Ctrl+Shift+B), **Rules…** (Ctrl+Shift+L), and **Command Palette…** (Ctrl+Shift+P). Address Book, Rules, and Command Palette moved here from their previous menus, so each command now lives in exactly one place; their keyboard shortcuts are unchanged.

## Windows High Contrast

When Windows High Contrast is on, QuickMail steps aside entirely — every color comes from your Windows High Contrast palette, QuickMail's own control styling is withdrawn so the system's High Contrast controls take over, and the reading pane defers to the browser's forced-colors handling. Your theme choice is remembered and returns automatically when High Contrast is turned off. Font and text-size settings continue to apply.

## Report a Bug

**Help → Report a Bug** opens a report window without requiring a GitHub account. Fill in a summary and, optionally, what happened, what you expected, and steps to reproduce — a **Preview** area always shows exactly what will be sent. QuickMail submits the report directly to GitHub Issues using its own narrowly-scoped, app-owned credential, and shows a link to the created issue on success. If sending isn't possible (no credential available, or the request fails), **Copy report and open GitHub** copies your report to the clipboard and opens a pre-filled issue page in your browser instead. By deliberate design, the only things sent are what you type plus a short **Environment** section of non-sensitive details — currently the QuickMail version, your Windows version, the .NET runtime version, the active theme, the current view (message grouping, plus the saved view name if one is applied), and the message sort order. No message content, email addresses, account settings, credentials, or log file content is ever collected — and the Preview shows the whole report so you can confirm before sending.

## Accessibility

- The update dialogs are small native dialogs: focus lands on the primary action, Escape always dismisses, and closing them returns focus to the message list. A found update is announced once; the background check itself is silent when you are already current.
- Theme switching never moves focus, collapses trees, or clears selection; the only thing that re-renders is the open message.
- Every custom announcement continues to respect your **Screen Reader Announcements** settings; theme and High Contrast changes are announced as **status**, and Theme Manager outcomes as **results**.
- Every built-in theme meets the WCAG contrast policy (4.5:1 for text, 3:1 for indicators and status colors) — enforced automatically by a unit test, so a theme that would fail contrast cannot ship.
- The Report a Bug preview is a genuine editable `TextBox` (edits blocked in code, not by making it read-only), so arrow-key navigation through the preview behaves the same as any other text field instead of losing character-by-character review. Its window title reads and announces as "Report a Bug - QuickMail".

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback.

---

## Internal

### Compose-open hang fix (issue #181, PR #209)

- A third live capture of the intermittent compose-open freeze (2026-07-09) was taken with `dotnet-stack` while the process spun (~90% of one core, `Responding=True` — the documented signature). Two identical samples confirmed a permanent STA-thread wedge in a pumping COM wait under `Window.Show()` → `ContextLayoutManager.fireAutomationEvents` → `AutomationPeer.UpdateSubtree` → `get_HostRawElementProvider`.
- The root the earlier captures missed is at the base of the stack: `ReplyAll` ran **nested inside the reading-pane WebView2's `CoreWebView2Controller_AcceleratorKeyPressed` COM callback**. Building and `Show()`-ing the compose window inline issued an outbound cross-apartment UIA call while the STA thread was still servicing that inbound WebView2 COM call — re-entrant cross-apartment COM that never unblocks (widened by x64-on-ARM64 emulation). All three captured blocking sites (#190 spell-check init, #198 `LoadInto` UIA storm, and this `Window.Show()` layout burst) share this one trigger, so the fix removes the nesting rather than chasing each site.
- `MainWindow.OpenComposeWindow` now builds and shows the window via `Dispatcher.InvokeAsync(…, DispatcherPriority.Input)`, so the WebView2 callback returns and the inbound COM stack unwinds before the window opens on a clean, non-re-entrant turn. `e.Handled` is still set synchronously, so key routing is unaffected; compose windows are modeless, so the one-turn delay is imperceptible. An independent review confirmed the change is correct and free of swallowed-exception, ordering, and focus regressions. Build clean; 1086/1086 tests pass.

### Automatic updates (issue #156, PR #200)

- [Velopack](https://velopack.io/) 1.2.0 replaces the Inno Setup installer. `App.xaml.cs` declares an explicit `Main` that runs `VelopackApp.Build().Run()` before WPF initializes — `vpk pack` verifies this via IL inspection and refuses to pack without it (`App.xaml` compiles as `Page`).
- `UpdateCheckService` branches by runtime: installed copies use `UpdateManager` + `GithubSource` (silent background download; applied on exit, or immediately via the Restart to Update dialog, which is cancellable — dismissing the dialog retracts a restart waiting on a slow download); the portable exe keeps the original GitHub API notification-only check. The `AutoUpdate` setting switches installed copies to notification-only.
- The shipped installer is the WiX 5 MSI (`vpk --msi`) with welcome/license/conclusion pages; Velopack's one-click `Setup.exe` and `-Portable.zip` are deliberately not published. `--framework webview2` preserves WebView2 install-on-demand; `--instLocation PerUser` keeps updates elevation-free (per-machine installs are deliberately unsupported).
- Desktop shortcut is owned by the app (first-run offer + Settings → General checkbox, targeting the update-stable `current\QuickMail.exe` path); uninstall offers data removal via a detached prompt from `OnBeforeUninstallFastCallback` (hook processes are killed after 30 seconds), including Credential Manager cleanup, with diagnostics in `%TEMP%\quickmail-uninstall.log`.
- Release workflow: the tag must equal the csproj `Version`, `AssemblyVersion`, and `FileVersion`; `vpk download github` enables delta packages from the second release onward; `fail_on_unmatched_files` fails the release if expected assets are missing. A `--updateFeed <path>` startup flag allows full offline update-cycle testing against local `vpk pack` output (see `docs/INSTALLER.md`).
- An independent multi-angle review of the PR confirmed 10 findings, all fixed before merge — including a cancellable restart path, accurate failure messaging when a download fails, and elimination of a phantom "update installed" notice when portable and installed copies share a profile. Full test suite passing; complete update cycles verified live against a local feed.

### Theming system (issue #177, PR #179)

- Twenty-six semantic color tokens plus typography and focus-thickness are published as `Theme.*` DynamicResources; `Theming/ThemeKeys.cs` is the single source of truth for their names.
- `ThemeService` publishes tokens by **atomically replacing** one merged dictionary rather than mutating in place, so DynamicResource listeners never see a transient missing-resource state. Under High Contrast every token is rebuilt from live `SystemColors` and `Styles/ThemedControls.xaml` is withdrawn so WPF's built-in High-Contrast templates return. The service is `IDisposable`; `App.OnExit` disposes it and detaches the static OS-change handlers.
- `ThemeStore` keeps built-ins as embedded resources and user themes as one JSON file each under `{profile}\themes\`; corrupt files are skipped with a log line and never block startup. Parsed user themes are cached and invalidated on save/delete.
- The reading pane and compose preview consume theme colors through a `--qm-*` CSS-variable bridge; with no theme CSS the documents fall back to CSS system colors, so no-theme rendering is unchanged.
- `Helpers/ThemeDescriber.cs` generates the plain-language Theme description: it converts each hex color to a spoken descriptor (lightness, chroma-gated saturation, hue) plus the nearest documented CSS/X11 color name, and lays out where each token is used.
- The `ThemeRegressionGuard` test rejects new hardcoded colors or numeric font sizes in `Views/`, `Controls/`, and `Styles/` XAML outside a reviewed allowlist, so future work keeps everything token-driven.
- Two independent code reviews (logic and UI/accessibility) were run against the branch; findings were fixed, including a case where applying a theme from the Theme Manager could be silent when the selection resolved to the palette already showing. Full test suite: 1017/1017 passing.
- Post-merge screenshot review of the dark theme found three controls with a local `Style` lacking `BasedOn`, which silently discarded the themed background/foreground and reverted to WPF's default light chrome: the message list and the Conversation/Sender/Recipient tree views (severe — white background made dark-theme Conversation view unreadable), Rules Manager text boxes and dropdowns (moderate), and the compose formatting toolbar (minor). All now chain `BasedOn` to the themed implicit or keyed styles.
- Theme files are a shareable, attacker-influenced artifact (Export/Import, dropped into `{profile}\themes\`), so their parse path was hardened per issue #183: `fontFamily`/`monoFontFamily` are validated to plain family names (a raw WPF `FontFamily` string allows `#` to mean `baseUri#familyName`, which could otherwise point the font loader at an attacker-controlled file/UNC/pack URI); theme files over 256 KB are rejected before being read; and `PathForId` asserts the resolved path stays a direct child of the themes folder as defense-in-depth against path traversal.

### Bug reporting (issue #186)

- `IBugReportService` submits directly to the GitHub Issues API using an app-owned, repo-scoped (`Issues:Write` only) token stored through the existing `ICredentialService` — no user GitHub sign-in. `BuildFallbackUrl` uses `Uri.EscapeDataString` (correct percent-encoding) and truncates the body so the pre-filled fallback URL can't exceed what `ShellExecute` will open; the full report is always available via the clipboard regardless.
- `ReportBugViewModel` collects only what the user types plus a fixed Environment block (QuickMail version, Windows version, .NET runtime version, active theme, current view/grouping, and message sort — captured as a `BugReportContext` snapshot when the window opens). It never reads `quickmail.log`, an explicit product decision recorded in `docs/planning/bug-reporting-pm-dev-spec.md`, not an oversight.
- `ReportBugWindow` is opened modeless (`.Show()`, not `.ShowDialog()`) per this codebase's modal-dialog rules, since it has editable text fields and can be opened over a window with a live WebView2 reading pane. Its event subscriptions use named handlers so `OnClosed` can unsubscribe all of them, preventing a Send still in flight from touching the window after it closes and is disposed.

### Version

- Bumped to `0.8.1` (`Version`, `AssemblyVersion`, `FileVersion`).
