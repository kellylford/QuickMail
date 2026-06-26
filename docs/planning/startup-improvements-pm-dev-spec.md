# Startup Improvements — PM + Dev Spec

**GitHub issue:** [#144 Improve QuickMail startup](https://github.com/kellylford/QuickMail/issues/144)

## Status

| Phase | Description | Status |
|---|---|---|
| 1 | Inbox-first parallel sync in `SyncService` | ✅ Complete — shipped in v0.7.6 |
| 2 | `ConfigModel.StartupFolder` + `ConfigService` persistence | ⬜ Not started |
| 3 | Apply `StartupFolder` in `InitialLoadAsync` | ⬜ Not started |
| 4 | Settings UI — read-only field + "Choose…" button (tree-view picker) | ⬜ Not started |

**Resume at Phase 2.** Start a new implementation session with: *"Implement Phases 2–4 of `docs/planning/startup-improvements-pm-dev-spec.md`. Phase 1 is complete. Read the spec in full before writing any code."*

---

## Section 1: Executive Summary

For users with many email accounts and many folders, QuickMail's startup can feel slow: the cached message list appears immediately, but new mail from the server doesn't show until every folder across every account has been synced — a sequential process that can take 20–40 seconds or more. This spec addresses startup latency through two complementary strategies: (1) smarter sync ordering that prioritizes Inbox folders so new inbox mail surfaces quickly, and (2) a configurable startup folder/view so users choose what they see at launch rather than always landing on "All Mail." Together these changes make the first 5–10 seconds of use feel much more responsive without changing behavior for users who are happy with the current defaults.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Startup folder | Always "All Mail" (`AllMailFolder` sentinel, hardcoded at `MainViewModel.cs:1301`) | Users who mostly live in their inbox see a big "All Mail" list that then changes again after sync | Multi-account users, inbox-focused users |
| Sync ordering | All accounts synced sequentially, all folders per account before moving to next account (`SyncService.cs:50-88`) | New inbox mail doesn't appear until every sent/trash/bulk folder for every account is done | Anyone with more than ~2 accounts or 20+ folders |
| Default view override | Requires creating a saved view and marking it IsDefault | User-hostile: a persistent UI artifact just to control startup destination | Power users, accessibility users |
| Startup folder setting | Does not exist | No way to say "start me in All Inboxes" without creating a saved view | All users |

**Code-verified facts:**
- `InitialLoadAsync()` (`MainViewModel.cs:1299`) hardcodes `SelectedFolder = AllMailFolder`.
- `SyncAllAccountsAsync()` (`SyncService.cs:33`) iterates accounts sequentially, then folders within each account sequentially. No prioritization.
- A saved-view default IS applied, but only after `ConnectAllAccountsAsync()` completes (`MainViewModel.cs:1407`). This typically means ~5–15s after launch before the default view is applied.
- `ConfigModel` has no `StartupFolder` or startup-destination property.

### 2.2 Target personas

**Alex — 4-account power user.** Has work IMAP, personal IMAP, a newsletter account, and a list account. Each has 40+ folders. Startup sync takes 45 seconds. By the time new mail appears, Alex has given up and checked the phone instead.

**Sam — single account, lots of folders.** Uses aggressive sub-folder filing. Has 80+ folders. Starting in "All Mail" dumps 2,000+ cached messages that reload again after sync. Sam just wants to see their inbox.

**Jordan — screen reader user.** The long sync with announcements every 10 folders creates many "Synced X of Y folders" announcements before the real mail is visible. Jordan wants the inbox-relevant mail announced first.

**Casey — new user.** Has 2 accounts, all mail in inbox. The default "All Mail" view is confusing — shows duplicates of inbox mail alongside sent, and Casey doesn't understand the difference from "All Inboxes."

**Morgan — rules-heavy user.** Has mail rules that move everything out of inbox into folders. "All Mail" is the right default for Morgan. A configurable setting lets Morgan keep the current behavior while others change theirs.

### 2.3 Why now

- The saved-view default mechanism (issue #57 fix) is already in place — this builds on top of it.
- Sync infrastructure is stable; inbox-first ordering is a targeted change to `SyncService`.
- No external dependencies. No schema changes to SQLite.

---

## Section 3: Design Principles

1. **Zero behavior change for existing users who don't opt in.** The default startup folder stays "All Mail" to match today's behavior. Inbox-first sync is a transparent optimization with no user-visible change except speed.
2. **The setting is simple: one choice, not a tree.** The startup folder picker presents a flat list of options (virtual folders + saved views), not the full folder tree.
3. **Fast path for the common case.** Inbox-first sync must reduce time-to-new-mail for inbox-focused users even if they never touch the setting.
4. **Respect the existing saved-view default.** If a user has set a saved view as default, that takes precedence over the new `StartupFolder` setting.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Location | Default | Notes |
|---|---|---|---|
| Startup folder setting | `StartupFolder` in `[startup]` section of config.ini | `"AllMail"` (current behavior) | Accepts: `"AllMail"`, `"AllInboxes"`, `"AllDrafts"`, `"AllSent"`, `"AllTrash"`, or a saved-view name |
| Startup folder UI | **Settings → General → Startup folder** — read-only field + "Choose…" button | All Mail | Opens `FolderPickerWindow` (tree-view) scoped to virtual folders and saved views; keyboard-friendly |
| Inbox-first sync ordering | Internal to `SyncService`; no UI | On (transparent) | Inbox/Inbox-kind folders across all accounts sync first, then remaining folders |
| Parallel account sync | Internal to `SyncService`; no UI | On (transparent) | Accounts sync concurrently (capped at N connections per account, already enforced by `ImapMailService`) |
| Change default from All Mail to All Inboxes | Opt-in via setting | No change (stays All Mail) | Recommended in onboarding/docs, not enforced |

### 4.2 Explicitly out of scope (v1)

- Per-account startup folder (one global setting only).
- Startup folder set to a specific real IMAP folder (e.g. "work/Projects"). Only virtual folders and saved views in v1.
- Persisting startup folder across profile directories.
- Startup folder in the first-run tutorial UI (can be added later).
- Changing `SyncDays` or `InitialSyncCount` as a startup-speed lever (already exposed; out of scope here).
- Background/idle sync scheduling changes.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision A: Inbox-first sync ordering inside `SyncService`.**

Reorder `SyncAllAccountsAsync` to iterate in two passes:
1. Pass 1: For each account, sync only folders with `SpecialFolderKind.Inbox` (i.e., where `folder.Kind == SpecialFolderKind.Inbox`). Accounts can run in parallel in this pass.
2. Pass 2: For each account, sync all remaining non-Inbox folders that aren't excluded (`!folder.ExcludeFromAllMail`). Accounts can run in parallel.
3. After both passes, fire `FolderSynced` and let `StartBackgroundSyncAsync` call `RefreshAsync` as today.

**Alternatives:**
1. Keep sequential, but put Inbox first per account. Pro: simpler, no concurrency. Con: still slow for many accounts — account 1's inbox syncs, then all of account 1's folders, then account 2's inbox, etc.
2. Full parallelism (all folders, all accounts concurrently). Pro: fastest. Con: can exhaust IMAP connections; harder to reason about; more complex error handling.

**Rationale:** Two-pass with per-account parallelism gives the biggest win with manageable complexity. Each account still uses its own IMAP client (already isolated in `ImapMailService`); parallelism across accounts is safe. `MaxImapConnectionsPerAccount` already caps connections.

---

**Decision B: `StartupFolder` stored in `config.ini` as a string key.**

Use the same sentinel naming scheme as virtual folders (e.g., `"AllMail"`, `"AllInboxes"`) plus saved-view names. Saved views are identified by `SavedView.Name` (not Guid, for readability).

**Alternatives:**
1. Store a Guid for saved views, sentinel string for virtuals. Pro: rename-safe. Con: config file is unreadable; Guid lookup needed on every startup.
2. Store a JSON blob. Pro: extensible. Con: over-engineered for one setting.

**Rationale:** String keys match how saved views are already serialized (`VirtualFolderKey` in `SavedView`). Renames are an acceptable edge case — if a saved view is renamed, startup falls back to "All Mail" gracefully.

---

**Decision C: `StartupFolder` setting takes lower precedence than the saved-view `IsDefault` flag.**

The existing "Mark as default" saved-view mechanism stays and takes precedence. `StartupFolder` is the new baseline when no saved view is marked default.

**Rationale:** Backwards compatibility. Users who have relied on the saved-view default mechanism since issue #57 see no change. `StartupFolder` is additive.

---

**Decision D: Parallel account sync uses `Task.WhenAll` with one task per account.**

Each account runs its Inbox-pass folders concurrently via `Task.WhenAll`. Exception handling: a faulted account task logs and continues; it does not cancel others.

**Rationale:** `ImapMailService.GetOrReconnectAsync` is account-scoped and thread-safe for concurrent calls on different accounts. Same pattern used elsewhere in the codebase.

---

### 5.2 Runtime mode compatibility

| Mode | Behavior |
|---|---|
| Normal | Full feature: inbox-first sync, startup folder applied from config |
| `--online` | No sync at all today; startup folder still applies (controls which virtual folder is selected before the IMAP fetch) |
| `--profileDir <path>` | Uses alternate config.ini — `StartupFolder` read from that profile's config |

### 5.3 Code reuse and duplication risks

- `FetchVirtualAsync(AllMailFolder)` is currently called in three places when online mode or no default view: at `InitialLoadAsync` for the cache, and again in `StartBackgroundSyncAsync`. The startup-folder selection needs to call the right fetch method for each virtual folder type. Use the existing `RefreshAsync()` (which already handles all sentinel types) rather than duplicating dispatch logic.

### 5.4 Shared component audit

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `SyncService.SyncAllAccountsAsync` | `Services/SyncService.cs` | `StartBackgroundSyncAsync` in `MainViewModel` | Add two-pass inbox-first + parallel account sync | Progress counter math changes; `SyncProgressChanged` must still fire accurately. Tests must cover new ordering. |
| `ConfigModel` | `Models/ConfigModel.cs` | `ConfigService`, every settings VM | Add `StartupFolder` string property | Additive only; no existing consumers affected. |
| `ConfigService` | `Services/ConfigService.cs` | All settings read/write | Read/write `StartupFolder` from `[startup]` section | Well-isolated INI section; no risk to other sections. |
| `MainViewModel.InitialLoadAsync` | `ViewModels/MainViewModel.cs` | `App.xaml.cs` (called once on startup) | Apply `StartupFolder` setting instead of hardcoded `AllMailFolder` | Must fall back to `AllMailFolder` if setting is invalid or no match. |
| `SettingsViewModel` | `ViewModels/SettingsViewModel.cs` | `SettingsWindow` | Add `StartupFolder` property + picker options | No other consumers of `SettingsViewModel`. |
| `SettingsWindow.xaml` | `Views/SettingsWindow.xaml` | — | Add ComboBox for Startup folder | XAML parse test covers this. |
| `ISyncService` | `Services/ISyncService.cs` | `MainViewModel`, `StubServices` (tests) | `SyncAllAccountsAsync` signature unchanged | `StubSyncService` in tests needs no change if signature unchanged. |

---

## Section 6: Keyboard Walkthrough

### Path A: Change startup folder in settings

1. User opens Settings (Ctrl+,). **Expected:** Settings window opens, focus on first tab.
2. User navigates to the "General" tab. **Expected:** General settings pane is visible.
3. User presses Tab to reach the "Startup folder" read-only text field. **Expected:** Screen reader announces "Startup folder" label then field value (e.g. "All Mail").
4. User presses Tab to the "Choose…" button and activates it. **Expected:** Folder picker window opens — using `FolderPickerWindow` (tree-view) scoped to virtual folders and saved views. Focus lands on the folder tree. Screen reader announces the current selection (e.g. "All Mail").
5. User presses Down arrow to move to "All Inboxes". **Expected:** Screen reader announces "All Inboxes".
6. User presses Enter to confirm. **Expected:** Picker closes, "Startup folder" field in Settings updates to "All Inboxes". Change is not yet saved.
7. User activates "Save" button. **Expected:** Settings window closes, setting written to config.ini. No restart required.
8. User restarts QuickMail. **Expected:** App opens with "All Inboxes" selected in the folder tree, "All Inboxes" messages loaded from cache immediately.

### Path B: Startup with "All Inboxes" selected (user has set it)

1. App launches. **Expected:** Folder tree visible, "All Inboxes" is the selected folder. Cache shows inbox messages immediately. Status bar says "N messages (cached — syncing…)".
2. Sync runs (inbox-first pass). **Expected:** After a few seconds (inbox folders only), message list updates with new inbox mail. Screen reader announces "N messages loaded." (when `AnnounceStatus` is on).
3. Sync continues (remaining folders in background). **Expected:** No UI change to the message list (user is in All Inboxes; non-inbox folders don't affect this view). Status bar updates to "N messages." when sync completes.

### Path C: Inbox-first sync (transparent, no setting changed)

1. App launches with default "All Mail" setting. **Expected:** Cached messages shown immediately as today.
2. Inbox folders across all accounts sync first. **Expected:** New inbox messages appear in the All Mail list within a few seconds of launch (much sooner than today).
3. Remaining folders sync. **Expected:** Additional non-inbox messages trickle in as each folder completes, exactly as today.
4. Sync completes. **Expected:** Status bar and announcement as today.

### Path D: Saved view default (existing behavior, unchanged)

1. User has saved view "Work Inbox" marked as default. **Expected:** After connections ready (~5s), "Work Inbox" view is applied. `StartupFolder` setting is ignored (saved-view default takes precedence).

### Path E: Edge case — saved view referenced by StartupFolder is deleted

1. User had saved view "My View" set as startup folder. User then deleted "My View". **Expected:** App falls back to "All Mail" silently. No crash. No error dialog.

---

## Section 7: Accessibility Checklist

- **`AutomationProperties.Name`:** The read-only text field gets its name from a `<Label Target="{Binding ElementName=StartupFolderField}">` binding (matching the existing settings UI pattern). The "Choose…" button gets `AutomationProperties.Name="Choose startup folder"`. `FolderPickerWindow` already has its own accessible names — no new ones needed there.
- **Announcements:** No new `AccessibilityHelper.Announce` calls needed. The existing "N messages loaded" `AnnouncementCategory.Status` announcement at end of sync covers the startup-folder case. Inbox-first sync is transparent — no new announcement.
- **Focus restoration:** The Settings ComboBox is in the existing tab order; no special focus restoration needed.
- **F6 ring:** No new panes. No F6 changes.
- **Radio/checkbox groups:** No new grouped controls; the ComboBox is a single control.
- **Color-only information:** None.

---

## Section 8: Acceptance Walkthrough

### Scenario 1: Change startup folder to All Inboxes

**Setup:** App running. At least 2 accounts configured with mail in Inbox. Current startup folder is "All Mail" (default).

1. Open Settings (Ctrl+,). **Verify:** Settings window opens.
2. Navigate to General tab. **Verify:** "Startup folder" field is present showing "All Mail", and a "Choose…" button is beside it.
3. Activate "Choose…", navigate to "All Inboxes" in the tree picker, press Enter. Activate "Save". **Verify:** Settings closes, config.ini `[startup]` section contains `StartupFolder=AllInboxes`.
4. Restart app. **Verify:** Folder tree shows "All Inboxes" selected, message list shows inbox messages from cache. Message count status bar visible.
5. Wait for sync to complete. **Verify:** Message list updates with new inbox mail. No "All Mail" messages visible.

### Scenario 2: Inbox-first sync speed improvement

**Setup:** App configured with 2+ accounts, each with 10+ folders. Observer: watch the message list update during startup.

1. Launch app (default All Mail startup folder). **Verify:** Cached messages appear immediately.
2. Watch first message list update after sync begins. **Verify:** New messages arrive in the list within ~5 seconds (inbox folders complete first, before sent/trash/bulk). Previously this took longer.
3. Continue watching. **Verify:** Additional messages arrive as other folders complete. No regression in final message count.

### Scenario 3: Saved-view default takes precedence over StartupFolder

**Setup:** A saved view "Work" is marked as default (IsDefault = true). StartupFolder is set to "AllInboxes" in config.

1. Launch app. **Verify:** After connections ready, "Work" saved view is applied — not "All Inboxes". The saved-view default wins.

### Scenario 4: Invalid StartupFolder falls back gracefully

**Setup:** Manually set `StartupFolder=NonExistentView` in config.ini.

1. Launch app. **Verify:** App starts normally with "All Mail" selected. No crash, no error dialog.

### Scenario 5: Settings — screen reader

**Setup:** Screen reader active.

1. Tab to "Startup folder" ComboBox in Settings. **Verify:** Screen reader announces "Startup folder" and current value (e.g., "All Mail").
2. Open dropdown and navigate options. **Verify:** Each option name is announced as focus moves.
3. Save with changed selection. **Verify:** No unexpected announcements. Settings closes.

### Scenario 6: `--online` mode

**Setup:** Launch with `--online` flag. StartupFolder set to "AllInboxes".

1. Launch app. **Verify:** "All Inboxes" is selected at startup. IMAP fetch runs for All Inboxes virtual folder. No crash.

---

## Section 9: Success Metrics

- **Primary happy path works:** User sets StartupFolder to "AllInboxes", restarts, lands in All Inboxes with cached messages visible immediately.
- **Speed improvement is perceptible:** On a 3-account × 30-folder setup, new inbox mail appears in the list within 5–10 seconds (inbox-first pass) rather than 30–45 seconds (after all folders).
- **No regression:** Existing behavior for users with a saved-view default is unchanged. `IsDefault` saved view still overrides StartupFolder.
- **Invalid config handled:** Deleting a referenced saved view does not crash on startup.
- **Keyboard-only:** All new settings UI operable with Tab, arrow keys, Enter, Escape.
- **`--online` mode compatible:** Feature works correctly under `--online`.

---

## Section 10: Implementation Phases

### Phase 1: Inbox-first parallel sync in SyncService ✅ Complete (v0.7.6)

**Goal:** `SyncAllAccountsAsync` runs Inbox folders first (all accounts concurrently), then all remaining folders (all accounts concurrently). Progress reporting still accurate.

**Deliverables:**
- Modify `QuickMail/Services/SyncService.cs`: restructure `SyncAllAccountsAsync` into two `Task.WhenAll` passes.
- Pass 1: one `Task` per account, each syncing only `folder.Kind == SpecialFolderKind.Inbox` folders.
- Pass 2: one `Task` per account, each syncing remaining non-excluded folders.
- `SyncProgressChanged` fires from within each task; total count stays the same.

**Tests:**
- `SyncServiceTests` (new): verify inbox folders complete before non-inbox folders using a controllable stub. Verify `SyncProgressChanged` fires the correct total count. Verify an exception in one account task does not prevent other accounts from completing.

**Risk:** Race in `SyncProgressChanged` counter across parallel tasks — use `Interlocked.Increment` on `completedFolders`. Medium probability, Medium impact. Mitigation: unit test with two concurrent accounts.

**Duration:** 2–3 hours

---

### Phase 2: ConfigModel + ConfigService for StartupFolder

**Goal:** `ConfigModel.StartupFolder` property reads from and writes to `[startup]` section of config.ini.

**Deliverables:**
- Modify `QuickMail/Models/ConfigModel.cs`: add `public string StartupFolder { get; set; } = "AllMail";`
- Modify `QuickMail/Services/ConfigService.cs`: read/write `StartupFolder` from `[startup]` section (new section).

**Tests:**
- `SettingsViewModelTests` or a new `ConfigServiceStartupTests`: round-trip read/write of `StartupFolder`. Test default (key absent from config.ini returns `"AllMail"`). Test unknown value round-trips as-is.

**Risk:** New INI section parsing — low risk given existing pattern. Low probability, Minor impact.

**Duration:** 1–2 hours

---

### Phase 3: Apply StartupFolder in InitialLoadAsync

**Goal:** `InitialLoadAsync` selects the startup folder based on `ConfigModel.StartupFolder`, falling back to `AllMailFolder` for unrecognized values.

**Deliverables:**
- Modify `QuickMail/ViewModels/MainViewModel.cs` `InitialLoadAsync()`:
  - Read `_configService.Load().StartupFolder`.
  - Map the value to the correct `MailFolderModel` sentinel (using existing sentinel constants: `AllMailFolder`, `AllInboxesFolder`, `AllDraftsFolder`, `AllSentFolder`, `AllTrashFolder`, or a matching saved view).
  - Fall back to `AllMailFolder` if no match.
  - Set `SelectedFolder` to the resolved folder.
  - Load cache for that folder (for virtual folders: `LoadAllSummariesAsync` already loads everything from SQLite, which is correct for any virtual view — no change needed there).

**Tests:**
- `MainViewModelStartupTests` (new or extend existing): verify `InitialLoadAsync` selects the correct `SelectedFolder` for each valid `StartupFolder` value; verify fallback to `AllMailFolder` for unknown value.

**Risk:** If `SavedViews` is not yet populated when `InitialLoadAsync` runs (saved views load async from store), the saved-view lookup may fail to match. Mitigation: load saved views synchronously during `InitialLoadAsync` before the folder lookup, or accept that saved-view startup folder may silently fall back to "All Mail" on first launch (acceptable — saved views are populated before display in normal flow).

**Duration:** 1–2 hours

---

### Phase 4: Settings UI

**Goal:** Users can select their startup folder in Settings → General.

**Deliverables:**
- Modify `QuickMail/ViewModels/SettingsViewModel.cs`: add `StartupFolder` string property (two-way bound to config). Add `StartupFolderOptions` list populated from virtual folder names + current saved views.
- Modify `QuickMail/Views/SettingsWindow.xaml`: add Label + ComboBox in General section, bound to `StartupFolder` and `StartupFolderOptions`.
- Options list: "All Mail", "All Inboxes", "All Drafts", "All Sent", "All Trash", then each saved view by name.

**Tests:**
- `XamlParseTests`: `SettingsWindow` XAML loads without error (catches XAML binding mistakes).
- `SettingsViewModelTests`: verify `StartupFolderOptions` contains at least the 5 virtual folder options; verify `StartupFolder` property round-trips through config.

**Risk:** `SettingsViewModel` currently has no dependency on `ISavedViewService` or the saved views list. Options: (a) inject saved views at construction (preferred), (b) derive options from `_viewService.GetSavedViews()`. Verify that existing `SettingsViewModelTests` compile and pass with the new injection.

**Duration:** 2–3 hours

---

## Section 11: Files to Create / Modify

### Files to Modify

| File | Changes | Lines changed (est.) |
|---|---|---|
| `Services/SyncService.cs` | Restructure `SyncAllAccountsAsync` into two-pass parallel; `Interlocked.Increment` for progress counter | +40, -20 |
| `Models/ConfigModel.cs` | Add `StartupFolder` string property with default `"AllMail"` | +5 |
| `Services/ConfigService.cs` | Read/write `[startup]` section; parse `StartupFolder` | +15 |
| `ViewModels/MainViewModel.cs` | `InitialLoadAsync`: resolve startup folder from config | +20 |
| `ViewModels/SettingsViewModel.cs` | Add `StartupFolder`, `StartupFolderOptions` | +30 |
| `Views/SettingsWindow.xaml` | Add Label, read-only TextBlock, and "Choose…" button for startup folder in General tab | +15 |

### Files to Create

None. All changes extend existing files.

---

## Section 12: Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `SyncServiceTests` (new) | `InboxFoldersCompleteBeforeOtherFolders`, `ExceptionInOneAccountDoesNotCancelOthers`, `ProgressCounterAccurateWithParallelAccounts` | Sync ordering, error isolation, progress reporting |
| `ConfigServiceStartupTests` (new or extend `SettingsViewModelTests`) | `StartupFolder_DefaultIsAllMail`, `StartupFolder_RoundTrip`, `StartupFolder_AbsentKeyReturnsDefault` | Config persistence |
| `MainViewModelStartupTests` (new or extend `ViewModelConstructionTests`) | `InitialLoad_DefaultStartupFolder_SelectsAllMail`, `InitialLoad_AllInboxesStartupFolder`, `InitialLoad_UnknownStartupFolder_FallsBackToAllMail` | Startup folder resolution |
| `SettingsViewModelTests` (extend) | `StartupFolderOptions_ContainsVirtualFolders`, `StartupFolder_BindsToConfig` | Settings persistence |
| `XamlParseTests` (extend) | `SettingsWindow_ParsesWithoutError` (already exists, covers new XAML) | XAML correctness |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `Interlocked.Increment` race in `SyncProgressChanged` counter with parallel accounts | Medium | Minor (cosmetic: incorrect progress count) | Use `Interlocked.Increment`; unit test with two concurrent accounts |
| Saved-view lookup in `InitialLoadAsync` fails because views not yet loaded from SQLite | Medium | Minor (silent fallback to All Mail) | Acceptable for v1; document in Phase 3 implementation note |
| Parallel account sync reveals hidden concurrency bug in `ImapMailService` | Low | Major | Each account already has its own IMAP client stack; concurrent calls on *different* accounts are safe. Test with 3+ accounts. |
| `SyncService` progress total count changes if Inbox-pass and remaining-pass overlap | Low | Minor | Count all non-excluded folders once at the start, same as today; the two-pass split doesn't change total. |

### 13.2 Open questions

All resolved before implementation:

- **Q: Should parallel account sync be capped?** No additional cap needed — each account's IMAP connections are already limited by `MaxImapConnectionsPerAccount`. `Task.WhenAll` over accounts is safe.
- **Q: What if a user's saved view is named "AllMail" — does it shadow the virtual folder?** The virtual folders are matched first by sentinel name; saved views are matched second. "AllMail" (no sentinel prefix) would only shadow if we search saved views before virtuals. Implementation must check sentinel names first.
- **Q: Should "All Inboxes" become the new default instead of "All Mail"?** No — keep "All Mail" as the default for v1 to avoid surprising existing users. The user guide can recommend "All Inboxes" as the better default for inbox-focused users. This can be revisited for v2.

---

## Section 14: Implementation Guidance for AI

### 14.1 Adjustments you're expected to make

- The spec describes `Task.WhenAll` over accounts. If you discover that `ImapMailService.GetOrReconnectAsync` is NOT thread-safe for concurrent calls on *different* account IDs (e.g., a shared dictionary without locking), fall back to sequential account processing but keep the inbox-first folder ordering within each account. Document the deviation.
- `StartupFolderOptions` in `SettingsViewModel` requires access to saved views. Inject the saved views list via the constructor or a method. Choose whichever approach is consistent with how `SettingsViewModel` currently gets other dynamic data.
- The ComboBox in `SettingsWindow.xaml` should use `DisplayMemberPath` or an item template that shows the display name. The stored value should be the sentinel/name string, not an index.

### 14.2 When to ask for clarification

- If `ImapMailService` has a shared lock or synchronization concern that makes parallel account access risky, stop and ask before proceeding with Phase 1.
- If saved views are not available at the time `SettingsViewModel` is constructed (because they load async), stop and ask how to populate `StartupFolderOptions`.

### 14.3 Acceptance walkthrough preview

After implementation, the highest-risk steps to verify are:

1. **Scenario 2, step 2** — inbox messages appear in the list faster than today (subjective but perceptible on a multi-account setup).
2. **Scenario 3** — saved-view default still overrides `StartupFolder`. This is the most likely regression.
3. **Scenario 4** — unknown `StartupFolder` value does not crash on startup.
