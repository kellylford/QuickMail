# Mail Rules — Branch Review: `MailFiltering`

**Reviewer:** Claude Code (Sonnet 4.6)  
**Date:** 2026-05-22  
**Branch:** `origin/MailFiltering` (9 commits, 21 files, ~4,100 lines added)  
**Specs reviewed:** `mail-rules-pm-spec.md`, `mail-rules-dev-spec.md`  
**Purpose:** Evaluate AI agent output quality before deciding whether to merge.

---

## Overall Assessment

**Short version:** The core of this feature is solid and mergeable with targeted fixes. The architecture is correct, MVVM rules are respected, the rule matching engine is well tested, and the integration into the sync pipeline is done right. The agent made several good judgment calls (real folder picker instead of editable ComboBox, shortcut collision avoidance, running rules on existing mail after dialog close). There are meaningful gaps — missing VM and XAML tests, a dead semaphore, a non-keyboard-accessible status bar element — but nothing that corrupts data or crashes the app.

**Recommendation:** Fix the five issues in the "Must Fix Before Merge" section, then merge.

---

## What the Agent Got Right

### Architecture and MVVM compliance

The implementation follows MVVM strictly. `RulesManagerViewModel` has no `MessageBox`, `Window`, or `Dispatcher` references. Delete confirmation goes through a `Func<string, string, bool>? ConfirmDeleteRequested` event, folder picking through `Func<string?>? PickFolderRequested`, announcements through `Action<string, AnnouncementCategory>? AnnouncementRequested`. The code-behind is correctly limited to focus management, keyboard routing, and dialog event wiring.

Event unsubscription is handled correctly in `OnClosed` — all four events are unsubscribed. This follows the pattern mandated in CLAUDE.md to prevent ghost callbacks.

### Rule execution pipeline

The integration point in `SyncService` is correct: rules run after `UpsertSummariesAsync` and before `FolderSynced` fires, so the UI sees the post-rule state. The `RemovedMessages` list is used to delete moved/deleted messages from the local SQLite store before the UI notification, which prevents them from reappearing on the next cache load.

`FolderSynced`, `RulesApplied`, and `MessagesRemoved` are all raised together in a single `Dispatcher.InvokeAsync` call, ensuring UI updates are atomic. This is the right approach.

### Good judgment calls that deviated from spec

**Folder picker over editable ComboBox.** The dev spec called for an editable `ComboBox` for the target folder. The agent replaced this with a "Choose Folder…" button that opens the existing `FolderPickerWindow`. This is substantially better UX — the user gets the same accessible folder tree they already know, instead of needing to type IMAP folder paths.

**Shortcut collision resolution.** The PM spec assigned `Ctrl+Shift+R` to "Manage Rules." That key is already taken by `mail.replyAll`. The agent correctly changed to `Ctrl+Shift+L` and updated both the CommandRegistry registration and the menu `InputGestureText`. Good catch.

**"Apply rules to existing mail" implemented.** The PM spec deferred this to v0.7 as out of scope. The agent implemented `ApplyRulesToExistingAsync` and calls it after the Rules Manager dialog closes. This is strictly additive — it doesn't break anything — and it means rules take effect immediately on existing mail when saved, which is the behavior users will expect. Worth keeping.

**OnFolderSynced fix for regular folders.** The agent identified and fixed a pre-existing bug: `OnFolderSynced` only processed incoming messages when viewing virtual folders (All Mail, All Inboxes). When viewing a regular INBOX, new messages from sync were silently dropped. This was masked by the rules feature making it visible. The fix is in commit `c9df845`.

### Test coverage for RuleService

`RuleServiceTests.cs` (452 lines, 16 tests) covers all the scenarios the dev spec listed: load/save round-trip, corrupted file, empty file, caching, every condition type, case-insensitivity, AND semantics, disabled rules, account scoping, cancellation, and match counting. This is the right level of testing for a service this critical.

---

## Must Fix Before Merge

### 1. `_loadLock` semaphore is dead code — `LoadRules()` is not thread-safe

**File:** `QuickMail/Services/RuleService.cs`

`RuleService` declares `private readonly SemaphoreSlim _loadLock = new(1, 1)` but never uses it. `LoadRules()` checks `_loaded` and then reads the file without any synchronization. If `SyncService` calls `ApplyRulesAsync` (which calls `LoadRules`) on a background thread at the same time the dialog calls `LoadRules` on the UI thread, both could see `_loaded = false` and race to deserialize. The subsequent `_cache = ...` write is not atomic.

Fix: Either use `_loadLock` properly in `LoadRules()`, or remove the semaphore and make `LoadRules()` a simple synchronous method that's only called from the UI thread (since `ApplyRulesAsync` is the only non-UI caller, and it can call a separate internal method). Given that `ContactService` uses the same pattern and it's not thread-safe either, a pragmatic fix is to just remove the dead semaphore field and document that `LoadRules` assumes single-threaded access — the cache protects against repeated I/O, not concurrent access.

### 2. Missing VM and XAML tests

**Files:** `QuickMail.Tests/RulesManagerViewModelTests.cs` (not present), `QuickMail.Tests/RulesManagerXamlParseTests.cs` (not present)

Both were in the dev spec's required test list. The XAML parse test in particular catches `XamlParseException` at check-in time rather than at user runtime. The VM tests would catch command behavior regressions.

The XAML is at moderate risk: it binds `IsFromEnabled`, `IsToEnabled`, `IsSubjectEnabled`, `IsBodyEnabled` via `InverseBoolConverter` — if those property names were misspelled or the converter mis-keyed, the binding silently fails at runtime. A `[StaFact]` XAML parse test would catch this.

The dev spec's test table for `RulesManagerViewModelTests` (13 tests) can be written against `StubRuleService`, which is already present in `StubServices.cs`.

### 3. Rules status bar item is not keyboard-reachable

**File:** `QuickMail/Views/MainWindow.xaml`

```xml
<StatusBarItem x:Name="RulesStatusItem"
               HorizontalAlignment="Right"
               Focusable="False"
               IsTabStop="False"
               ...>
    <TextBox Style="{StaticResource StatusTextBoxStyle}"
             Text="{Binding RulesStatusText, Mode=OneWay}"
             IsTabStop="False"
             Focusable="True"
             .../>
</StatusBarItem>
```

The outer `StatusBarItem` has `Focusable="False"` and `IsTabStop="False"`. The inner `TextBox` has `Focusable="True"` but `IsTabStop="False"`. The result: Tab navigation skips this element entirely. Keyboard users cannot activate it to open the Rules Manager, and screen readers won't announce it during tab traversal.

`MainWindow.xaml.cs` wires `RulesStatusItem.KeyDown` but that handler can never fire because the element never receives focus.

Fix: Set `Focusable="True"` and `IsTabStop="True"` on `RulesStatusItem`. Set `IsReadOnly="True"` on the inner `TextBox` so it doesn't accept text input but still participates in focus. The existing `KeyDown` handler is then sufficient.

### 4. `CreateRuleFromMessage` only works in Messages view mode

**File:** `QuickMail/ViewModels/MainViewModel.cs`

```csharp
private void CreateRuleFromMessage()
{
    var source = SelectedMessage;
    if (source == null) return;  // exits here in Conversations and From/To view modes
    ...
}
```

The dev spec explicitly called for handling all three selection types. The `isAvailable` predicate uses `HasSelectedMessage` which already returns false in Conversations/From/To mode, so the command will be greyed out — but the context menu item has no visibility binding to `CreateRuleFromMessageCommand.CanExecute`, so it may appear enabled when it isn't.

Fix: Either (a) add `CanExecute` binding to the context menu item (`IsEnabled="{Binding CreateRuleFromMessageCommand.CanExecute}"`), or (b) implement the multi-mode selection path. Option (a) is the minimum; option (b) matches the spec.

### 5. `CreateRuleFromMessage` context menu missing `InputGestureText`

**File:** `QuickMail/Views/MainWindow.xaml`

```xml
<MenuItem Header="Create _Rule from Message…"
          AutomationProperties.Name="Create Rule from Message"
          Command="{Binding CreateRuleFromMessageCommand}"/>
```

The registered shortcut is `Ctrl+Shift+T`, but there is no `InputGestureText="Ctrl+Shift+T"` on this menu item. Users won't see the shortcut hint in the context menu.

---

## Should Fix (Not Blocking)

### Match count overcounts messages hit by multiple rules

**File:** `QuickMail/Services/RuleService.cs` (ApplyRulesAsync)

`matchedCount` accumulates per-rule, not per-message. If message A matches both rule 1 and rule 2, `matchedCount` is 2, but only one message was affected. The status bar will read "2 matched" when only 1 message was acted on. This affects the status bar display only — rule execution is correct.

Fix: Track a `HashSet<(uint Uid, Guid AccountId, string FolderName)>` of affected messages and return its count.

### `_imap` field in `RulesManagerViewModel` is unused

```csharp
private readonly IImapService _imap;
```

`IImapService` was passed to the VM in the dev spec to support folder listing (for the editable ComboBox target folder picker). Since the folder picker was moved to code-behind via `PickFolderRequested`, the VM no longer needs IMAP access. Remove the field and constructor parameter to keep the VM lean. Update `StubServices.cs`'s `StubSyncService` test calls accordingly.

### Debug logging is too verbose for production

**File:** `QuickMail/Services/RuleService.cs`

```csharp
LogService.Debug($"ApplyRulesAsync: {enabledRules.Count} enabled rules, {incoming.Count} incoming...");
LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched...");
foreach (var m in matched.Take(3))
    LogService.Debug($"    Match: From='{m.From}' Subject='{m.Subject}'...");
```

`LogService.Debug()` writes only when `/debug` is passed at startup, so this won't spam regular users. But it will produce very noisy logs during debug sessions. Consider reducing to one summary line per sync cycle rather than per-rule-per-message.

### `UseXCondition` flags are unspecced and confusing in `rules.json`

**File:** `QuickMail/Models/MailRule.cs`

The agent added `UseFromCondition`, `UseToCondition`, `UseSubjectCondition`, `UseBodyCondition` booleans to `MailRule`. The intended purpose (enable/disable individual conditions without clearing their values) is sensible UX. But:

1. They're not in the PM or dev spec. The PM spec's design only showed a `TextBox` per condition — no checkbox-per-condition.
2. They bloat `rules.json` with four extra keys per rule.
3. The matching logic changes: `MatchesRule` now requires both `UseXCondition == true` AND the value to be non-empty. A rule with `UseFromCondition = false` and `FromContains = "alice@company.com"` will silently not match on From.

The UI implementation (checkbox + read-only text box per condition) is functional, but it adds complexity beyond what the spec asked for. If the design intent is to keep this, it needs documentation and tests. There are currently zero tests covering the `UseXCondition` flags.

---

## Minor Observations

### ISyncService interface extended correctly

`RulesApplied` event is added to both `ISyncService` and `SyncService`. The stub in `StubServices.cs` does not implement it (it inherits the default `event` which is null), but that's fine for existing tests. New tests for the rules integration path would need it.

### `ApplyRulesToExistingAsync` after dialog close is fire-and-forget with a 30s timeout

`MainWindow.OpenRulesManager` launches a background task post-dialog. The 30-second `CancellationTokenSource` is reasonable, and the `Dispatcher.InvokeAsync(() => _vm.RefreshCommand.Execute(null))` call if messages were removed is correct. One edge case: if the user closes and reopens the dialog rapidly, two background tasks could overlap. Low probability but worth a note.

### `RulesManagerWindow` constructor signature richer than spec

Spec had `RulesManagerWindow(RulesManagerViewModel vm)`. Actual is `(vm, accounts, cachedFolders)`. The extra parameters support the folder picker — this is correct. `_vm.CachedFolders` (a public property on `MainViewModel`) supplies the folder data.

### SaveRules in DeleteRule doesn't announce the count of remaining rules

After deleting a rule, the announcement is `"Rule 'X' deleted."` — fine. The PM spec suggested also surfacing how many rules remain, but the spec text was in the "Rules Manager opened" announcement. No action required.

---

## Test Gap Summary

| Test file | Status |
|---|---|
| `RuleServiceTests.cs` | Present, comprehensive (16 tests) |
| `RulesManagerViewModelTests.cs` | **Missing** — must add |
| `RulesManagerXamlParseTests.cs` | **Missing** — must add |
| Tests for `UseXCondition` flags | **Missing** |
| Test for `ApplyRulesToExistingAsync` | Not present (new method, not tested) |

---

## Spec Compliance Checklist

| Item | Status | Notes |
|---|---|---|
| `MailRule.cs` data model | Partial | Adds 4 unspecced boolean fields |
| `IRuleService` interface | Partial | Returns tuple instead of `int`; adds `ApplyRulesToExistingAsync` |
| `RuleService` storage, matching, execution | Done | Minor: dead semaphore, overcount |
| `SyncService` integration | Done | Correct pipeline position |
| `RulesManagerViewModel` MVVM compliance | Done | `_imap` unused |
| `RulesManagerWindow` XAML layout | Done | Folder picker improved over spec |
| `RulesManagerWindow` code-behind | Done | |
| `MainViewModel` commands + status | Done | |
| `MainWindow` menu items | Partial | `InputGestureText` missing on Create Rule |
| `MainWindow` command registration | Partial | Shortcut `Ctrl+Shift+L` vs spec `Ctrl+Shift+R` (correct change) |
| `App.xaml.cs` DI wiring | Done | |
| `StubRuleService` | Done | |
| `RuleServiceTests` | Done | |
| `RulesManagerViewModelTests` | **Missing** | |
| `RulesManagerXamlParseTests` | **Missing** | |
| Status bar rules display | Partial | Not keyboard-reachable |
| Keyboard shortcuts registered | Done | |
| Accessibility announcements | Done | Uses `AccessibilityHelper.Announce()` with categories |
| `USERGUIDE.md` updated | Done | |
| `CreateRuleFromMessage` multi-view support | Partial | Only works in Messages mode |

---

## Evaluation of the AI Agent Process

This was a well-structured spec-driven generation with a very detailed dev spec that gave the agent specific code to emit. Here is what that process produced and what it didn't.

**What worked well:** The agent followed the dev spec's file list and DI wiring faithfully. It correctly applied CLAUDE.md constraints (no MessageBox in VMs, event-based dialog patterns, CommandRegistry for all shortcuts). It wrote good unit tests for the service layer. It identified and fixed a real pre-existing bug in `OnFolderSynced`. It made thoughtful improvements over the spec (folder picker, existing-mail apply).

**What the process missed:** Tests for the ViewModel and XAML layers — the two most fragile layers in a WPF app. The dev spec called for both; the agent skipped them. The accessibility gap in the status bar (not keyboard-reachable) is exactly the kind of thing that gets caught by an accessibility audit pass but not by automated tests. The `_loadLock` dead code suggests the agent copied the semaphore pattern from `ContactService` but never wired it up — a sign that code generation without execution missed a TODO.

**Overall:** Productive starting point. The feature is architecturally sound and the core engine is correct. The gaps are mostly in test coverage and polish. A human review pass before merge is exactly right for catching this class of issues.
